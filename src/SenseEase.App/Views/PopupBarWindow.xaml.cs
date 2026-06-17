using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SenseEase.App.Accessibility;
using SenseEase.App.Interop;
using SenseEase.App.Services;
using SenseEase.App.ViewModels;

namespace SenseEase.App.Views;

/// <summary>
/// UI-RESHAPE prep — the glass popup bar window. Frosted via Win11 acrylic
/// (<see cref="AcrylicGlass"/>, light-blue tint) and never steals focus
/// (<see cref="ClickThrough.EnableNoActivate"/>, so its buttons stay clickable). Draggable
/// by its bar background. Binds a <see cref="PopupBarViewModel"/>; the future
/// <c>ShellController</c> passes a live <see cref="IShellController"/> so the engine owns
/// the tier. Implements <see cref="IShellSurface"/> for the WGC-exclude + topmost contract.
/// </summary>
public partial class PopupBarWindow : Window, IShellSurface
{
    // Light-blue glass acrylic tint (ABGR 0xAABBGGRR), from Popup.BarBg #B3D2E0FF (70% opacity).
    private const uint BarAcrylicTint = 0xB3FFE0D2;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_FORCE_TOPMOST = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER

    public PopupBarWindow() : this(null, null) { }

    public PopupBarWindow(IShellController? controller, ChatbotViewModel? chat)
    {
        InitializeComponent();
        DataContext = new PopupBarViewModel(controller, chat);
        if (DataContext is INotifyPropertyChanged inpc) inpc.PropertyChanged += OnVmPropertyChanged;
        SourceInitialized += OnSourceInitialized;
        Deactivated += OnDeactivated;
    }

    // ── #2/#6: magnetic expand/collapse — the chat panel slides in from the bar and the
    //    settings flyout slides UP from the gear (decelerate-settle on open, quick ease-in on
    //    close), instead of an instant pop. Reduced-motion snaps. Driven by the VM's open flags. ──
    private const double PanelSlideOffset = 28.0;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not PopupBarViewModel vm) return;
        if (e.PropertyName == nameof(PopupBarViewModel.IsChatOpen))
        {
            // Activate ONLY when the chat opens, so the input can take keyboard focus — the bar
            // itself is shown non-activating (ShowActivated=False) to avoid stealing focus.
            if (vm.IsChatOpen) Activate();
            AnimatePanel(ChatPanel, ChatXform, isX: true, open: vm.IsChatOpen, isOpenNow: () => vm.IsChatOpen);
        }
        else if (e.PropertyName == nameof(PopupBarViewModel.IsSettingsOpen))
            AnimatePanel(SettingsFlyout, SettingsXform, isX: false, open: vm.IsSettingsOpen, isOpenNow: () => vm.IsSettingsOpen);
    }

    private void AnimatePanel(FrameworkElement panel, TranslateTransform xform, bool isX, bool open, Func<bool> isOpenNow)
    {
        var prop = isX ? TranslateTransform.XProperty : TranslateTransform.YProperty;

        if (AccessibilityHelper.ReducedMotion())
        {
            xform.BeginAnimation(prop, null);
            panel.BeginAnimation(OpacityProperty, null);
            xform.X = 0; xform.Y = 0;
            panel.Opacity = open ? 1.0 : 0.0;
            panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (open)
        {
            panel.Visibility = Visibility.Visible;
            // Magnetic settle: decelerate with a slight pull-into-place (BackEase ease-out).
            var slide = new DoubleAnimation(PanelSlideOffset, 0, new Duration(TimeSpan.FromMilliseconds(240)))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 } };
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(190)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            xform.BeginAnimation(prop, slide);
            panel.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            // Retract faster (≈60% of enter), ease-in toward the trigger.
            var slide = new DoubleAnimation(PanelSlideOffset, new Duration(TimeSpan.FromMilliseconds(150)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(140)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            // Collapse only once fully closed AND still closed (interruptible: a re-open cancels it).
            fade.Completed += (_, _) => { if (!isOpenNow()) panel.Visibility = Visibility.Collapsed; };
            xform.BeginAnimation(prop, slide);
            panel.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // UI-RESHAPE iter: the popup is now an ACTIVATABLE flyout (NoActivate removed) so the chat
        // input is typable. ShellController calls Activate() on show; clicking any other window or
        // the desktop fires Deactivated → the popup hides itself (close-on-click-away). Topmost +
        // ShowInTaskbar=false keep it unobtrusive; acrylic provides the glass.
        AcrylicGlass.EnableAcrylic(this, BarAcrylicTint);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // #8 (liquid glass): clip the window to a rounded slab so the acrylic NEVER shows a
        // square translucent peek behind the rounded content corners — the glass reads as one
        // clean rounded liquid-glass piece instead of a translucent box.
        RootGrid.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 18, 18);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Close-on-click-away: focus left the popup (the user clicked another window / the desktop),
        // so collapse the whole UI back to the discreet edge handle and reset the panels so the next
        // reveal opens clean. The edge sidebar stays; this only hides the expanded popup.
        if (DataContext is PopupBarViewModel vm) { vm.IsChatOpen = false; vm.IsSettingsOpen = false; }
        Hide();
    }

    private void OnChatInputVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // When the chat panel reveals, put the caret in the input so the user can type at once
        // (the window is active, so keyboard focus lands here).
        if (e.NewValue is true && sender is System.Windows.Controls.TextBox tb)
            Dispatcher.BeginInvoke(
                new Action(() => { tb.Focus(); System.Windows.Input.Keyboard.Focus(tb); }),
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnChatInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (DataContext is PopupBarViewModel { Chat: { } chat } && chat.SendCommand.CanExecute(null))
        {
            chat.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return; // let buttons/inputs win
        try { DragMove(); } catch { /* DragMove throws if not in a left-button-down context */ }
    }

    private static bool IsInteractive(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ButtonBase || d is TextBoxBase || d is System.Windows.Controls.Primitives.ToggleButton)
                return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null;
        }
        return false;
    }

    public void ReassertTopmost()
    {
        if (!IsLoaded) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_FORCE_TOPMOST);
    }

    public (int Left, int Top, int Width, int Height) GetMonitorRectInPhysicalPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int left = (int)Math.Floor(Left * dpi.DpiScaleX);
        int top = (int)Math.Floor(Top * dpi.DpiScaleY);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        return (left, top, width, height);
    }
}
