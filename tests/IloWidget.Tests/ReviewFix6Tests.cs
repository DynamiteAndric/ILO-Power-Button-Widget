using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using IloWidget.Models;
using IloWidget.Services;
using Xunit;

namespace IloWidget.Tests;

public class ReviewFix6Tests
{
    private static readonly EndPoint Anywhere = new DnsEndPoint("ilo.invalid", 443);

    /// <summary>A connect that never completes by itself, only when its token is cancelled.</summary>
    private static ValueTask Hang(Socket _, EndPoint __, CancellationToken ct) => new(Task.Delay(Timeout.Infinite, ct));

    // ---- 6: connect timeout and cancellation without a network ----

    [Fact]
    public async Task Connect_timeout_is_a_timed_out_socket_exception()
    {
        var e = await Assert.ThrowsAsync<SocketException>(
            async () => await IloClient.ConnectAsync(Anywhere, TimeSpan.FromMilliseconds(50), CancellationToken.None, Hang));

        Assert.Equal(SocketError.TimedOut, e.SocketErrorCode);
    }

    [Fact]
    public async Task Caller_cancellation_during_connect_stays_a_cancellation()
    {
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await IloClient.ConnectAsync(Anywhere, TimeSpan.FromSeconds(30), cts.Token, Hang));
    }

    [Fact]
    public void A_connection_error_caused_by_a_timeout_is_not_sent()
    {
        var inner = new SocketException((int)SocketError.TimedOut);
        var e = new HttpRequestException(HttpRequestError.ConnectionError, "connect failed", inner);

        Assert.Equal(IloErrorKind.NotSent, IloClient.Translate(e).Kind);
    }

    // ---- 3: closing waits for a logout that ReleaseAsync already started ----

    private sealed class SlowLogoutSession : IIloWebSession
    {
        public readonly TaskCompletionSource LogoutGate = new();
        public bool LogoutDone;

        public Task<string> OpenWebSessionAsync(CancellationToken ct = default) => Task.FromResult("key");

        public async Task CloseWebSessionAsync(string sessionKey, CancellationToken ct = default)
        {
            await LogoutGate.Task;
            LogoutDone = true;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Close_awaits_a_logout_started_by_release()
    {
        var fake = new SlowLogoutSession();
        var session = new ConsoleSession(() => fake);
        await session.OpenAsync();

        var release = session.ReleaseAsync(); // logout in flight
        var close = session.CloseAsync();
        await Task.Delay(50);
        Assert.False(close.IsCompleted);

        fake.LogoutGate.SetResult();
        await close;
        await release;
        Assert.True(fake.LogoutDone);
    }

    // ---- 5: pixel to DIP conversion of a monitor work area ----

    [Fact]
    public void Work_area_is_converted_from_pixels_to_dips()
    {
        var r = MainWindow.PixelsToDips(1920, 0, 3000, 1500, 1.5, 2.0);

        Assert.Equal(new Rect(1280, 0, 2000, 750), r);
    }
}

/// <summary>Runs in the same collection as the other WPF tests: they share one Application and are timing sensitive.</summary>
[Collection("Wpf")]
public class ReviewFix6WpfTests
{
    // ---- 1: closing a console with no session must not throw inside the Closing event ----

    [Fact]
    public void Console_without_a_session_closes_for_real()
    {
        Exception? failure = null;
        var closed = false;
        var thread = new Thread(() =>
        {
            try
            {
                // the window's XAML needs the app's theme resources
                var app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/IloWidget;component/Theme/Halo.xaml"),
                });
                var window = new ConsoleWindow(new WidgetSettings { Host = "10.0.0.5", User = "u", ProtectedPassword = "x" },
                    _ => new FakeWebSession());
                window.Closed += (_, _) =>
                {
                    closed = true;
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                timeout.Tick += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                timeout.Start();
                Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        window.Close();
                    }
                    catch (Exception e)
                    {
                        failure = e;
                        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    }
                });
                Dispatcher.Run();
                Assert.True(window.CloseAndLogoutAsync().IsCompletedSuccessfully);
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(closed);
    }
}
