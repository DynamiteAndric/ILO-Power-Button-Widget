using IloWidget.Models;

namespace IloWidget.ViewModels;

public readonly record struct HudReadout(HudState State, string Label, string Detail);

/// <summary>Turns iLO data and errors into what the HUD shows.</summary>
public static class HudMapper
{
    public static readonly HudReadout NotConfigured = new(HudState.NotConfigured, "AWAITING CONFIG", "OPEN SETTINGS TO LINK ILO");
    public static readonly HudReadout Connecting = new(HudState.Connecting, "LINKING", "CONTACTING ILO");

    public static readonly HudReadout Stopping = new(HudState.Stopping, "SHUTTING DOWN", "POWER BUTTON PRESSED");

    public static readonly HudReadout PowerOnSent = new(HudState.Booting, "BOOT SEQUENCE", "POWER COMMAND SENT");

    public static HudReadout Map(ServerStatus s)
    {
        if (s.IsOff)
            return new(HudState.Offline, "OFFLINE", "SYSTEM POWERED DOWN");

        if (s.IsOn)
        {
            // Only a finished POST (or firmware that reports no POST state at all) counts as online; any other
            // value (InPost*, Reset, PowerOff, Unknown, ...) keeps the shutdown button inactive.
            if (!string.IsNullOrEmpty(s.PostState) && !string.Equals(s.PostState, "FinishedPost", StringComparison.OrdinalIgnoreCase))
            {
                return s.PostState.StartsWith("InPost", StringComparison.OrdinalIgnoreCase)
                    ? new(HudState.Booting, "BOOT SEQUENCE", "POST IN PROGRESS")
                    : new(HudState.Booting, "BOOT SEQUENCE", "SYSTEM STARTING");
            }

            var healthy = string.IsNullOrEmpty(s.Health) || string.Equals(s.Health, "OK", StringComparison.OrdinalIgnoreCase);
            return new(HudState.Online, "ONLINE", healthy ? "ALL SYSTEMS NOMINAL" : $"HEALTH: {s.Health!.ToUpperInvariant()}");
        }

        return new(HudState.Connecting, s.Power.ToUpperInvariant(), "POWER STATE IN TRANSITION");
    }

    public static HudReadout Map(IloException e) => e.Kind switch
    {
        IloErrorKind.Auth => new(HudState.Error, "ACCESS DENIED", "CHECK USER NAME AND PASSWORD"),
        IloErrorKind.Forbidden => new(HudState.Error, "INSUFFICIENT PRIVILEGE", "USER NEEDS VIRTUAL POWER AND RESET"),
        IloErrorKind.ReadForbidden => new(HudState.Error, "ACCESS DENIED", "ACCOUNT DISABLED OR LACKS LOGIN PRIVILEGE"),
        IloErrorKind.Certificate => new(HudState.Error, "CERT REJECTED", "ALLOW SELF-SIGNED CERT IN SETTINGS"),
        IloErrorKind.Tls => new(HudState.Error, "NO LINK", "TLS FAILED - ILO FIRMWARE MAY BE TOO OLD"),
        IloErrorKind.Unreachable or IloErrorKind.NotSent => new(HudState.Error, "NO LINK", "ILO UNREACHABLE - CHECK NETWORK / ADDRESS"),
        IloErrorKind.PasswordUnreadable => new(HudState.Error, "PASSWORD UNREADABLE", "RE-ENTER THE PASSWORD IN SETTINGS"),
        IloErrorKind.NoApi => new(HudState.Error, "FIRMWARE UNSUPPORTED", "NO REDFISH / REST API FOUND"),
        IloErrorKind.Config => new(HudState.Error, "BAD ADDRESS", "CHECK THE ILO HOST IN SETTINGS"),
        _ => new(HudState.Error, "LINK ERROR", e.Message.ToUpperInvariant()),
    };
}
