using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ParentCLT.Agent.Services;

/// <summary>
/// Ciclo principal: registro -> GET /policy (If-None-Match) -> aplicar DNS + blacklist -> heartbeat.
/// Corre indefinidamente con el intervalo de poll configurado por el servidor.
/// </summary>
public sealed class PolicyWorker : BackgroundService
{
    private readonly ILogger<PolicyWorker> _logger;
    private readonly IConfiguration _config;
    private readonly ConfigStore _store;
    private readonly HttpClient _http = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly JsonSerializerOptions _json = JsonOptions.Create();
    private DnsFilterServer? _dnsFilter;
    private DnsManager? _dnsManager;
    private List<PolicyScheduleDto> _schedule = new();
    private string _appliedScheduleKey = "";
    private DateTime _lastSyncUtc = DateTime.MinValue;
    private int _scheduleEvalSec = 30;

    public PolicyWorker(ILogger<PolicyWorker> logger, ILoggerFactory loggerFactory, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        _store = new ConfigStore();
        _dnsFilter = new DnsFilterServer(
            loggerFactory.CreateLogger<DnsFilterServer>(),
            // IP literal: evita el bucle de bootstrap (el filtro es el propio resolver del sistema)
            // y los bloqueos SNI de DoH que aplican algunos ISPs.
            config["Dns:UpstreamDoH"] ?? "https://1.1.1.1/dns-query");
        _dnsManager = new DnsManager(loggerFactory.CreateLogger<DnsManager>(), _store);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var baseUrl = _config["Server:BaseUrl"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Falta 'Server:BaseUrl' en appsettings.json");
        _http.BaseAddress = new Uri(baseUrl);

        // El dominio del servidor (y su raíz: cparental.quanther.com -> quanther.com)
        // queda SIEMPRE permitido en el filtro: el agente debe alcanzar su API incluso
        // durante block-all, y el panel no debe morir nunca para la familia.
        var serverHost = new Uri(baseUrl).Host.TrimEnd('.').ToLowerInvariant();
        var mgmt = new List<string> { serverHost };
        var labels = serverHost.Split('.');
        if (labels.Length > 2)
            mgmt.Add(string.Join('.', labels[^2..]));
        _dnsFilter!.SetManagementAllowed(mgmt);

        var state = _store.Load() ?? new AgentState();
        if (!string.IsNullOrWhiteSpace(_config["Device:MachineId"]))
            state.MachineId = _config["Device:MachineId"]!;
        if (!string.IsNullOrWhiteSpace(_config["Device:Token"]))
            state.Token = _config["Device:Token"]!;
        _schedule = state.Schedule ?? new();
        _appliedScheduleKey = "";
        if (state.PollIntervalSec >= 30)
            _lastSyncUtc = DateTime.UtcNow - TimeSpan.FromDays(1); // fuerza primer sync inmediato

        _logger.LogInformation(
            "ParentCLT Agent iniciado. Servidor={BaseUrl}, machine_id={MachineId}",
            baseUrl, string.IsNullOrWhiteSpace(state.MachineId) ? "<sin registrar>" : state.MachineId);

        await _dnsFilter!.StartAsync(ct);
        _dnsFilter.ApplyRules(state.Blacklist);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 1) Evaluación local de la programación (barata, cada schedule_eval_sec)
                EvaluateScheduleTick(state);

                // 2) Sincronización con el servidor (register/policy/heartbeat) cada poll_interval
                if (DateTime.UtcNow - _lastSyncUtc >= TimeSpan.FromSeconds(Math.Max(state.PollIntervalSec, 30)))
                {
                    await SyncCycleAsync(state, ct);
                    _lastSyncUtc = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falló el ciclo de sincronización");
            }

            var tickMs = Math.Clamp(_scheduleEvalSec * 1000, 10_000, 300_000);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(tickMs), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Agente detenido.");
    }

    private async Task SyncCycleAsync(AgentState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state.MachineId))
        {
            state.MachineId = Guid.NewGuid().ToString();
            await RegisterAsync(state, ct);
        }
        else if (string.IsNullOrWhiteSpace(state.Token))
        {
            // Sin token: el admin pudo regenerarlo, o aún no nos hemos registrado.
            await RegisterAsync(state, ct);
        }
        else
        {
            await FetchPolicyAsync(state, ct);
        }

        await EnsureDnsAsync(state, ct);
        await SendHeartbeatAsync(state, ct);
    }

    private async Task RegisterAsync(AgentState state, CancellationToken ct)
    {
        var payload = new RegisterRequest(
            state.MachineId,
            _config["Device:Name"] ?? "PC-Sala",
            Environment.OSVersion.VersionString);

        using var resp = await PostJsonAsync("api/v1/devices/register", payload, null, ct);

        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            // El machine_id ya existe en el servidor pero no tenemos token válido.
            _logger.LogWarning("Registro devolvió 409. Regenera el token del dispositivo desde el panel (payload.machine_id={MachineId}).", state.MachineId);
            return;
        }

        resp.EnsureSuccessStatusCode();
        var reg = await resp.Content.ReadFromJsonAsync<RegisterResponse>(_json, ct);
        if (reg is null || string.IsNullOrWhiteSpace(reg.Token))
            throw new InvalidOperationException("Respuesta de registro inválida (sin token)");

        state.Token = reg.Token;
        _logger.LogInformation("Dispositivo registrado. machine_id={MachineId}", state.MachineId);

        if (reg.Policy != null)
            ApplyPolicy(state, reg.Policy);
        else
            _store.Save(state);
    }

    private async Task FetchPolicyAsync(AgentState state, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v1/policy/{state.MachineId}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.Token);
        if (!string.IsNullOrEmpty(state.Version))
            req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{state.Version}\""));

        using var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.NotModified)
            return;

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            state.Token = "";
            _store.Save(state);
            _logger.LogWarning("Token rechazado (401). Se reintentará el registro en el próximo ciclo.");
            return;
        }

        resp.EnsureSuccessStatusCode();
        var policy = await resp.Content.ReadFromJsonAsync<PolicyDto>(_json, ct);
        if (policy != null)
            ApplyPolicy(state, policy);
    }

    private void ApplyPolicy(AgentState state, PolicyDto policy)
    {
        state.Version = policy.Version.ToString();
        state.DnsMode = policy.Dns?.Mode ?? "local-filter";
        if (policy.Settings?.PollIntervalSec is int p && p >= 30)
            state.PollIntervalSec = p;
        if (policy.Settings?.ScheduleEvalSec is int se && se >= 10)
            _scheduleEvalSec = se;
        state.Blacklist = (policy.Blacklist ?? new())
            .Select(r => new BlacklistRule(r.Domain ?? "", r.Type ?? "exact"))
            .Where(r => !string.IsNullOrWhiteSpace(r.Domain))
            .ToList();
        _schedule = policy.Schedule ?? new();
        state.Schedule = _schedule;

        if (_schedule.Count == 0)
        {
            // Compatibilidad heredada: sin programación, se aplica la blacklist global tal cual.
            _dnsFilter!.BlockAll = state.DnsMode == "block-all";
            _dnsFilter!.AllowOnly = false;
            _dnsFilter!.ApplyRules(state.Blacklist);
        }

        _store.Save(state);
        _logger.LogInformation(
            "Política aplicada: version={Version}, {Count} reglas, schedule={Windows} ventanas",
            state.Version, state.Blacklist.Count, _schedule.Count);

        if (_schedule.Count > 0)
            EvaluateScheduleTick(state);
    }

    /// <summary>
    /// Evalúa con el reloj local qué ventana de la programación está activa
    /// (día de la semana + franja horaria) y aplica su perfil. Sin ventana activa,
    /// la programación manda: modo "off" (DNS original, sin filtro).
    /// </summary>
    private void EvaluateScheduleTick(AgentState state)
    {
        if (_schedule.Count == 0)
            return;

        var now = DateTime.Now;
        var dow = (int)now.DayOfWeek; // .NET: 0=Domingo..6=Sábado
        PolicyScheduleDto? active = null;
        foreach (var w in _schedule)
        {
            if (w.DayOfWeek is not int d || d != dow || w.StartTime == null || w.EndTime == null)
                continue;
            if (!TimeSpan.TryParseExact(w.StartTime, @"hh\:mm", null, out var start)
                || !TimeSpan.TryParseExact(w.EndTime, @"hh\:mm", null, out var end))
                continue;
            if (now.TimeOfDay >= start && now.TimeOfDay < end)
            {
                active = w;
                break;
            }
        }

        if (active?.Profile == null)
        {
            SetScheduledMode(state, "off", new List<BlacklistRule>(), new List<string>(), "schedule:sin-ventana");
            return;
        }

        var mode = active.Profile.DnsMode is { Length: > 0 } m ? m : "local-filter";
        var rules = (active.Profile.Blacklist ?? new())
            .Select(r => new BlacklistRule(r.Domain ?? "", r.Type ?? "exact"))
            .Where(r => !string.IsNullOrWhiteSpace(r.Domain))
            .ToList();
        var allowed = (active.Profile.Allowlist ?? new())
            .Select(r => r.Domain ?? "")
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList();
        SetScheduledMode(state, mode, rules, allowed, $"schedule:{active.Profile.Name}:{mode}:{rules.Count}:{allowed.Count}");
    }

    private void SetScheduledMode(AgentState state, string mode, List<BlacklistRule> rules, List<string> allowed, string key)
    {
        if (key == _appliedScheduleKey)
            return;

        _logger.LogInformation("Ventana programada: {Key} (modo={Mode})", key, mode);
        _dnsFilter!.BlockAll = mode == "block-all";
        _dnsFilter!.AllowOnly = mode == "allow-only";
        if (mode == "allow-only")
            _dnsFilter!.SetAllowed(allowed);
        _dnsFilter!.ApplyRules(rules);

        if (mode == "off")
        {
            if (state.DnsFilterApplied)
            {
                _dnsManager!.RestoreOriginalDns();
                state.DnsFilterApplied = false;
                _store.Save(state);
                FlushDns();
            }
        }
        else
        {
            if (!state.DnsFilterApplied)
            {
                _dnsManager!.ApplyLocalFilter();
                state.DnsFilterApplied = true;
                _store.Save(state);
                FlushDns();
            }
        }

        _appliedScheduleKey = key;
    }

    private async Task EnsureDnsAsync(AgentState state, CancellationToken ct)
    {
        if (!_config.GetValue<bool>("Dns:Enabled", true))
        {
            _logger.LogDebug("Dns:Enabled=false; no se modifica la configuracion DNS del equipo.");
            return;
        }

        // Con programación activa, el modo DNS lo gobierna EvaluateScheduleTick
        // (ventana activa -> local-filter; sin ventana -> off/DNS original).
        if (_schedule.Count > 0)
            return;

        if (state.DnsMode == "off")
        {
            if (state.DnsFilterApplied)
            {
                _dnsManager!.RestoreOriginalDns();
                state.DnsFilterApplied = false;
                _store.Save(state);
                FlushDns();
            }
            return;
        }

        if (_dnsFilter!.Running && !state.DnsFilterApplied)
        {
            _dnsManager!.ApplyLocalFilter();
            state.DnsFilterApplied = true;
            _store.Save(state);
            FlushDns();
        }
        else if (!_dnsFilter.Running)
        {
            _logger.LogWarning("Filtro DNS no activo ({Error}); no se modificó la configuración DNS del equipo.", _dnsFilter.LastError ?? "desconocido");
        }
    }

    private async Task SendHeartbeatAsync(AgentState state, CancellationToken ct)
    {
        var errors = new List<string>();
        if (!_dnsFilter!.Running && _dnsFilter.LastError != null)
            errors.Add(_dnsFilter.LastError);

        int.TryParse(state.Version, out var appliedVersion);
        var hb = new HeartbeatRequest
        {
            Status = errors.Count == 0 ? "online" : "degraded",
            AppliedVersion = appliedVersion,
            Errors = errors,
            UptimeSec = (long)(DateTime.UtcNow - _startedAt).TotalSeconds,
            AppliedAt = DateTime.UtcNow.ToString("O")
        };

        try
        {
            using var resp = await PostJsonAsync("api/v1/heartbeat", hb, state.Token, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat fallido");
        }
    }

    private Task<HttpResponseMessage> PostJsonAsync(string url, object body, string? token, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: _json)
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _http.SendAsync(req, ct);
    }

    private void FlushDns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            p?.WaitForExit(10_000);
            _logger.LogInformation("Caché DNS vaciada.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo ejecutar ipconfig /flushdns");
        }
    }

    public override void Dispose()
    {
        _dnsFilter?.Dispose();
        _http.Dispose();
        base.Dispose();
    }
}