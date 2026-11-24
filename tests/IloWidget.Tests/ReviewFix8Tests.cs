using System.Net;
using System.Net.Http;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.ViewModels;
using Xunit;

namespace IloWidget.Tests;

public class ReviewFix8ApiTests
{
    // ---- 1: API discovery falls through to the next base on redirects, 400 and 501 ----

    [Theory]
    [InlineData(HttpStatusCode.MultipleChoices)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task Redfish_not_here_falls_back_to_rest(HttpStatusCode code)
    {
        var h = new StubHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/redfish")
            ? StubHandler.Json(code)
            : StubHandler.Json(HttpStatusCode.OK, """{"Power":"Off"}"""));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var s = await c.GetStatusAsync();

        Assert.True(s.IsOff);
        Assert.Equal("/rest/v1", s.ApiBase);
        Assert.Equal("/rest/v1/Systems/1/", h.Requests.Last().Path);
    }

    [Fact]
    public async Task Redfish_503_is_a_protocol_error_and_rest_is_not_tried()
    {
        var h = new StubHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/redfish")
            ? StubHandler.Json(HttpStatusCode.ServiceUnavailable)
            : StubHandler.Json(HttpStatusCode.OK, """{"Power":"Off"}"""));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var e = await Assert.ThrowsAsync<IloException>(() => c.GetStatusAsync());

        Assert.Equal(IloErrorKind.Protocol, e.Kind);
        Assert.All(h.Requests, r => Assert.StartsWith("/redfish", r.Path));
    }
}

public class ReviewFix8ViewModelTests
{
    private static ServerStatus St(string power, string? post = null, string? health = "OK") =>
        new(power, "ProLiant MicroServer Gen8", "SN", health, post, "J06", "/redfish/v1");

    private static WidgetSettings Configured() =>
        new() { Host = "10.0.0.5", User = "u", ProtectedPassword = "x", PollSeconds = 10 };

    // ---- 2: the power-on grace holds Booting for any state that is not On ----

    [Fact]
    public async Task Poll_reporting_reset_within_the_power_on_grace_keeps_booting()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var fake = new FakeClient { Status = St("Off", "PowerOff") };
        using var vm = new MainViewModel(Configured(), _ => fake, () => now);
        await vm.RefreshAsync();

        await vm.PowerOnAsync();
        fake.Status = St("Reset", null);
        now = now.AddSeconds(5);
        await vm.RefreshAsync();

        Assert.Equal(HudState.Booting, vm.State);
        Assert.Equal("BOOT SEQUENCE", vm.Label);
        Assert.Equal("POWER COMMAND SENT", vm.Detail);
    }

    // ---- 3: one transient link failure is tolerated while Booting or Stopping too ----

    [Fact]
    public async Task Stopping_keeps_state_on_one_transient_failure_and_shows_error_on_the_second()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        using var vm = new MainViewModel(Configured(), _ => fake, () => now);
        await vm.RefreshAsync();
        await vm.PowerOffAsync();
        Assert.Equal(HudState.Stopping, vm.State);

        fake.Error = new IloException(IloErrorKind.Unreachable, "connect failed");
        await vm.RefreshAsync();

        Assert.Equal(HudState.Stopping, vm.State);
        Assert.Equal("LINK UNSTABLE - RETRYING", vm.Detail);

        await vm.RefreshAsync();

        Assert.Equal(HudState.Error, vm.State);
    }

    [Fact]
    public async Task Booting_keeps_state_on_one_transient_failure()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var fake = new FakeClient { Status = St("Off", "PowerOff") };
        using var vm = new MainViewModel(Configured(), _ => fake, () => now);
        await vm.RefreshAsync();
        await vm.PowerOnAsync();
        Assert.Equal(HudState.Booting, vm.State);

        fake.Error = new IloException(IloErrorKind.NotSent, "refused");
        await vm.RefreshAsync();

        Assert.Equal(HudState.Booting, vm.State);
        Assert.Equal("LINK UNSTABLE - RETRYING", vm.Detail);
    }
}

public class ReviewFix8ConsoleTests
{
    /// <summary>A web session whose logout waits until the test completes the gate.</summary>
    private sealed class GatedLogoutSession : IIloWebSession
    {
        public readonly TaskCompletionSource LogoutGate = new();

        public Task<string> OpenWebSessionAsync(CancellationToken ct = default) => Task.FromResult("key");

        public async Task CloseWebSessionAsync(string sessionKey, CancellationToken ct = default) => await LogoutGate.Task;

        public void Dispose()
        {
        }
    }

    // ---- 4: a reconnect whose logout outlives the console does not start a new session ----

    [Fact]
    public async Task Reopen_whose_logout_outlives_close_returns_null_without_a_new_session()
    {
        var first = new GatedLogoutSession();
        var created = 0;
        var session = new ConsoleSession(() => ++created == 1 ? first : new GatedLogoutSession());
        Assert.Equal("key", await session.OpenAsync());

        var reopen = session.OpenAsync(); // reconnect: its release starts the logout, which is blocked
        await Task.Delay(50);
        var close = session.CloseAsync();
        first.LogoutGate.SetResult();

        Assert.Null(await reopen);
        await close;
        Assert.Equal(1, created);
    }
}
