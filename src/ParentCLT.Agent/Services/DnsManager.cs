using System.Management;
using Microsoft.Extensions.Logging;

namespace ParentCLT.Agent.Services;

/// <summary>
/// Configura el DNS del adaptador de red activo via WMI.
/// Guarda los servidores DNS originales en el ConfigStore para restaurarlos
/// cuando el filtro se desactiva o se desinstala.
/// </summary>
public sealed class DnsManager
{
    private readonly ILogger<DnsManager> _logger;
    private readonly ConfigStore _store;

    public DnsManager(ILogger<DnsManager> logger, ConfigStore store)
    {
        _logger = logger;
        _store = store;
    }

    /// <summary>Apunta el DNS de todos los adaptadores activos a 127.0.0.1.</summary>
    public void ApplyLocalFilter()
    {
        var applied = new List<OriginalDns>();
        foreach (var iface in ActiveInterfaces())
        {
            var name = (string?)iface["Description"] ?? "desconocido";
            var guid = (string?)iface["SettingID"];
            var current = Normalize(iface["DNSServerSearchOrder"]);
            if (current == null || current.Length == 0)
                continue;

            // Nunca guardar el filtro local como si fuera el DNS original:
            // si el adaptador ya apunta a 127.0.0.1 (re-aplicacion / watchdog),
            // conservar el original ya persistido, no sobrescribirlo.
            var isAlreadyFiltered = current.All(s => string.Equals(s, "127.0.0.1", StringComparison.OrdinalIgnoreCase));
            if (isAlreadyFiltered)
            {
                var prev = _store.Load()?.OriginalDns?
                    .FirstOrDefault(o => string.Equals(o.Adapter, name, StringComparison.OrdinalIgnoreCase));
                if (prev != null && (prev.Dhcp || prev.Servers.Length > 0))
                {
                    applied.Add(prev);
                }
                continue;
            }

            // Si el DNS venia por DHCP, la restauracion devuelve el adaptador a DHCP
            // (no se fijan IPs que pueden quedar obsoletas al cambiar de red).
            var esDhcp = guid != null && DnsVieneDeDhcp(guid);

            object? ret = null;
            try
            {
                ret = iface.InvokeMethod(
                    "SetDNSServerSearchOrder",
                    new object[] { new[] { "127.0.0.1" } });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo cambiar DNS en {Adapter}", name);
                continue;
            }

            var code = Convert.ToUInt32(ret);
            if (code == 0)
            {
                applied.Add(new OriginalDns(name, esDhcp ? Array.Empty<string>() : current, esDhcp));
                _logger.LogInformation(
                    "DNS de '{Adapter}' -> 127.0.0.1 (original: {Origen})", name,
                    esDhcp ? "DHCP (automatico)" : string.Join(", ", current));
            }
            else
            {
                _logger.LogWarning("WMI SetDNSServerSearchOrder en '{Adapter}' devolvio codigo {Code}", name, code);
            }
        }

        var state = _store.Load() ?? new AgentState();
        state.OriginalDns = applied;
        _store.Save(state);
    }

    /// <summary>
    /// Determina si el DNS del adaptador viene por DHCP: en el registro,
    /// NameServer vacio/ausente = automatico (DHCP); con valores = estatico.
    /// </summary>
    private static bool DnsVieneDeDhcp(string settingId)
    {
        try
        {
            var guid = settingId.StartsWith("{") ? settingId : $"{{{settingId}}}";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}");
            var nameServer = key?.GetValue("NameServer") as string;
            return string.IsNullOrWhiteSpace(nameServer);
        }
        catch
        {
            return false; // conservador: tratar como estatico
        }
    }

    /// <summary>Restaura los servidores DNS guardados.</summary>
    public void RestoreOriginalDns()
    {
        var state = _store.Load();
        if (state?.OriginalDns is not { Count: > 0 })
        {
            _logger.LogInformation("No hay DNS original guardado que restaurar.");
            return;
        }

        var pendientes = new List<OriginalDns>();
        foreach (var original in state.OriginalDns)
        {
            var ok = false;
            foreach (var iface in ActiveInterfaces())
            {
                if (!string.Equals((string?)iface["Description"], original.Adapter, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Dhcp=true -> volver al DNS automatico (null = valores del DHCP);
                // estatico -> restaurar los servidores guardados.
                var ret = Convert.ToUInt32(iface.InvokeMethod(
                    "SetDNSServerSearchOrder",
                    new object?[] { original.Dhcp ? null : original.Servers }));
                if (ret == 0)
                {
                    ok = true;
                    _logger.LogInformation(
                        "DNS restaurado en '{Adapter}': {Modo}",
                        original.Adapter,
                        original.Dhcp ? "DHCP (automatico)" : string.Join(", ", original.Servers));
                }
                else
                {
                    _logger.LogWarning("Fallo restaurando DNS en '{Adapter}' (codigo {Code})", original.Adapter, ret);
                }
            }

            // Conservar el original si no se pudo restaurar (reintento en el proximo ciclo/uninstall).
            if (!ok)
                pendientes.Add(original);
        }

        state.OriginalDns = pendientes;
        _store.Save(state);
    }

    private static IEnumerable<ManagementObject> ActiveInterfaces()
    {
        using var searcher = new ManagementObjectSearcher(
            "root\\cimv2",
            "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=TRUE");
        foreach (var obj in searcher.Get())
        {
            if (obj is ManagementObject mo)
                yield return mo;
            else
                obj.Dispose();
        }
    }

    private static string[]? Normalize(object? value)
    {
        if (value is string s) return new[] { s };
        if (value is string[] arr) return arr;
        return null;
    }
}