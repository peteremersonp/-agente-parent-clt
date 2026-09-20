using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ParentCLT.Agent.Services;

/// <summary>
/// Mini servidor DNS filtrador en 127.0.0.1:53.
/// - Coincide con blacklist (exact o wildcard "*.dominio") -> NXDOMAIN.
/// - El resto se reenvia por DNS-over-HTTPS al upstream configurado.
/// </summary>
public sealed class DnsFilterServer : IDisposable
{
    private readonly ILogger<DnsFilterServer> _logger;
    private readonly HttpClient _doh;
    private volatile IReadOnlyList<BlacklistRule> _rules = Array.Empty<BlacklistRule>();
    private volatile IReadOnlyList<string> _allowed = Array.Empty<string>();
    private volatile IReadOnlyList<string> _management = Array.Empty<string>();
    private volatile bool _blockAll;
    private volatile bool _allowOnly;
    private UdpClient? _udp;
    private readonly object _rulesLock = new();

    public bool Running { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// Dominios de GESTIÓN siempre permitidos (incluso en block-all/allow-only):
    /// el propio dominio del servidor y su dominio raíz (p. ej. cparental.quanther.com
    /// -> cparental.quanther.com + quanther.com y subdominios). Sin esto, el agente
    /// no podría alcanzar la API durante bloqueos totales.
    /// </summary>
    public void SetManagementAllowed(IReadOnlyList<string> domains)
    {
        lock (_rulesLock)
        {
            _management = domains
                .Select(d => d.Trim().TrimEnd('.').ToLowerInvariant())
                .Where(d => d.Length > 0)
                .ToArray();
        }
        _logger.LogInformation("Dominios de gestion siempre permitidos: {Domains}", string.Join(", ", _management));
    }

    /// <summary>
    /// Modo bloqueo total: responde NXDOMAIN a CUALQUIER consulta (internet sin salida).
    /// Se usa con perfiles dns_mode = "block-all" (p. ej. perfil "dormir").
    /// </summary>
    public bool BlockAll
    {
        get => _blockAll;
        set
        {
            _blockAll = value;
            _logger.LogInformation("BlockAll={Value}", value);
        }
    }

    /// <summary>
    /// Modo whitelist (dns_mode = "allow-only"): bloquea TODO excepto los dominios
    /// de la lista de permitidos (incluye subdominios).
    /// </summary>
    public bool AllowOnly
    {
        get => _allowOnly;
        set
        {
            _allowOnly = value;
            _logger.LogInformation("AllowOnly={Value}", value);
        }
    }

    /// <summary>Fijar la lista de dominios permitidos (para el modo AllowOnly).</summary>
    public void SetAllowed(IReadOnlyList<string> allowed)
    {
        lock (_rulesLock)
        {
            _allowed = allowed
                .Select(d => d.Trim().TrimEnd('.').ToLowerInvariant())
                .Where(d => d.Length > 0)
                .ToArray();
        }
        _logger.LogInformation("Lista de permitidos actualizada: {Count} dominios", _allowed.Count);
    }

    public DnsFilterServer(ILogger<DnsFilterServer> logger, string upstreamDoH)
    {
        _logger = logger;
        _doh = new HttpClient { BaseAddress = new Uri(upstreamDoH) };
        _doh.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/dns-message");
    }

    public void ApplyRules(IReadOnlyList<BlacklistRule> rules)
    {
        lock (_rulesLock)
        {
            _rules = rules.ToArray();
        }
        _logger.LogInformation("Blacklist actualizada: {Count} reglas", rules.Count);
    }

    public Task StartAsync(CancellationToken ct)
    {
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53));
        }
        catch (SocketException ex)
        {
            // 10048 = puerto en uso (otro control parental, etc.)
            if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                LastError = "Puerto 53 ocupado por otro servicio; filtro DNS desactivado.";
                _logger.LogError("{Error}", LastError);
                return Task.CompletedTask;
            }
            throw;
        }

        Running = true;
        LastError = null;
        _logger.LogInformation("Mini DNS escuchando en 127.0.0.1:53");
        _ = ReceiveLoopAsync(ct);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                _ = Task.Run(() => HandleAsync(result.Buffer, result.RemoteEndPoint, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error recibiendo datagrama DNS");
            }
        }
    }

    private async Task HandleAsync(byte[] query, IPEndPoint client, CancellationToken ct)
    {
        try
        {
            var qname = TryParseQueryName(query);
            if (qname != null && IsBlocked(qname))
            {
                var nx = BuildNxDomain(query);
                await _udp!.SendAsync(nx, client, ct);
                return;
            }

            var answer = await _doh.PostAsync("", new ByteArrayContent(query)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message") }
            }, ct);
            var bytes = await answer.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 0)
                await _udp!.SendAsync(bytes, client, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo resolviendo consulta DNS");
        }
    }

    private bool IsBlocked(string qname)
    {
        qname = qname.TrimEnd('.').ToLowerInvariant();

        // Dominios de gestión SIEMPRE permitidos (incluso en block-all): el agente
        // debe poder alcanzar su API y el panel cparental.quanther.com nunca muere.
        foreach (var mgmt in _management)
        {
            if (qname.Equals(mgmt, StringComparison.Ordinal)
                || qname.EndsWith("." + mgmt, StringComparison.Ordinal))
                return false;
        }

        if (_blockAll)
            return true;

        if (_allowOnly)
        {
            foreach (var allowed in _allowed)
            {
                // Permitido el dominio exacto y sus subdominios.
                if (qname.Equals(allowed, StringComparison.Ordinal)
                    || qname.EndsWith("." + allowed, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        qname = qname.TrimEnd('.').ToLowerInvariant();
        foreach (var rule in _rules)
        {
            if (!rule.Enabled) continue;
            var d = rule.Domain.TrimEnd('.').ToLowerInvariant();
            if (rule.Type == "wildcard" && d.StartsWith("*."))
            {
                var suffix = d[2..];
                if (qname.Equals(suffix, StringComparison.Ordinal) || qname.EndsWith("." + suffix, StringComparison.Ordinal))
                    return true;
            }
            else if (qname.Equals(d, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Extrae el QNAME (etiquetas sin compression) de una consulta DNS.</summary>
    internal static string? TryParseQueryName(byte[] msg)
    {
        if (msg.Length < 12) return null;
        int pos = 12;
        var sb = new StringBuilder();
        while (true)
        {
            if (pos >= msg.Length) return null;
            int len = msg[pos++];
            if (len == 0) break;
            if ((len & 0xC0) == 0xC0) return null; // puntero de compresion: no parseable, se reenvia tal cual
            if (pos + len > msg.Length) return null;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(msg, pos, len));
            pos += len;
            if (sb.Length > 253) return null;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Responde NXDOMAIN reutilizando el ID y la pregunta original.
    /// </summary>
    internal static byte[] BuildNxDomain(byte[] query)
    {
        var bodyLen = query.Length - 12;
        var resp = new byte[12 + Math.Max(0, bodyLen)];
        resp[0] = query[0];
        resp[1] = query[1];
        resp[2] = (byte)(0x80 | (query[2] & 0x01)); // QR=1, keep RD
        resp[3] = 0x83;                             // RA=1, RCODE=3 (NXDOMAIN)
        resp[4] = query[4]; resp[5] = query[5];     // QDCOUNT
        resp[6] = 0; resp[7] = 0;                   // ANCOUNT=0
        resp[8] = 0; resp[9] = 0;                   // NSCOUNT=0
        resp[10] = query[10]; resp[11] = query[11]; // ARCOUNT (ECO/OPT intactos)
        if (bodyLen > 0)
            Buffer.BlockCopy(query, 12, resp, 12, bodyLen);
        return resp;
    }

    public void Dispose()
    {
        _doh.Dispose();
        _udp?.Dispose();
    }
}