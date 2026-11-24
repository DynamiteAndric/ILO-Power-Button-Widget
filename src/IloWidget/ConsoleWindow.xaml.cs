using System.IO;
using System.Windows;
using System.Windows.Media;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.Theme;
using IloWidget.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace IloWidget;

/// <summary>
/// Hosts the iLO HTML5 remote console (/irc.html). The window logs in to the iLO web interface with the
/// stored credentials, passes the session cookie to the embedded browser and logs out again when closed.
/// </summary>
public partial class ConsoleWindow : Window
{
    private readonly WidgetSettings _settings;
    private readonly ConsoleSession _session;
    private readonly Uri _consoleUri;

    private bool _connecting;
    private bool _ended;
    private bool _handlersAttached;
    private bool _browserReady;

    /// <summary>The close-and-logout operation, started by the first close request.</summary>
    private Task? _closing;

    public ConsoleWindow(WidgetSettings settings, Func<WidgetSettings, IIloWebSession>? sessionFactory = null)
    {
        InitializeComponent();
        _settings = settings;
        var factory = sessionFactory ?? IloClient.FromSettings;
        _session = new ConsoleSession(() => factory(settings));
        try
        {
            _consoleUri = IloClient.ConsoleUri(settings.Host);
        }
        catch (IloException)
        {
            // placeholder: the login in ConnectAsync fails with the same error and shows it in the overlay
            _consoleUri = new Uri("https://localhost/irc.html");
        }
        TitleText.Text = "UNSC // REMOTE CONSOLE - " + IloClient.NormalizeHost(settings.Host).ToUpperInvariant();

        Loaded += async (_, _) => await ConnectAsync();
        Closing += OnClosing;
        SourceInitialized += (_, _) => ApplyCornerRegion();
        SizeChanged += (_, _) => ApplyCornerRegion();
        StateChanged += (_, _) => ApplyCornerRegion();
    }

    // ---------- chamfered top-left corner ----------

    private const double CornerCut = 12; // DIPs, matches the accent line in the title bar

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreatePolygonRgn(POINT[] points, int count, int fillMode);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    /// <summary>The window is opaque (WebView2), so the corner is cut with a window region, not transparency.</summary>
    private void ApplyCornerRegion()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        if (WindowState != WindowState.Normal)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        int c = (int)Math.Round(CornerCut * dpi.DpiScaleX);
        var pts = new[]
        {
            new POINT { X = 0, Y = c }, new POINT { X = c, Y = 0 }, new POINT { X = w, Y = 0 },
            new POINT { X = w, Y = h }, new POINT { X = 0, Y = h },
        };
        var region = CreatePolygonRgn(pts, pts.Length, 1);
        if (region == IntPtr.Zero)
            return;
        // the system owns the region after SetWindowRgn succeeds; otherwise it is ours to free
        if (SetWindowRgn(hwnd, region, true) == 0)
            DeleteObject(region);
    }

    // ---------- connect ----------

    private async Task ConnectAsync()
    {
        if (_connecting || _ended)
            return;
        _connecting = true;
        ShowOverlay(StateToBrushConverter.Cyan, "LINKING", "OPENING REMOTE CONSOLE SESSION", retry: false);
        try
        {
            await EnsureBrowserAsync();
            // an "always allow" for a certificate error is remembered by the browser process: forget it
            // so that turning off "accept self-signed" takes effect
            if (!_settings.AcceptSelfSigned)
                await Web.CoreWebView2!.ClearServerCertificateErrorActionsAsync();
            await EndSessionAsync(); // on reconnect: release the previous iLO session first

            var key = await _session.OpenAsync();
            if (key is null || _ended)
                return; // closed while logging in; the session was logged out

            var core = Web.CoreWebView2;
            var cookie = core.CookieManager.CreateCookie("sessionKey", key, IloClient.CookieDomain(_consoleUri), "/");
            cookie.IsSecure = true;
            core.CookieManager.AddOrUpdateCookie(cookie);

            Overlay.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
            core.Navigate(_consoleUri.AbsoluteUri);
        }
        catch (IloException e)
        {
            var r = HudMapper.Map(e);
            ShowOverlay(StateToBrushConverter.Red, r.Label, r.Detail, retry: true);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowOverlay(StateToBrushConverter.Red, "RUNTIME MISSING",
                "THE MICROSOFT EDGE WEBVIEW2 RUNTIME IS NOT INSTALLED. INSTALL IT FROM MICROSOFT AND TRY AGAIN.", retry: true);
        }
        catch (OperationCanceledException) when (_ended)
        {
            return; // the window closed while connecting: nothing to show
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException
            || (e is OperationCanceledException && !_ended))
        {
            ShowOverlay(StateToBrushConverter.Red, "CONSOLE ERROR", e.Message.ToUpperInvariant(), retry: true);
        }
        finally
        {
            _connecting = false;
        }
    }

    private async Task EnsureBrowserAsync()
    {
        if (_browserReady)
            return;

        if (Web.CoreWebView2 is null)
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IloWidget", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await Web.EnsureCoreWebView2Async(env);
        }

        var core = Web.CoreWebView2!;
        // the guards go on before anything else is awaited, so a failed setup step cannot leave the page unguarded
        AttachHandlers(core);

        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        // Restyle HPE's console controls to the Halo theme and hide its full-screen and close icons
        // (the window has its own). This only affects the page as shown here, not the iLO.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ConsoleTheme.InjectionScript);
        _browserReady = true;
    }

    private void AttachHandlers(CoreWebView2 core)
    {
        if (_handlersAttached)
            return;
        _handlersAttached = true;

        // Trust the iLO's self-signed certificate only if the user enabled that, and only for the iLO itself.
        core.ServerCertificateErrorDetected += (_, args) =>
        {
            args.Action = _settings.AcceptSelfSigned && IsIlo(args.RequestUri)
                ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                : CoreWebView2ServerCertificateErrorAction.Default;
        };

        // This window is a console, not a browser: stay on the iLO.
        core.NavigationStarting += (_, args) =>
        {
            if (!IsIlo(args.Uri))
                args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;

        // the console page's own close button calls window.close()
        core.WindowCloseRequested += (_, _) => Close();

        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess || _ended || args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                return;
            if (IsCertificateStatus(args.WebErrorStatus))
            {
                // with the self-signed option on, the certificate handler above allows the iLO: keep the old behavior
                if (!_settings.AcceptSelfSigned)
                {
                    var r = HudMapper.Map(new IloException(IloErrorKind.Certificate, "The iLO certificate was not trusted."));
                    ShowOverlay(StateToBrushConverter.Red, r.Label, r.Detail, retry: true);
                }
                return;
            }
            ShowOverlay(StateToBrushConverter.Red, "NO LINK",
                "CONSOLE PAGE DID NOT LOAD: " + args.WebErrorStatus.ToString().ToUpperInvariant(), retry: true);
        };
    }

    private bool IsIlo(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(IloClient.CookieDomain(u), IloClient.CookieDomain(_consoleUri), StringComparison.OrdinalIgnoreCase)
        && u.Port == _consoleUri.Port;

    private static bool IsCertificateStatus(CoreWebView2WebErrorStatus s) => s is
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or
        CoreWebView2WebErrorStatus.CertificateExpired or
        CoreWebView2WebErrorStatus.CertificateRevoked or
        CoreWebView2WebErrorStatus.CertificateIsInvalid or
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors;

    private void ShowOverlay(Brush color, string label, string detail, bool retry)
    {
        Web.Visibility = Visibility.Collapsed;
        OverlayLabel.Foreground = color;
        OverlayLabel.Text = label;
        OverlayDetail.Text = detail;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    // ---------- disconnect ----------

    /// <summary>Logs the iLO web session out (best effort, short timeout) and forgets the cookie.</summary>
    private async Task EndSessionAsync()
    {
        DeleteCookie();
        await _session.ReleaseAsync();
    }

    private void DeleteCookie()
    {
        if (Web.CoreWebView2 is { } core)
            core.CookieManager.DeleteCookiesWithDomainAndPath("sessionKey", IloClient.CookieDomain(_consoleUri), "/");
    }

    /// <summary>Closes the window after logging out. Safe to call more than once: every call awaits the same close.</summary>
    public Task CloseAndLogoutAsync() => _closing ??= CloseCoreAsync();

    /// <summary>True once the close has started: the window is hidden and logging out, and must not be reused.</summary>
    public bool IsClosing => _ended;

    private async Task CloseCoreAsync()
    {
        _ended = true;
        try
        {
            Hide();
            // a crashed WebView2 may throw here or in Dispose; the logout and the close must still happen
            try
            {
                DeleteCookie();
            }
            catch (Exception e)
            {
                IloLog.Error("Deleting the console cookie failed", e);
            }
            await _session.CloseAsync();
            try
            {
                Web.Dispose();
            }
            catch (Exception e)
            {
                IloLog.Error("Disposing the console browser failed", e);
            }
        }
        catch (Exception e)
        {
            IloLog.Error("Closing the console session failed", e);
        }

        // Close() must not run inside the Closing event that started this (WPF throws); this never faults,
        // so the cached task cannot block later close requests
        try
        {
            await System.Windows.Threading.Dispatcher.Yield();
            Close();
        }
        catch (Exception e)
        {
            IloLog.Error("Closing the console window failed", e);
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_ended)
            return; // the logout has started or is done: this is the final Close()
        e.Cancel = true; // finish the logout first, then close for real
        await CloseAndLogoutAsync();
    }

    // ---------- title bar ----------

    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
