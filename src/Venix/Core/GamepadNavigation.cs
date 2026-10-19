using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Venix.Pages;

namespace Venix.Core;

/// <summary>
/// Steam-style controller navigation of the launcher. A 60 Hz loop on its own thread reads the primary
/// controller (XInput directly, or the last HID report fed by <see cref="GamepadService"/>) and posts actions to
/// the UI thread: D-pad / left stick move focus geometrically between the focusable controls of the visible
/// area (buttons, toggles, text boxes), A activates, B goes back, Y jumps to the search box, LB/RB switch the
/// sidebar section, Start launches the selected game and the right stick scrolls. The loop only runs while
/// navigation is enabled, a controller is present, Venix is the active window and no game launched from the
/// library is running — otherwise the thread does not exist.
/// </summary>
public sealed class GamepadNavigation
{
    /// <summary>Attached to a container whose controls must never receive controller focus (window chrome buttons).</summary>
    public static readonly DependencyProperty SkipProperty = DependencyProperty.RegisterAttached("Skip", typeof(bool), typeof(GamepadNavigation), new PropertyMetadata(false));
    public static void SetSkip(DependencyObject d, bool v) => d.SetValue(SkipProperty, v);
    public static bool GetSkip(DependencyObject d) => (bool)d.GetValue(SkipProperty);

    enum Dir { None, Up, Down, Left, Right }

    static readonly string[] Sections = { "home", "catalog", "library", "cheats", "downloads", "settings" };

    readonly MainWindow _win;
    readonly GamepadService _pads;
    readonly Dispatcher _ui;
    readonly object _lock = new();
    PadState _hid;
    Thread? _thread;
    volatile bool _run;
    bool _windowActive;
    bool _usingPad;
    Point _lastMouse;
    Rect _ringRect = Rect.Empty;

    public bool Enabled { get; private set; }
    public bool Running => _thread != null;

    public GamepadNavigation(MainWindow win, GamepadService pads)
    {
        _win = win;
        _pads = pads;
        _ui = win.Dispatcher;
        Enabled = Hub.Store.Settings.GamepadNavigation;
        _windowActive = win.IsActive;

        pads.Changed += Update;
        pads.GameRunningChanged += _ => Update();
        pads.HidInput += (pad, s) => { if (pad == _pads.Primary) lock (_lock) _hid = s; };
        win.Activated += (_, _) => { _windowActive = true; Update(); };
        win.Deactivated += (_, _) => { _windowActive = false; HideRing(); Update(); };
        win.Closed += (_, _) => Stop();
        win.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => { if (_usingPad) ShowRing(); }), true);
        win.PreviewMouseMove += (_, e) =>
        {
            var p = e.GetPosition(win);
            if (_usingPad && (Math.Abs(p.X - _lastMouse.X) > 6 || Math.Abs(p.Y - _lastMouse.Y) > 6)) { _usingPad = false; HideRing(); }
            _lastMouse = p;
        };
        win.Root.LayoutUpdated += (_, _) => { if (_usingPad && _win.GamepadFocus.Visibility == Visibility.Visible) PlaceRing(); };
        Update();
    }

    public void SetEnabled(bool on)
    {
        Enabled = on;
        Hub.Store.Settings.GamepadNavigation = on;
        Hub.Store.SaveSoon();
        if (!on) { _usingPad = false; HideRing(); }
        Update();
    }

    /* ---------------------------------------------------------- loop lifecycle */

    void Update()
    {
        var want = Enabled && _pads.Primary != null && _windowActive && !_pads.GameRunning;
        if (want) Start(); else Stop();
    }

    void Start()
    {
        if (_thread != null) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "gamepad-nav", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void Stop()
    {
        _run = false;
        _thread = null;
    }

    void Loop()
    {
        var prev = default(PadState);
        var lastDir = Dir.None;
        long nextRepeat = 0;
        var clock = Stopwatch.StartNew();
        var idle = 0;
        while (_run)
        {
            var primary = _pads.Primary;
            PadState s;
            if (primary == null) { Thread.Sleep(100); continue; }
            if (primary.IsXInput)
            {
                if (primary.XInputSlot < 0 || !XInput.TryGetState(primary.XInputSlot, out var xs)) { Thread.Sleep(100); continue; }
                s = XInput.ToPadState(xs.Gamepad);
            }
            else lock (_lock) s = _hid;

            var now = clock.ElapsedMilliseconds;
            var dir = Direction(s, lastDir);
            if (dir != lastDir)
            {
                lastDir = dir;
                if (dir != Dir.None) { Post(() => Move(dir)); nextRepeat = now + 380; }
            }
            else if (dir != Dir.None && now >= nextRepeat) { Post(() => Move(dir)); nextRepeat = now + 110; }

            var pressed = (ushort)(s.Buttons & ~prev.Buttons);
            if ((pressed & XInput.A) != 0) Post(Activate);
            if ((pressed & XInput.B) != 0) Post(Back);
            if ((pressed & XInput.Y) != 0) Post(FocusSearch);
            if ((pressed & XInput.LeftShoulder) != 0) Post(() => Cycle(-1));
            if ((pressed & XInput.RightShoulder) != 0) Post(() => Cycle(+1));
            if ((pressed & XInput.Start) != 0) Post(LaunchGame);
            if (MathF.Abs(s.RY) > 0.25f) { var dy = s.RY; Post(() => Scroll(-dy * 22)); }
            prev = s;

            // 60 Hz while something is happening, 20 Hz after a few idle seconds (still instant to react)
            idle = s.Any ? 0 : Math.Min(idle + 1, 1000);
            Thread.Sleep(idle > 180 ? 50 : 16);
        }
    }

    static Dir Direction(in PadState s, Dir current)
    {
        if (s.Has(XInput.DpadUp)) return Dir.Up;
        if (s.Has(XInput.DpadDown)) return Dir.Down;
        if (s.Has(XInput.DpadLeft)) return Dir.Left;
        if (s.Has(XInput.DpadRight)) return Dir.Right;
        var threshold = current == Dir.None ? 0.55f : 0.38f; // hysteresis so a held stick does not flicker
        var ax = MathF.Abs(s.LX); var ay = MathF.Abs(s.LY);
        if (ax < threshold && ay < threshold) return Dir.None;
        return ax > ay ? (s.LX > 0 ? Dir.Right : Dir.Left) : (s.LY > 0 ? Dir.Up : Dir.Down);
    }

    void Post(Action a)
    {
        if (!_run) return;
        _ui.BeginInvoke(DispatcherPriority.Input, () => { if (_run && _win.IsActive) { _usingPad = true; try { a(); } catch { } } });
    }

    /* ---------------------------------------------------------- actions (UI thread) */

    FrameworkElement SearchRoot() => _win.Overlay.Visibility == Visibility.Visible ? _win.OverlayHost : _win.Shell;

    FrameworkElement? Focused()
    {
        var el = Keyboard.FocusedElement as FrameworkElement;
        return el != null && el.IsVisible && IsCandidate(el) && el.IsDescendantOf(SearchRoot()) ? el : null;
    }

    void Move(Dir dir)
    {
        var root = SearchRoot();
        var cur = Focused();
        if (cur == null) { FocusFirst(root); return; }
        var from = Bounds(cur, root);
        if (from == null) { FocusFirst(root); return; }
        FrameworkElement? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var c in Candidates(root))
        {
            if (c == cur) continue;
            var r = Bounds(c, root);
            if (r == null) continue;
            var score = Score(dir, from.Value, r.Value);
            if (score < bestScore) { bestScore = score; best = c; }
        }
        if (best != null) Focus(best);
    }

    static double Score(Dir dir, Rect from, Rect to)
    {
        var fc = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
        var tc = new Point(to.X + to.Width / 2, to.Y + to.Height / 2);
        double primary, gap, perpendicular;
        switch (dir)
        {
            case Dir.Down:
                if (tc.Y <= fc.Y + 1 || to.Top < from.Top + 2) return double.PositiveInfinity;
                primary = Math.Max(0, to.Top - from.Bottom); gap = Gap(from.Left, from.Right, to.Left, to.Right); perpendicular = Math.Abs(tc.X - fc.X); break;
            case Dir.Up:
                if (tc.Y >= fc.Y - 1 || to.Bottom > from.Bottom - 2) return double.PositiveInfinity;
                primary = Math.Max(0, from.Top - to.Bottom); gap = Gap(from.Left, from.Right, to.Left, to.Right); perpendicular = Math.Abs(tc.X - fc.X); break;
            case Dir.Right:
                if (tc.X <= fc.X + 1 || to.Left < from.Left + 2) return double.PositiveInfinity;
                primary = Math.Max(0, to.Left - from.Right); gap = Gap(from.Top, from.Bottom, to.Top, to.Bottom); perpendicular = Math.Abs(tc.Y - fc.Y); break;
            default:
                if (tc.X >= fc.X - 1 || to.Right > from.Right - 2) return double.PositiveInfinity;
                primary = Math.Max(0, from.Left - to.Right); gap = Gap(from.Top, from.Bottom, to.Top, to.Bottom); perpendicular = Math.Abs(tc.Y - fc.Y); break;
        }
        // elements that overlap on the cross axis win; otherwise the nearest, with a penalty for drifting sideways
        return primary + gap * 3 + perpendicular * 0.25;
    }

    static double Gap(double a1, double a2, double b1, double b2) => Math.Max(0, Math.Max(b1 - a2, a1 - b2));

    void FocusFirst(FrameworkElement root)
    {
        // prefer the page content (or the dialog), then anything else in the window
        var page = _win.Overlay.Visibility == Visibility.Visible ? root : _win.PageHost;
        var target = First(page) ?? First(root);
        if (target != null) Focus(target);
    }

    FrameworkElement? First(FrameworkElement scope)
    {
        FrameworkElement? best = null;
        Rect bestRect = Rect.Empty;
        foreach (var c in Candidates(scope))
        {
            var r = Bounds(c, scope);
            if (r == null || r.Value.Bottom < 0) continue;
            if (best == null || r.Value.Top < bestRect.Top - 8 || (Math.Abs(r.Value.Top - bestRect.Top) <= 8 && r.Value.Left < bestRect.Left)) { best = c; bestRect = r.Value; }
        }
        return best;
    }

    void Focus(FrameworkElement el)
    {
        Keyboard.Focus(el);
        el.BringIntoView();
        ShowRing();
    }

    static Rect? Bounds(FrameworkElement el, Visual root)
    {
        try { return el.TransformToAncestor(root).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight)); }
        catch { return null; }
    }

    static bool IsCandidate(FrameworkElement fe) =>
        fe.Focusable && fe.IsEnabled && fe.ActualWidth > 0 && fe.ActualHeight > 0 &&
        fe is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Slider or ListBoxItem;

    static IEnumerable<FrameworkElement> Candidates(DependencyObject node)
    {
        if (node is UIElement u && (!u.IsVisible || !u.IsHitTestVisible || u.Opacity == 0 || GetSkip(u))) yield break;
        if (node is FrameworkElement fe && IsCandidate(fe)) { yield return fe; yield break; }
        var n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
            foreach (var c in Candidates(VisualTreeHelper.GetChild(node, i))) yield return c;
    }

    void Activate()
    {
        var el = Focused();
        if (el == null) { FocusFirst(SearchRoot()); return; }
        switch (el)
        {
            case ToggleButton tb:
                if (tb is RadioButton) tb.IsChecked = true; else tb.IsChecked = tb.IsChecked != true;
                tb.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, tb));
                break;
            case ButtonBase b:
                b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
                break;
            case TextBoxBase or PasswordBox:
                SendKey(el, Key.Enter);
                break;
            case ComboBox cb:
                cb.IsDropDownOpen = !cb.IsDropDownOpen;
                break;
        }
    }

    static void SendKey(FrameworkElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target);
        if (source == null) return;
        var ts = Environment.TickCount;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, ts, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, ts, key) { RoutedEvent = Keyboard.KeyDownEvent });
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, ts, key) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    void Back()
    {
        if (_win.Overlay.Visibility == Visibility.Visible) { _win.CloseModal(); return; }
        _win.Back();
        if (Focused() == null) FocusFirst(_win.Shell);
    }

    void FocusSearch()
    {
        _win.GlobalSearch.Focus();
        _win.GlobalSearch.SelectAll();
        ShowRing();
    }

    void Cycle(int step)
    {
        if (_win.Overlay.Visibility == Visibility.Visible) return;
        var current = _win.PageHost.Content is IPage p && Array.IndexOf(Sections, p.NavKey) >= 0 ? Array.IndexOf(Sections, p.NavKey) : 0;
        var key = Sections[(current + step + Sections.Length) % Sections.Length];
        _win.Go(key);
        var radio = key switch { "catalog" => _win.NavCatalog, "library" => _win.NavLibrary, "cheats" => _win.NavCheats, "downloads" => _win.NavDownloads, "settings" => _win.NavSettings, _ => _win.NavHome };
        Focus(radio);
    }

    void LaunchGame()
    {
        if (_win.Overlay.Visibility == Visibility.Visible) return;
        if (_win.PageHost.Content is GamePage gp && gp.PlayBtn.IsVisible)
        {
            gp.PlayBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, gp.PlayBtn));
            return;
        }
        var game = Focused()?.DataContext switch { LibraryGame g => g, Tile { Payload: LibraryGame g } => g, _ => null };
        if (game == null) { _win.Toast("Abra a página de um jogo da biblioteca e pressione Start para jogar."); return; }
        if (!game.HasExe) { _win.OpenGame(game.AppId, game.Title); return; }
        try { Hub.Store.Launch(game); }
        catch (Exception ex) { _win.Toast(ex.Message, true); }
    }

    void Scroll(double dy)
    {
        ScrollViewer? sv = null;
        DependencyObject? node = Focused();
        while (node != null && sv == null)
        {
            if (node is ScrollViewer s && s.ScrollableHeight > 0) sv = s;
            node = VisualTreeHelper.GetParent(node);
        }
        sv ??= FindScroll(_win.Overlay.Visibility == Visibility.Visible ? _win.OverlayHost : _win.PageHost);
        sv?.ScrollToVerticalOffset(sv.VerticalOffset + dy);
    }

    static ScrollViewer? FindScroll(DependencyObject node)
    {
        if (node is ScrollViewer s && s.ScrollableHeight > 0) return s;
        var n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
            if (FindScroll(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    /* ---------------------------------------------------------- focus ring */

    void ShowRing()
    {
        _usingPad = true;
        _ringRect = Rect.Empty;
        PlaceRing();
    }

    void HideRing()
    {
        _win.GamepadFocus.Visibility = Visibility.Collapsed;
        _ringRect = Rect.Empty;
    }

    void PlaceRing()
    {
        var el = Focused();
        var ring = _win.GamepadFocus;
        if (el == null) { ring.Visibility = Visibility.Collapsed; return; }
        var r = Bounds(el, _win.Root);
        if (r == null || r.Value.Width <= 0) { ring.Visibility = Visibility.Collapsed; return; }
        var rect = r.Value;
        if (rect == _ringRect) return;
        _ringRect = rect;
        const double pad = 4;
        ring.Margin = new Thickness(rect.X - pad, rect.Y - pad, 0, 0);
        ring.Width = rect.Width + pad * 2;
        ring.Height = rect.Height + pad * 2;
        var radius = CornerOf(el);
        ring.CornerRadius = new CornerRadius(Math.Min(radius + pad, (rect.Height + pad * 2) / 2));
        ring.Visibility = Visibility.Visible;
    }

    /// <summary>Corner radius of the element's template root border, so the ring hugs pills and cards alike.</summary>
    static double CornerOf(DependencyObject el)
    {
        var node = el;
        for (int depth = 0; depth < 3 && node != null; depth++)
        {
            if (node is Border b) return b.CornerRadius.TopLeft > 0 ? b.CornerRadius.TopLeft : Math.Min(b.ActualHeight, b.ActualWidth) / 2;
            node = VisualTreeHelper.GetChildrenCount(node) > 0 ? VisualTreeHelper.GetChild(node, 0) : null;
        }
        return el is TextBox ? 21 : 12;
    }
}
