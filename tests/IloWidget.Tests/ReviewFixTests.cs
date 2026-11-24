using System.IO;
using System.Net.Http;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.ViewModels;
using Xunit;

namespace IloWidget.Tests;

internal sealed class FakeWebSession : IIloWebSession
{
    public TaskCompletionSource<string> Login = new();
    public List<string> LoggedOut = new();
    public bool Disposed;

    public Task<string> OpenWebSessionAsync(CancellationToken ct = default) => Login.Task;

    public Task CloseWebSessionAsync(string sessionKey, CancellationToken ct = default)
    {
        LoggedOut.Add(sessionKey);
        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}

public class ReviewFixTests
{
    private static WidgetSettings Configured() =>
        new() { Host = "10.0.0.5", User = "u", ProtectedPassword = "x", PollSeconds = 10 };

    private static ServerStatus Off() => new("Off", "M", "SN", "OK", "PowerOff", "J06", "/redfish/v1");

    // ---- 1: invalid host ----

    [Theory]
    [InlineData("bad host")]
    [InlineData("")]
    [InlineData("https://")]
    public void Invalid_host_is_an_ilo_config_error(string host)
    {
        Assert.Equal(IloErrorKind.Config, Assert.Throws<IloException>(() => new IloClient(host, "u", "p", false)).Kind);
        Assert.Equal(IloErrorKind.Config, Assert.Throws<IloException>(() => IloClient.ConsoleUri(host)).Kind);
    }

    [Fact]
    public async Task Invalid_host_shows_bad_address_instead_of_crashing()
    {
        var settings = Configured();
        settings.Host = "bad host";
        using var vm = new MainViewModel(settings); // default factory builds a real IloClient

        await vm.RefreshAsync();
        Assert.Equal(HudState.Error, vm.State);
        Assert.Equal("BAD ADDRESS", vm.Label);

        var (ok, message) = await vm.TestAsync(settings);
        Assert.False(ok);
        Assert.StartsWith("BAD ADDRESS", message);
    }

    // ---- 2: settings change while a call is in flight ----

    [Fact]
    public async Task Settings_change_drops_the_old_answer_and_lets_the_next_poll_run()
    {
        var gate = new TaskCompletionSource();
        var oldClient = new FakeClient { Status = Off(), StatusGate = gate };
        var newClient = new FakeClient { Status = Off() };
        var clients = new Queue<FakeClient>(new[] { oldClient, newClient });
        using var vm = new MainViewModel(Configured(), _ => clients.Dequeue());

        var inFlight = vm.RefreshAsync();
        var applied = vm.ApplySettingsAsync(Configured());
        Assert.False(applied.IsCompleted); // waits for the old call
        Assert.False(oldClient.Disposed);

        oldClient.Error = new IloException(IloErrorKind.Unreachable, "cancelled by dispose");
        gate.SetResult();
        await inFlight;
        await applied;

        Assert.True(oldClient.Disposed);
        Assert.NotEqual(HudState.Error, vm.State); // the old failure was not applied

        await vm.RefreshAsync(); // the poll after saving runs on the new client
        Assert.Equal(HudState.Offline, vm.State);
    }

    // ---- 3: closing the console during login ----

    [Fact]
    public async Task Console_closed_during_login_logs_the_new_session_out()
    {
        var web = new FakeWebSession();
        var session = new ConsoleSession(() => web);

        var open = session.OpenAsync();
        await session.CloseAsync();
        Assert.Empty(web.LoggedOut);
        Assert.False(web.Disposed); // the login in flight is not disposed under its feet

        web.Login.SetResult("key1");
        Assert.Null(await open);
        Assert.Equal(new[] { "key1" }, web.LoggedOut);
        Assert.True(web.Disposed);
    }

    [Fact]
    public async Task Console_session_is_released_on_close_and_reconnect()
    {
        var first = new FakeWebSession();
        var second = new FakeWebSession();
        var sessions = new Queue<FakeWebSession>(new[] { first, second });
        var session = new ConsoleSession(() => sessions.Dequeue());

        first.Login.SetResult("k1");
        Assert.Equal("k1", await session.OpenAsync());
        second.Login.SetResult("k2");
        Assert.Equal("k2", await session.OpenAsync()); // reconnect releases the first one
        Assert.Equal(new[] { "k1" }, first.LoggedOut);

        await session.CloseAsync();
        Assert.Equal(new[] { "k2" }, second.LoggedOut);
        Assert.True(second.Disposed);
    }

    // ---- 5: autostart naming ----

    [Fact]
    public void Autostart_default_settings_keep_the_plain_name_and_command()
    {
        Assert.Equal("IloWidget", Autostart.ValueName(SettingsStore.DefaultPath));
        Assert.Equal("\"C:\\w\\IloWidget.exe\"", Autostart.Command("C:\\w\\IloWidget.exe", SettingsStore.DefaultPath));
    }

    [Fact]
    public void Autostart_custom_settings_get_their_own_value_and_pass_the_path()
    {
        var a = Autostart.ValueName("C:\\a\\settings.json");
        var b = Autostart.ValueName("C:\\b\\settings.json");
        Assert.StartsWith("IloWidget.", a);
        Assert.NotEqual(a, b);
        Assert.Equal(a, Autostart.ValueName("c:\\A\\Settings.json"));
        Assert.Equal("\"C:\\w\\IloWidget.exe\" --settings \"C:\\a\\settings.json\"",
            Autostart.Command("C:\\w\\IloWidget.exe", "C:\\a\\settings.json"));
    }

    [Fact]
    public void Autostart_command_uses_a_full_settings_path()
    {
        var cmd = Autostart.Command("C:/w/IloWidget.exe", "rel/settings.json");
        Assert.Contains("--settings \"" + System.IO.Path.GetFullPath("rel/settings.json") + "\"", cmd);
    }

    // ---- 7: dispose during a poll ----

    [Fact]
    public async Task Dispose_during_a_poll_does_not_throw_when_the_poll_finishes()
    {
        var gate = new TaskCompletionSource();
        var client = new FakeClient { Status = Off(), StatusGate = gate };
        var vm = new MainViewModel(Configured(), _ => client);

        var poll = vm.RefreshAsync();
        vm.Dispose();
        gate.SetResult();

        await poll;
    }

    [Fact]
    public async Task Dispose_during_a_poll_disposes_the_client_once_and_skips_thermal()
    {
        var gate = new TaskCompletionSource();
        var client = new FakeClient { Status = Off(), StatusGate = gate };
        var settings = Configured();
        settings.ShowTemperatures = true;
        var vm = new MainViewModel(settings, _ => client);

        var poll = vm.RefreshAsync();
        vm.Dispose();
        Assert.False(client.Disposed); // still in use: disposed when the call finishes
        gate.SetResult();
        await poll;

        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(0, client.ThermalCalls);
        await vm.RefreshAsync(); // after dispose nothing touches iLO
        Assert.Equal(1, client.DisposeCount);
    }

    // ---- 8: self-signed certificate only for the iLO host ----

    [Fact]
    public void Self_signed_certificate_is_accepted_only_for_the_ilo_host()
    {
        Assert.True(IloClient.IsIloHost("ILO.lan", "ilo.lan"));
        Assert.False(IloClient.IsIloHost("evil.example", "ilo.lan"));
        Assert.False(IloClient.IsIloHost(null, "ilo.lan"));

        var handler = Assert.IsType<SocketsHttpHandler>(IloClient.CreateHandler("ilo.lan", acceptSelfSigned: true));
        Assert.False(handler.AllowAutoRedirect);
        var callback = handler.SslOptions.RemoteCertificateValidationCallback!;
        Assert.False(callback(new object(), null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));

        var strict = (SocketsHttpHandler)IloClient.CreateHandler("ilo.lan", acceptSelfSigned: false);
        Assert.Null(strict.SslOptions.RemoteCertificateValidationCallback);
    }

    // ---- 9: log redaction ----

    [Theory]
    [InlineData("""{"session_key":"secret1","user_name":"a"}""", "secret1")]
    [InlineData("""{"method":"logout","sessionKey" : "secret2"}""", "secret2")]
    [InlineData("""{"password":"secret3"}""", "secret3")]
    [InlineData("GET /x Cookie: a=b; sessionKey=secret4; c=d", "secret4")]
    public void Redact_masks_secrets(string input, string secret)
    {
        var output = IloLog.Redact(input);
        Assert.DoesNotContain(secret, output);
        Assert.Contains("***", output);
    }

    [Fact]
    public void Redact_keeps_other_values()
    {
        Assert.Contains("\"user_name\":\"a\"", IloLog.Redact("""{"session_key":"s","user_name":"a"}"""));
    }

    // ---- 10: atomic settings save ----

    [Fact]
    public void Save_writes_valid_json_and_leaves_no_temp_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ilo-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            store.Save(new WidgetSettings { Host = "10.0.0.5", User = "u" });
            store.Save(new WidgetSettings { Host = "10.0.0.6", User = "u" }); // overwrite

            Assert.Equal("10.0.0.6", store.Load().Host);
            Assert.Equal(new[] { store.Path }, Directory.GetFiles(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    // ---- round 3 ----

    private static MainViewModel NewVm(FakeClient fake, Func<DateTime> now) => new(Configured(), _ => fake, () => now());

    [Fact]
    public async Task Timeout_after_send_is_unknown_not_a_retry_invitation()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var fake = new FakeClient { Status = new ServerStatus("On", "M", "SN", "OK", "FinishedPost", "J06", "/redfish/v1"), PressError = new IloException(IloErrorKind.Unreachable, "timeout") };
        using var vm = NewVm(fake, () => now);
        await vm.RefreshAsync();
        Assert.True(vm.CanPowerOff);

        await vm.PowerOffAsync();

        Assert.Equal(HudState.Stopping, vm.State);
        Assert.Equal("SHUTDOWN SENT?", vm.Label);
        Assert.False(vm.CanUsePower);
        Assert.False(vm.IsCommandPending);
        Assert.Equal(1, fake.PowerButtonCalls);
        Assert.Equal(TimeSpan.FromSeconds(3), vm.NextDelay);
        await vm.RefreshAsync(); // still On: the shutdown window holds
        Assert.Equal(HudState.Stopping, vm.State);
        await vm.PowerOffAsync(); // not allowed while Stopping
        Assert.Equal(1, fake.PowerButtonCalls);
    }

    [Fact]
    public async Task Timeout_after_power_on_send_is_unknown_booting()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var fake = new FakeClient { Status = Off(), PowerOnError = new IloException(IloErrorKind.Unreachable, "timeout") };
        using var vm = NewVm(fake, () => now);
        await vm.RefreshAsync();

        await vm.PowerOnAsync();

        Assert.Equal(HudState.Booting, vm.State);
        Assert.Equal("POWER-ON SENT?", vm.Label);
        Assert.False(vm.CanUsePower);
        Assert.Equal(1, fake.PowerOnCalls);
    }

    [Fact]
    public async Task Status_read_failure_before_send_is_still_not_delivered()
    {
        var fake = new FakeClient { Status = Off() };
        using var vm = NewVm(fake, () => DateTime.Now);
        await vm.RefreshAsync();
        fake.Error = new IloException(IloErrorKind.Unreachable, "down");

        await vm.PowerOnAsync();

        Assert.Equal(HudState.Error, vm.State);
        Assert.Equal("COMMAND FAILED", vm.Label);
        Assert.Equal(0, fake.PowerOnCalls);
    }

    [Fact]
    public async Task Auth_failure_after_send_is_mapped_normally()
    {
        var fake = new FakeClient { Status = Off(), PowerOnError = new IloException(IloErrorKind.Forbidden, "no") };
        using var vm = NewVm(fake, () => DateTime.Now);
        await vm.RefreshAsync();
        await vm.PowerOnAsync();
        Assert.Equal("INSUFFICIENT PRIVILEGE", vm.Label);
    }

    [Fact]
    public async Task Command_is_pending_while_it_runs()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeClient { Status = Off() };
        using var vm = NewVm(fake, () => DateTime.Now);
        await vm.RefreshAsync();
        fake.StatusGate = gate;

        var run = vm.PowerOnAsync();
        Assert.True(vm.IsCommandPending);
        gate.SetResult();
        await run;
        Assert.False(vm.IsCommandPending);
    }

    [Fact]
    public void Tls_host_is_shared_between_constructor_and_fresh_connection()
    {
        var field = typeof(IloClient).GetField("_tlsHost", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        using var c = new IloClient("https://10.0.0.5/", "u", "p", false);
        Assert.Equal("10.0.0.5", field!.GetValue(c));
    }

    [Fact]
    public void Settings_path_is_absolute()
    {
        Assert.True(Path.IsPathRooted(new SettingsStore("rel.json").Path));
    }

    [Theory]
    [InlineData("10.0.0.5", "admin", "https://10.0.0.5/", "admin", true)]
    [InlineData("ILO.local", "admin", " ilo.LOCAL ", "admin", true)]
    [InlineData("10.0.0.5", "admin", "10.0.0.6", "admin", false)]
    [InlineData("10.0.0.5", "admin", "10.0.0.5", "root", false)]
    [InlineData("10.0.0.5", "admin", "10.0.0.5", "Admin", false)]
    public void Stored_password_is_kept_only_for_the_same_host_and_user(string saved, string savedUser, string typed, string typedUser, bool keep)
    {
        var s = new WidgetSettings { Host = saved, User = savedUser };
        Assert.Equal(keep, WidgetSettings.KeepsStoredPassword(s, typed, typedUser));
    }

    [Fact]
    public async Task Discovery_does_not_cache_or_skip_on_transient_server_error()
    {
        var fail = true;
        var h = new StubHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/redfish") && fail
            ? StubHandler.Json(System.Net.HttpStatusCode.ServiceUnavailable)
            : StubHandler.Json(System.Net.HttpStatusCode.OK, """{"PowerState":"On"}"""));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);

        var ex = await Assert.ThrowsAsync<IloException>(() => c.GetStatusAsync());
        Assert.Equal(IloErrorKind.Protocol, ex.Kind);
        Assert.Single(h.Requests); // rest/v1 was never tried

        fail = false;
        var s = await c.GetStatusAsync();
        Assert.Equal("/redfish/v1", s.ApiBase);
    }

    [Fact]
    public async Task Discovery_falls_back_to_rest_on_404()
    {
        var h = new StubHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/redfish")
            ? StubHandler.Json(System.Net.HttpStatusCode.NotFound)
            : StubHandler.Json(System.Net.HttpStatusCode.OK, """{"Power":"On"}"""));
        using var c = new IloClient("10.0.0.5", "u", "p", false, h);
        Assert.Equal("/rest/v1", (await c.GetStatusAsync()).ApiBase);
    }
}
