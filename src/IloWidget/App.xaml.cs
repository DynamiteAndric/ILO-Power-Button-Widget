using System.Windows;
using IloWidget.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace IloWidget;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // optional: --settings <path> keeps the configuration somewhere other than %APPDATA%\IloWidget
        string? settingsPath = null;
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--settings")
                settingsPath = e.Args[i + 1];
        }

        // optional: --console opens the remote console right after the first successful status check
        var openConsole = e.Args.Contains("--console");

        var store = new SettingsStore(settingsPath);
        _singleInstance = new Mutex(true, "IloWidget.SingleInstance." + Hashing.Stable(store.Path.ToLowerInvariant()), out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        IloLog.Directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(store.Path) ?? ".", "logs");
        IloLog.Info("Widget started");

        // an unexpected exception must not kill the widget or leave polling dead
        DispatcherUnhandledException += (_, args) =>
        {
            IloLog.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            IloLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _window = new MainWindow(store, openConsole);
        MainWindow = _window;
        _window.Closed += (_, _) => Shutdown();
        _window.Show();

        CreateTrayIcon();
    }

    private static void OpenLogFolder()
    {
        if (IloLog.Directory is not { } dir)
            return;
        System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show widget", null, (_, _) => _window?.ShowAndActivate());
        menu.Items.Add("Remote console", null, (_, _) => _window?.OpenConsole());
        menu.Items.Add("Settings", null, (_, _) =>
        {
            _window?.ShowAndActivate();
            _window?.ShowSettings();
        });
        menu.Items.Add("Display", null, (_, _) =>
        {
            _window?.ShowAndActivate();
            _window?.ShowDisplay();
        });
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) =>
        {
            if (_window is not null)
                await _window.ExitAsync();
            else
                Shutdown();
        });

        _tray = new Forms.NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "iLO Widget",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                _window?.ShowAndActivate();
        };
    }

    /// <summary>Cyan power glyph drawn at runtime, so no icon asset has to ship.</summary>
    private static Drawing.Icon DrawIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(0x4F, 0xD8, 0xFF), 3.5f)
            {
                StartCap = Drawing.Drawing2D.LineCap.Round,
                EndCap = Drawing.Drawing2D.LineCap.Round,
            };
            g.DrawArc(pen, 5, 7, 22, 22, -90 + 35, 360 - 70);
            g.DrawLine(pen, 16, 3, 16, 15);
        }
        var handle = bmp.GetHicon();
        try
        {
            using var tmp = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)tmp.Clone(); // the clone owns a copy, so the original handle can go
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
