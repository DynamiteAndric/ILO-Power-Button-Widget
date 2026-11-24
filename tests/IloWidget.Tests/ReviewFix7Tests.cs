using System.IO;
using System.Windows.Input;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.ViewModels;
using Xunit;

namespace IloWidget.Tests;

public sealed class ReviewFix7SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ilo-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public ReviewFix7SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static WidgetSettings Real() => new() { Host = "10.0.0.5", User = "u", ProtectedPassword = "x" };

    [Fact]
    public void Missing_file_gives_defaults_without_a_failure_and_can_be_saved()
    {
        var store = new SettingsStore(FilePath);

        var s = store.Load();

        Assert.False(store.LoadFailed);
        Assert.False(s.IsConfigured);
        store.Save(Real());
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Locked_file_gives_defaults_with_LoadFailed_and_a_plain_save_keeps_the_original()
    {
        var first = new SettingsStore(FilePath);
        first.Save(Real());
        var original = File.ReadAllText(FilePath);

        var store = new SettingsStore(FilePath);
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var s = store.Load();
            Assert.True(store.LoadFailed);
            Assert.False(s.IsConfigured);
            Assert.ThrowsAny<IOException>(() => store.Save(s));
        }

        Assert.Equal(original, File.ReadAllText(FilePath));
        Assert.True(store.LoadFailed);
    }

    [Fact]
    public void Forced_save_works_and_clears_the_flag()
    {
        File.WriteAllText(FilePath, "{ not json");
        var store = new SettingsStore(FilePath);
        store.Load();
        Assert.True(store.LoadFailed);

        store.Save(Real(), force: true);

        Assert.False(store.LoadFailed);
        Assert.Equal("10.0.0.5", new SettingsStore(FilePath).Load().Host);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_before_a_forced_save()
    {
        File.WriteAllText(FilePath, "{ not json");
        var store = new SettingsStore(FilePath);
        store.Load();
        Assert.True(store.LoadFailed);
        Assert.ThrowsAny<IOException>(() => store.Save(Real()));
        Assert.Equal("{ not json", File.ReadAllText(FilePath));

        store.Save(Real(), force: true);

        Assert.Equal("{ not json", File.ReadAllText(FilePath + ".bad"));
    }
}

public class ReviewFix7ViewModelTests
{
    private static ServerStatus St(string power, string? post = null) =>
        new(power, "ProLiant", "SN", "OK", post, "J06", "/redfish/v1");

    private static WidgetSettings Configured() =>
        new() { Host = "10.0.0.5", User = "u", ProtectedPassword = "x", PollSeconds = 10, ShowTemperatures = true };

    private static IloException Err(IloErrorKind kind) => new(kind, "x");

    [Fact]
    public async Task One_transient_failure_while_online_keeps_state_and_polls_fast()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();
        Assert.Equal(TimeSpan.FromSeconds(10), vm.NextDelay);

        fake.Error = Err(IloErrorKind.Unreachable);
        await vm.RefreshAsync();

        Assert.Equal(HudState.Online, vm.State);
        Assert.True(vm.CanUsePower);
        Assert.Equal("LINK UNSTABLE - RETRYING", vm.Detail);
        Assert.Equal(TimeSpan.FromSeconds(3), vm.NextDelay);
    }

    [Fact]
    public async Task Two_consecutive_transient_failures_show_the_error()
    {
        var fake = new FakeClient { Status = St("Off", "PowerOff") };
        var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();

        fake.Error = Err(IloErrorKind.BadResponse);
        await vm.RefreshAsync();
        Assert.Equal(HudState.Offline, vm.State);
        await vm.RefreshAsync();

        Assert.Equal(HudState.Error, vm.State);
        Assert.False(vm.CanUsePower);
    }

    [Fact]
    public async Task Auth_failure_shows_the_error_immediately()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();

        fake.Error = Err(IloErrorKind.Auth);
        await vm.RefreshAsync();

        Assert.Equal(HudState.Error, vm.State);
    }

    [Fact]
    public async Task A_successful_poll_resets_the_failure_count()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();

        fake.Error = Err(IloErrorKind.NotSent);
        await vm.RefreshAsync();
        fake.Error = null;
        await vm.RefreshAsync();
        Assert.Equal(TimeSpan.FromSeconds(10), vm.NextDelay);
        fake.Error = Err(IloErrorKind.NotSent);
        await vm.RefreshAsync();

        Assert.Equal(HudState.Online, vm.State);
    }

    [Fact]
    public async Task A_failure_while_not_power_capable_applies_at_once()
    {
        var fake = new FakeClient { Error = Err(IloErrorKind.Unreachable) };
        var vm = new MainViewModel(Configured(), _ => fake);

        await vm.RefreshAsync();

        Assert.Equal(HudState.Error, vm.State);
    }

    [Fact]
    public async Task Error_readout_blanks_stale_sensor_values_but_a_tolerated_failure_does_not()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();
        Assert.NotEqual("--", vm.Temperature);

        fake.Error = Err(IloErrorKind.Unreachable);
        await vm.RefreshAsync();
        Assert.NotEqual("--", vm.Temperature);
        Assert.NotEqual("--", vm.Fans);

        await vm.RefreshAsync();
        Assert.Equal("--", vm.Temperature);
        Assert.Equal("--", vm.Fans);
    }
}

[Collection("Wpf")]
public class ReviewFix7KeyGuardTests
{
    [Fact]
    public void Key_down_is_swallowed_only_with_the_panel_visible_and_guard_or_repeat()
    {
        Assert.Equal((true, true), MainWindow.DecideConfirmKey(true, true, Key.Space, false, false));
        Assert.Equal((true, false), MainWindow.DecideConfirmKey(false, true, Key.Enter, true, false));
        Assert.Equal((false, false), MainWindow.DecideConfirmKey(false, true, Key.Space, false, false));
        Assert.Equal((false, true), MainWindow.DecideConfirmKey(true, false, Key.Space, false, false));
    }

    [Fact]
    public void Key_up_with_the_panel_visible_is_swallowed_once_and_clears_the_guard()
    {
        Assert.Equal((true, false), MainWindow.DecideConfirmKey(true, true, Key.Space, false, true));
        Assert.Equal((false, false), MainWindow.DecideConfirmKey(false, true, Key.Space, false, true));
    }

    [Fact]
    public void Stale_guard_with_the_panel_hidden_is_cleared_and_the_key_up_passes_through()
    {
        Assert.Equal((false, false), MainWindow.DecideConfirmKey(true, false, Key.Space, false, true));
    }

    [Fact]
    public void Other_keys_are_ignored()
    {
        Assert.Equal((false, true), MainWindow.DecideConfirmKey(true, true, Key.A, false, true));
    }
}

[Collection("IloLog")]
public class ReviewFix7LogTests
{
    [Fact]
    public void A_write_lands_while_the_file_is_held_open_shared_by_another_writer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ilolog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            IloLog.Directory = dir;
            var file = IloLog.CurrentFile!;
            using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                IloLog.Info("from the second instance");
            }

            Assert.Contains("from the second instance", File.ReadAllText(file));
        }
        finally
        {
            IloLog.Directory = null;
            Directory.Delete(dir, recursive: true);
        }
    }
}
