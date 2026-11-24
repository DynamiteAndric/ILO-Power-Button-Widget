using System.IO;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.ViewModels;
using Xunit;

namespace IloWidget.Tests;

internal sealed class FakeClient : IIloClient
{
    public ServerStatus? Status;
    public IloException? Error;
    public IloException? PressError;
    public IloException? PowerOnError;
    public int PowerOnCalls;
    public int PowerButtonCalls;
    public int ThermalCalls;
    public ThermalStatus Thermal = new(22, 40, 35);
    public IloException? ThermalError;
    public bool TurnsOnAfterCommand = true;

    /// <summary>When set, the next status read waits until it is completed (simulates a slow iLO).</summary>
    public TaskCompletionSource? StatusGate;

    public async Task<ServerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (StatusGate is { } gate)
        {
            StatusGate = null;
            await gate.Task;
        }
        if (Error is not null)
            throw Error;
        return Status!;
    }

    public Task<ThermalStatus> GetThermalAsync(CancellationToken ct = default)
    {
        ThermalCalls++;
        return ThermalError is not null ? Task.FromException<ThermalStatus>(ThermalError) : Task.FromResult(Thermal);
    }

    public Task PressPowerButtonAsync(CancellationToken ct = default)
    {
        PowerButtonCalls++;
        return PressError is not null ? Task.FromException(PressError) : Task.CompletedTask;
    }

    public Task PowerOnAsync(CancellationToken ct = default)
    {
        PowerOnCalls++;
        return PowerOnError is not null ? Task.FromException(PowerOnError) : Task.CompletedTask;
    }

    public int DisposeCount;
    public bool Disposed => DisposeCount > 0;

    public void Dispose() => DisposeCount++;
}

public class ViewModelTests
{
    private static ServerStatus St(string power, string? post = null, string? health = "OK") =>
        new(power, "ProLiant MicroServer Gen8", "SN", health, post, "J06", "/redfish/v1");

    private static WidgetSettings Configured() =>
        new() { Host = "10.0.0.5", User = "u", ProtectedPassword = "x", PollSeconds = 10 };

    [Fact]
    public void Mapper_covers_power_and_post_states()
    {
        Assert.Equal(HudState.Offline, HudMapper.Map(St("Off", "PowerOff")).State);
        Assert.Equal(HudState.Booting, HudMapper.Map(St("On", "InPost")).State);
        Assert.Equal(HudState.Booting, HudMapper.Map(St("On", "InPostDiscoveryComplete")).State);
        Assert.Equal(HudState.Online, HudMapper.Map(St("On", "FinishedPost")).State);
        Assert.Equal(HudState.Online, HudMapper.Map(St("On", null)).State);
        Assert.Equal("HEALTH: WARNING", HudMapper.Map(St("On", "FinishedPost", "Warning")).Detail);
        Assert.Equal("ACCESS DENIED", HudMapper.Map(new IloException(IloErrorKind.Auth, "x")).Label);
        Assert.Equal("NO LINK", HudMapper.Map(new IloException(IloErrorKind.Unreachable, "x")).Label);
    }

    [Fact]
    public async Task Unconfigured_widget_never_creates_a_client()
    {
        var created = 0;
        var vm = new MainViewModel(new WidgetSettings(), _ => { created++; return new FakeClient(); });

        await vm.RefreshAsync();

        Assert.Equal(HudState.NotConfigured, vm.State);
        Assert.False(vm.CanPowerOn);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Refresh_shows_status_and_errors()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        var vm = new MainViewModel(Configured(), _ => fake);

        await vm.RefreshAsync();
        Assert.Equal(HudState.Online, vm.State);
        Assert.Equal("ProLiant MicroServer Gen8", vm.Model);
        Assert.False(vm.CanPowerOn);
        Assert.Equal(1.0, vm.ShieldLevel);

        fake.Error = new IloException(IloErrorKind.Auth, "401");
        await vm.RefreshAsync();
        Assert.Equal(HudState.Error, vm.State);
        Assert.Equal("ACCESS DENIED", vm.Label);
        Assert.False(vm.CanPowerOn);
    }

    [Fact]
    public async Task PowerOn_sends_command_only_when_off_and_then_polls_fast()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var fake = new FakeClient { Status = St("Off", "PowerOff") };
        var vm = new MainViewModel(Configured(), _ => fake, () => now);

        await vm.PowerOnAsync();               // state not yet known to be Offline
        Assert.Equal(0, fake.PowerOnCalls);

        await vm.RefreshAsync();
        Assert.True(vm.CanPowerOn);
        Assert.Equal(TimeSpan.FromSeconds(10), vm.NextDelay);

        await vm.PowerOnAsync();
        Assert.Equal(1, fake.PowerOnCalls);
        Assert.Equal(HudState.Booting, vm.State);
        Assert.False(vm.CanPowerOn);
        Assert.Equal(TimeSpan.FromSeconds(3), vm.NextDelay);

        // iLO still says Off a few seconds later: stay in the boot state, do not re-arm the button
        now = now.AddSeconds(5);
        await vm.RefreshAsync();
        Assert.Equal(HudState.Booting, vm.State);

        fake.Status = St("On", "FinishedPost");
        now = now.AddSeconds(60);
        await vm.RefreshAsync();
        Assert.Equal(HudState.Online, vm.State);
        Assert.Equal(TimeSpan.FromSeconds(10), vm.NextDelay);
    }

    [Fact]
    public async Task Background_poll_does_not_disable_the_button_and_power_on_waits_for_it()
    {
        var fake = new FakeClient { Status = St("Off", "PowerOff") };
        var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();
        Assert.True(vm.CanUsePower);

        // a slow poll starts while the user is holding the button
        var gate = new TaskCompletionSource();
        fake.StatusGate = gate;
        var poll = vm.RefreshAsync();
        Assert.True(vm.CanUsePower);           // the hold must not be cancelled
        Assert.True(vm.CanPowerOn);

        // the hold completes during the poll: the command is queued, not dropped
        var powerOn = vm.PowerOnAsync();
        Assert.False(vm.CanUsePower);          // no second command while one is pending
        Assert.Equal(0, fake.PowerOnCalls);

        gate.SetResult();
        await poll;
        await powerOn;
        Assert.Equal(1, fake.PowerOnCalls);
        Assert.Equal(HudState.Booting, vm.State);
    }

    [Fact]
    public async Task PowerOn_is_skipped_if_server_turned_on_meanwhile()
    {
        var fake = new FakeClient { Status = St("Off") };
        var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();

        fake.Status = St("On", "InPost");
        await vm.PowerOnAsync();

        Assert.Equal(0, fake.PowerOnCalls);
        Assert.Equal(HudState.Booting, vm.State);
    }

    [Fact]
    public async Task PowerOff_presses_the_button_only_when_online_and_waits_for_the_os()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var fake = new FakeClient { Status = St("On", "InPost") };
        var vm = new MainViewModel(Configured(), _ => fake, () => now);

        await vm.RefreshAsync();                 // in POST: a press would cut the power
        Assert.False(vm.CanPowerOff);
        Assert.False(vm.CanUsePower);
        await vm.PowerOffAsync();
        Assert.Equal(0, fake.PowerButtonCalls);

        fake.Status = St("On", "FinishedPost");
        await vm.RefreshAsync();
        Assert.True(vm.CanPowerOff);
        Assert.True(vm.CanUsePower);
        Assert.False(vm.CanPowerOn);
        Assert.Equal("HOLD TO SHUT DOWN", vm.PowerHint);

        await vm.PowerOffAsync();
        Assert.Equal(1, fake.PowerButtonCalls);
        Assert.Equal(0, fake.PowerOnCalls);
        Assert.Equal(HudState.Stopping, vm.State);
        Assert.Equal("SHUTTING DOWN", vm.Label);
        Assert.True(vm.IsTransitioning);
        Assert.False(vm.CanUsePower);
        Assert.True(vm.CanOpenConsole);
        Assert.Equal(TimeSpan.FromSeconds(3), vm.NextDelay);

        // iLO keeps saying On while the OS shuts down
        now = now.AddSeconds(30);
        await vm.RefreshAsync();
        Assert.Equal(HudState.Stopping, vm.State);

        fake.Status = St("Off", "PowerOff");
        now = now.AddSeconds(30);
        await vm.RefreshAsync();
        Assert.Equal(HudState.Offline, vm.State);
        Assert.True(vm.CanPowerOn);
        Assert.Equal(TimeSpan.FromSeconds(10), vm.NextDelay);
    }

    [Fact]
    public async Task PowerOff_that_the_os_ignores_falls_back_to_online()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        var vm = new MainViewModel(Configured(), _ => fake, () => now);
        await vm.RefreshAsync();
        await vm.PowerOffAsync();
        Assert.Equal(HudState.Stopping, vm.State);

        now = now.AddMinutes(3).AddSeconds(1);
        await vm.RefreshAsync();

        Assert.Equal(HudState.Online, vm.State);
        Assert.True(vm.CanPowerOff);
    }

    [Fact]
    public async Task PowerOff_is_skipped_if_server_went_down_meanwhile()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();

        fake.Status = St("Off");
        await vm.PowerOffAsync();

        Assert.Equal(0, fake.PowerButtonCalls);
        Assert.Equal(HudState.Offline, vm.State);
    }

    [Fact]
    public async Task Sensors_are_read_only_when_a_sensor_row_is_shown()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        var vm = new MainViewModel(Configured(), _ => fake);

        await vm.RefreshAsync();
        Assert.Equal(0, fake.ThermalCalls);
        Assert.Equal("--", vm.Temperature);

        var shown = vm.Settings.Clone();
        shown.ShowFans = true;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.ApplyDisplaySettings(shown);
        Assert.Contains(nameof(MainViewModel.Settings), raised);
        Assert.Equal(HudState.Online, vm.State);   // only the rows changed, the readout stays

        await vm.RefreshAsync();
        Assert.Equal(1, fake.ThermalCalls);
        Assert.Equal("AMB 22°C / CPU 40°C", vm.Temperature);
        Assert.Equal("35%", vm.Fans);

        fake.Thermal = new ThermalStatus(21, null, null);
        await vm.RefreshAsync();
        Assert.Equal("AMB 21°C", vm.Temperature);
        Assert.Equal("--", vm.Fans);
    }

    [Fact]
    public async Task Sensor_failure_blanks_the_rows_but_not_the_status()
    {
        var settings = Configured();
        settings.ShowTemperatures = true;
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        var vm = new MainViewModel(settings, _ => fake);
        await vm.RefreshAsync();
        Assert.Equal("AMB 22°C / CPU 40°C", vm.Temperature);

        fake.ThermalError = new IloException(IloErrorKind.Protocol, "HTTP 404");
        await vm.RefreshAsync();

        Assert.Equal(HudState.Online, vm.State);
        Assert.Equal("--", vm.Temperature);
        Assert.Equal("--", vm.Fans);
    }

    [Fact]
    public async Task Refresh_fills_the_optional_rows()
    {
        var fake = new FakeClient
        {
            Status = St("On", "FinishedPost") with { HostName = "nas01", Cpu = "1x Intel Xeon E3-1220L V2", MemoryGb = 16 },
        };
        var vm = new MainViewModel(Configured(), _ => fake);

        await vm.RefreshAsync();

        Assert.Equal(("nas01", "1x Intel Xeon E3-1220L V2", "16 GB", "FinishedPost"), (vm.HostName, vm.Cpu, vm.Memory, vm.PostState));

        fake.Status = St("Off");
        await vm.RefreshAsync();
        Assert.Equal(("--", "--", "--", "--"), (vm.HostName, vm.Cpu, vm.Memory, vm.PostState));
    }

    [Fact]
    public async Task Test_link_reports_result()
    {
        var fake = new FakeClient { Status = St("Off") };
        var vm = new MainViewModel(new WidgetSettings(), _ => fake);

        Assert.False((await vm.TestAsync(new WidgetSettings())).Ok);
        var ok = await vm.TestAsync(Configured());
        Assert.True(ok.Ok);
        Assert.Contains("POWER OFF", ok.Message);

        fake.Error = new IloException(IloErrorKind.Certificate, "cert");
        Assert.StartsWith("CERT REJECTED", (await vm.TestAsync(Configured())).Message);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Password_round_trips_through_dpapi_and_is_not_stored_in_clear()
    {
        var path = Path.Combine(Path.GetTempPath(), "ilowidget-test-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            Assert.False(store.Load().IsConfigured);

            var s = new WidgetSettings
            {
                Host = "10.0.0.5",
                User = "admin",
                ProtectedPassword = SettingsStore.Protect("S3cret!pw"),
                AcceptSelfSigned = true,
                PollSeconds = 30,
                Left = 12,
                Top = 34,
            };
            store.Save(s);

            Assert.DoesNotContain("S3cret!pw", File.ReadAllText(path));
            var loaded = store.Load();
            Assert.True(loaded.IsConfigured);
            Assert.Equal("S3cret!pw", SettingsStore.Unprotect(loaded.ProtectedPassword));
            Assert.Equal((30, true, 12.0, 34.0), (loaded.PollSeconds, loaded.AcceptSelfSigned, loaded.Left!.Value, loaded.Top!.Value));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Settings_file_from_before_the_display_options_keeps_the_old_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), "ilowidget-test-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{"Host":"10.0.0.5","User":"admin","ProtectedPassword":"x","PollSeconds":10}""");
            var store = new SettingsStore(path);

            var s = store.Load();
            Assert.True(s.ShowModel && s.ShowAddress && s.ShowHealth && s.ShowBios && s.ShowLastContact);
            Assert.False(s.ShowHostName || s.ShowCpu || s.ShowMemory || s.ShowPostState || s.ShowTemperatures || s.ShowFans);
            Assert.False(s.NeedsThermal);

            s.ShowModel = false;
            s.ShowFans = true;
            store.Save(s);
            var loaded = store.Load();
            Assert.False(loaded.ShowModel);
            Assert.True(loaded.ShowFans && loaded.NeedsThermal);
            Assert.DoesNotContain("NeedsThermal", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Garbage_protected_value_yields_empty_password()
    {
        Assert.Equal("", SettingsStore.Unprotect("not-base64!"));
        Assert.Equal("", SettingsStore.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3 })));
        Assert.Equal("", SettingsStore.Unprotect(""));
    }
}

public class IloLogTests
{
    [Fact]
    public void Redact_masks_passwords_in_bodies()
    {
        var body = """{"method":"login","user_login":"admin","password":"s3\"cr=et"}""";
        var r = IloLog.Redact(body);
        Assert.DoesNotContain("s3", r);
        Assert.Contains("\"password\":\"***\"", r);
        Assert.Contains("admin", r);
        Assert.Equal("""{"ResetType":"On"}""", IloLog.Redact("""{"ResetType":"On"}"""));
    }
}
