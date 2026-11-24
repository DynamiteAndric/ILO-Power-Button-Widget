namespace IloWidget.Models;

/// <summary>Snapshot of the server as reported by iLO (Systems/1).</summary>
public sealed record ServerStatus(
    string Power,
    string? Model,
    string? Serial,
    string? Health,
    string? PostState,
    string? Bios,
    string ApiBase)
{
    /// <summary>OS host name as reported to iLO; often empty while the server is off.</summary>
    public string? HostName { get; init; }

    /// <summary>Processor count and model, e.g. "1x Intel Xeon E3-1220L V2".</summary>
    public string? Cpu { get; init; }

    public int? MemoryGb { get; init; }

    public bool IsOn => string.Equals(Power, "On", StringComparison.OrdinalIgnoreCase);
    public bool IsOff => string.Equals(Power, "Off", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Sensor readings from Chassis/1/Thermal. A value is null when iLO does not report it.</summary>
public sealed record ThermalStatus(int? AmbientC, int? CpuC, int? FanPercent);

public enum HudState
{
    NotConfigured,
    Connecting,
    Offline,
    Booting,
    Online,
    Stopping,
    Error,
}

public enum IloErrorKind
{
    Auth,
    Forbidden,

    /// <summary>A read (status, sensors) was refused with 403: the account is disabled or has no Login privilege.</summary>
    ReadForbidden,
    Certificate,
    Tls,
    Unreachable,
    NoApi,
    Protocol,

    /// <summary>The request went out but the response could not be read or understood (not an HTTP status).</summary>
    BadResponse,
    Config,

    /// <summary>The link failed before any request bytes went out (name lookup, refused connection, TLS handshake).</summary>
    NotSent,

    /// <summary>The stored password cannot be decrypted (e.g. the settings file came from another Windows account).</summary>
    PasswordUnreadable,
}

public sealed class IloException : Exception
{
    public IloErrorKind Kind { get; }

    public IloException(IloErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}
