using System.Net;
using System.Net.Http;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.ViewModels;
using Xunit;

namespace IloWidget.Tests;

public class ReviewFix9ApiTests
{
    // ---- 2: a 200 that is not a JSON object is a bad response and does not cache the API base ----

    [Theory]
    [InlineData("")]
    [InlineData("<html>login</html>")]
    public async Task Unreadable_200_on_discovery_is_bad_response_and_the_base_is_not_cached(string body)
    {
        var first = true;
        var h = new StubHandler(r =>
        {
            if (first)
            {
                first = false;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
            return StubHandler.Json(HttpStatusCode.OK, """{"Power":"Off"}""");
        });
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var e = await Assert.ThrowsAsync<IloException>(() => c.GetStatusAsync());
        Assert.Equal(IloErrorKind.BadResponse, e.Kind);

        var s = await c.GetStatusAsync();

        Assert.Equal("/redfish/v1", s.ApiBase);
        Assert.All(h.Requests, r => Assert.StartsWith("/redfish/v1/Systems/1/", r.Path));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Unreadable_200_after_the_base_is_cached_is_bad_response()
    {
        var calls = 0;
        var h = new StubHandler(_ => ++calls == 1
            ? StubHandler.Json(HttpStatusCode.OK, """{"Power":"On"}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>busy</html>") });
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);
        Assert.True((await c.GetStatusAsync()).IsOn);

        var e = await Assert.ThrowsAsync<IloException>(() => c.GetStatusAsync());

        Assert.Equal(IloErrorKind.BadResponse, e.Kind);
    }

    // ---- 4: a 403 on a read is not the power privilege message; a 403 on a power POST still is ----

    [Fact]
    public async Task Status_read_403_is_read_forbidden_with_the_account_readout()
    {
        var h = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Forbidden));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var e = await Assert.ThrowsAsync<IloException>(() => c.GetStatusAsync());

        Assert.Equal(IloErrorKind.ReadForbidden, e.Kind);
        var r = HudMapper.Map(e);
        Assert.Equal(HudState.Error, r.State);
        Assert.Equal("ACCESS DENIED", r.Label);
        Assert.Equal("ACCOUNT DISABLED OR LACKS LOGIN PRIVILEGE", r.Detail);
    }

    [Fact]
    public async Task Power_post_403_stays_forbidden_with_the_power_privilege_readout()
    {
        var h = new StubHandler(r => r.Method == HttpMethod.Get
            ? StubHandler.Json(HttpStatusCode.OK, """{"PowerState":"On"}""")
            : StubHandler.Json(HttpStatusCode.Forbidden));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var e = await Assert.ThrowsAsync<IloException>(() => c.PressPowerButtonAsync());

        Assert.Equal(IloErrorKind.Forbidden, e.Kind);
        Assert.Equal("USER NEEDS VIRTUAL POWER AND RESET", HudMapper.Map(e).Detail);
    }
}

public class ReviewFix9ViewModelTests
{
    private static ServerStatus St(string power, string? post = null) =>
        new(power, "ProLiant MicroServer Gen8", "SN", "OK", post, "J06", "/redfish/v1");

    private static WidgetSettings Configured() =>
        new() { Host = "10.0.0.5", User = "u", ProtectedPassword = "x", PollSeconds = 10 };

    // ---- 1: a successful status read inside a power command resets the poll-failure count ----

    [Fact]
    public async Task A_status_read_in_a_power_command_resets_the_poll_failure_count()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var fake = new FakeClient { Status = St("Off", "PowerOff") };
        using var vm = new MainViewModel(Configured(), _ => fake, () => now);
        await vm.RefreshAsync();

        fake.Error = new IloException(IloErrorKind.Unreachable, "connect failed");
        await vm.RefreshAsync();
        Assert.Equal(HudState.Offline, vm.State);
        Assert.Equal("LINK UNSTABLE - RETRYING", vm.Detail);

        fake.Error = null;
        await vm.PowerOnAsync();
        Assert.Equal(HudState.Booting, vm.State);

        fake.Error = new IloException(IloErrorKind.Unreachable, "connect failed");
        await vm.RefreshAsync();

        Assert.Equal(HudState.Booting, vm.State);
        Assert.Equal("LINK UNSTABLE - RETRYING", vm.Detail);
    }

    // ---- 4: a 403 on a status poll applies at once with the account readout ----

    [Fact]
    public async Task Read_forbidden_poll_shows_the_account_error_at_once()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();

        fake.Error = new IloException(IloErrorKind.ReadForbidden, "Access denied (403)");
        await vm.RefreshAsync();

        Assert.Equal(HudState.Error, vm.State);
        Assert.Equal("ACCESS DENIED", vm.Label);
        Assert.Equal("ACCOUNT DISABLED OR LACKS LOGIN PRIVILEGE", vm.Detail);
    }
}
