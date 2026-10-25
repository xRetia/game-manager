using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GameManager;

public partial class MainWindow : Window
{
    public ObservableCollection<GameInfo> Games { get; } = new();

    public bool StartFullScreen { get; set; }

    private int _selectedIndex = -1;
    private bool _fullScreen;
    private WindowState _savedState = WindowState.Normal;
    private ResizeMode _savedResize = ResizeMode.CanResize;

    // Gamepad polling runs on a thread-pool timer: joyGetPosEx is a blocking WinMM
    // call and can stall for tens of milliseconds with slow drivers/wireless
    // adapters. Keeping it off the UI thread is what keeps animations and touch
    // scrolling smooth. 30 ms tick; auto-repeat ~300 ms hold, then ~120 ms/step.
    private Timer? _padPollTimer;
    private int _padPollBusy;
    private bool _padDirActive;
    private bool _padConfirmPrev;
    private int _padRepeatCount;
    private const int PadRepeatDelayTicks = 10;
    private const int PadRepeatRateTicks = 4;

    // render diagnostics for the About box
    private int _renderFrames;
    private DateTime _renderWindowStart = DateTime.UtcNow;
    private double _lastFps;
    private bool _fpsMeasured;

    public MainWindow(bool forceFront = false)
    {
        InitializeComponent();
        Scroller.SizeChanged += OnGameAreaSizeChanged;
        TilesControl.ItemsSource = Games;

        // window fades in on show
        Opacity = 0;
        Loaded += (_, _) => this.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        if (forceFront)
        {
            // Spawned by the service: no foreground rights, force the window to the front.
            Loaded += (_, _) => ForceToFront();
        }

        Loaded += (_, _) =>
        {
            if (StartFullScreen && !_fullScreen) ToggleFullScreen();
            LoadGames();
        };

        _padPollTimer = new Timer(_ => PadPollCallback(), null, 0, 30);

        // framerate sampling (only fires while something renders, i.e. it measures
        // the active framerate, not idle) for the About diagnostics line
        CompositionTarget.Rendering += OnCompositionTargetRendering;

        Closed += (_, _) =>
        {
            _padPollTimer?.Dispose();
            _padPollTimer = null;
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
        };

        // keep the selection cursor glued to its tile through relayouts
        // (window resize, column count changes, icon size changes)
        TilesHost.SizeChanged += (_, _) => MoveCursorTo(_selectedIndex, animate: false);

        // console-style input hint fades in shortly after the window shows up
        Loaded += (_, _) => InputHint.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 0.95, TimeSpan.FromMilliseconds(450))
            {
                BeginTime = TimeSpan.FromMilliseconds(700),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private bool _userInteracted;

    /// <summary>Forces this window above everything and keeps re-asserting until the user interacts.</summary>
    private void ForceToFront()
    {
        Activated += OnFirstActivated;
        PreviewMouseDown += OnFirstUserInput;
        PreviewKeyDown += OnFirstUserInput;
        PreviewMouseWheel += OnFirstUserInput;

        Topmost = true;
        TakeForeground();

        // Re-assert topmost over ~2s and keep trying to steal the keyboard focus
        // (Windows denies foreground rights to service-spawned processes).
        var attempts = 0;
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        poll.Tick += (_, _) =>
        {
            attempts++;
            if (_userInteracted || attempts > 14)
            {
                if (!_userInteracted) Topmost = true; // stay floating until interaction
                poll.Stop();
                poll = null!;
                return;
            }
            Topmost = true;
            if (attempts % 3 == 0) TakeForeground();
        };
        poll.Start();
    }

    /// <summary>Steals the Win32 foreground (and with it keyboard focus) via the
    /// AttachThreadInput technique - the standard workaround for foreground lock.</summary>
    private void TakeForeground()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var fg = GetForegroundWindow();
            if (fg == hwnd)
            {
                Focus();
                return;
            }

            uint thisThread = GetCurrentThreadId();
            if (fg != IntPtr.Zero)
            {
                uint fgThread = GetWindowThreadProcessId(fg, out _);
                if (fgThread != thisThread)
                {
                    AttachThreadInput(thisThread, fgThread, true);
                    SetForegroundWindow(hwnd);
                    AttachThreadInput(thisThread, fgThread, false);
                }
                else
                {
                    SetForegroundWindow(hwnd);
                }
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
            Focus();
            Keyboard.Focus(this);
        }
        catch
        {
            // Best effort - Topmost keeps the window visible regardless.
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void OnFirstActivated(object? sender, EventArgs e)
    {
        ReleaseTopMost();
    }

    private void OnFirstUserInput(object sender, RoutedEventArgs e)
    {
        ReleaseTopMost();
    }

    private void ReleaseTopMost()
    {
        _userInteracted = true;
        Topmost = false;
        Activated -= OnFirstActivated;
        PreviewMouseDown -= OnFirstUserInput;
        PreviewKeyDown -= OnFirstUserInput;
        PreviewMouseWheel -= OnFirstUserInput;
    }

    // ---------------- data ----------------

    private void LoadGames()
    {
        Games.Clear();
        _selectedIndex = -1;
        AcrylicImage.Source = null;
        EmptyHint.Visibility = Visibility.Collapsed;

        string[] files = Array.Empty<string>();
        try
        {
            if (Directory.Exists(Launcher.LinksFolder))
            {
                files = Directory.GetFiles(Launcher.LinksFolder, "*.lnk");
            }
        }
        catch
        {
            // Ignore and show an empty grid.
        }

        foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            Games.Add(new GameInfo(file, ShellIcons.GetIconSource(file, 256)));
        }

        CountText.Text = Games.Count == 0 ? Strings.CountNone
            : (Games.Count == 1 ? Strings.CountOne : Strings.CountMany(Games.Count));
        if (Games.Count == 0)
        {
            EmptyHint.Text = Strings.EmptyHint(Launcher.LinksFolder);
            EmptyHint.Visibility = Visibility.Visible;
        }

        RecalcMetrics();
    }

    /// <summary>Adaptive sizing: large screens keep 256px icons, small screens shrink.</summary>
    private void RecalcMetrics()
    {
        if (Scroller.ActualWidth <= 0) return;
        double inner = Scroller.ActualWidth - 96;
        double innerH = Scroller.ActualHeight - 60;

        int iconSize;
        int columns;
        if (inner >= 1600)
        {
            iconSize = 256;
            columns = Math.Clamp((int)(inner / 356), 4, 10);
        }
        else
        {
            columns = Math.Clamp((int)(inner / 220), 3, 8);
            iconSize = Math.Clamp((int)(inner / columns) - 100, 96, 256);
        }
        // height guard for very short windows
        iconSize = Math.Min(iconSize, Math.Max(96, (int)innerH - 116));

        if (FindUniformGrid() is { } grid) grid.Columns = columns;
        foreach (var g in Games) g.IconSize = iconSize;
    }

    private UniformGrid? FindUniformGrid()
    {
        return FindVisualChild<UniformGrid>(TilesControl);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            if (FindVisualChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void OnGameAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecalcMetrics();
    }

    // ---------------- selection / navigation ----------------

    private GameInfo? SelectedGame => (_selectedIndex >= 0 && _selectedIndex < Games.Count) ? Games[_selectedIndex] : null;

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameInfo game) return;
        if (SelectedGame == game)
        {
            game.Launch();
            return;
        }
        SelectGame(Games.IndexOf(game), scroll: true);
    }

    private void OnTileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // double-click launches immediately (Click already highlighted on the first press)
        if ((e.OriginalSource as FrameworkElement)?.DataContext is GameInfo game)
        {
            game.Launch();
        }
    }

    private void SelectGame(int index, bool scroll)
    {
        if (index < 0 || index >= Games.Count) return;
        if (index == _selectedIndex) return;

        if (SelectedGame is { } prev) prev.IsSelected = false;
        _selectedIndex = index;
        var game = Games[index];
        game.IsSelected = true;
        UpdateAcrylic(game);
        MoveCursorTo(index, animate: true);

        if (scroll) BringIntoView(game);
    }

    /// <summary>Scrolls only if the tile is partially out of view - and only by the minimum amount.</summary>
    private void BringIntoView(GameInfo game)
    {
        int index = Games.IndexOf(game);
        if (TilesControl.ItemContainerGenerator.ContainerFromIndex(index)
                is not FrameworkElement container) return;

        // Bounds relative to the scroll viewport (already includes the current offset).
        var rect = container.TransformToAncestor(Scroller)
                             .TransformBounds(new Rect(container.RenderSize));

        if (rect.Top < 0)
        {
            SmoothScrollTo(Scroller.VerticalOffset + rect.Top - 12);
        }
        else if (rect.Bottom > Scroller.ViewportHeight)
        {
            SmoothScrollTo(Scroller.VerticalOffset + (rect.Bottom - Scroller.ViewportHeight) + 12);
        }
        // else: fully visible -> do not scroll at all
    }

    // animated scrolling (ScrollViewer.VerticalOffset itself is read-only)

    private static readonly DependencyProperty ScrollAnimProperty =
        DependencyProperty.Register(nameof(ScrollAnim), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, OnScrollAnimChanged));

    private double ScrollAnim
    {
        get => (double)GetValue(ScrollAnimProperty);
        set => SetValue(ScrollAnimProperty, value);
    }

    private static void OnScrollAnimChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MainWindow)d).Scroller.ScrollToVerticalOffset((double)e.NewValue);

    private void SmoothScrollTo(double target)
    {
        target = Math.Clamp(target, 0, Scroller.ScrollableHeight);
        double from = Scroller.VerticalOffset;
        if (Math.Abs(target - from) < 1)
        {
            Scroller.ScrollToVerticalOffset(target);
            return;
        }
        BeginAnimation(ScrollAnimProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void Navigate(int dx, int dy)
    {
        if (Games.Count == 0) return;
        if (_selectedIndex < 0)
        {
            SelectGame(0, true);
            return;
        }
        int columns = FindUniformGrid()?.Columns ?? 1;
        int index = _selectedIndex + dx + dy * columns;
        SelectGame(Math.Clamp(index, 0, Games.Count - 1), true);
    }

    private void ConfirmSelection()
    {
        if (Games.Count == 0) return;
        if (_selectedIndex < 0)
        {
            SelectGame(0, true);
            return;
        }
        Games[_selectedIndex].Launch();
    }

    // ---------------- sliding selection cursor ----------------

    private const double CursorPad = 7; // the frame floats this far outside the tile edge
    private static readonly TimeSpan CursorSlideTime = TimeSpan.FromMilliseconds(170);

    /// <summary>Places the selection frame over the tile at <paramref name="index"/>. With
    /// animate it glides there from its current position, console-style; quick repeats read
    /// the transform mid-animation, so they chain into one continuous glide. Snaps otherwise.</summary>
    private void MoveCursorTo(int index, bool animate)
    {
        Border? card = null;
        if (index >= 0 && index < Games.Count &&
            TilesControl.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container &&
            System.Windows.Media.VisualTreeHelper.GetChildrenCount(container) > 0 &&
            System.Windows.Media.VisualTreeHelper.GetChild(container, 0) is Button button &&
            System.Windows.Media.VisualTreeHelper.GetChildrenCount(button) > 0 &&
            System.Windows.Media.VisualTreeHelper.GetChild(button, 0) is Border border)
        {
            card = border;
        }

        if (card is null)
        {
            CursorFrame.Visibility = Visibility.Collapsed;
            return;
        }

        // Card bounds in the cursor's own coordinate space (the scroll content root),
        // so the frame scrolls along with the tiles instead of staying in the viewport.
        var rect = card.TransformToAncestor(TilesHost)
                       .TransformBounds(new Rect(card.RenderSize));

        double x = rect.Left - CursorPad;
        double y = rect.Top - CursorPad;
        CursorFrame.Width = rect.Width + CursorPad * 2;
        CursorFrame.Height = rect.Height + CursorPad * 2;

        bool wasHidden = CursorFrame.Visibility != Visibility.Visible;
        if (animate && !wasHidden)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            CursorTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(CursorTransform.X, x, CursorSlideTime) { EasingFunction = ease });
            CursorTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(CursorTransform.Y, y, CursorSlideTime) { EasingFunction = ease });
        }
        else
        {
            // snap (initial placement / relayout): stop any running animation first
            CursorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            CursorTransform.BeginAnimation(TranslateTransform.YProperty, null);
            CursorTransform.X = x;
            CursorTransform.Y = y;
        }

        if (wasHidden)
        {
            CursorFrame.Visibility = Visibility.Visible;
            CursorFrame.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
    }

    // ---------------- acrylic backdrop ----------------

    private void UpdateAcrylic(GameInfo game)
    {
        var src = game.Icon as ImageSource;
        if (ReferenceEquals(AcrylicImage.Source, src)) return;

        // cross-fade to the new game's backdrop
        AcrylicImage.Source = src;
        AcrylicImage.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        // the dark veil Border in XAML keeps the tiles readable on top
    }

    // ---------------- gamepad ----------------

    /// <summary>Thread-pool callback: polls the pads off the UI thread and marshals the
    /// result over. Re-entry guarded - a slow joyGetPosEx never piles up calls.</summary>
    private void PadPollCallback()
    {
        if (Interlocked.CompareExchange(ref _padPollBusy, 1, 0) != 0) return;
        try
        {
            if (!GamePad.Poll(out int dx, out int dy, out bool confirm)) return;
            Dispatcher.BeginInvoke(new Action(() => HandlePadInput(dx, dy, confirm)));
        }
        finally
        {
            Interlocked.Exchange(ref _padPollBusy, 0);
        }
    }

    /// <summary>Gamepad state machine; runs on the UI thread.</summary>
    private void HandlePadInput(int dx, int dy, bool confirm)
    {
        if (dx != 0 || dy != 0)
        {
            if (!_padDirActive)
            {
                Navigate(dx, dy);
                _padRepeatCount = 0;
            }
            else if (_padRepeatCount < PadRepeatDelayTicks)
            {
                // hold the direction for a moment before auto-repeat starts
                if (++_padRepeatCount == PadRepeatDelayTicks) Navigate(dx, dy);
            }
            else if (++_padRepeatCount >= PadRepeatDelayTicks + PadRepeatRateTicks)
            {
                Navigate(dx, dy);
                _padRepeatCount = PadRepeatDelayTicks;
            }
            _padDirActive = true;
        }
        else
        {
            _padDirActive = false;
            _padRepeatCount = 0;
        }

        if (confirm && !_padConfirmPrev)
        {
            ConfirmSelection();
        }
        _padConfirmPrev = confirm;
    }

    // ---------------- chrome ----------------

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadGames();

    /// <summary>Swallows boundary feedback so touch over-scroll never shakes the window.</summary>
    private void OnManipulationBoundary(object sender, ManipulationBoundaryFeedbackEventArgs e)
    {
        e.Handled = true;
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _savedState = WindowState;
            _savedResize = ResizeMode;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal; // re-enter maximize so WM_GETMINMAXINFO applies
            WindowState = WindowState.Maximized;
            _fullScreen = true;
        }
        else
        {
            WindowState = _savedState;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = _savedResize;
            _fullScreen = false;
        }
    }

    // ---------------- borderless fullscreen covering the taskbar (WM_GETMINMAXINFO) ----------------

    private void OnSourceInit(object sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProcHook);
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO && _fullScreen)
        {
            var bounds = GetMonitorBounds(hwnd);
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMaxPosition.X = bounds.Left;
            mmi.ptMaxPosition.Y = bounds.Top;
            mmi.ptMaxSize.X = bounds.Width;
            mmi.ptMaxSize.Y = bounds.Height;
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static (int Left, int Top, int Width, int Height) GetMonitorBounds(IntPtr hwnd)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        return (info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    private void OnAdvanced(object sender, RoutedEventArgs e)
    {
        // Colors/rounded chrome come from the implicit ContextMenu styles in App.xaml.
        var menu = new ContextMenu();

        void Item(string header, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (action != null)
            {
                item.Click += (_, _) => action();
            }
            menu.Items.Add(item);
        }

        Item(Launcher.ProtocolStatusText, false, null!);
        Item(Strings.MenuRegisterAll, !Launcher.IsRegisteredAllUsers(),
            () => Launcher.RunElevated("--register-all"));
        Item(Strings.MenuUnregisterAll, true,
            () => Launcher.RunElevated("--unregister-all"));
        menu.Items.Add(new Separator());
        Item(IsServiceInstalled() ? Strings.MenuServiceInstalled : Strings.MenuServiceNotInstalled, false, null!);
        Item(Strings.MenuInstallService, !IsServiceInstalled(),
            () => Launcher.RunElevated("--install-service"));
        Item(Strings.MenuRemoveService, IsServiceInstalled(),
            () => Launcher.RunElevated("--uninstall-service"));
        menu.Items.Add(new Separator());
        Item(Launcher.RemoteGpuStatusText, false, null!);
        Item(Strings.MenuEnableRemoteGpu, !Launcher.IsRemoteGpuEnabled(),
            () => Launcher.RunElevated("--enable-remote-gpu"));
        Item(Strings.MenuDisableRemoteGpu, Launcher.IsRemoteGpuEnabled(),
            () => Launcher.RunElevated("--disable-remote-gpu"));

        // Standard WPF popup positioning: below the button.
        menu.PlacementTarget = AdvancedButton;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 8;
        menu.IsOpen = true;
    }

    private static bool IsServiceInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + ServiceHost.ServiceName);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            Strings.AboutBody(Launcher.LinksFolder) + "\n\n" + RenderDiagnostics(),
            Strings.AboutTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------- render diagnostics ----------------

    /// <summary>Self-check shown in About: whether WPF is likely running on hardware,
    /// and the measured active framerate. Best-effort inference - WPF exposes no
    /// direct "am I software rendering right now" query.</summary>
    private string RenderDiagnostics()
    {
        string mode;
        try
        {
            object? hwOff = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Avalon.Graphics",
                "DisableHWAcceleration", null);
            if (hwOff is int v && v != 0)
            {
                mode = Strings.DiagHwDisabled;
            }
            else if (GetSystemMetrics(0x1000) != 0) // SM_REMOTESESSION
            {
                // Multi-seat seats are usually RDP-type sessions - the classic cause
                // of "everything feels sluggish with no obvious reason". When the
                // hardware-graphics policy is already on, the session still reports
                // as remote but should be rendering on the physical GPU; a reboot-
                // style hint otherwise (policy only affects newly created sessions).
                mode = Launcher.IsRemoteGpuEnabled()
                    ? Strings.DiagRemoteSessionGpuOn
                    : Strings.DiagRemoteSession + "  " + Strings.DiagRemoteSessionHint;
            }
            else if (RenderCapability.Tier >> 16 == 0)
            {
                mode = Strings.DiagTier0;
            }
            else
            {
                mode = Strings.DiagHwOn;
            }
        }
        catch
        {
            mode = Strings.DiagHwOn;
        }

        string fpsPart = _fpsMeasured ? "  |  " + Strings.DiagFps(_lastFps) : "";
        return Strings.DiagRender(mode + fpsPart);
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        _renderFrames++;
        var now = DateTime.UtcNow;
        double seconds = (now - _renderWindowStart).TotalSeconds;
        if (seconds >= 1.5)
        {
            _lastFps = _renderFrames / seconds;
            _fpsMeasured = true;
            _renderFrames = 0;
            _renderWindowStart = now;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { LoadGames(); e.Handled = true; }
        else if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullScreen) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Left) { Navigate(-1, 0); e.Handled = true; }
        else if (e.Key == Key.Right) { Navigate(1, 0); e.Handled = true; }
        else if (e.Key == Key.Up) { Navigate(0, -1); e.Handled = true; }
        else if (e.Key == Key.Down) { Navigate(0, 1); e.Handled = true; }
        else if (e.Key == Key.Enter) { ConfirmSelection(); e.Handled = true; }
    }
}

/// <summary>Tiny ICommand helper for the protocol menu.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    public RelayCommand(Action<object?> execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}