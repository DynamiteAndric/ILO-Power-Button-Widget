using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using IloWidget.Models;

namespace IloWidget.Services;

public interface IIloClient : IDisposable
{
    Task<ServerStatus> GetStatusAsync(CancellationToken ct = default);
    Task<ThermalStatus> GetThermalAsync(CancellationToken ct = default);
    Task PowerOnAsync(CancellationToken ct = default);

    /// <summary>Momentary press of the power button: a running OS shuts down gracefully.</summary>
    Task PressPowerButtonAsync(CancellationToken ct = default);
}

/// <summary>Web-UI session of the iLO, as used by the HTML5 remote console page.</summary>
public interface IIloWebSession : IDisposable
{
    /// <summary>Logs in to the iLO web interface and returns the session key.</summary>
    Task<string> OpenWebSessionAsync(CancellationToken ct = default);

    /// <summary>Logs the session out. iLO allows only a few concurrent sessions, so always call this.</summary>
    Task CloseWebSessionAsync(string sessionKey, CancellationToken ct = default);
}

/// <summary>
/// Talks to HPE iLO 4. Prefers Redfish (/redfish/v1, firmware 2.30+) and falls back to the
/// older /rest/v1. It can read the state, power the server on and press the power button for a graceful
/// shutdown; it deliberately cannot force the power off.
/// </summary>
public sealed class IloClient : IIloClient, IIloWebSession
{
    private static readonly string[] ApiBases = { "/redfish/v1", "/rest/v1" };

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly string _tlsHost;
    private readonly bool _acceptSelfSigned;

    /// <summary>True when a test supplied the handler; then every request goes through it.</summary>
    private readonly bool _handlerInjected;
    private readonly string _user;
    private readonly string _password;
    private readonly AuthenticationHeaderValue _basic;
    private string? _base;
    private static int _requestCounter;

    public IloClient(string host, string user, string password, bool acceptSelfSigned, HttpMessageHandler? handler = null)
    {
        _user = user;
        _password = password;
        _acceptSelfSigned = acceptSelfSigned;
        _handlerInjected = handler is not null;
        _baseAddress = BaseUri(host);
        _tlsHost = _baseAddress.IdnHost;
        _http = CreateClient(handler ?? CreateHandler(_tlsHost, acceptSelfSigned));
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        _basic = new AuthenticationHeaderValue("Basic", token);
    }

    private HttpClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = _baseAddress, Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public static string NormalizeHost(string? host)
    {
        host = (host ?? "").Trim();
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                host = host[scheme.Length..];
        }
        return host.TrimEnd('/');
    }

    /// <summary>https address of the iLO; a host that is not a valid address is a configuration error, not a crash.</summary>
    private static Uri BaseUri(string host) =>
        Uri.TryCreate("https://" + NormalizeHost(host), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri
            : throw new IloException(IloErrorKind.Config, $"The iLO host \"{host}\" is not a valid address.");

    /// <summary>True when the TLS target host of a connection is the iLO host.</summary>
    public static bool IsIloHost(string? targetHost, string iloHost) =>
        targetHost is not null && string.Equals(Unbracket(targetHost), Unbracket(iloHost), StringComparison.OrdinalIgnoreCase);

    /// <summary>An IPv6 literal is "[::1]" in a URI but "::1" as a TLS target host.</summary>
    private static string Unbracket(string host) => host.Trim('[', ']');

    /// <summary>Builds a client from saved settings (decrypts the stored password).</summary>
    public static IloClient FromSettings(WidgetSettings s)
    {
        _ = BaseUri(s.Host); // a bad address is reported before a bad password
        // an undecryptable password would otherwise be sent as an empty one
        if (!string.IsNullOrEmpty(s.ProtectedPassword) && !SettingsStore.CanUnprotect(s.ProtectedPassword))
            throw new IloException(IloErrorKind.PasswordUnreadable, "The stored password cannot be decrypted. Enter it again in the settings.");
        return new(s.Host, s.User, SettingsStore.Unprotect(s.ProtectedPassword), s.AcceptSelfSigned);
    }

    /// <summary>How long a connection attempt may take before it counts as "not sent".</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(8);

    public static HttpMessageHandler CreateHandler(string iloHost, bool acceptSelfSigned, TimeSpan? connectTimeout = null)
    {
        var timeout = connectTimeout ?? DefaultConnectTimeout;
        // The iLO is on the local network, so system proxies are deliberately ignored.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            // the handler's own ConnectTimeout surfaces as a TaskCanceledException that cannot be told from a
            // response timeout; the callback below reports an expired connect as a SocketException instead
            ConnectTimeout = Timeout.InfiniteTimeSpan,
            ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, timeout, ct),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // iLO 4 drops an idle keep-alive connection after a few seconds; never reuse one that old
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(2),
            // a redirect must not carry the credentials, or the certificate exception, to another host
            AllowAutoRedirect = false,
        };
        // the self-signed exception applies to the iLO host only
        if (acceptSelfSigned)
            handler.SslOptions.RemoteCertificateValidationCallback = (sender, _, _, _) =>
                sender is System.Net.Security.SslStream ssl && IsIloHost(ssl.TargetHostName, iloHost);
        return handler;
    }

    /// <summary>
    /// Opens the TCP connection. An expired connect timeout is thrown as SocketError.TimedOut (the handler
    /// reports it as HttpRequestError.ConnectionError); a cancellation by the caller stays a cancellation.
    /// </summary>
    internal static async ValueTask<Stream> ConnectAsync(
        System.Net.EndPoint endPoint, TimeSpan timeout, CancellationToken ct,
        Func<Socket, System.Net.EndPoint, CancellationToken, ValueTask>? connect = null)
    {
        connect ??= static (s, ep, token) => s.ConnectAsync(ep, token);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await connect(socket, endPoint, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SocketException((int)SocketError.TimedOut);
            }
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<ServerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var sys = await GetSystemAsync(ct).ConfigureAwait(false);
        return new ServerStatus(
            Power: Str(sys, "Power") ?? Str(sys, "PowerState") ?? "Unknown",
            Model: Str(sys, "Model"),
            Serial: Str(sys, "SerialNumber"),
            Health: Str(sys, "Status", "Health"),
            PostState: Str(sys, "Oem", "Hp", "PostState"),
            Bios: Str(sys, "BiosVersion"),
            ApiBase: _base!)
        {
            HostName = NullIfBlank(Str(sys, "HostName")),
            Cpu = DescribeCpu(sys),
            MemoryGb = (Num(sys, "MemorySummary", "TotalSystemMemoryGiB") ?? Num(sys, "Memory", "TotalSystemMemoryGB")) is { } gb and > 0
                ? (int)Math.Round(gb)
                : null,
        };
    }

    public async Task<ThermalStatus> GetThermalAsync(CancellationToken ct = default)
    {
        if (_base is null)
            await GetSystemAsync(ct).ConfigureAwait(false);

        var (code, json) = await SendAsync(HttpMethod.Get, $"{_base}/Chassis/1/Thermal/", null, ct).ConfigureAwait(false);
        if (code == 200)
            return ParseThermal(json);
        ThrowForAuth(code, powerAction: false);
        throw new IloException(IloErrorKind.Protocol, $"Reading the sensors failed: HTTP {code}");
    }

    /// <summary>
    /// Picks the inlet (ambient) temperature, the hottest CPU temperature and the fastest fan.
    /// Accepts both the Redfish and the legacy /rest/v1 property names; absent sensors are skipped.
    /// </summary>
    public static ThermalStatus ParseThermal(JsonElement root)
    {
        int? ambient = null, cpu = null, fan = null;

        foreach (var t in Items(root, "Temperatures"))
        {
            if (IsAbsent(t) || (Num(t, "ReadingCelsius") ?? Num(t, "CurrentReading")) is not { } reading || reading <= 0)
                continue;
            var value = (int)Math.Round(reading);
            var name = Str(t, "Name") ?? "";
            var context = Str(t, "PhysicalContext") ?? "";
            if (context.Equals("Intake", StringComparison.OrdinalIgnoreCase) || name.Contains("Ambient", StringComparison.OrdinalIgnoreCase))
                ambient ??= value;
            else if (context.Equals("CPU", StringComparison.OrdinalIgnoreCase) || name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                cpu = Math.Max(cpu ?? value, value);
        }

        foreach (var f in Items(root, "Fans"))
        {
            if (IsAbsent(f) || (Num(f, "CurrentReading") ?? Num(f, "Reading")) is not { } reading || reading < 0)
                continue;
            var value = (int)Math.Round(reading);
            fan = Math.Max(fan ?? value, value);
        }

        return new ThermalStatus(ambient, cpu, fan);
    }

    public Task PowerOnAsync(CancellationToken ct = default) =>
        PostPowerActionAsync("""{"ResetType":"On"}""", """{"Action":"Reset","ResetType":"On"}""", "Power-on", ct);

    public Task PressPowerButtonAsync(CancellationToken ct = default) =>
        PostPowerActionAsync(
            """{"ResetType":"PushPowerButton"}""",
            """{"Action":"PowerButton","PushType":"Press","Target":"/Oem/Hp"}""",
            "Shutdown request", ct);

    private async Task PostPowerActionAsync(string redfishBody, string legacyBody, string what, CancellationToken ct)
    {
        if (_base is null)
            await GetSystemAsync(ct).ConfigureAwait(false);

        var (path, body) = _base == "/redfish/v1"
            ? ("/redfish/v1/Systems/1/Actions/ComputerSystem.Reset/", redfishBody)
            : ("/rest/v1/Systems/1", legacyBody);

        // A power command goes over a new connection. On a kept-alive connection that iLO has just closed the
        // POST fails before it reaches iLO, and unlike a GET .NET does not retry it. Retrying here is not safe
        // either: a second press of the power button could undo the first.
        var (code, _) = await SendAsync(HttpMethod.Post, path, body, ct, freshConnection: true).ConfigureAwait(false);
        if (code is 200 or 201 or 202 or 204)
            return;
        ThrowForAuth(code, powerAction: true);
        throw new IloException(IloErrorKind.Protocol, $"{what} failed: HTTP {code}");
    }

    /// <summary>Address of the standalone HTML5 remote console page (iLO 4 firmware 2.70+).</summary>
    public static Uri ConsoleUri(string host) => new(BaseUri(host), "/irc.html");

    public async Task<string> OpenWebSessionAsync(CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { method = "login", user_login = _user, password = _password });
        var (code, json) = await SendAsync(HttpMethod.Post, "/json/login_session", body, ct, webSession: true).ConfigureAwait(false);
        if (code is 401 or 403)
            throw new IloException(IloErrorKind.Auth, $"Web login rejected ({code}): check the user name and password.");
        if (code != 200)
            throw new IloException(IloErrorKind.Protocol, $"Web login failed: HTTP {code}");
        var key = Str(json, "session_key");
        if (string.IsNullOrEmpty(key))
            throw new IloException(IloErrorKind.Protocol, "Web login returned no session key.");
        return key;
    }

    public async Task CloseWebSessionAsync(string sessionKey, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { method = "logout", session_key = sessionKey });
        await SendAsync(HttpMethod.Post, "/json/login_session", body, ct, webSession: true, cookie: "sessionKey=" + sessionKey).ConfigureAwait(false);
    }

    private async Task<JsonElement> GetSystemAsync(CancellationToken ct)
    {
        if (_base is not null)
        {
            var (code, json) = await SendAsync(HttpMethod.Get, $"{_base}/Systems/1/", null, ct).ConfigureAwait(false);
            if (code == 200)
                return RequireObject(json);
            ThrowForAuth(code, powerAction: false);
            throw new IloException(IloErrorKind.Protocol, $"Reading the power state failed: HTTP {code}");
        }

        foreach (var candidate in ApiBases)
        {
            var (code, json) = await SendAsync(HttpMethod.Get, $"{candidate}/Systems/1/", null, ct).ConfigureAwait(false);
            ThrowForAuth(code, powerAction: false);
            if (code == 200)
            {
                // a 200 that is not a JSON object is not this API: the base is cached only for a readable answer
                var sys = RequireObject(json);
                _base = candidate;
                return sys;
            }
            // only "not here" means try the next API (404/405, a redirect, 400, 501); a 500/503 may be transient and must not pick or hide an API
            var notHere = code is 400 or 404 or 405 or 501 || code is >= 300 and < 400;
            if (!notHere)
                throw new IloException(IloErrorKind.Protocol, $"Reading the power state failed: HTTP {code}");
        }
        throw new IloException(IloErrorKind.NoApi, "Neither /redfish/v1 nor /rest/v1 was found. Check the iLO firmware.");
    }

    /// <param name="powerAction">True for a power POST, where a 403 means the user lacks Virtual Power and Reset.</param>
    private static void ThrowForAuth(int code, bool powerAction)
    {
        if (code == 401)
            throw new IloException(IloErrorKind.Auth, "Login rejected (401): check the user name and password.");
        if (code == 403 && powerAction)
            throw new IloException(IloErrorKind.Forbidden, "Access denied (403): the user lacks the Virtual Power and Reset privilege.");
        if (code == 403)
            throw new IloException(IloErrorKind.ReadForbidden, "Access denied (403): the account is disabled or lacks the Login privilege.");
    }

    /// <summary>A 200 answer must be a JSON object; an empty or HTML body is not a status.</summary>
    private static JsonElement RequireObject(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object
            ? json
            : throw new IloException(IloErrorKind.BadResponse, "The iLO answered with an unreadable status.");

    private async Task<(int Code, JsonElement Json)> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct, bool webSession = false, string? cookie = null, bool freshConnection = false)
    {
        var http = freshConnection && !_handlerInjected ? CreateClient(CreateHandler(_tlsHost, _acceptSelfSigned)) : _http;
        try
        {
            return await SendCoreAsync(http, method, path, body, ct, webSession, cookie).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(http, _http))
                http.Dispose();
        }
    }

    private async Task<(int Code, JsonElement Json)> SendCoreAsync(HttpClient http, HttpMethod method, string path, string? body, CancellationToken ct, bool webSession, string? cookie)
    {
        using var req = new HttpRequestMessage(method, path);
        // Web-UI session calls must look like a browser: a login sent together with a Basic header yields a
        // session that iLO accepts only while that header keeps being sent, so the console page would reject it.
        if (!webSession)
            req.Headers.Authorization = _basic;
        if (cookie is not null)
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var id = Interlocked.Increment(ref _requestCounter);
        IloLog.Request(id, method.Method, path + (ReferenceEquals(http, _http) ? "" : " [new connection]"), body);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            IloLog.Response(id, (int)resp.StatusCode, clock.Elapsed, raw);
            JsonElement json = default;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    json = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // non-JSON error pages are fine; only the status code matters then
                }
            }
            return ((int)resp.StatusCode, json);
        }
        catch (HttpRequestException e)
        {
            IloLog.Failure(id, clock.Elapsed, e);
            throw Translate(e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            IloLog.Failure(id, clock.Elapsed, e);
            throw new IloException(IloErrorKind.Unreachable, $"The iLO did not answer in time ({_http.Timeout.TotalSeconds:0} s).", e);
        }
        catch (Exception e) when (e is not OperationCanceledException and not IloException)
        {
            // e.g. an invalid charset or a disposed handler: the HUD must show an error, not a stale state
            IloLog.Failure(id, clock.Elapsed, e);
            throw new IloException(IloErrorKind.BadResponse, "Unexpected iLO response: " + e.Message, e);
        }
    }

    /// <summary>Cookie domain for the console session: IPv6 literals without the brackets a URI host keeps.</summary>
    public static string CookieDomain(Uri uri) => uri.IdnHost;

    internal static IloException Translate(HttpRequestException e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is AuthenticationException auth)
            {
                return auth.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                    ? new IloException(IloErrorKind.Certificate, "The iLO certificate was not trusted.", e)
                    : new IloException(IloErrorKind.Tls, "TLS handshake failed: " + auth.Message, e);
            }
        }
        // verified with SocketsHttpHandler: these happen while connecting, before any request bytes are written;
        // a failure after the send is ResponseEnded or Unknown (a connect timeout is not told apart from a response timeout)
        if (e.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError)
            return new IloException(IloErrorKind.NotSent, e.InnerException?.Message ?? e.Message, e);
        return new IloException(IloErrorKind.Unreachable, e.InnerException?.Message ?? e.Message, e);
    }

    private static string? Str(JsonElement root, params string[] path) =>
        Find(root, path) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    private static double? Num(JsonElement root, params string[] path) =>
        Find(root, path) is { ValueKind: JsonValueKind.Number } e && e.TryGetDouble(out var d) ? d : null;

    private static JsonElement? Find(JsonElement root, string[] path)
    {
        var cur = root;
        foreach (var key in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(key, out cur))
                return null;
        }
        return cur;
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string array) =>
        Find(root, new[] { array }) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static bool IsAbsent(JsonElement sensor) =>
        string.Equals(Str(sensor, "Status", "State"), "Absent", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>"2x Intel Xeon E5-2620 v2" from the Redfish summary or the legacy Processors block.</summary>
    private static string? DescribeCpu(JsonElement sys)
    {
        var model = NullIfBlank(Str(sys, "ProcessorSummary", "Model") ?? Str(sys, "Processors", "ProcessorFamily"));
        if (model is null)
            return null;

        // "Intel(R) Xeon(R) CPU E3-1220L V2 @ 2.30GHz" is too long for the HUD row
        var at = model.IndexOf('@');
        if (at > 0)
            model = model[..at];
        foreach (var noise in new[] { "(R)", "(TM)", "(r)", "(tm)", " CPU", " Processor" })
            model = model.Replace(noise, "");
        model = string.Join(' ', model.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var count = Num(sys, "ProcessorSummary", "Count") ?? Num(sys, "Processors", "Count");
        return count is > 0 ? $"{(int)count}x {model}" : model;
    }

    public void Dispose() => _http.Dispose();
}
