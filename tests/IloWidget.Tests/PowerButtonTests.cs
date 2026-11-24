using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using IloWidget.Controls;
using Xunit;

namespace IloWidget.Tests;

/// <summary>Drives the real control on an STA thread (off-screen window) to check the hold-to-activate rule.</summary>
[Collection("Wpf")]
public class PowerButtonTests
{
    private static int Run(bool enabled, int holdMs, int waitAfterMs)
    {
        var activated = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var button = new PowerButton { Width = 150, Height = 150, IsEnabled = enabled };
                button.Activated += (_, _) => activated++;
                var window = new Window
                {
                    Content = button,
                    Width = 200,
                    Height = 200,
                    Left = -20000,
                    Top = -20000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.None,
                };
                window.Show();

                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Pump(holdMs);
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Pump(waitAfterMs);
                window.Close();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Exception("STA test body failed", failure);
        return activated;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Full_hold_activates_exactly_once() => Assert.Equal(1, Run(enabled: true, holdMs: 1600, waitAfterMs: 300));

    [Fact]
    public void Short_click_does_not_activate() => Assert.Equal(0, Run(enabled: true, holdMs: 250, waitAfterMs: 1500));

    [Fact]
    public void Disabled_button_ignores_hold() => Assert.Equal(0, Run(enabled: false, holdMs: 1600, waitAfterMs: 300));
}
