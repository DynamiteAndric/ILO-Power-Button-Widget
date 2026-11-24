using System.ComponentModel;
using System.IO;
using System.Net.Http;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.ViewModels;
using Xunit;

namespace IloWidget.Tests;

public class ReviewFix4Tests
{
    private static ServerStatus On() => new("On", "M", "SN", "OK", "FinishedPost", "J06", "/redfish/v1");

    private static ServerStatus Off() => new("Off", "M", "SN", "OK", "PowerOff", "J06", "/redfish/v1");

    private static WidgetSettings Configured() =>
        new() { Host = "10.0.0.5", User = "u", ProtectedPassword = "x", PollSeconds = 10 };

    // ---- 1: clock jumps ----

    [Fact]
    public async Task Clock_jumping_back_after_a_press_does_not_extend_stopping()
    {
        var now = new DateTime(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);
        var fake = new FakeClient { Status = On() };
        using var vm = new MainViewModel(Configured(), _ => fake, () => now);
        await vm.RefreshAsync();
        await vm.PowerOffAsync();
        Assert.Equal(HudState.Stopping, vm.State);

        now = now.AddHours(-1); // DST fall-back, as seen on a local clock
        await vm.RefreshAsync();

        Assert.Equal(HudState.Online, vm.State);
        Assert.Equal(TimeSpan.FromSeconds(10), vm.NextDelay); // no stale fast-poll window either
    }

    [Fact]
    public async Task Clock_jumping_back_after_power_on_ends_the_boot_grace()
    {
        var now = new DateTime(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);
        var fake = new FakeClient { Status = Off() };
        using var vm = new MainViewModel(Configured(), _ => fake, () => now);
        await vm.RefreshAsync();
        await vm.PowerOnAsync();
        Assert.Equal(HudState.Booting, vm.State);

        now = now.AddHours(-1);
        await vm.RefreshAsync();

        Assert.Equal(HudState.Offline, vm.State);
    }

    [Fact]
    public async Task Last_contact_of_a_utc_clock_is_shown_in_local_time()
    {
        var utc = new DateTime(2026, 7, 1, 12, 34, 56, DateTimeKind.Utc);
        var fake = new FakeClient { Status = On() };
        using var vm = new MainViewModel(Configured(), _ => fake, () => utc);

        await vm.RefreshAsync();

        Assert.Equal(utc.ToLocalTime().ToString("HH:mm:ss"), vm.LastContact);
    }

    [Fact]
    public async Task Last_contact_of_an_unspecified_clock_is_shown_as_is()
    {
        var t = new DateTime(2026, 7, 1, 12, 34, 56);
        var fake = new FakeClient { Status = On() };
        using var vm = new MainViewModel(Configured(), _ => fake, () => t);

        await vm.RefreshAsync();

        Assert.Equal("12:34:56", vm.LastContact);
    }

    // ---- 3: failures before the send ----

    private static IloClient ClientThrowing(HttpRequestException e) =>
        new("10.0.0.5", "u", "p", false, new StubHandler(_ => throw e));

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task Connect_phase_errors_are_not_sent(HttpRequestError error)
    {
        using var c = ClientThrowing(new HttpRequestException(error, "connect failed", new IOException("x")));

        var e = await Assert.ThrowsAsync<IloException>(() => c.GetStatusAsync());

        Assert.Equal(IloErrorKind.NotSent, e.Kind);
    }

    [Theory]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.Unknown)]
    public async Task Errors_after_the_send_stay_unreachable(HttpRequestError error)
    {
        using var c = ClientThrowing(new HttpRequestException(error, "ended", new IOException("x")));

        var e = await Assert.ThrowsAsync<IloException>(() => c.GetStatusAsync());

        Assert.Equal(IloErrorKind.Unreachable, e.Kind);
    }

    [Fact]
    public void Not_sent_looks_like_unreachable_on_the_hud()
    {
        Assert.Equal(
            HudMapper.Map(new IloException(IloErrorKind.Unreachable, "x")),
            HudMapper.Map(new IloException(IloErrorKind.NotSent, "x")));
    }

    [Fact]
    public async Task Not_sent_after_the_send_started_is_not_delivered_and_the_button_is_usable_again()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var fake = new FakeClient { Status = On(), PressError = new IloException(IloErrorKind.NotSent, "refused") };
        using var vm = new MainViewModel(Configured(), _ => fake, () => now);
        await vm.RefreshAsync();

        await vm.PowerOffAsync();

        Assert.Equal(1, fake.PowerButtonCalls);
        Assert.Equal(HudState.Error, vm.State);
        Assert.Equal("COMMAND FAILED", vm.Label);
        Assert.Equal("SHUTDOWN NOT DELIVERED - TRY AGAIN", vm.Detail);
        Assert.Equal(TimeSpan.FromSeconds(10), vm.NextDelay);

        await vm.RefreshAsync(); // the next poll restores the real state, and the button works again
        Assert.Equal(HudState.Online, vm.State);
        Assert.True(vm.CanUsePower);
    }

    [Fact]
    public async Task Power_on_not_sent_after_the_send_started_is_not_delivered()
    {
        var fake = new FakeClient { Status = Off(), PowerOnError = new IloException(IloErrorKind.NotSent, "refused") };
        using var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();

        await vm.PowerOnAsync();

        Assert.Equal("COMMAND FAILED", vm.Label);
        Assert.Equal("POWER-ON NOT DELIVERED - TRY AGAIN", vm.Detail);
    }

    // ---- 4: unreadable password ----

    [Fact]
    public void CanUnprotect_accepts_only_a_decryptable_value()
    {
        Assert.False(SettingsStore.CanUnprotect(""));
        Assert.False(SettingsStore.CanUnprotect("not base64 !!"));
        Assert.False(SettingsStore.CanUnprotect(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));
        Assert.True(SettingsStore.CanUnprotect(SettingsStore.Protect("secret")));
    }

    [Fact]
    public void Factory_rejects_an_unreadable_stored_password()
    {
        var s = Configured();
        s.ProtectedPassword = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });

        var e = Assert.Throws<IloException>(() => IloClient.FromSettings(s));

        Assert.Equal(IloErrorKind.PasswordUnreadable, e.Kind);
        var hud = HudMapper.Map(e);
        Assert.Equal("PASSWORD UNREADABLE", hud.Label);
        Assert.Equal("RE-ENTER THE PASSWORD IN SETTINGS", hud.Detail);
    }

    [Fact]
    public void Factory_accepts_a_readable_password()
    {
        var s = Configured();
        s.ProtectedPassword = SettingsStore.Protect("secret");

        using var c = IloClient.FromSettings(s);

        Assert.NotNull(c);
    }

    [Fact]
    public async Task Unreadable_password_shows_on_the_hud_through_the_default_factory()
    {
        var s = Configured();
        s.ProtectedPassword = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        using var vm = new MainViewModel(s);

        await vm.RefreshAsync();

        Assert.Equal("PASSWORD UNREADABLE", vm.Label);
    }

    // ---- 5: display settings raise Host ----

    [Fact]
    public void Display_settings_raise_host()
    {
        using var vm = new MainViewModel(Configured(), _ => new FakeClient());
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var s = Configured();
        s.Host = "10.0.0.9";

        vm.ApplyDisplaySettings(s);

        Assert.Contains(nameof(MainViewModel.Host), raised);
        Assert.Equal("10.0.0.9", vm.Host);
    }

    // ---- 6: dispose during a failing poll ----

    [Fact]
    public async Task Poll_failing_after_dispose_leaves_the_state_alone()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeClient { StatusGate = gate };
        var vm = new MainViewModel(Configured(), _ => fake);
        var before = vm.State;
        var poll = vm.RefreshAsync();

        vm.Dispose();
        fake.Error = new IloException(IloErrorKind.Auth, "401");
        gate.SetResult();
        await poll;

        Assert.Equal(before, vm.State);
        Assert.NotEqual("ACCESS DENIED", vm.Label);
    }
}
