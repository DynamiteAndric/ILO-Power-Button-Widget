using System.Net;
using System.Net.Http;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.ViewModels;
using Xunit;

namespace IloWidget.Tests;

public class WebSessionTests
{
    [Fact]
    public async Task Login_posts_credentials_and_returns_session_key()
    {
        var h = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"session_key":"abc123","user_name":"admin"}"""));
        using var c = new IloClient("10.0.0.5", "admin", "p\"w\\d", false, h);

        var key = await c.OpenWebSessionAsync();

        Assert.Equal("abc123", key);
        var r = Assert.Single(h.Requests);
        Assert.Equal(("POST", "/json/login_session"), (r.Method, r.Path));
        // a login that carries Basic credentials produces a session the console page cannot use
        Assert.Null(r.Auth);
        // the password is JSON-escaped, not concatenated
        using var doc = System.Text.Json.JsonDocument.Parse(r.Body!);
        Assert.Equal("login", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal("admin", doc.RootElement.GetProperty("user_login").GetString());
        Assert.Equal("p\"w\\d", doc.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Logout_posts_session_key()
    {
        var h = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK));
        using var c = new IloClient("10.0.0.5", "admin", "pw", false, h);

        await c.CloseWebSessionAsync("abc123");

        var r = Assert.Single(h.Requests);
        Assert.Null(r.Auth);
        Assert.Equal(("POST", "/json/login_session", """{"method":"logout","session_key":"abc123"}"""), (r.Method, r.Path, r.Body));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", IloErrorKind.Auth)]
    [InlineData(HttpStatusCode.Forbidden, "{}", IloErrorKind.Auth)]
    [InlineData(HttpStatusCode.InternalServerError, "{}", IloErrorKind.Protocol)]
    [InlineData(HttpStatusCode.OK, """{"message":"no key here"}""", IloErrorKind.Protocol)]
    public async Task Login_failures_are_typed(HttpStatusCode code, string json, IloErrorKind kind)
    {
        var h = new StubHandler(_ => StubHandler.Json(code, json));
        using var c = new IloClient("10.0.0.5", "admin", "pw", false, h);

        var e = await Assert.ThrowsAsync<IloException>(() => c.OpenWebSessionAsync());
        Assert.Equal(kind, e.Kind);
    }

    [Theory]
    [InlineData("10.0.0.5", "https://10.0.0.5/irc.html")]
    [InlineData("https://ilo.lan/", "https://ilo.lan/irc.html")]
    [InlineData("10.0.0.5:8443", "https://10.0.0.5:8443/irc.html")]
    public void Console_uri_points_at_the_html5_console(string host, string expected) =>
        Assert.Equal(expected, IloClient.ConsoleUri(host).AbsoluteUri);

    [Fact]
    public async Task Console_is_available_only_when_the_ilo_answers()
    {
        var settings = new WidgetSettings { Host = "10.0.0.5", User = "u", ProtectedPassword = "x" };
        var fake = new FakeClient { Status = new ServerStatus("Off", null, null, "OK", null, null, "/redfish/v1") };

        Assert.False(new MainViewModel(new WidgetSettings(), _ => fake).CanOpenConsole);

        var vm = new MainViewModel(settings, _ => fake);
        Assert.False(vm.CanOpenConsole); // not polled yet
        await vm.RefreshAsync();
        Assert.True(vm.CanOpenConsole);  // server off: the console is still useful for watching the boot

        fake.Error = new IloException(IloErrorKind.Unreachable, "down");
        await vm.RefreshAsync();
        Assert.True(vm.CanOpenConsole);  // the first failure is tolerated
        await vm.RefreshAsync();
        Assert.False(vm.CanOpenConsole);
    }
}
