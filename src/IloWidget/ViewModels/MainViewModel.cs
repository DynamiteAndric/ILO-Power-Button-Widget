using System.ComponentModel;
using System.Runtime.CompilerServices;
using IloWidget.Models;
using IloWidget.Services;

namespace IloWidget.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FastPollWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PowerOnGrace = TimeSpan.FromSeconds(20);

    /// <summary>How long a pressed power button is given to shut the OS down before the widget shows ONLINE again.</summary>
    private static readonly TimeSpan ShutdownWindow = TimeSpan.FromMinutes(3);

    private readonly Func<WidgetSettings, IIloClient> _clientFactory;

    /// <summary>The clock. All timing is done on it as UTC, so a DST change cannot stretch or shorten a window.</summary>
    private readonly Func<DateTime> _now;
    private IIloClient? _client;

    /// <summary>Bumped by <see cref="ApplySettingsAsync"/>; a call started under an older value drops its result.</summary>
    private int _generation;

    /// <summary>Serializes iLO calls: a poll and a power command never run at the same time.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A power command was requested and has not finished yet.</summary>
    private bool _commandPending;

    /// <summary>Set by <see cref="Dispose"/>; no call touches iLO after that.</summary>
    private bool _disposed;
    private DateTime _fastUntil = DateTime.MinValue;
    private DateTime _powerOnSentAt = DateTime.MinValue;
    private DateTime _powerOffSentAt = DateTime.MinValue;

    /// <summary>Consecutive failed polls; a successful status read resets it.</summary>
    private int _pollFailures;

    public MainViewModel(WidgetSettings settings, Func<WidgetSettings, IIloClient>? clientFactory = null, Func<DateTime>? now = null)
    {
        _clientFactory = clientFactory ?? IloClient.FromSettings;
        _now = now ?? (() => DateTime.UtcNow);
        Settings = settings;
        Apply(settings.IsConfigured ? HudMapper.Connecting : HudMapper.NotConfigured);
    }

    public WidgetSettings Settings { get; private set; }

    private HudState _state;
    public HudState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value))
                return;
            RaisePowerAbility();
            Raise(nameof(CanOpenConsole));
            Raise(nameof(ShieldLevel));
            Raise(nameof(PowerHint));
            Raise(nameof(PowerTip));
            Raise(nameof(IsTransitioning));
        }
    }

    private string _label = "";
    public string Label { get => _label; private set => Set(ref _label, value); }

    private string _detail = "";
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    private string _model = "--";
    public string Model { get => _model; private set => Set(ref _model, value); }

    private string _health = "--";
    public string Health { get => _health; private set { if (Set(ref _health, value)) Raise(nameof(HealthBad)); } }

    public bool HealthBad => Health != "--" && !string.Equals(Health, "OK", StringComparison.OrdinalIgnoreCase);

    private string _bios = "--";
    public string Bios { get => _bios; private set => Set(ref _bios, value); }

    private string _lastContact = "--";
    public string LastContact { get => _lastContact; private set => Set(ref _lastContact, value); }

    private string _hostName = "--";
    public string HostName { get => _hostName; private set => Set(ref _hostName, value); }

    private string _cpu = "--";
    public string Cpu { get => _cpu; private set => Set(ref _cpu, value); }

    private string _memory = "--";
    public string Memory { get => _memory; private set => Set(ref _memory, value); }

    private string _postState = "--";
    public string PostState { get => _postState; private set => Set(ref _postState, value); }

    private string _temperature = "--";
    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }

    private string _fans = "--";
    public string Fans { get => _fans; private set => Set(ref _fans, value); }

    public string Host => string.IsNullOrWhiteSpace(Settings.Host) ? "--" : IloClient.NormalizeHost(Settings.Host);

    /// <summary>Powering on is possible only when iLO says the server is off.</summary>
    /// <remarks>
    /// A background poll does not count here: it must not disable the button (that would cancel a hold
    /// in progress); a power command started during a poll waits for it instead.
    /// </remarks>
    public bool CanPowerOn => State == HudState.Offline && !_commandPending;

    /// <summary>A graceful shutdown is offered only for a server that is on and past POST.</summary>
    public bool CanPowerOff => State == HudState.Online && !_commandPending;

    /// <summary>What a power-button hold is meant to do, decided when the hold starts.</summary>
    public enum PowerIntent
    {
        None,
        PowerOn,
        Shutdown,
    }

    /// <summary>The intent a hold starting now has.</summary>
    public PowerIntent CurrentIntent => CanPowerOff ? PowerIntent.Shutdown : CanPowerOn ? PowerIntent.PowerOn : PowerIntent.None;

    /// <summary>
    /// A completed hold acts only if the state still allows what it was started for; a poll that changed
    /// the state meanwhile must not turn a shutdown hold into a power-on (or the other way round).
    /// </summary>
    public static bool IntentStillValid(PowerIntent intent, bool canPowerOff, bool canPowerOn) =>
        intent == PowerIntent.Shutdown ? canPowerOff : intent == PowerIntent.PowerOn && canPowerOn;

    /// <summary>The power button is live when it can do either.</summary>
    public bool CanUsePower => CanPowerOn || CanPowerOff;

    /// <summary>The remote console needs a configured iLO that answered the last poll.</summary>
    public bool CanOpenConsole => Settings.IsConfigured
        && State is HudState.Offline or HudState.Booting or HudState.Online or HudState.Stopping;

    /// <summary>The server is on its way up or down; the status label pulses.</summary>
    public bool IsTransitioning => State is HudState.Booting or HudState.Stopping;

    public string PowerHint => State switch
    {
        HudState.Offline => "HOLD TO POWER ON",
        HudState.Booting => "SERVER STARTING",
        HudState.Online => "HOLD TO SHUT DOWN",
        HudState.Stopping => "SERVER SHUTTING DOWN",
        _ => "POWER CONTROL UNAVAILABLE",
    };

    public string PowerTip => State switch
    {
        HudState.Online => "Hold for one second, then confirm, to shut the server down gracefully",
        HudState.Offline => "Hold for one second to power the server on",
        _ => "Power control is unavailable in this state",
    };

    /// <summary>0..1 fill of the shield bar.</summary>
    public double ShieldLevel => State switch
    {
        HudState.Online => 1.0,
        HudState.Booting or HudState.Stopping => 0.5,
        HudState.Error => 1.0,
        _ => 0.0,
    };

    /// <summary>How long to wait before the next poll.</summary>
    public TimeSpan NextDelay
    {
        get
        {
            var normal = TimeSpan.FromSeconds(Math.Clamp(Settings.PollSeconds, WidgetSettings.MinPollSeconds, WidgetSettings.MaxPollSeconds));
            // a clock that went back by more than the window no longer counts as "fast until"
            var left = _fastUntil - _now();
            var fast = left > TimeSpan.Zero && left <= FastPollWindow && State != HudState.Online;
            // one tolerated link failure: look again soon rather than after a full interval
            fast |= _pollFailures > 0 && State is HudState.Offline or HudState.Online or HudState.Booting or HudState.Stopping;
            return fast && FastPoll < normal ? FastPoll : normal;
        }
    }

    /// <summary>
    /// Switches to new link settings. A call still talking to the old iLO is not cut off: its result is
    /// dropped, and the old client is disposed once that call has released the gate. When the returned
    /// task completes, a poll can run at once.
    /// </summary>
    public async Task ApplySettingsAsync(WidgetSettings settings)
    {
        var sameHost = WidgetSettings.SameHost(Settings.Host, settings.Host);
        Settings = settings;
        _generation++;
        _pollFailures = 0;
        var old = _client;
        _client = null;
        // only a different server invalidates what was sent: with the same host it is still shutting down or booting
        if (!sameHost)
            _fastUntil = _powerOnSentAt = _powerOffSentAt = DateTime.MinValue;
        Model = Health = Bios = LastContact = HostName = Cpu = Memory = PostState = Temperature = Fans = "--";
        Raise(nameof(Settings));
        Raise(nameof(Host));
        Apply(settings.IsConfigured ? HudMapper.Connecting : HudMapper.NotConfigured);

        if (old is null)
            return;
        await _gate.WaitAsync();
        try
        {
            old.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes over settings in which only the shown rows changed: the link, the client and the
    /// current readout stay as they are.
    /// </summary>
    public void ApplyDisplaySettings(WidgetSettings settings)
    {
        Settings = settings;
        Raise(nameof(Settings));
        Raise(nameof(Host));
    }

    public async Task RefreshAsync()
    {
        if (!Settings.IsConfigured)
        {
            Apply(HudMapper.NotConfigured);
            return;
        }
        // a poll or power command is already talking to iLO; it updates the HUD itself
        if (!_gate.Wait(0))
            return;

        var gen = _generation;
        try
        {
            if (_disposed)
                return;
            var client = _client ??= _clientFactory(Settings);
            var status = await client.GetStatusAsync();
            if (_disposed || gen != _generation)
                return; // the settings changed meanwhile: this answer is for the old iLO
            ApplyStatus(status);
            await RefreshThermalAsync(client, gen);
        }
        catch (IloException e)
        {
            IloLog.Error($"{e.Kind}: {e.Message}", e.InnerException);
            if (!_disposed && gen == _generation)
            {
                _pollFailures++;
                // Polls must not change CanUsePower: one transient link failure while the power button is live
                // keeps the state (a hold in progress would be cancelled); the second one shows the error.
                if (_pollFailures == 1 && State is HudState.Offline or HudState.Online or HudState.Booting or HudState.Stopping
                    && e.Kind is IloErrorKind.Unreachable or IloErrorKind.NotSent or IloErrorKind.BadResponse)
                {
                    Detail = "LINK UNSTABLE - RETRYING";
                    return;
                }
                Apply(HudMapper.Map(e));
                Temperature = Fans = "--";
            }
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>Reads the sensors when a sensor row is shown. A failure only blanks those rows.</summary>
    private async Task RefreshThermalAsync(IIloClient client, int gen)
    {
        if (!Settings.NeedsThermal)
        {
            Temperature = Fans = "--";
            return;
        }

        try
        {
            var t = await client.GetThermalAsync();
            if (_disposed || gen != _generation)
                return;
            var parts = new List<string>(2);
            if (t.AmbientC is { } ambient)
                parts.Add($"AMB {ambient}°C");
            if (t.CpuC is { } cpu)
                parts.Add($"CPU {cpu}°C");
            Temperature = parts.Count > 0 ? string.Join(" / ", parts) : "--";
            Fans = t.FanPercent is { } fan ? $"{fan}%" : "--";
        }
        catch (IloException)
        {
            if (!_disposed && gen == _generation)
                Temperature = Fans = "--";
        }
    }

    /// <summary>
    /// Presses the server's power button so the OS shuts down gracefully. Re-reads the state first
    /// and does nothing unless the server is on and past POST.
    /// </summary>
    public Task PowerOffAsync() => RunPowerCommandAsync(
        CanPowerOff,
        new PowerCommand(
            RequestedLog: null,
            NotSentLog: "Shutdown NOT sent",
            SendingLog: "Shutdown confirmed: pressing the power button",
            AcceptedLog: "Power button press accepted by iLO",
            NotDelivered: "SHUTDOWN NOT DELIVERED - TRY AGAIN",
            Sent: HudMapper.Stopping,
            Unknown: HudMapper.Stopping with { Label = "SHUTDOWN SENT?", Detail = NoReply },
            StillAllowed: status => HudMapper.Map(status).State == HudState.Online,
            Send: client => client.PressPowerButtonAsync(),
            MarkSent: now =>
            {
                _powerOffSentAt = now;
                _powerOnSentAt = DateTime.MinValue;
            }));

    /// <summary>Sends the power-on command. Re-reads the state first and does nothing if the server is not off.</summary>
    public Task PowerOnAsync() => RunPowerCommandAsync(
        CanPowerOn,
        new PowerCommand(
            RequestedLog: "Power-on requested: re-reading the power state first",
            NotSentLog: "Power-on NOT sent",
            SendingLog: "Server is off: sending power-on",
            AcceptedLog: "Power-on accepted by iLO",
            NotDelivered: "POWER-ON NOT DELIVERED - TRY AGAIN",
            Sent: HudMapper.PowerOnSent,
            Unknown: HudMapper.PowerOnSent with { Label = "POWER-ON SENT?", Detail = NoReply },
            StillAllowed: status => status.IsOff,
            Send: client => client.PowerOnAsync(),
            MarkSent: now =>
            {
                _powerOnSentAt = now;
                _powerOffSentAt = DateTime.MinValue;
            }));

    private const string NoReply = "NO REPLY FROM ILO - CHECKING STATE";

    /// <summary>What differs between the power-on and the shutdown command.</summary>
    private sealed record PowerCommand(
        string? RequestedLog,
        string NotSentLog,
        string SendingLog,
        string AcceptedLog,
        string NotDelivered,
        HudReadout Sent,
        HudReadout Unknown,
        Func<ServerStatus, bool> StillAllowed,
        Func<IIloClient, Task> Send,
        Action<DateTime> MarkSent);

    /// <remarks>
    /// Never retries: a second button press can undo the first. The state is re-read first and the
    /// command goes out only if it still applies.
    /// </remarks>
    private async Task RunPowerCommandAsync(bool allowed, PowerCommand cmd)
    {
        if (!allowed)
            return;

        var gen = _generation;
        var sendStarted = false;
        SetCommandPending(true);
        await _gate.WaitAsync(); // let a poll that is in flight finish first
        try
        {
            if (_disposed || gen != _generation)
                return; // the settings changed while waiting: the request was meant for the old iLO
            if (cmd.RequestedLog is not null)
                IloLog.Info(cmd.RequestedLog);
            var client = _client ??= _clientFactory(Settings);
            var status = await client.GetStatusAsync();
            if (_disposed || gen != _generation)
                return;
            _pollFailures = 0; // a successful status read: the link works again
            if (!cmd.StillAllowed(status))
            {
                IloLog.Info($"{cmd.NotSentLog}: iLO reports Power={status.Power}, PostState={status.PostState}");
                ApplyStatus(status);
                return;
            }

            IloLog.Info(cmd.SendingLog);
            sendStarted = true;
            await cmd.Send(client);
            IloLog.Info(cmd.AcceptedLog);
            if (_disposed || gen != _generation)
                return;
            var now = _now();
            cmd.MarkSent(now);
            _fastUntil = now + FastPollWindow;
            Apply(cmd.Sent);
        }
        catch (IloException e)
        {
            IloLog.Error($"{e.Kind}: {e.Message}", e.InnerException);
            if (!_disposed && gen == _generation)
            {
                if (sendStarted && e.Kind is IloErrorKind.Unreachable or IloErrorKind.BadResponse)
                {
                    // No (readable) reply: iLO may have executed the command. Do not invite a second press; the
                    // normal hold windows and fast polling show what really happened.
                    var now = _now();
                    cmd.MarkSent(now);
                    _fastUntil = now + FastPollWindow;
                    Apply(cmd.Unknown);
                }
                else
                {
                    // a link failure before the send, or one that provably happened before any request bytes went out
                    Apply(e.Kind is IloErrorKind.Unreachable or IloErrorKind.NotSent
                        ? new HudReadout(HudState.Error, "COMMAND FAILED", cmd.NotDelivered)
                        : HudMapper.Map(e));
                }
            }
        }
        finally
        {
            ReleaseGate();
            SetCommandPending(false);
        }
    }

    /// <summary>Tries the given (unsaved) settings and returns a one-line result for the settings panel.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(WidgetSettings candidate)
    {
        if (!candidate.IsConfigured)
            return (false, "HOST, USER AND PASSWORD ARE REQUIRED");
        // like every other iLO call, the test waits for a call in flight (a poll or a power command)
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return (false, "WIDGET IS CLOSING");
            using var client = _clientFactory(candidate);
            var s = await client.GetStatusAsync();
            return (true, $"LINK OK - {(s.Model ?? "SERVER").ToUpperInvariant()} - POWER {s.Power.ToUpperInvariant()}");
        }
        catch (IloException e)
        {
            var r = HudMapper.Map(e);
            return (false, $"{r.Label} - {r.Detail}");
        }
        finally
        {
            ReleaseGate();
        }
    }

    private void ApplyStatus(ServerStatus s)
    {
        _pollFailures = 0;
        Model = s.Model ?? "--";
        Health = s.Health ?? "--";
        Bios = s.Bios ?? "--";
        HostName = s.HostName ?? "--";
        Cpu = s.Cpu ?? "--";
        Memory = s.MemoryGb is { } gb ? $"{gb} GB" : "--";
        PostState = s.PostState ?? "--";
        var contact = _now();
        LastContact = (contact.Kind == DateTimeKind.Utc ? contact.ToLocalTime() : contact).ToString("HH:mm:ss");

        // Right after the power-on command iLO may still report Off for a moment; don't flicker back.
        if (!s.IsOn && InWindow(_powerOnSentAt, PowerOnGrace))
        {
            Apply(HudMapper.PowerOnSent);
            return;
        }

        // After the power button was pressed the OS needs a while to shut down; iLO says On until it has.
        if (InWindow(_powerOffSentAt, ShutdownWindow))
        {
            if (!s.IsOff)
            {
                // On, PoweringOff, Reset or an unreadable answer: still shutting down
                Apply(HudMapper.Stopping);
                return;
            }
            _powerOffSentAt = _fastUntil = DateTime.MinValue;
        }
        Apply(HudMapper.Map(s));
    }

    /// <summary>
    /// True while <paramref name="length"/> has not yet passed since <paramref name="since"/>. A clock that went
    /// backwards (a manual change, a time sync) counts as outside the window rather than extending it.
    /// </summary>
    private bool InWindow(DateTime since, TimeSpan length)
    {
        var elapsed = _now() - since;
        return elapsed >= TimeSpan.Zero && elapsed < length;
    }

    private void Apply(HudReadout r)
    {
        if (r.State != State || r.Label != Label)
            IloLog.Info($"HUD: {r.State} - {r.Label} / {r.Detail}");
        State = r.State;
        Label = r.Label;
        Detail = r.Detail;
    }

    /// <summary>A power command is waiting for or talking to iLO; link settings must not change meanwhile.</summary>
    public bool IsCommandPending => _commandPending;

    private void SetCommandPending(bool value)
    {
        _commandPending = value;
        Raise(nameof(IsCommandPending));
        RaisePowerAbility();
    }

    private void RaisePowerAbility()
    {
        Raise(nameof(CanPowerOn));
        Raise(nameof(CanPowerOff));
        Raise(nameof(CanUsePower));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Releases the gate; a client whose owner was disposed during the call is disposed here.</summary>
    private void ReleaseGate()
    {
        if (_disposed)
            DisposeClient();
        _gate.Release();
    }

    private void DisposeClient()
    {
        var client = _client;
        _client = null;
        client?.Dispose();
    }

    public void Dispose()
    {
        // The gate is not disposed: a call still in flight releases it in its finally block, and a
        // SemaphoreSlim without an AvailableWaitHandle holds no unmanaged resources anyway.
        _disposed = true;
        if (!_gate.Wait(0))
            return; // a call is in flight: it disposes the client when it releases the gate
        try
        {
            DisposeClient();
        }
        finally
        {
            _gate.Release();
        }
    }
}
