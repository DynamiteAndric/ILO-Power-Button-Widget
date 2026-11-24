using System.IO;
using System.Net;
using System.Net.Http;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.ViewModels;
using Xunit;

namespace IloWidget.Tests;

/// <summary>An HTTP body whose read fails, as a response with an invalid charset or a disposed stream does.</summary>
internal sealed class ThrowingContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
        throw new InvalidOperationException("cannot read the body");

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

public class ReviewFix3Tests
{
    private static ServerStatus St(string power, string? post) => new(power, "M", "SN", "OK", post, "J06", "/redfish/v1");

    private static WidgetSettings Configured() =>
        new() { Host = "10.0.0.5", User = "u", ProtectedPassword = "x", PollSeconds = 10 };

    // ---- 1: only a finished POST is online ----

    [Theory]
    [InlineData("FinishedPost", HudState.Online)]
    [InlineData("finishedpost", HudState.Online)]
    [InlineData(null, HudState.Online)]
    [InlineData("", HudState.Online)]
    [InlineData("InPost", HudState.Booting)]
    [InlineData("InPostDiscoveryComplete", HudState.Booting)]
    [InlineData("Reset", HudState.Booting)]
    [InlineData("PowerOff", HudState.Booting)]
    [InlineData("Unknown", HudState.Booting)]
    [InlineData("Null", HudState.Booting)]
    public void Power_on_maps_to_online_only_after_a_finished_post(string? post, HudState expected)
    {
        Assert.Equal(expected, HudMapper.Map(St("On", post)).State);
    }

    [Fact]
    public void Post_details_distinguish_in_post_from_other_states()
    {
        Assert.Equal("POST IN PROGRESS", HudMapper.Map(St("On", "InPost")).Detail);
        Assert.Equal("SYSTEM STARTING", HudMapper.Map(St("On", "Reset")).Detail);
    }

    [Fact]
    public async Task PowerOff_does_not_send_when_post_state_is_reset()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        using var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();
        Assert.True(vm.CanPowerOff);

        fake.Status = St("On", "Reset");
        await vm.PowerOffAsync();

        Assert.Equal(0, fake.PowerButtonCalls);
        Assert.Equal(HudState.Booting, vm.State);
    }

    // ---- 3: cookie domain ----

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("ilo.lan", "ilo.lan")]
    [InlineData("[fd00::10]", "fd00::10")]
    public void Cookie_domain_has_no_brackets(string host, string expected)
    {
        Assert.Equal(expected, IloClient.CookieDomain(IloClient.ConsoleUri(host)));
    }

    // ---- 4: unreadable responses ----

    private static IloClient ClientWith(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new("10.0.0.5", "u", "p", false, new StubHandler(respond));

    [Fact]
    public async Task Unreadable_response_body_is_translated_to_an_ilo_error()
    {
        using var c = ClientWith(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ThrowingContent() });

        var e = await Assert.ThrowsAsync<IloException>(() => c.GetStatusAsync());

        Assert.Equal(IloErrorKind.BadResponse, e.Kind);
        Assert.Contains("Unexpected iLO response", e.Message);
    }

    [Fact]
    public async Task Refresh_shows_an_error_for_an_unreadable_response()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost") };
        using var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();
        Assert.Equal(HudState.Online, vm.State);

        fake.Error = new IloException(IloErrorKind.BadResponse, "Unexpected iLO response: x");
        await vm.RefreshAsync();
        await vm.RefreshAsync(); // the first transient failure is tolerated

        Assert.Equal(HudState.Error, vm.State);
        Assert.Equal("LINK ERROR", vm.Label);
    }

    [Fact]
    public async Task Unreadable_reply_after_power_off_send_is_unknown_not_a_retry_invitation()
    {
        var fake = new FakeClient { Status = St("On", "FinishedPost"), PressError = new IloException(IloErrorKind.BadResponse, "Unexpected iLO response: x") };
        using var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();

        await vm.PowerOffAsync();

        Assert.Equal(HudState.Stopping, vm.State);
        Assert.Equal("SHUTDOWN SENT?", vm.Label);
        Assert.False(vm.CanUsePower);
        Assert.Equal(1, fake.PowerButtonCalls);
    }

    [Fact]
    public async Task Unreadable_response_to_the_power_post_through_the_client_is_bad_response()
    {
        using var c = ClientWith(r => r.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ThrowingContent() }
            : StubHandler.Json(HttpStatusCode.OK, """{"Power":"On"}"""));

        var e = await Assert.ThrowsAsync<IloException>(() => c.PressPowerButtonAsync());

        Assert.Equal(IloErrorKind.BadResponse, e.Kind);
    }

    // ---- 5: power tip ----

    [Fact]
    public async Task Power_tip_follows_the_state()
    {
        var fake = new FakeClient { Status = St("Off", "PowerOff") };
        using var vm = new MainViewModel(Configured(), _ => fake);
        await vm.RefreshAsync();
        Assert.Contains("power the server on", vm.PowerTip);

        fake.Status = St("On", "InPost");
        await vm.RefreshAsync();
        Assert.Equal("Power control is unavailable in this state", vm.PowerTip);

        fake.Status = St("On", "FinishedPost");
        await vm.RefreshAsync();
        Assert.Contains("shut the server down", vm.PowerTip);
    }
}
