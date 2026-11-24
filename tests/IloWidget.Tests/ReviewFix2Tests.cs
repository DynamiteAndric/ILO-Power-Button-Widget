using System.IO;
using System.Windows;
using System.Windows.Input;
using IloWidget.Controls;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.ViewModels;
using Xunit;

namespace IloWidget.Tests;

public class ReviewFix2Tests
{
    private static WidgetSettings Configured() =>
        new() { Host = "10.0.0.5", User = "u", ProtectedPassword = "x", PollSeconds = 10 };

    private static ServerStatus St(string power, string? post = null) =>
        new(power, "M", "SN", "OK", post, "J06", "/redfish/v1");

    // ---- 1: intent recorded at hold start ----

    [Theory]
    [InlineData(MainViewModel.PowerIntent.Shutdown, true, false, true)]
    [InlineData(MainViewModel.PowerIntent.Shutdown, false, true, false)] // state flipped to off during the hold
    [InlineData(MainViewModel.PowerIntent.Shutdown, false, false, false)]
    [InlineData(MainViewModel.PowerIntent.PowerOn, false, true, true)]
    [InlineData(MainViewModel.PowerIntent.PowerOn, true, false, false)] // state flipped to on during the hold
    [InlineData(MainViewModel.PowerIntent.None, true, false, false)]
    [InlineData(MainViewModel.PowerIntent.None, false, true, false)]
    public void Activation_acts_only_when_the_intent_still_matches(MainViewModel.PowerIntent intent, bool canOff, bool canOn, bool expected) =>
        Assert.Equal(expected, MainViewModel.IntentStillValid(intent, canOff, canOn));

    [Fact]
    public async Task Current_intent_follows_the_state()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        using var vm = new MainViewModel(Configured(), _ => fake);
        Assert.Equal(MainViewModel.PowerIntent.None, vm.CurrentIntent);
        await vm.RefreshAsync();
        Assert.Equal(MainViewModel.PowerIntent.Shutdown, vm.CurrentIntent);
        fake.Status = St("Off", "PowerOff");
        await vm.RefreshAsync();
        Assert.Equal(MainViewModel.PowerIntent.PowerOn, vm.CurrentIntent);
    }

    [Fact]
    public void PowerButton_raises_HoldStarted_when_a_hold_begins()
    {
        var started = 0;
        var activated = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var button = new PowerButton { Width = 150, Height = 150 };
                button.HoldStarted += (_, _) => started++;
                button.Activated += (_, _) => activated++;
                var window = new Window
                {
                    Content = button,
                    Width = 200,
                    Height = 200,
                    Left = -20000,
                    Top = -20000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                };
                window.Show();
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Assert.Equal(1, started);
                Assert.Equal(0, activated); // raised at the start, not at completion
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                window.Close();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Exception("STA test body failed", failure);
    }

    // ---- 3: link settings ----

    [Fact]
    public void SameLink_ignores_everything_but_the_link()
    {
        var a = Configured();
        var b = a.Clone();
        b.PollSeconds = 30;
        b.AlwaysOnTop = true;
        b.ShowCpu = true;
        b.Host = "HTTPS://10.0.0.5/";
        Assert.True(WidgetSettings.SameLink(a, b));

        foreach (var change in new Action<WidgetSettings>[]
        {
            s => s.Host = "10.0.0.6",
            s => s.User = "other",
            s => s.ProtectedPassword = "y",
            s => s.AcceptSelfSigned = true,
        })
        {
            var c = a.Clone();
            change(c);
            Assert.False(WidgetSettings.SameLink(a, c));
        }
    }

    [Fact]
    public async Task Same_host_settings_change_keeps_the_shutdown_hold_but_a_new_host_clears_it()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        using var vm = new MainViewModel(Configured(), _ => fake, () => now);
        await vm.RefreshAsync();
        await vm.PowerOffAsync();
        Assert.Equal(HudState.Stopping, vm.State);

        var sameHost = Configured();
        sameHost.ProtectedPassword = "new";
        await vm.ApplySettingsAsync(sameHost);
        now += TimeSpan.FromSeconds(10);
        await vm.RefreshAsync(); // iLO still says On
        Assert.Equal(HudState.Stopping, vm.State);
        Assert.Equal(TimeSpan.FromSeconds(3), vm.NextDelay);

        var otherHost = Configured();
        otherHost.Host = "10.0.0.9";
        await vm.ApplySettingsAsync(otherHost);
        await vm.RefreshAsync();
        Assert.Equal(HudState.Online, vm.State);
    }

    // ---- 5: Stopping until the server is really off ----

    [Fact]
    public async Task Stopping_lasts_until_ilo_reports_off()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        using var vm = new MainViewModel(Configured(), _ => fake, () => now);
        await vm.RefreshAsync();
        await vm.PowerOffAsync();

        fake.Status = St("PoweringOff");
        now += TimeSpan.FromSeconds(5);
        await vm.RefreshAsync();
        Assert.Equal(HudState.Stopping, vm.State);
        Assert.Equal(TimeSpan.FromSeconds(3), vm.NextDelay);

        fake.Status = St("On", "FinishedPost");
        now += TimeSpan.FromSeconds(5);
        await vm.RefreshAsync();
        Assert.Equal(HudState.Stopping, vm.State);
        Assert.Equal(TimeSpan.FromSeconds(3), vm.NextDelay);

        fake.Status = St("Off", "PowerOff");
        now += TimeSpan.FromSeconds(5);
        await vm.RefreshAsync();
        Assert.Equal(HudState.Offline, vm.State);
        Assert.Equal(1, fake.PowerButtonCalls);
    }

    // ---- 4: closing the console during a login ----

    [Fact]
    public async Task Close_during_login_completes_only_after_the_logout()
    {
        var web = new FakeWebSession();
        var session = new ConsoleSession(() => web, TimeSpan.FromSeconds(30));
        var open = session.OpenAsync();

        var close = session.CloseAsync();
        var second = session.CloseAsync(); // safe to call twice
        await Task.Delay(100);
        Assert.False(close.IsCompleted);
        Assert.Empty(web.LoggedOut);

        web.Login.SetResult("key-1");
        await close;
        await second;
        Assert.Equal(new[] { "key-1" }, web.LoggedOut);
        Assert.Null(await open);
    }

    [Fact]
    public async Task Close_gives_up_on_a_login_that_never_returns()
    {
        var web = new FakeWebSession();
        var session = new ConsoleSession(() => web, TimeSpan.FromMilliseconds(150));
        _ = session.OpenAsync();

        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(web.LoggedOut);
    }

}

/// <summary>IloLog.Directory is process-wide, so the tests that set it must not overlap with any other test.</summary>
[CollectionDefinition("IloLog", DisableParallelization = true)]
public class IloLogCollection
{
}

[Collection("IloLog")]
public class IloLogRolloverTests
{
    // ---- 7: log cleanup on a new file ----

    [Fact]
    public void Log_cleans_old_files_when_a_new_file_is_started()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ilolog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            IloLog.Directory = dir;
            var old = Path.Combine(dir, "ilo-20200101.log");
            File.WriteAllText(old, "x");
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-10));

            IloLog.Info("first write of the day");

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(IloLog.CurrentFile));
        }
        finally
        {
            IloLog.Directory = null;
            Directory.Delete(dir, recursive: true);
        }
    }
}
