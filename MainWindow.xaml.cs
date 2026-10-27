using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using System.Windows.Media.Effects;
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
    private bool _padBackPrev;
    private int _padRepeatCount;
    private const int PadRepeatDelayTicks = 10;
    private const int PadRepeatRateTicks = 4;

    // cached window handle for the foreground check in the polling callback
    private IntPtr _hwnd;

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
            _launchHideTimer?.Stop();
            _launchHideTimer = null;
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
        };

        // keep the selection cursor glued to its tile through relayouts
        // (window resize, column count changes, icon size changes)
        TilesHost.SizeChanged += (_, _) => MoveCursorTo(_selectedIndex, animate: false);
        // the chrome focus frame needs the same treatment on header relayouts
        // (fullscreen toggles, window resizes)
        SizeChanged += (_, _) => UpdateChromeFocus(animate: false);

        // console-style input hint fades in shortly after the window shows up
        Loaded += (_, _) => InputHint.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 0.95, TimeSpan.FromMilliseconds(450))
            {
                BeginTime = TimeSpan.FromMilliseconds(700),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        // mouse users get the keyboard-style hint glyphs
        PreviewMouseDown += (_, _) => MarkInput(pad: false);
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
            var game = new GameInfo(file, ShellIcons.GetIconSource(file, 256));
            game.PropertyChanged += OnGamePropertyChanged; // drives the name marquee
            Games.Add(game);
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
            LaunchGame(game);
            return;
        }
        SelectGame(Games.IndexOf(game), scroll: true);
    }

    private void OnTileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // double-click launches immediately (Click already highlighted on the first press)
        if ((e.OriginalSource as FrameworkElement)?.DataContext is GameInfo game)
        {
            LaunchGame(game);
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
        // chrome (top button bar) navigation: left/right cycles the buttons,
        // down returns to the tile grid
        if (_chromeIndex >= 0)
        {
            if (dy > 0)
            {
                ExitChrome();
            }
            else if (dx != 0)
            {
                _chromeIndex = ((_chromeIndex + dx) % 5 + 5) % 5;
                UpdateChromeFocus(animate: true);
            }
            return; // already at the top edge: up is a no-op
        }

        if (Games.Count == 0) return;
        if (_selectedIndex < 0)
        {
            SelectGame(0, true);
            return;
        }

        int columns = FindUniformGrid()?.Columns ?? 1;

        // moving up from the first row jumps to the top button bar,
        // the same way consoles hand the dpad over to header actions
        if (dy < 0 && _selectedIndex < columns)
        {
            EnterChrome();
            return;
        }

        int index = _selectedIndex + dx + dy * columns;
        SelectGame(Math.Clamp(index, 0, Games.Count - 1), true);
    }

    private void ConfirmSelection()
    {
        if (_chromeIndex >= 0)
        {
            TriggerChromeButton();
            return;
        }
        if (Games.Count == 0) return;
        if (_selectedIndex < 0)
        {
            SelectGame(0, true);
            return;
        }
        LaunchGame(Games[_selectedIndex]);
    }

    // ---------------- chrome (header buttons) navigation ----------------

    // -1 = the tile grid has the cursor; 0..4 = the header buttons
    // (refresh / fullscreen / advanced / about / power)
    private int _chromeIndex = -1;

    private FrameworkElement[] ChromeButtons =>
        new[] { RefreshButton, FullScreenButton, AdvancedButton, AboutButton, PowerButton };

    private void EnterChrome()
    {
        _chromeIndex = 0;
        CursorFrame.Visibility = Visibility.Collapsed; // tile cursor yields to the chrome focus
        ChromeFocus.Visibility = Visibility.Visible;
        ChromeFocus.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        UpdateChromeFocus(animate: false);
    }

    private void ExitChrome()
    {
        if (_chromeIndex < 0) return;
        _chromeIndex = -1;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => ChromeFocus.Visibility = Visibility.Collapsed;
        ChromeFocus.BeginAnimation(OpacityProperty, fade);

        // bring the tile cursor back where it was
        if (_selectedIndex >= 0 && _selectedIndex < Games.Count)
        {
            CursorFrame.Visibility = Visibility.Visible;
            MoveCursorTo(_selectedIndex, animate: false);
        }
    }

    /// <summary>Places the chrome focus frame over the button at _chromeIndex.
    /// Reads the transform mid-animation so quick cycles chain into a glide.</summary>
    private void UpdateChromeFocus(bool animate)
    {
        if (_chromeIndex < 0) return;
        var btn = ChromeButtons[_chromeIndex];
        var rect = btn.TransformToAncestor(HeaderRoot)
                      .TransformBounds(new Rect(btn.RenderSize));

        double x = rect.Left - 4;
        double y = rect.Top - 4;
        ChromeFocus.Width = rect.Width + 8;
        ChromeFocus.Height = rect.Height + 8;

        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ChromeTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(ChromeTransform.X, x, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            ChromeTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(ChromeTransform.Y, y, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        }
        else
        {
            ChromeTransform.BeginAnimation(TranslateTransform.XProperty, null);
            ChromeTransform.BeginAnimation(TranslateTransform.YProperty, null);
            ChromeTransform.X = x;
            ChromeTransform.Y = y;
        }
    }

    private void TriggerChromeButton()
    {
        switch (_chromeIndex)
        {
            case 0:
                OnRefresh(null!, null!);
                ExitChrome();
                break;
            case 1:
                OnFullScreen(null!, null!);
                ExitChrome();
                break;
            case 2:
                OnAdvanced(null!, null!); // the open menu takes over; chrome stays
                break;
            case 3:
                OnAbout(null!, null!);
                ExitChrome();
                break;
            case 4:
                OnPower(null!, null!); // opens the modal power panel
                if (IsLoaded) ExitChrome(); // disconnect already closed the window
                break;
        }
    }

    // ---------------- launch toast ----------------

    // Games take seconds to show a window after Process.Start; a small toast
    // with a spinner and the game name gives that feedback without taking the
    // whole screen over. The launcher stays fully usable - repeated launches
    // of the same game are already debounced inside GameInfo.Launch().
    private DispatcherTimer? _launchHideTimer;

    /// <summary>Single UI launch path: starts the game and shows the launch toast.</summary>
    private void LaunchGame(GameInfo game)
    {
        if (!game.Launch()) return; // debounced or failed - no toast either

        // console-style launch feedback: short pad rumble + a streaming light
        // band sweeping around the selection frame (whichever is visible)
        GamePad.Rumble();
        LaunchBurst(CursorFrame, "CursorStream", "CursorStreamSpin");
        LaunchBurst(ChromeFocus, "ChromeStream", "ChromeStreamSpin");

        LaunchText.Text = Strings.Launching(game.Name);
        ShowLaunchToast();
    }

    private void ShowLaunchToast()
    {
        _launchHideTimer?.Stop();
        LaunchToast.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        LaunchToast.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        LaunchToastShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        LaunchSpinner.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });

        _launchHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _launchHideTimer.Tick += (_, _) =>
        {
            _launchHideTimer?.Stop();
            HideLaunchToast();
        };
        _launchHideTimer.Start();
    }

    private void HideLaunchToast()
    {
        LaunchSpinner.BeginAnimation(RotateTransform.AngleProperty, null);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        fade.Completed += (_, _) => LaunchToast.Visibility = Visibility.Collapsed;
        LaunchToast.BeginAnimation(OpacityProperty, fade);
        LaunchToastShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    /// <summary>Launch burst on a focus frame: a light band sweeps around the
    /// frame for two laps while the glow breathes up and settles back. Runs on
    /// whichever frame is currently visible; re-launches restart it cleanly.</summary>
    private void LaunchBurst(FrameworkElement frame, string streamName, string spinName)
    {
        if (frame.Visibility != Visibility.Visible) return;
        if (frame.FindName(streamName) is not Border stream) return;
        if (frame.FindName(spinName) is not RotateTransform spin) return;

        // reset any previous burst, then light the band up (quick fade-in
        // softens the pop)
        stream.BeginAnimation(OpacityProperty, null);
        stream.Visibility = Visibility.Visible;
        stream.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));

        // two slow laps around the frame, then fade the band out. Linear easing:
        // the band flows at a constant pace (~1.6 s per lap) - an eased start
        // would snap it around several times in the first moments.
        var sweep = new DoubleAnimation(0, 720, TimeSpan.FromMilliseconds(3200));
        sweep.Completed += (_, _) =>
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fade.Completed += (_, _) =>
            {
                spin.BeginAnimation(RotateTransform.AngleProperty, null);
                stream.Visibility = Visibility.Collapsed;
            };
            stream.BeginAnimation(OpacityProperty, fade);
        };
        spin.BeginAnimation(RotateTransform.AngleProperty, sweep);

        // glow breathing: brighter + wider while the band sweeps its first lap,
        // settling back over the second (same 1.6 s half-period as the sweep)
        if (frame.Effect is DropShadowEffect glow)
        {
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            double restOpacity = glow.Opacity;
            double restBlur = glow.BlurRadius;
            var breathe = new CubicEase { EasingMode = EasingMode.EaseInOut };
            glow.BeginAnimation(DropShadowEffect.OpacityProperty,
                new DoubleAnimation(restOpacity, 1, TimeSpan.FromMilliseconds(1600))
                {
                    AutoReverse = true, EasingFunction = breathe
                });
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
                new DoubleAnimation(restBlur, restBlur * 1.5, TimeSpan.FromMilliseconds(1600))
                {
                    AutoReverse = true, EasingFunction = breathe
                });
        }
    }

    // ---------------- input-hint auto detection ----------------

    // The bottom-right hint bar mirrors the last used input device: gamepad input
    // swaps it to dpad/A glyphs, keyboard/mouse swaps it to arrow-key/Enter glyphs.
    private bool _hintPadMode;

    private void MarkInput(bool pad)
    {
        if (_hintPadMode == pad) return;
        _hintPadMode = pad;
        UpdateInputHint();
    }

    private void UpdateInputHint()
    {
        PadHints.Visibility = _hintPadMode ? Visibility.Visible : Visibility.Collapsed;
        KeyHints.Visibility = _hintPadMode ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- tile name marquee ----------------

    // Long names are ellipsized while unselected; the selected tile scrolls its
    // full name back and forth so nothing is unreachable. Widths are measured
    // at runtime, so the animation is driven from code, not from the template.

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameInfo.IsSelected)) return;
        if (sender is not GameInfo game) return;
        int index = Games.IndexOf(game);
        if (index < 0) return;
        if (TilesControl.ItemContainerGenerator.ContainerFromIndex(index)
                is not FrameworkElement container) return;

        var text = FindVisualChild<TextBlock>(container);
        if (text == null || text.RenderTransform is not TranslateTransform scroll) return;

        if (game.IsSelected) StartMarquee(text, scroll);
        else StopMarquee(text, scroll);
    }

    private void StartMarquee(TextBlock text, TranslateTransform scroll)
    {
        if (text.Parent is not FrameworkElement clip) return;
        double overflow = text.ActualWidth - clip.ActualWidth;
        if (overflow <= 4) return; // fits: nothing to scroll

        text.TextAlignment = TextAlignment.Left; // anchor left so the scroll reveals the tail
        var sweep = new DoubleAnimation(0, -overflow, TimeSpan.FromSeconds(Math.Max(1.2, overflow / 45.0)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        scroll.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private void StopMarquee(TextBlock text, TranslateTransform scroll)
    {
        scroll.BeginAnimation(TranslateTransform.XProperty, null);
        scroll.X = 0;
        text.TextAlignment = TextAlignment.Center;
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
            // Only react to the gamepad while THIS window is the foreground window
            // of its session. joyGetPosEx reads the device regardless of window
            // state, so without this check a backgrounded GameManager would keep
            // navigating - and launch games on A - while the user plays a
            // fullscreen game with the same pad. Keyboard/touch are unaffected:
            // they only ever reach a focused window anyway.
            // Exception: while OUR Advanced menu is open the foreground window is
            // the menu's popup HWND - same process, same intent - so input passes.
            if (_hwnd != IntPtr.Zero)
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != _hwnd && !IsOwnProcess(fg))
                {
                    return;
                }
            }
            if (!GamePad.Poll(out int dx, out int dy, out bool confirm, out bool back,
                    out double scrollY)) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                HandlePadInput(dx, dy, confirm, back);
                if (_aboutWindow is { } about) about.PadScrollY = scrollY;
            }));
        }
        finally
        {
            Interlocked.Exchange(ref _padPollBusy, 0);
        }
    }

    /// <summary>True while the Advanced menu or the About dialog is open and the
    /// given window belongs to this process (the menu popup / About window).
    /// MessageBoxes and other apps still block pad input.</summary>
    private bool IsOwnProcess(IntPtr hwnd)
    {
        if ((_openMenu == null && _aboutWindow == null && _powerDialog == null && _confirmDialog == null) || hwnd == IntPtr.Zero) return false;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Clears the pad state machine. Called on Deactivated so background
    /// presses never leak into the foreground as a repeat burst or a phantom
    /// confirm (hence _padConfirmPrev = true: the first foreground poll with a
    /// still-held A must not count as a fresh press).</summary>
    private void ResetPadState()
    {
        _padDirActive = false;
        _padRepeatCount = 0;
        _padConfirmPrev = true;
        _padBackPrev = true;
    }

    /// <summary>Gamepad state machine; runs on the UI thread. Directions run
    /// through one shared press/repeat gate, so a held or noisy stick moves one
    /// step at a time everywhere: tiles, header buttons and the Advanced menu
    /// alike (per-poll stepping made the menu race and jitter).</summary>
    private void HandlePadInput(int dx, int dy, bool confirm, bool back)
    {
        if (dx != 0 || dy != 0 || confirm || back) MarkInput(pad: true);

        // direction gate: one step on the initial press, auto-repeat only after
        // a deliberate hold. Every mode below consumes the gated step.
        bool moveNow = false;
        if (dx != 0 || dy != 0)
        {
            if (!_padDirActive)
            {
                moveNow = true;
                _padRepeatCount = 0;
            }
            else if (_padRepeatCount < PadRepeatDelayTicks)
            {
                // hold the direction for a moment before auto-repeat starts
                if (++_padRepeatCount == PadRepeatDelayTicks) moveNow = true;
            }
            else if (++_padRepeatCount >= PadRepeatDelayTicks + PadRepeatRateTicks)
            {
                moveNow = true;
                _padRepeatCount = PadRepeatDelayTicks;
            }
            _padDirActive = true;
        }
        else
        {
            _padDirActive = false;
            _padRepeatCount = 0;
        }

        // confirmation dialog (shut down / restart): left/right moves the
        // button focus, A presses the focused button, B cancels. It owns the
        // pad completely while open - the panel/tiles never see input.
        if (_confirmDialog is { } cdlg)
        {
            if (dx != 0 || dy != 0 || confirm || back) cdlg.MarkPadInput(pad: true);
            if (moveNow && dx != 0) cdlg.SwitchFocus(dx);
            if (back && !_padBackPrev)
            {
                cdlg.Cancel();
            }
            else if (confirm && !_padConfirmPrev)
            {
                cdlg.PressFocused();
            }
            _padConfirmPrev = confirm;
            _padBackPrev = back;
            return;
        }

        // power dialog: left/right moves the button focus (the action row
        // wraps), A presses the focused button, B cancels. It owns the pad
        // completely while open - the menu/tiles never see input.
        if (_powerDialog is { } pdlg)
        {
            // badge follows the live input device - but ONLY on real pad input.
            // Poll() returns true for a connected-but-idle pad too (all axes
            // zeroed), and an unguarded call here would force the badge to B
            // every 30ms even while the user is on the keyboard/mouse.
            if (dx != 0 || dy != 0 || confirm || back) pdlg.MarkPadInput(pad: true);
            if (moveNow && dx != 0) pdlg.SwitchFocus(dx);
            if (back && !_padBackPrev)
            {
                pdlg.Cancel();
            }
            else if (confirm && !_padConfirmPrev)
            {
                pdlg.PressFocused();
            }
            _padConfirmPrev = confirm;
            _padBackPrev = back;
            return;
        }

        // About dialog: B closes it; everything else waits for the dialog
        if (_aboutWindow is { } about)
        {
            if (back && !_padBackPrev)
            {
                _aboutWindow = null; // cleared first so a second poll cannot re-close
                about.Close();
            }
            _padConfirmPrev = confirm;
            _padBackPrev = back;
            return;
        }

        // while the Advanced menu is open the pad drives the menu instead
        if (_openMenu is { } menu)
        {
            if (_menuItems.Count > 0)
            {
                if (moveNow)
                {
                    int dir = dy != 0 ? dy : dx; // vertical list: map left/right to up/down
                    int next = _menuIndex;
                    do
                    {
                        next = ((next + (dir > 0 ? 1 : -1)) % _menuItems.Count + _menuItems.Count) % _menuItems.Count;
                    } while (!_menuItems[next].IsEnabled && next != _menuIndex);
                    _menuIndex = next;
                    _menuItems[_menuIndex].Focus();
                }
                if (confirm && !_padConfirmPrev)
                {
                    _menuItems[_menuIndex].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    if (_openMenu != null) _openMenu.IsOpen = false;
                }
            }
            if (back && !_padBackPrev && _openMenu != null) _openMenu.IsOpen = false;
            _padConfirmPrev = confirm;
            _padBackPrev = back;
            return;
        }

        if (moveNow) Navigate(dx, dy);

        // B is the console "back" button: leaves the header button row
        if (back && !_padBackPrev && _chromeIndex >= 0) ExitChrome();

        if (confirm && !_padConfirmPrev)
        {
            ConfirmSelection();
        }
        _padConfirmPrev = confirm;
        _padBackPrev = back;
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
        _hwnd = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProcHook);

        // Losing activation (another window takes the foreground, a menu/message
        // box opens, the window is minimized) must freeze pad navigation: reset
        // the auto-repeat state so a held direction does not chain into repeat on
        // refocus, and pretend confirm was held so re-entering the foreground
        // with A still down cannot launch anything.
        Deactivated += (_, _) => ResetPadState();
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

    // ---------------- Advanced menu & power panel (gamepad-navigable) ----------------

    private ContextMenu? _openMenu;
    private List<MenuItem> _menuItems = new();
    private int _menuIndex;

    /// <summary>Opens a chrome dropdown below its header button and hands it to
    /// the shared gamepad navigation (_openMenu/_menuItems). Only one chrome
    /// menu is open at a time: an already-open menu is closed first.</summary>
    private void OpenChromeMenu(ContextMenu menu, FrameworkElement target)
    {
        CloseChromeMenu();

        // Standard WPF popup positioning: below the button.
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 8;

        // While the menu is open it owns the gamepad: the popup HWND is the
        // foreground window (same process - see IsOwnProcess), and pad input
        // drives menu navigation instead of the tile grid.
        _openMenu = menu;
        _menuItems = menu.Items.OfType<MenuItem>().ToList();
        _menuIndex = 0;
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_openMenu, menu)) return; // already superseded/cleaned up
            _openMenu = null;
            _menuItems = new();
            ResetPadState(); // a held direction/A must not leak back into the UI
        };
        menu.IsOpen = true;
        if (_menuItems.Count > 0 && _menuItems[0].IsEnabled)
        {
            _menuItems[0].Focus();
        }
        else
        {
            // first item is a read-only status line: start on the first action
            _menuIndex = _menuItems.FindIndex(m => m.IsEnabled);
            if (_menuIndex >= 0) _menuItems[_menuIndex].Focus();
        }
    }

    private void CloseChromeMenu()
    {
        if (_openMenu is not { } open) return;
        _openMenu = null;
        _menuItems = new();
        open.IsOpen = false;
        ResetPadState();
    }

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
        // explicit style: ContextMenu-hosted Separators ignore implicit styles
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparatorStyle") });
        Item(IsServiceInstalled() ? Strings.MenuServiceInstalled : Strings.MenuServiceNotInstalled, false, null!);
        Item(Strings.MenuInstallService, !IsServiceInstalled(),
            () => Launcher.RunElevated("--install-service"));
        Item(Strings.MenuRemoveService, IsServiceInstalled(),
            () => Launcher.RunElevated("--uninstall-service"));

        OpenChromeMenu(menu, AdvancedButton);
    }

    private void OnPower(object sender, RoutedEventArgs e)
    {
        // The header power button opens the action panel directly - no dropdown
        // step in between. All four actions share this one confirmation surface;
        // the initial focus sits on the first (mildest) action, console-style.
        ShowPowerDialog(PowerAction.Disconnect);
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
        var about = new AboutWindow(Strings.AboutBody(Launcher.LinksFolder), RenderDiagnostics())
        {
            Owner = this
        };
        // track it while modal so pad B can close it (see IsOwnProcess/HandlePadInput)
        _aboutWindow = about;
        try
        {
            about.ShowDialog();
        }
        finally
        {
            _aboutWindow = null;
        }
    }

    private AboutWindow? _aboutWindow;

    // ---------------- power actions ----------------

    private PowerDialog? _powerDialog;
    private ConfirmDialog? _confirmDialog;

    /// <summary>断开：先切断本会话串流客户端的 TCP 连接（趁自己还活着），
    /// 再关闭管理器。不碰 sunshine.exe —— RTSP 连接一断，Apollo 会自行结束
    /// 串流会话并回到空闲。无管理员权限时切断退化为空操作，依然关闭管理器。</summary>
    private void OnExitStream()
    {
        int killed = PowerActions.DisconnectStreamClients();
        if (killed == 0)
        {
            ServiceHost.Log("exit-stream: nothing cut - closing the launcher only");
        }
        Close(); // App shutdown follows (ShutdownMode=OnMainWindowClose)
    }

    /// <summary>Power panel: one dialog with all four actions (disconnect /
    /// sign out / shut down / restart). Opened directly by the header power
    /// button (mouse, keyboard Enter, gamepad A) - there is no dropdown step.
    /// Focus starts on the first action (console power-menu convention) and
    /// left/right moves it; cancelled with Esc / gamepad B.</summary>
    private void ShowPowerDialog(PowerAction initial)
    {
        CloseChromeMenu(); // a stray Advanced dropdown must not linger behind the modal

        var dlg = new PowerDialog(initial, _hintPadMode) { Owner = this };
        _powerDialog = dlg;
        PowerAction? chosen;
        try
        {
            chosen = dlg.ShowDialog() == true ? dlg.Result : null;
        }
        finally
        {
            _powerDialog = null;
        }

        switch (chosen)
        {
            case PowerAction.Disconnect:
                OnExitStream();
                break;
            case PowerAction.LogOff:
                PowerActions.LogOff();
                break;
            case PowerAction.Shutdown:
                if (ConfirmDestructive(shutdown: true)) PowerActions.Shutdown();
                break;
            case PowerAction.Restart:
                if (ConfirmDestructive(shutdown: false)) PowerActions.Restart();
                break;
        }
    }

    /// <summary>Second-stage confirmation for the forced shut down / restart:
    /// the power panel picks the action, this dark card makes the irreversible
    /// step explicit (original requirement: these two need a confirmation
    /// dialog). Focus starts on Cancel; confirmed by click, Enter or pad A on
    /// the confirm button, cancelled by Esc / X / pad B.</summary>
    private bool ConfirmDestructive(bool shutdown)
    {
        var dlg = new ConfirmDialog(
            shutdown ? Strings.ConfirmShutdownTitle : Strings.ConfirmRestartTitle,
            shutdown ? Strings.ConfirmShutdownBody : Strings.ConfirmRestartBody,
            shutdown ? Strings.PowerShutdown : Strings.PowerRestart,
            _hintPadMode)
        { Owner = this };

        _confirmDialog = dlg;
        try
        {
            return dlg.ShowDialog() == true && dlg.Confirmed;
        }
        finally
        {
            _confirmDialog = null;
        }
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
                // Multi-seat seats are usually RDP-type sessions. The app-level
                // RDP hardware switch (Switch.System.Windows.Media.
                // EnableHardwareAccelerationInRdp) is baked into the exe, so the
                // deciding factor at runtime is the system policy: with it the
                // session enumerates the physical GPU (WPF then uses it); without
                // it only the software adapter is visible in the session.
                mode = Launcher.IsRemoteGpuEnabled()
                    ? Strings.DiagRemoteSessionGpuOn
                    : Strings.DiagRemoteSessionAppSwitchOn;
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
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter
            or Key.F5 or Key.F11 or Key.Escape)
        {
            MarkInput(pad: false);
        }
        if (e.Key == Key.F5) { LoadGames(); e.Handled = true; }
        else if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _chromeIndex >= 0) { ExitChrome(); e.Handled = true; }
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