using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.ViewModels;
using Xunit;

namespace IloWidget.Tests;

public class ReviewFix5Tests
{
    private static ServerStatus On() => new("On", "M", "SN", "OK", "FinishedPost", "J06", "/redfish/v1");

    private static WidgetSettings Configured() =>
        new() { Host = "10.0.0.5", User = "u", ProtectedPassword = "x", PollSeconds = 10 };

    // ---- 2: connect timeout is "not sent" ----

    // ---- 5: the settings test is serialized with other iLO calls ----

    [Fact]
    public async Task Test_waits_for_a_poll_in_flight()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeClient { Status = On(), StatusGate = gate };
        using var vm = new MainViewModel(Configured(), _ => fake);
        var poll = vm.RefreshAsync();

        var test = vm.TestAsync(Configured());
        await Task.Delay(100);
        Assert.False(test.IsCompleted);

        gate.SetResult();
        await poll;
        var (ok, _) = await test;
        Assert.True(ok);
    }

    // ---- 6: null strings in settings.json ----

    [Fact]
    public void Load_turns_null_strings_into_empty_ones()
    {
        var path = Path.Combine(Path.GetTempPath(), "ilo-null-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"Host":null,"User":null,"ProtectedPassword":null,"PollSeconds":5}""");

            var s = new SettingsStore(path).Load();

            Assert.Equal("", s.Host);
            Assert.Equal("", s.User);
            Assert.Equal("", s.ProtectedPassword);
            Assert.Equal(5, s.PollSeconds);
            Assert.False(s.IsConfigured);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NormalizeHost_accepts_null()
    {
        Assert.Equal("", IloClient.NormalizeHost(null));
    }

    // ---- 8: saved window position must be on a monitor ----

    private static readonly Rect[] LShape =
    {
        new(0, 0, 1920, 1080),
        new(-1280, 1080, 1280, 1024), // second monitor below-left: the box around both has an empty top-left corner
    };

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(1900, 100, false)]    // only 20 px of the grip on the screen
    [InlineData(-1000, 100, false)]   // inside the bounding box, on no monitor
    [InlineData(-1000, 1200, true)]
    [InlineData(5000, 100, false)]
    public void Saved_position_must_overlap_a_monitor(double left, double top, bool visible)
    {
        Assert.Equal(visible, MainWindow.IsPositionVisible(left, top, 60, LShape));
    }
}
