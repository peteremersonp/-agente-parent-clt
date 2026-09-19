using System.Text.Json;

namespace ParentCLT.Agent.Services;

/// <summary>
/// Persiste el estado del agente en %ProgramData%\ParentCLT\state.json.
/// Sirve de fallback offline: si no hay internet se mantiene la ultima politica.
/// </summary>
public sealed class ConfigStore
{
    public string FilePath { get; }

    public ConfigStore()
    {
        FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ParentCLT", "state.json");
    }

    public AgentState? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            return JsonSerializer.Deserialize<AgentState>(File.ReadAllText(FilePath));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(AgentState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class AgentState
{
    public string Version { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string Token { get; set; } = "";
    public int PollIntervalSec { get; set; } = 600;
    public string DnsMode { get; set; } = "local-filter";
    public List<BlacklistRule> Blacklist { get; set; } = new();
    public List<OriginalDns> OriginalDns { get; set; } = new();
    public bool DnsFilterApplied { get; set; }
    public List<PolicyScheduleDto> Schedule { get; set; } = new();
}

public sealed record BlacklistRule(string Domain, string Type, bool Enabled = true);

/// <summary>
/// DNS original de un adaptador. Si Dhcp=true, Servers viene vacio y la restauracion
/// devuelve el adaptador al DNS provisto por DHCP (SetDNSServerSearchOrder null).
/// </summary>
public sealed record OriginalDns(string Adapter, string[] Servers, bool Dhcp = false);

// ---- DTOs del contrato de API v1 ----

public sealed record RegisterRequest(string MachineId, string Name, string? OsVersion);
public sealed record RegisterResponse(DeviceInfo? Device, string? Token, PolicyDto? Policy);
public sealed record DeviceInfo(string? MachineId, string? Name);

public sealed record PolicyDto
{
    public int Version { get; init; }
    public DnsPolicyDto? Dns { get; init; }
    public List<PolicyRuleDto> Blacklist { get; init; } = new();
    public List<PolicyScheduleDto>? Schedule { get; init; }
    public SettingsDto? Settings { get; init; }
}

public sealed record DnsPolicyDto(string? Mode);
public sealed record PolicyRuleDto(string? Domain, string? Type);
public sealed record SettingsDto(int? PollIntervalSec, int? ScheduleEvalSec);

/// <summary>
/// Ventana de programación: día de la semana (.NET DayOfWeek: 0=Domingo..6=Sábado),
/// franja "HH:mm" (start inclusive, end exclusive; no cruza medianoche) y perfil activo.
/// </summary>
public sealed record PolicyScheduleDto(
    int? DayOfWeek,
    string? StartTime,
    string? EndTime,
    PolicyScheduleProfileDto? Profile);

public sealed record PolicyScheduleProfileDto(
    string? Name,
    string? DnsMode,
    List<PolicyRuleDto>? Blacklist,
    List<PolicyRuleDto>? Allowlist);

public sealed record HeartbeatRequest
{
    public string Status { get; init; } = "online";
    public int AppliedVersion { get; init; }
    public List<string>? Errors { get; init; }
    public long? UptimeSec { get; init; }
    public string? AppliedAt { get; init; }
}

/// <summary>
/// El contrato de la API usa snake_case (machine_id, poll_interval_sec, ...).
/// </summary>
public static class JsonOptions
{
    public static JsonSerializerOptions Create() => new()
    {
        PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
        PropertyNameCaseInsensitive = true
    };
}

public sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        foreach (var c in name)
        {
            if (char.IsUpper(c))
            {
                if (sb.Length > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}