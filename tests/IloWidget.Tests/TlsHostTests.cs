using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IloWidget.Services;
using Xunit;

namespace IloWidget.Tests;

/// <summary>Real TLS connections to a loopback server: the self-signed exception must match the iLO host exactly.</summary>
public class TlsHostTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx)); // re-import so SslStream can use the private key
    }

    /// <summary>Accepts connections until disposed; each gets one HTTP response (a failed handshake is ignored).</summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert = SelfSigned();
        private readonly CancellationTokenSource _cts = new();

        public TlsServer(IPAddress address)
        {
            _listener = new TcpListener(address, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        public int Port { get; }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => Serve(tcp));
            }
        }

        private async Task Serve(TcpClient tcp)
        {
            try
            {
                using (tcp)
                using (var ssl = new SslStream(tcp.GetStream(), false))
                {
                    await ssl.AuthenticateAsServerAsync(_cert);
                    var buffer = new byte[4096];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var n = await ssl.ReadAsync(buffer.AsMemory(read), _cts.Token);
                        if (n == 0)
                            break;
                        read += n;
                        if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n"))
                            break;
                    }
                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                    await ssl.WriteAsync(response, _cts.Token);
                    await ssl.FlushAsync(_cts.Token);
                }
            }
            catch
            {
                // a client that rejects the certificate aborts the handshake; that is expected
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cert.Dispose();
        }
    }

    private static async Task<HttpStatusCode> GetAsync(HttpMessageHandler handler, string url)
    {
        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync(url);
        return response.StatusCode;
    }

    [Fact]
    public async Task Ip_address_host_is_accepted_when_it_is_the_ilo_host()
    {
        using var server = new TlsServer(IPAddress.Loopback);
        var handler = IloClient.CreateHandler("127.0.0.1", acceptSelfSigned: true);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(handler, $"https://127.0.0.1:{server.Port}/"));
    }

    [Fact]
    public async Task Host_name_is_accepted_when_it_is_the_ilo_host()
    {
        using var server = new TlsServer(IPAddress.Loopback);
        var handler = IloClient.CreateHandler("localhost", acceptSelfSigned: true);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(handler, $"https://localhost:{server.Port}/"));
    }

    [Fact]
    public async Task Another_host_is_rejected()
    {
        using var server = new TlsServer(IPAddress.Loopback);
        var handler = IloClient.CreateHandler("localhost", acceptSelfSigned: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(handler, $"https://127.0.0.1:{server.Port}/"));
    }

    [Fact]
    public async Task Self_signed_certificate_is_rejected_unless_enabled()
    {
        using var server = new TlsServer(IPAddress.Loopback);
        var handler = IloClient.CreateHandler("127.0.0.1", acceptSelfSigned: false);
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(handler, $"https://127.0.0.1:{server.Port}/"));
    }

    [Fact]
    public async Task Client_connects_to_an_ipv4_host_through_the_normalized_base_address()
    {
        using var server = new TlsServer(IPAddress.Loopback);
        using var client = new IloClient($"https://127.0.0.1:{server.Port}/", "u", "p", acceptSelfSigned: true);
        var status = await client.GetStatusAsync(); // reaches the server over TLS; the "{}" body maps to an unknown power state
        Assert.Equal("Unknown", status.Power);
    }

    [Fact]
    public async Task Ipv6_loopback_host_in_brackets_is_accepted()
    {
        if (!Socket.OSSupportsIPv6)
            return; // no IPv6 on this machine: nothing to test
        TlsServer server;
        try
        {
            server = new TlsServer(IPAddress.IPv6Loopback);
        }
        catch (SocketException)
        {
            return; // IPv6 loopback not usable
        }
        using (server)
        {
            Assert.True(IloClient.IsIloHost("::1", "[::1]"));
            using var client = new IloClient($"[::1]:{server.Port}", "u", "p", acceptSelfSigned: true);
            var status = await client.GetStatusAsync();
            Assert.Equal("Unknown", status.Power);
        }
    }
}
