using System.Text.Json.Serialization;

namespace IloWidget.Models;

public sealed class WidgetSettings
{
    public string Host { get; set; } = "";
    public string User { get; set; } = "";

    /// <summary>Password encrypted with Windows DPAPI (current user), base64. Never the plain text.</summary>
    public string ProtectedPassword { get; set; } = "";

    public bool AcceptSelfSigned { get; set; }
    public const int MinPollSeconds = 3, MaxPollSeconds = 3600, DefaultPollSeconds = 10;

    public int PollSeconds { get; set; } = DefaultPollSeconds;
    public bool AlwaysOnTop { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }

    // Which info rows the widget shows. The rows that existed before this option default to on.
    public bool ShowModel { get; set; } = true;
    public bool ShowAddress { get; set; } = true;
    public bool ShowHealth { get; set; } = true;
    public bool ShowBios { get; set; } = true;
    public bool ShowLastContact { get; set; } = true;
    public bool ShowHostName { get; set; }
    public bool ShowCpu { get; set; }
    public bool ShowMemory { get; set; }
    public bool ShowPostState { get; set; }
    public bool ShowTemperatures { get; set; }
    public bool ShowFans { get; set; }

    /// <summary>The sensor rows cost one extra iLO request per poll, made only when one of them is shown.</summary>
    [JsonIgnore]
    public bool NeedsThermal => ShowTemperatures || ShowFans;

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(ProtectedPassword);

    public static bool SameHost(string? a, string? b) =>
        string.Equals(Services.IloClient.NormalizeHost(a), Services.IloClient.NormalizeHost(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The stored password may be reused only for the host and user it was saved with; otherwise a changed
    /// host or user name would receive the old credentials. User names are compared case-sensitively.
    /// </summary>
    public static bool KeepsStoredPassword(WidgetSettings saved, string typedHost, string typedUser) =>
        SameHost(saved.Host, typedHost) && string.Equals(saved.User, typedUser, StringComparison.Ordinal);

    /// <summary>
    /// True when nothing that defines the link differs: same host, user, stored password and TLS option.
    /// Poll interval, position, always-on-top and the shown rows are not part of the link.
    /// </summary>
    public static bool SameLink(WidgetSettings a, WidgetSettings b) =>
        SameHost(a.Host, b.Host)
        && string.Equals(a.User, b.User, StringComparison.Ordinal)
        && string.Equals(a.ProtectedPassword, b.ProtectedPassword, StringComparison.Ordinal)
        && a.AcceptSelfSigned == b.AcceptSelfSigned;

    public WidgetSettings Clone() => (WidgetSettings)MemberwiseClone();
}
