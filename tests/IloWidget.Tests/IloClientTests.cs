using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using IloWidget.Models;
using IloWidget.Services;
using Xunit;

namespace IloWidget.Tests;

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public List<(string Method, string Path, string? Body, string? Auth)> Requests { get; } = new();

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
        return _respond(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json = "{}") =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class IloClientTests
{
    private const string RedfishSystem = """
        {"PowerState":"On","Model":"ProLiant MicroServer Gen8","SerialNumber":"CZ1234","BiosVersion":"J06",
         "Status":{"Health":"OK"},"Oem":{"Hp":{"PostState":"FinishedPost"}}}
        """;

    [Fact]
    public async Task Status_uses_redfish_and_parses_fields()
    {
        var h = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, RedfishSystem));
        using var c = new IloClient("https://10.0.0.5/", "admin", "pw", false, h);

        var s = await c.GetStatusAsync();

        Assert.Equal("On", s.Power);
        Assert.True(s.IsOn);
        Assert.Equal("ProLiant MicroServer Gen8", s.Model);
        Assert.Equal("CZ1234", s.Serial);
        Assert.Equal("OK", s.Health);
        Assert.Equal("FinishedPost", s.PostState);
        Assert.Equal("J06", s.Bios);
        Assert.Equal("/redfish/v1", s.ApiBase);
        var r = Assert.Single(h.Requests);
        Assert.Equal(("GET", "/redfish/v1/Systems/1/"), (r.Method, r.Path));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:pw")), r.Auth);
    }

    [Fact]
    public async Task Falls_back_to_legacy_rest_and_reads_Power_field()
    {
        var h = new StubHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/redfish")
            ? StubHandler.Json(HttpStatusCode.NotFound)
            : StubHandler.Json(HttpStatusCode.OK, """{"Power":"Off"}"""));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var s = await c.GetStatusAsync();
        await c.GetStatusAsync();

        Assert.True(s.IsOff);
        Assert.Equal("/rest/v1", s.ApiBase);
        Assert.Null(s.PostState);
        // detection happens once; the second call goes straight to /rest/v1
        Assert.Equal(new[] { "/redfish/v1/Systems/1/", "/rest/v1/Systems/1/", "/rest/v1/Systems/1/" },
            h.Requests.Select(r => r.Path).ToArray());
    }

    [Fact]
    public async Task PowerOn_redfish_posts_reset_action()
    {
        var h = new StubHandler(r => r.Method == HttpMethod.Get
            ? StubHandler.Json(HttpStatusCode.OK, """{"PowerState":"Off"}""")
            : StubHandler.Json(HttpStatusCode.OK));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        await c.PowerOnAsync();

        var post = h.Requests.Last();
        Assert.Equal(("POST", "/redfish/v1/Systems/1/Actions/ComputerSystem.Reset/", """{"ResetType":"On"}"""),
            (post.Method, post.Path, post.Body));
    }

    [Fact]
    public async Task PowerOn_legacy_posts_action_body()
    {
        var h = new StubHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath.StartsWith("/redfish")) return StubHandler.Json(HttpStatusCode.NotFound);
            return StubHandler.Json(HttpStatusCode.OK, """{"Power":"Off"}""");
        });
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        await c.PowerOnAsync();

        var post = h.Requests.Last();
        Assert.Equal(("POST", "/rest/v1/Systems/1", """{"Action":"Reset","ResetType":"On"}"""),
            (post.Method, post.Path, post.Body));
    }

    [Fact]
    public async Task PowerButton_press_uses_the_graceful_action_on_both_apis()
    {
        var redfish = new StubHandler(r => r.Method == HttpMethod.Get
            ? StubHandler.Json(HttpStatusCode.OK, """{"PowerState":"On"}""")
            : StubHandler.Json(HttpStatusCode.OK));
        using var c1 = new IloClient("10.0.0.5", "u", "p", false, redfish);
        await c1.PressPowerButtonAsync();
        var post = redfish.Requests.Last();
        Assert.Equal(("POST", "/redfish/v1/Systems/1/Actions/ComputerSystem.Reset/", """{"ResetType":"PushPowerButton"}"""),
            (post.Method, post.Path, post.Body));

        var legacy = new StubHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/redfish")
            ? StubHandler.Json(HttpStatusCode.NotFound)
            : StubHandler.Json(HttpStatusCode.OK, """{"Power":"On"}"""));
        using var c2 = new IloClient("10.0.0.5", "u", "p", false, legacy);
        await c2.PressPowerButtonAsync();
        post = legacy.Requests.Last();
        Assert.Equal(("POST", "/rest/v1/Systems/1", """{"Action":"PowerButton","PushType":"Press","Target":"/Oem/Hp"}"""),
            (post.Method, post.Path, post.Body));

        var refused = new StubHandler(r => r.Method == HttpMethod.Get
            ? StubHandler.Json(HttpStatusCode.OK, """{"PowerState":"On"}""")
            : StubHandler.Json(HttpStatusCode.Forbidden));
        using var c3 = new IloClient("10.0.0.5", "u", "p", false, refused);
        Assert.Equal(IloErrorKind.Forbidden, (await Assert.ThrowsAsync<IloException>(() => c3.PressPowerButtonAsync())).Kind);
    }

    [Fact]
    public async Task Status_reads_host_cpu_and_memory_in_redfish_and_legacy_shape()
    {
        var redfish = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """
            {"PowerState":"On","HostName":"nas01",
             "ProcessorSummary":{"Count":1,"Model":"Intel(R) Xeon(R) CPU E3-1220L V2 @ 2.30GHz"},
             "MemorySummary":{"TotalSystemMemoryGiB":16}}
            """));
        using var c1 = new IloClient("10.0.0.5", "u", "p", false, redfish);
        var s = await c1.GetStatusAsync();
        Assert.Equal(("nas01", "1x Intel Xeon E3-1220L V2", 16), (s.HostName, s.Cpu, s.MemoryGb));

        var legacy = new StubHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/redfish")
            ? StubHandler.Json(HttpStatusCode.NotFound)
            : StubHandler.Json(HttpStatusCode.OK, """
                {"Power":"On","HostName":"","Processors":{"Count":2,"ProcessorFamily":"Intel(R) Xeon(R) CPU E5-2620 v2 @ 2.10GHz"},
                 "Memory":{"TotalSystemMemoryGB":32}}
                """));
        using var c2 = new IloClient("10.0.0.5", "u", "p", false, legacy);
        s = await c2.GetStatusAsync();
        Assert.Equal((null, "2x Intel Xeon E5-2620 v2", 32), (s.HostName, s.Cpu, s.MemoryGb));

        var bare = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"PowerState":"Off"}"""));
        using var c3 = new IloClient("10.0.0.5", "u", "p", false, bare);
        s = await c3.GetStatusAsync();
        Assert.Equal((null, null, null), (s.HostName, s.Cpu, s.MemoryGb));
    }

    [Fact]
    public async Task Thermal_picks_ambient_hottest_cpu_and_fastest_fan()
    {
        const string thermal = """
            {"Temperatures":[
               {"Name":"01-Inlet Ambient","PhysicalContext":"Intake","ReadingCelsius":22,"Status":{"State":"Enabled"}},
               {"Name":"02-CPU 1","PhysicalContext":"CPU","ReadingCelsius":40,"Status":{"State":"Enabled"}},
               {"Name":"03-CPU 2","PhysicalContext":"CPU","ReadingCelsius":47,"Status":{"State":"Enabled"}},
               {"Name":"04-CPU 3","PhysicalContext":"CPU","ReadingCelsius":0,"Status":{"State":"Absent"}},
               {"Name":"05-P/S 1","PhysicalContext":"PowerSupply","ReadingCelsius":60,"Status":{"State":"Enabled"}}],
             "Fans":[
               {"FanName":"Fan 1","CurrentReading":12,"Units":"Percent","Status":{"State":"Enabled"}},
               {"FanName":"Fan 2","CurrentReading":35,"Units":"Percent","Status":{"State":"Enabled"}},
               {"FanName":"Fan 3","CurrentReading":90,"Units":"Percent","Status":{"State":"Absent"}}]}
            """;
        var h = new StubHandler(r => r.RequestUri!.AbsolutePath.Contains("Thermal")
            ? StubHandler.Json(HttpStatusCode.OK, thermal)
            : StubHandler.Json(HttpStatusCode.OK, RedfishSystem));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var t = await c.GetThermalAsync();

        Assert.Equal(new ThermalStatus(22, 47, 35), t);
        Assert.Equal("/redfish/v1/Chassis/1/Thermal/", h.Requests.Last().Path);
    }

    [Fact]
    public async Task Thermal_reads_legacy_names_and_reports_failures()
    {
        const string thermal = """
            {"Temperatures":[{"Name":"01-Inlet Ambient","CurrentReading":19},{"Name":"02-CPU","CurrentReading":38}],
             "Fans":[{"FanName":"Fan 1","CurrentReading":6}]}
            """;
        var legacy = new StubHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.StartsWith("/redfish")) return StubHandler.Json(HttpStatusCode.NotFound);
            return StubHandler.Json(HttpStatusCode.OK, path.Contains("Thermal") ? thermal : """{"Power":"On"}""");
        });
        using var c1 = new IloClient("10.0.0.5", "u", "p", false, legacy);
        Assert.Equal(new ThermalStatus(19, 38, 6), await c1.GetThermalAsync());
        Assert.Equal("/rest/v1/Chassis/1/Thermal/", legacy.Requests.Last().Path);

        var missing = new StubHandler(r => r.RequestUri!.AbsolutePath.Contains("Thermal")
            ? StubHandler.Json(HttpStatusCode.NotFound)
            : StubHandler.Json(HttpStatusCode.OK, RedfishSystem));
        using var c2 = new IloClient("10.0.0.5", "u", "p", false, missing);
        Assert.Equal(IloErrorKind.Protocol, (await Assert.ThrowsAsync<IloException>(() => c2.GetThermalAsync())).Kind);

        using var empty = System.Text.Json.JsonDocument.Parse("{}");
        Assert.Equal(new ThermalStatus(null, null, null), IloClient.ParseThermal(empty.RootElement));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, IloErrorKind.Auth)]
    [InlineData(HttpStatusCode.Forbidden, IloErrorKind.ReadForbidden)]
    public async Task Auth_errors_are_typed(HttpStatusCode code, IloErrorKind kind)
    {
        var h = new StubHandler(_ => StubHandler.Json(code));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var e = await Assert.ThrowsAsync<IloException>(() => c.GetStatusAsync());
        Assert.Equal(kind, e.Kind);
    }

    [Fact]
    public async Task No_api_is_reported()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>nope</html>") });
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var e = await Assert.ThrowsAsync<IloException>(() => c.GetStatusAsync());
        Assert.Equal(IloErrorKind.NoApi, e.Kind);
    }

    [Fact]
    public async Task PowerOn_failure_is_protocol_error()
    {
        var h = new StubHandler(r => r.Method == HttpMethod.Get
            ? StubHandler.Json(HttpStatusCode.OK, """{"PowerState":"Off"}""")
            : StubHandler.Json(HttpStatusCode.BadRequest));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var e = await Assert.ThrowsAsync<IloException>(() => c.PowerOnAsync());
        Assert.Equal(IloErrorKind.Protocol, e.Kind);
    }

    [Fact]
    public async Task Network_and_certificate_failures_are_typed()
    {
        var down = new StubHandler(_ => throw new HttpRequestException("connect failed"));
        using var c1 = new IloClient("10.0.0.5", "u", "p", false, down);
        Assert.Equal(IloErrorKind.Unreachable, (await Assert.ThrowsAsync<IloException>(() => c1.GetStatusAsync())).Kind);

        var cert = new StubHandler(_ => throw new HttpRequestException("ssl",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure")));
        using var c2 = new IloClient("10.0.0.5", "u", "p", false, cert);
        Assert.Equal(IloErrorKind.Certificate, (await Assert.ThrowsAsync<IloException>(() => c2.GetStatusAsync())).Kind);

        var tls = new StubHandler(_ => throw new HttpRequestException("ssl",
            new AuthenticationException("Authentication failed because the remote party sent a TLS alert")));
        using var c3 = new IloClient("10.0.0.5", "u", "p", false, tls);
        Assert.Equal(IloErrorKind.Tls, (await Assert.ThrowsAsync<IloException>(() => c3.GetStatusAsync())).Kind);
    }

    [Theory]
    [InlineData("https://ilo.lan/", "ilo.lan")]
    [InlineData(" HTTP://10.0.0.5:8443 ", "10.0.0.5:8443")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void Host_is_normalized(string input, string expected) =>
        Assert.Equal(expected, IloClient.NormalizeHost(input));
}
