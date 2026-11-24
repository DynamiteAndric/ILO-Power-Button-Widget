using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IloWidget.Models;
using IloWidget.Services;
using IloWidget.Theme;
using IloWidget.ViewModels;

namespace IloWidget;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer = new();
    private ConsoleWindow? _console;

    /// <summary>Consoles replaced by a newer one while they were still logging out; ExitAsync waits for them too.</summary>
    private readonly HashSet<ConsoleWindow> _closingConsoles = new();
    private bool _exiting;
    private bool _closeAllowed;

    /// <summary>--console was requested and the first successful poll has not happened yet.</summary>
    private bool _openConsolePending;

    /// <summary>What the hold in progress was started for; checked again when the hold completes.</summary>
    private MainViewModel.PowerIntent _holdIntent;

    /// <summary>Set while the key that completed a hold is still down, so it cannot press a confirm button.</summary>
    private bool _confirmKeyGuard;

    /// <summary>The link settings are being replaced; a console must not open from the old ones meanwhile.</summary>
    private bool _linkChanging;
    private bool _saving;

    /// <summary>Top edge the user chose by dragging; null while the widget sits in its default corner.</summary>
    private double? _anchorTop;

    public MainWindow(SettingsStore store, bool openConsoleOnStart = false)
    {
        InitializeComponent();
        _openConsolePending = openConsoleOnStart;
        _store = store;
        _vm = new MainViewModel(store.Load());
        DataContext = _vm;

        Topmost = _vm.Settings.AlwaysOnTop;
        PlaceWindow();
        SizeChanged += (_, _) => AdjustTop();
        _vm.PropertyChanged += (_, e) =>
        {
            // the confirmation is only valid for a server that is still up
            if (e.PropertyName == nameof(MainViewModel.State) && _vm.State != HudState.Online)
                HideConfirm();
        };

        HostBox.TextChanged += (_, _) => UpdatePasswordLabel();
        UserBox.TextChanged += (_, _) => UpdatePasswordLabel();
        Power.HoldStarted += (_, _) => _holdIntent = _vm.CurrentIntent;
        PreviewKeyDown += ConfirmKeyGuard_PreviewKeyDown;
        PreviewKeyUp += ConfirmKeyGuard_PreviewKeyUp;
        Deactivated += (_, _) => _confirmKeyGuard = false; // the key-up may go to another window
        _timer.Tick += async (_, _) => await PollAsync();
        ContextMenuOpening += (_, _) =>
        {
            TopmostItem.IsChecked = Topmost;
            ConsoleItem.IsEnabled = _vm.CanOpenConsole;
            AutostartItem.IsChecked = Autostart.IsEnabled(_store.Path);
        };
        Loaded += async (_, _) =>
        {
            if (!_vm.Settings.IsConfigured)
                ShowSettings();
            await PollAsync();
        };
        Closing += (_, e) =>
        {
            // Alt+F4 and the like must also log the console out; ExitAsync lets the close through when done
            if (_closeAllowed)
                return;
            e.Cancel = true;
            _ = ExitAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _vm.Dispose();
        };
    }

    // ---------- polling ----------

    private async Task PollAsync()
    {
        _timer.Stop();
        try
        {
            await _vm.RefreshAsync();
            if (_openConsolePending && !_linkChanging && _vm.CanOpenConsole)
            {
                _openConsolePending = false;
                OpenConsole();
            }
        }
        finally
        {
            // whatever happened, polling must go on; the power handler restarts the timer while a command runs
            if (!_vm.IsCommandPending)
                RestartTimer();
        }
    }

    private void RestartTimer()
    {
        _timer.Interval = _vm.NextDelay;
        _timer.Start();
    }

    private async void Power_Activated(object? sender, EventArgs e)
    {
        var intent = _holdIntent;
        _holdIntent = MainViewModel.PowerIntent.None;
        if (!MainViewModel.IntentStillValid(intent, _vm.CanPowerOff, _vm.CanPowerOn))
        {
            // a poll changed the state during the hold: what the user held for no longer applies
            IloLog.Info($"Power hold ignored: started for {intent}, state is now {_vm.State}");
            return;
        }

        // a running server is only shut down after an explicit confirmation
        if (intent == MainViewModel.PowerIntent.Shutdown)
        {
            ShowConfirm();
            return;
        }

        _timer.Stop();
        try
        {
            await _vm.PowerOnAsync();
        }
        finally
        {
            RestartTimer();
        }
    }

    private async void ConfirmShutdown_Click(object sender, RoutedEventArgs e)
    {
        HideConfirm();
        _timer.Stop();
        try
        {
            await _vm.PowerOffAsync();
        }
        finally
        {
            RestartTimer();
        }
    }

    private static bool IsActivationKey(Key key) => key is Key.Space or Key.Enter;

    // Space/Enter that completed the hold is still down (and repeating) when the confirmation appears and
    // focuses CANCEL; swallow it until it is released so it cannot click a button. The mouse is not affected.
    private void ConfirmKeyGuard_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var (handled, guard) = DecideConfirmKey(_confirmKeyGuard, ConfirmPanel.IsVisible, e.Key, e.IsRepeat, isKeyUp: false);
        _confirmKeyGuard = guard;
        if (handled)
            e.Handled = true;
    }

    private void ConfirmKeyGuard_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var (handled, guard) = DecideConfirmKey(_confirmKeyGuard, ConfirmPanel.IsVisible, e.Key, e.IsRepeat, isKeyUp: true);
        _confirmKeyGuard = guard;
        if (handled)
            e.Handled = true;
    }

    /// <summary>
    /// Whether the confirmation swallows a key event, and the guard afterwards. The guard only lives while the
    /// panel is visible: a key-up with the panel hidden clears it and passes through, so it can still cancel a
    /// hold on the power button.
    /// </summary>
    internal static (bool Handled, bool Guard) DecideConfirmKey(bool guard, bool panelVisible, Key key, bool isRepeat, bool isKeyUp)
    {
        if (!IsActivationKey(key))
            return (false, guard);
        if (isKeyUp)
            return (guard && panelVisible, false);
        return (panelVisible && (guard || isRepeat), guard);
    }

    private void CancelShutdown_Click(object sender, RoutedEventArgs e) => HideConfirm();

    private void ShowConfirm()
    {
        _confirmKeyGuard = Keyboard.IsKeyDown(Key.Space) || Keyboard.IsKeyDown(Key.Enter);
        PowerHintText.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
        CancelShutdownButton.Focus(); // Enter/Space on a stray key press cancels rather than shuts down
    }

    private void HideConfirm()
    {
        _confirmKeyGuard = false;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        PowerHintText.Visibility = Visibility.Visible;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await PollAsync();

    // ---------- window chrome ----------

    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea;
        var s = _vm.Settings;
        var onScreen = s.Left is { } l && s.Top is { } t && IsOnAnyScreen(l, t);
        if (onScreen)
        {
            Left = s.Left!.Value;
            Top = s.Top!.Value;
            _anchorTop = Top;
        }
        else
        {
            // the height is only known after layout; AdjustTop then puts the widget into the corner
            Left = area.Right - Width - 16;
            Top = area.Top;
        }
    }

    /// <summary>True when the top-left corner of the widget at (left, top) lies on one of the monitors.</summary>
    private bool IsOnAnyScreen(double left, double top)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var areas = System.Windows.Forms.Screen.AllScreens
            .Select(m => new Rect(m.WorkingArea.X, m.WorkingArea.Y, m.WorkingArea.Width, m.WorkingArea.Height));
        return IsPositionVisible(left * dpi.DpiScaleX, top * dpi.DpiScaleY, GripSize * dpi.DpiScaleX, areas);
    }

    /// <summary>Size (DIPs) of the widget's top-left area that must be on a monitor for a saved position to be used.</summary>
    private const double GripSize = 60;

    /// <summary>
    /// True when the square of the given size at (left, top) overlaps at least half its width and height with
    /// one of the monitor work areas (all in the same unit, device pixels).
    /// </summary>
    public static bool IsPositionVisible(double left, double top, double size, IEnumerable<Rect> workAreas)
    {
        var grip = new Rect(left, top, size, size);
        foreach (var area in workAreas)
        {
            var overlap = Rect.Intersect(grip, area);
            if (!overlap.IsEmpty && overlap.Width >= size / 2 && overlap.Height >= size / 2)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The widget's height follows its content. A widget in the default corner stays glued to the bottom
    /// of the work area; a dragged one keeps its top edge and only moves up if it would leave the screen.
    /// </summary>
    private void AdjustTop()
    {
        var area = CurrentWorkArea();
        if (_anchorTop is not { } anchor)
        {
            Top = area.Bottom - ActualHeight - 16;
            return;
        }

        var screenBottom = anchor < area.Bottom
            ? area.Bottom
            : SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        Top = Math.Max(SystemParameters.VirtualScreenTop, Math.Min(anchor, screenBottom - ActualHeight));
    }

    /// <summary>Work area (DIPs) of the monitor the widget is on; the primary one before the window has a handle.</summary>
    private Rect CurrentWorkArea()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return SystemParameters.WorkArea;
        var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        return PixelsToDips(wa.X, wa.Y, wa.Width, wa.Height, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    internal static Rect PixelsToDips(int x, int y, int width, int height, double scaleX, double scaleY) =>
        new(x / scaleX, y / scaleY, width / scaleX, height / scaleY);

    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        var h = e.NewSize.Height;
        PanelShape.Data = Geometry.Parse(FormattableString.Invariant(
            $"M 20,0 L {w},0 L {w},{h - 20} L {w - 20},{h} L 0,{h} L 0,20 Z"));
        CornerAccent.Data = Geometry.Parse(FormattableString.Invariant(
            $"M {w},{h - 34} L {w},{h - 20} L {w - 20},{h} L {w - 60},{h}"));
    }

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;
        var (left, top) = (Left, Top);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (left != Left || top != Top)
        {
            _anchorTop = Top;
            _vm.Settings.Left = Left;
            _vm.Settings.Top = Top;
            TrySave(_vm.Settings);
        }
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostItem.IsChecked;
        _vm.Settings.AlwaysOnTop = Topmost;
        TrySave(_vm.Settings);
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Autostart.Set(_store.Path, AutostartItem.IsChecked);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            AutostartItem.IsChecked = Autostart.IsEnabled(_store.Path); // show what is really registered
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private async void Exit_Click(object sender, RoutedEventArgs e) => await ExitAsync();

    private static readonly TimeSpan ExitCloseTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Logs the remote console session out (if one is open) before the app shuts down.</summary>
    public async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;
        try
        {
            var closes = _closingConsoles.Select(c => c.CloseAndLogoutAsync()).ToList();
            if (_console is not null)
                closes.Add(_console.CloseAndLogoutAsync());
            if (closes.Count > 0)
            {
                // bounded: a hung logout must not keep the app from exiting
                await Task.WhenAny(Task.WhenAll(closes), Task.Delay(ExitCloseTimeout));
            }
        }
        catch (Exception e)
        {
            // a crashed WebView2 must not keep the app from exiting
            IloLog.Error("Closing the console on exit failed", e);
        }
        finally
        {
            _closeAllowed = true;
            Application.Current.Shutdown();
        }
    }

    // ---------- remote console ----------

    private void OpenConsole_Click(object sender, RoutedEventArgs e) => OpenConsole();

    public void OpenConsole()
    {
        if (_linkChanging)
            return; // the old settings are being replaced
        if (_console is { IsClosing: false })
        {
            if (_console.WindowState == WindowState.Minimized)
                _console.WindowState = WindowState.Normal;
            _console.Activate();
            return;
        }
        if (!_vm.CanOpenConsole)
            return;

        // a console that is still logging out (hidden) is replaced by a new one
        if (_console is { } old)
        {
            _closingConsoles.Add(old);
            old.Closed += (_, _) => _closingConsoles.Remove(old);
        }
        var console = new ConsoleWindow(_vm.Settings.Clone());
        _console = console;
        console.Closed += (_, _) =>
        {
            if (ReferenceEquals(_console, console))
                _console = null;
        };
        console.Show();
    }

    public void ShowAndActivate()
    {
        Show();
        Activate();
    }

    // ---------- settings panel ----------

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void UpdatePasswordLabel()
    {
        var s = _vm.Settings;
        PasswordLabel.Text = string.IsNullOrEmpty(s.ProtectedPassword) ? "PASSWORD"
            : !SettingsStore.CanUnprotect(s.ProtectedPassword) ? "PASSWORD (UNREADABLE - ENTER AGAIN)"
            : WidgetSettings.KeepsStoredPassword(s, HostBox.Text, UserBox.Text) ? "PASSWORD (STORED - LEAVE EMPTY TO KEEP)"
            : "PASSWORD (REQUIRED FOR A NEW HOST OR USER)";
    }

    public void ShowSettings()
    {
        var s = _vm.Settings;
        HostBox.Text = s.Host;
        UserBox.Text = s.User;
        PasswordInput.Clear();
        UpdatePasswordLabel();
        PollBox.Text = s.PollSeconds.ToString();
        SelfSignedBox.IsChecked = s.AcceptSelfSigned;
        TopmostBox.IsChecked = s.AlwaysOnTop;
        AutostartBox.IsChecked = Autostart.IsEnabled(_store.Path);
        SetResult("", ok: true);

        ShowView(SettingsView);
        HostBox.Focus();
    }

    private void HideSettings()
    {
        PasswordInput.Clear();
        ShowView(StatusView);
    }

    /// <summary>Shows one of the three views of the panel (status, settings, display).</summary>
    private void ShowView(FrameworkElement view)
    {
        HideConfirm();
        foreach (var v in new FrameworkElement[] { StatusView, SettingsView, DisplayView })
            v.Visibility = ReferenceEquals(v, view) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- display panel ----------

    private const string DisplayNote = "TEMPERATURE AND FANS ADD ONE ILO REQUEST PER POLL";

    private void OpenDisplay_Click(object sender, RoutedEventArgs e) => ShowDisplay();

    public void ShowDisplay()
    {
        var s = _vm.Settings;
        ShowModelBox.IsChecked = s.ShowModel;
        ShowHostNameBox.IsChecked = s.ShowHostName;
        ShowAddressBox.IsChecked = s.ShowAddress;
        ShowHealthBox.IsChecked = s.ShowHealth;
        ShowPostStateBox.IsChecked = s.ShowPostState;
        ShowCpuBox.IsChecked = s.ShowCpu;
        ShowMemoryBox.IsChecked = s.ShowMemory;
        ShowTemperaturesBox.IsChecked = s.ShowTemperatures;
        ShowFansBox.IsChecked = s.ShowFans;
        ShowBiosBox.IsChecked = s.ShowBios;
        ShowLastContactBox.IsChecked = s.ShowLastContact;
        DisplayResult.Text = DisplayNote;
        DisplayResult.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);

        PasswordInput.Clear();
        ShowView(DisplayView);
    }

    private async void SaveDisplay_Click(object sender, RoutedEventArgs e)
    {
        var s = _vm.Settings.Clone();
        s.ShowModel = ShowModelBox.IsChecked == true;
        s.ShowHostName = ShowHostNameBox.IsChecked == true;
        s.ShowAddress = ShowAddressBox.IsChecked == true;
        s.ShowHealth = ShowHealthBox.IsChecked == true;
        s.ShowPostState = ShowPostStateBox.IsChecked == true;
        s.ShowCpu = ShowCpuBox.IsChecked == true;
        s.ShowMemory = ShowMemoryBox.IsChecked == true;
        s.ShowTemperatures = ShowTemperaturesBox.IsChecked == true;
        s.ShowFans = ShowFansBox.IsChecked == true;
        s.ShowBios = ShowBiosBox.IsChecked == true;
        s.ShowLastContact = ShowLastContactBox.IsChecked == true;

        if (!TrySave(s))
        {
            DisplayResult.Text = "COULD NOT WRITE SETTINGS FILE";
            DisplayResult.Foreground = StateToBrushConverter.Red;
            return;
        }

        // only the rows changed: the link and an open console stay as they are
        _vm.ApplyDisplaySettings(s);
        ShowView(StatusView);
        await PollAsync(); // fills the sensor rows right away
    }

    private void CancelDisplay_Click(object sender, RoutedEventArgs e) => ShowView(StatusView);

    /// <summary>Settings as currently typed in the form. An empty password box keeps the stored password.</summary>
    private WidgetSettings ReadForm()
    {
        var s = _vm.Settings.Clone();
        s.Host = HostBox.Text.Trim();
        s.User = UserBox.Text.Trim();
        if (PasswordInput.Password.Length > 0)
            s.ProtectedPassword = SettingsStore.Protect(PasswordInput.Password);
        else if (!WidgetSettings.KeepsStoredPassword(_vm.Settings, s.Host, s.User) || !SettingsStore.CanUnprotect(s.ProtectedPassword))
            s.ProtectedPassword = ""; // never send the saved credentials to a different host or user; an unreadable one is as good as none
        s.PollSeconds = int.TryParse(PollBox.Text, out var poll) ? Math.Clamp(poll, WidgetSettings.MinPollSeconds, WidgetSettings.MaxPollSeconds) : WidgetSettings.DefaultPollSeconds;
        s.AcceptSelfSigned = SelfSignedBox.IsChecked == true;
        s.AlwaysOnTop = TopmostBox.IsChecked == true;
        return s;
    }

    private async void TestLink_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        SetResult("CONTACTING ILO...", ok: true);
        try
        {
            var (ok, message) = await _vm.TestAsync(ReadForm());
            SetResult(message, ok);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_saving)
            return;
        _saving = true;
        try
        {
            if (_vm.IsCommandPending)
            {
                SetResult("POWER COMMAND IN PROGRESS - TRY AGAIN IN A MOMENT", ok: false);
                return;
            }
            var s = ReadForm();
            if (!s.IsConfigured)
            {
                SetResult("HOST, USER AND PASSWORD ARE REQUIRED", ok: false);
                return;
            }
            if (!TrySave(s, force: true))
            {
                SetResult("COULD NOT WRITE SETTINGS FILE", ok: false);
                return;
            }

            try
            {
                Autostart.Set(_store.Path, AutostartBox.IsChecked == true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // autostart is a convenience; the link settings are already saved
            }

            Topmost = s.AlwaysOnTop;
            if (WidgetSettings.SameLink(_vm.Settings, s))
            {
                // only the poll interval or the window changed: keep the link, the readout and the timers
                _vm.ApplyDisplaySettings(s);
                HideSettings();
                await PollAsync();
                return;
            }

            _linkChanging = true;
            try
            {
                // the console belongs to the old address and credentials
                if (_console is not null)
                {
                    try
                    {
                        await _console.CloseAndLogoutAsync();
                    }
                    catch (Exception ex)
                    {
                        // the settings are saved: a failing console close must not stop them from being applied
                        IloLog.Error("Closing the console after a settings change failed", ex);
                    }
                }

                var applied = _vm.ApplySettingsAsync(s); // completes once an in-flight call to the old iLO is over
                HideSettings();
                await applied;
            }
            finally
            {
                _linkChanging = false;
            }
            await PollAsync();
        }
        finally
        {
            _saving = false;
        }
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs e) => HideSettings();

    private void SetResult(string text, bool ok)
    {
        SettingsResult.Text = text;
        SettingsResult.Foreground = ok ? StateToBrushConverter.Cyan : StateToBrushConverter.Red;
    }

    private bool TrySave(WidgetSettings s, bool force = false)
    {
        try
        {
            _store.Save(s, force);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
