using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MotionStabilizer.Models;
using MotionStabilizer.Services;

namespace MotionStabilizer.Overlay;

/// <summary>
/// The invisible rendering layer. This is a pure external transparent overlay window.
/// It draws overlay shapes, crosshair, and clock on top of all games.
/// 
/// Key properties:
/// - Click-through: mouse events pass through to the game below
/// - No-activate: never steals focus from the game
/// - Topmost: always rendered on top
/// - Transparent: only drawn shapes are visible
/// 
/// This approach is 100% external — no DLL injection, no process modification,
/// no memory access. It is safe under all anti-cheat systems (Vanguard, EAC, BattlEye).
/// </summary>
public partial class OverlayWindow : Window
{
    private OverlayConfig _overlayConfig = new();
    private CrosshairConfig _crosshairConfig = new();
    private ClockConfig _clockConfig = new();

    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _motionTimer;
    private TextBlock? _clockText;
    private bool _isClockDragging;
    private DispatcherTimer? _dragTimer;
    private Point _clockDragOffset;
    private bool _wasLeftButtonDown;

    private readonly List<MotionCueDot> _motionDots = new();
    private HwndSource? _windowSource;
    private readonly Stopwatch _motionClock = Stopwatch.StartNew();
    private TimeSpan _lastMotionFrame;
    private TimeSpan _lastColorSample;
    private TimeSpan _lastDotRespawn;
    private TimeSpan _lastImmediateTransform;
    private Vector _pendingMouseDelta;
    private Vector _motionOffset;
    private Vector _lastMotionDirection = new(1, 0);
    private double _movementEnergy;
    private int _respawnCursor;
    private Canvas? _motionLayer;
    private TranslateTransform? _motionLayerTransform;
    private Random _motionRandom = new(1);
    private int _motionGeneration;
    private bool _motionRenderingSubscribed;
    private readonly DirectCompositionMotionRenderer _nativeMotionRenderer = new();
    private bool _isWpfSurfaceCompact;

    private sealed class MotionCueDot
    {
        public required Ellipse Shape { get; init; }
        public required SolidColorBrush Brush { get; init; }
        public Point BasePosition { get; set; }
        public EdgeSide Edge { get; set; }
        public bool UsesLightColor { get; set; }
        public Color TargetColor { get; set; }
        public double Opacity { get; set; } = 1;
        public int FadeDirection { get; set; }
    }

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OverlayWindow_Loaded;
        Closed += OverlayWindow_Closed;
    }

    private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Apply Win32 extended styles for click-through + no-activate
        var helper = new WindowInteropHelper(this);
        Win32Interop.MakeOverlayWindow(helper.Handle);
        Win32Interop.RegisterRawMouseInput(helper.Handle);
        _windowSource = HwndSource.FromHwnd(helper.Handle);
        _windowSource?.AddHook(WindowMessageHook);

        // Size to full screen
        UpdateScreenBounds();

        // Start clock timer
        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _clockTimer.Tick += SlowTimer_Tick;
        _clockTimer.Start();

        _motionTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _motionTimer.Tick += OnCompositionRendering;

        _lastMotionFrame = _motionClock.Elapsed;
        Render();
    }

    private void OverlayWindow_Closed(object? sender, EventArgs e)
    {
        StopMotionRendering();
        _nativeMotionRenderer.Dispose();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _clockTimer?.Stop();
        _motionTimer?.Stop();
        _dragTimer?.Stop();
    }

    /// <summary>Update window bounds to cover the full screen.</summary>
    public void UpdateScreenBounds()
    {
        // GetScreenWidth/Height return physical pixels; WPF Window.Width is in DIP.
        // Convert physical → DIP so the window covers the full physical screen.
        double scale = Win32Interop.GetDpiScale();
        double w = _isWpfSurfaceCompact ? 1 : Win32Interop.GetScreenWidth() / scale;
        double h = _isWpfSurfaceCompact ? 1 : Win32Interop.GetScreenHeight() / scale;
        this.Left = 0;
        this.Top = 0;
        this.Width = w;
        this.Height = h;
        OverlayCanvas.Width = w;
        OverlayCanvas.Height = h;

        if (_nativeMotionRenderer.IsReady)
        {
            _nativeMotionRenderer.Configure(
                _overlayConfig,
                Win32Interop.GetScreenWidth(),
                Win32Interop.GetScreenHeight());
        }
    }

    /// <summary>Update all configs and re-render.</summary>
    public void UpdateConfigs(OverlayConfig overlay, CrosshairConfig crosshair, ClockConfig clock)
    {
        _overlayConfig = overlay;
        _crosshairConfig = crosshair;
        _clockConfig = clock;
        Render();
    }

    /// <summary>Re-render all overlay elements on the canvas.</summary>
    public void Render()
    {
        _motionGeneration++;
        OverlayCanvas.Children.Clear();
        _clockText = null;
        _motionDots.Clear();
        _motionLayer = null;
        _motionLayerTransform = null;

        int physicalWidth = Win32Interop.GetScreenWidth();
        int physicalHeight = Win32Interop.GetScreenHeight();
        bool wantsNativeMotion =
            _overlayConfig.IsVisible &&
            _overlayConfig.Shape == OverlayShape.MotionDots;
        bool nativeMotionActive =
            wantsNativeMotion &&
            _nativeMotionRenderer.TryInitialize(physicalWidth, physicalHeight);

        if (nativeMotionActive)
        {
            StopMotionRendering();
            _nativeMotionRenderer.Configure(
                _overlayConfig,
                physicalWidth,
                physicalHeight);
        }
        else
        {
            _nativeMotionRenderer.SetVisible(false);
        }

        // A full-screen AllowsTransparency WPF window still incurs composition
        // cost even when it has no shapes. Keep its input sink at 1x1 whenever
        // DirectComposition is the only visible overlay surface.
        SetWpfSurfaceCompact(
            nativeMotionActive &&
            !_crosshairConfig.IsVisible &&
            !_clockConfig.IsVisible);

        double sw = this.Width > 0 ? this.Width : Win32Interop.GetScreenWidth() / Win32Interop.GetDpiScale();
        double sh = this.Height > 0 ? this.Height : Win32Interop.GetScreenHeight() / Win32Interop.GetDpiScale();

        // Render edge overlay
        if (_overlayConfig.IsVisible)
        {
            if (_overlayConfig.Shape == OverlayShape.MotionDots)
            {
                if (!nativeMotionActive)
                {
                    RenderMotionDots(sw, sh);
                }
            }
            else
            {
                var area = new Rect(0, 0, sw, sh);
                var overlayShapes = RenderHelper.BuildOverlayShapes(_overlayConfig, area, sw, sh);
                foreach (var s in overlayShapes)
                    OverlayCanvas.Children.Add(s);
            }
        }
        // Render crosshair
        if (_crosshairConfig.IsVisible)
        {
            var crosshairShapes = RenderHelper.BuildCrosshairShapes(_crosshairConfig, sw, sh);
            foreach (var s in crosshairShapes)
                OverlayCanvas.Children.Add(s);
        }

        // Render clock
        if (_clockConfig.IsVisible)
        {
            RenderClock(sw, sh);
        }
    }

    private void SetWpfSurfaceCompact(bool compact)
    {
        if (_isWpfSurfaceCompact == compact)
            return;

        _isWpfSurfaceCompact = compact;
        if (compact)
        {
            Width = 1;
            Height = 1;
            OverlayCanvas.Width = 1;
            OverlayCanvas.Height = 1;
            return;
        }

        double scale = Win32Interop.GetDpiScale();
        double width = Win32Interop.GetScreenWidth() / scale;
        double height = Win32Interop.GetScreenHeight() / scale;
        Left = 0;
        Top = 0;
        Width = width;
        Height = height;
        OverlayCanvas.Width = width;
        OverlayCanvas.Height = height;
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == Win32Interop.WM_INPUT &&
            _overlayConfig.IsVisible &&
            _overlayConfig.Shape == OverlayShape.MotionDots &&
            Win32Interop.TryGetRawMouseDelta(lParam, out int deltaX, out int deltaY))
        {
            if (_nativeMotionRenderer.IsVisible)
            {
                _nativeMotionRenderer.OnMouseDelta(deltaX, deltaY);
                return IntPtr.Zero;
            }

            var delta = new Vector(
                deltaX,
                _overlayConfig.MotionInvertY ? -deltaY : deltaY);
            _pendingMouseDelta += delta;

            double sensitivity = Math.Clamp(_overlayConfig.MotionSensitivity, 0.05, 3.0);
            _motionOffset += delta * sensitivity;
            ClampMotionOffset();

            TimeSpan now = _motionClock.Elapsed;
            if ((now - _lastImmediateTransform).TotalMilliseconds >= 1)
            {
                ApplyMotionTransform();
                _lastImmediateTransform = now;
            }

            EnsureMotionRendering();
        }

        return IntPtr.Zero;
    }

    private void SlowTimer_Tick(object? sender, EventArgs e)
    {
        UpdateClock();

        TimeSpan now = _motionClock.Elapsed;
        if (_overlayConfig.IsVisible &&
            _overlayConfig.Shape == OverlayShape.MotionDots &&
            _overlayConfig.MotionAdaptiveColor &&
            (now - _lastColorSample).TotalMilliseconds >= 1000)
        {
            _lastColorSample = now;
            if (_nativeMotionRenderer.IsVisible)
            {
                _nativeMotionRenderer.UpdateAdaptiveColors();
            }
            else
            {
                UpdateAdaptiveColors(force: false);
                EnsureMotionRendering();
            }
        }
    }

    private void EnsureMotionRendering()
    {
        if (_motionRenderingSubscribed)
            return;

        _lastMotionFrame = _motionClock.Elapsed;
        _motionTimer?.Start();
        _motionRenderingSubscribed = true;
    }

    private void StopMotionRendering()
    {
        if (!_motionRenderingSubscribed)
            return;

        _motionTimer?.Stop();
        _motionRenderingSubscribed = false;
    }

    private void ClampMotionOffset()
    {
        double maxOffset = Math.Clamp(_overlayConfig.MotionMaxOffset, 8, 240);
        double length = _motionOffset.Length;
        if (length > maxOffset)
            _motionOffset *= maxOffset / length;
    }

    private void ApplyMotionTransform()
    {
        if (_motionLayerTransform == null)
            return;

        if (Math.Abs(_motionLayerTransform.X - _motionOffset.X) > 0.001)
            _motionLayerTransform.X = _motionOffset.X;
        if (Math.Abs(_motionLayerTransform.Y - _motionOffset.Y) > 0.001)
            _motionLayerTransform.Y = _motionOffset.Y;
    }

    private void RenderMotionDots(double screenWidth, double screenHeight)
    {
        _motionOffset = default;
        _pendingMouseDelta = default;
        _movementEnergy = 0;
        _respawnCursor = 0;
        _motionRandom = new Random(HashCode.Combine(
            (int)Math.Round(screenWidth),
            (int)Math.Round(screenHeight),
            _overlayConfig.MotionDotCount,
            (int)_overlayConfig.Size));

        _motionLayerTransform = new TranslateTransform();
        _motionLayer = new Canvas
        {
            Width = screenWidth,
            Height = screenHeight,
            IsHitTestVisible = false,
            RenderTransform = _motionLayerTransform
        };
        OverlayCanvas.Children.Add(_motionLayer);

        int targetCount = Math.Clamp(_overlayConfig.MotionDotCount * 8, 24, 96);
        double baseDiameter = RenderHelper.MotionDotDiameter(_overlayConfig.Size);
        int attempts = 0;

        while (_motionDots.Count < targetCount && attempts++ < targetCount * 10)
        {
            double diameter = baseDiameter * (0.55 + _motionRandom.NextDouble() * 1.05);
            Point? candidate = FindSeparatedDotPosition(
                screenWidth,
                screenHeight,
                diameter,
                null,
                null);
            if (!candidate.HasValue)
                continue;

            Point position = candidate.Value;
            EdgeSide edge = GetNearestEdge(position, screenWidth, screenHeight);
            var fallback = _overlayConfig.GetColor();
            var brush = new SolidColorBrush(Color.FromArgb(
                RenderHelper.OpacityToByte(_overlayConfig.GetEdgeOpacity(edge)),
                fallback.R,
                fallback.G,
                fallback.B));
            var ellipse = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Fill = brush,
                IsHitTestVisible = false
            };

            var dot = new MotionCueDot
            {
                Shape = ellipse,
                Brush = brush,
                BasePosition = new Point(position.X - diameter / 2, position.Y - diameter / 2),
                Edge = edge,
                UsesLightColor = true,
                TargetColor = fallback
            };

            Canvas.SetLeft(ellipse, dot.BasePosition.X);
            Canvas.SetTop(ellipse, dot.BasePosition.Y);
            _motionDots.Add(dot);
            _motionLayer.Children.Add(ellipse);
        }

        if (_overlayConfig.MotionAdaptiveColor)
        {
            UpdateAdaptiveColors(force: true);
            EnsureMotionRendering();
        }
    }

    private Point CreateRandomDotPosition(
        double screenWidth,
        double screenHeight,
        Vector? incomingDirection)
    {
        double marginX = Math.Max(12, screenWidth * 0.025);
        double marginY = Math.Max(12, screenHeight * 0.035);

        for (int attempt = 0; attempt < 24; attempt++)
        {
            double x;
            double y;

            bool useIncomingBand =
                incomingDirection.HasValue &&
                _motionRandom.NextDouble() < 0.65;

            if (useIncomingBand)
            {
                Vector direction = incomingDirection!.Value;
                if (Math.Abs(direction.X) >= Math.Abs(direction.Y))
                {
                    double band = screenWidth * (0.08 + _motionRandom.NextDouble() * 0.12);
                    x = direction.X > 0 ? marginX + band : screenWidth - marginX - band;
                    y = marginY + _motionRandom.NextDouble() * (screenHeight - marginY * 2);
                }
                else
                {
                    double band = screenHeight * (0.08 + _motionRandom.NextDouble() * 0.12);
                    x = marginX + _motionRandom.NextDouble() * (screenWidth - marginX * 2);
                    y = direction.Y > 0 ? marginY + band : screenHeight - marginY - band;
                }
            }
            else
            {
                x = marginX + _motionRandom.NextDouble() * (screenWidth - marginX * 2);
                y = marginY + _motionRandom.NextDouble() * (screenHeight - marginY * 2);
            }

            // Keep the central aiming/gameplay area clear.
            bool insideSafeCenter =
                x > screenWidth * 0.35 &&
                x < screenWidth * 0.65 &&
                y > screenHeight * 0.32 &&
                y < screenHeight * 0.68;
            if (!insideSafeCenter)
                return new Point(x, y);
        }

        return new Point(marginX, marginY);
    }

    private Point? FindSeparatedDotPosition(
        double screenWidth,
        double screenHeight,
        double diameter,
        Vector? incomingDirection,
        MotionCueDot? ignoredDot)
    {
        double extraGap = Math.Max(14, RenderHelper.MotionDotDiameter(_overlayConfig.Size) * 1.15);

        for (int attempt = 0; attempt < 80; attempt++)
        {
            Point candidate = CreateRandomDotPosition(
                screenWidth,
                screenHeight,
                incomingDirection);
            EdgeSide edge = GetNearestEdge(candidate, screenWidth, screenHeight);
            if (!_overlayConfig.IsEdgeVisible(edge))
                continue;

            bool overlaps = false;
            foreach (var existing in _motionDots)
            {
                if (ReferenceEquals(existing, ignoredDot))
                    continue;

                Point existingCenter = new(
                    existing.BasePosition.X + existing.Shape.Width / 2,
                    existing.BasePosition.Y + existing.Shape.Height / 2);
                double minimumDistance =
                    diameter / 2 +
                    existing.Shape.Width / 2 +
                    extraGap;
                Vector separation = candidate - existingCenter;
                if (separation.LengthSquared < minimumDistance * minimumDistance)
                {
                    overlaps = true;
                    break;
                }
            }

            if (!overlaps)
                return candidate;
        }

        return null;
    }

    private static EdgeSide GetNearestEdge(Point position, double width, double height)
    {
        double top = position.Y;
        double bottom = height - position.Y;
        double left = position.X;
        double right = width - position.X;
        double nearest = Math.Min(Math.Min(top, bottom), Math.Min(left, right));

        if (nearest == top) return EdgeSide.Top;
        if (nearest == bottom) return EdgeSide.Bottom;
        if (nearest == left) return EdgeSide.Left;
        return EdgeSide.Right;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        TimeSpan now = _motionClock.Elapsed;
        double dt = Math.Clamp((now - _lastMotionFrame).TotalSeconds, 0, 0.05);
        _lastMotionFrame = now;

        if (_motionDots.Count == 0 ||
            !_overlayConfig.IsVisible ||
            _overlayConfig.Shape != OverlayShape.MotionDots)
        {
            _pendingMouseDelta = default;
            StopMotionRendering();
            return;
        }

        Vector mouseDelta = _pendingMouseDelta;
        double mouseMagnitude = mouseDelta.Length;
        _pendingMouseDelta = default;

        if (mouseMagnitude > 0.01)
        {
            _lastMotionDirection = mouseDelta;
            _lastMotionDirection.Normalize();
        }

        double energyFromInput = Math.Clamp(mouseMagnitude / 28.0, 0, 1);
        _movementEnergy = Math.Max(
            energyFromInput,
            _movementEnergy * Math.Exp(-dt / 0.18));

        double returnSeconds = Math.Clamp(_overlayConfig.MotionReturnMs, 80, 1200) / 1000.0;
        _motionOffset *= Math.Exp(-dt / returnSeconds);
        if (_motionOffset.LengthSquared < 0.0025)
            _motionOffset = default;

        ApplyMotionTransform();

        ScheduleDotRespawns(now);

        foreach (var dot in _motionDots)
        {
            if (dot.FadeDirection != 0)
            {
                dot.Opacity = Math.Clamp(dot.Opacity + dot.FadeDirection * dt * 4.2, 0, 1);
                if (dot.FadeDirection < 0 && dot.Opacity <= 0)
                {
                    RelocateDot(dot);
                    dot.FadeDirection = 1;
                }
                else if (dot.FadeDirection > 0 && dot.Opacity >= 1)
                {
                    dot.FadeDirection = 0;
                }
            }

            Color current = dot.Brush.Color;
            Color target = _overlayConfig.MotionAdaptiveColor
                ? dot.TargetColor
                : _overlayConfig.GetColor();
            double colorFollow = 1.0 - Math.Exp(-dt / 0.14);
            byte baseAlpha = RenderHelper.OpacityToByte(_overlayConfig.GetEdgeOpacity(dot.Edge));
            Color next = Color.FromArgb(
                (byte)Math.Clamp((int)Math.Round(baseAlpha * dot.Opacity), 0, 255),
                LerpByte(current.R, target.R, colorFollow),
                LerpByte(current.G, target.G, colorFollow),
                LerpByte(current.B, target.B, colorFollow));
            if (next != current)
                dot.Brush.Color = next;
        }

        bool hasActiveFade = false;
        bool hasColorTransition = false;
        foreach (var dot in _motionDots)
        {
            if (dot.FadeDirection != 0)
                hasActiveFade = true;

            Color target = _overlayConfig.MotionAdaptiveColor
                ? dot.TargetColor
                : _overlayConfig.GetColor();
            if (dot.Brush.Color.R != target.R ||
                dot.Brush.Color.G != target.G ||
                dot.Brush.Color.B != target.B)
            {
                hasColorTransition = true;
            }
        }

        if (_pendingMouseDelta.LengthSquared == 0 &&
            _motionOffset.LengthSquared == 0 &&
            _movementEnergy < 0.01 &&
            !hasActiveFade &&
            !hasColorTransition)
        {
            StopMotionRendering();
        }
    }

    private void ScheduleDotRespawns(TimeSpan now)
    {
        if (_movementEnergy < 0.18 ||
            (now - _lastDotRespawn).TotalMilliseconds < 85 ||
            _motionDots.Count == 0)
        {
            return;
        }

        int activeTransitions = 0;
        foreach (var dot in _motionDots)
        {
            if (dot.FadeDirection != 0)
                activeTransitions++;
        }

        int transitionLimit = Math.Max(2, _motionDots.Count / 6);
        if (activeTransitions >= transitionLimit)
            return;

        int starts = _movementEnergy > 0.72 ? 2 : 1;
        for (int count = 0; count < starts; count++)
        {
            for (int attempt = 0; attempt < _motionDots.Count; attempt++)
            {
                var dot = _motionDots[_respawnCursor++ % _motionDots.Count];
                if (dot.FadeDirection == 0)
                {
                    dot.FadeDirection = -1;
                    break;
                }
            }
        }

        _lastDotRespawn = now;
    }

    private void RelocateDot(MotionCueDot dot)
    {
        double baseDiameter = RenderHelper.MotionDotDiameter(_overlayConfig.Size);
        double diameter = baseDiameter * (0.55 + _motionRandom.NextDouble() * 1.05);
        Point? candidate = FindSeparatedDotPosition(
            Width,
            Height,
            diameter,
            _lastMotionDirection,
            dot);
        if (!candidate.HasValue)
            return;

        Point position = candidate.Value;
        EdgeSide edge = GetNearestEdge(position, Width, Height);
        dot.Shape.Width = diameter;
        dot.Shape.Height = diameter;
        dot.BasePosition = new Point(position.X - diameter / 2, position.Y - diameter / 2);
        dot.Edge = edge;
        Canvas.SetLeft(dot.Shape, dot.BasePosition.X);
        Canvas.SetTop(dot.Shape, dot.BasePosition.Y);
    }

    private void UpdateAdaptiveColors(bool force)
    {
        try
        {
            int generation = _motionGeneration;
            MotionCueDot[] dots = _motionDots.ToArray();
            double dpiScale = Win32Interop.GetDpiScale();
            var samplePoints = new List<(int X, int Y)>(4);
            for (int row = 0; row < 2; row++)
            {
                for (int column = 0; column < 2; column++)
                {
                    double x = Width * ((column + 0.5) / 2.0);
                    double y = Height * ((row + 0.5) / 2.0);
                    samplePoints.Add((
                        (int)Math.Round(x * dpiScale),
                        (int)Math.Round(y * dpiScale)));
                }
            }

            var sampledColors = Win32Interop.SampleScreenColors(samplePoints);
            if (generation != _motionGeneration)
                return;

            for (int index = 0; index < dots.Length; index++)
            {
                var dot = dots[index];
                double centerX = dot.BasePosition.X + dot.Shape.Width / 2 + _motionOffset.X;
                double centerY = dot.BasePosition.Y + dot.Shape.Height / 2 + _motionOffset.Y;
                int column = Math.Clamp((int)(centerX / Math.Max(1, Width) * 2), 0, 1);
                int row = Math.Clamp((int)(centerY / Math.Max(1, Height) * 2), 0, 1);
                var sampled = sampledColors[row * 2 + column];
                if (!sampled.HasValue)
                    continue;

                var color = sampled.Value;
                double luminance = RelativeLuminance(color.R, color.G, color.B);
                if (force)
                    dot.UsesLightColor = luminance < 0.5;
                else if (dot.UsesLightColor && luminance > 0.64)
                    dot.UsesLightColor = false;
                else if (!dot.UsesLightColor && luminance < 0.36)
                    dot.UsesLightColor = true;

                dot.TargetColor = dot.UsesLightColor ? Colors.White : Colors.Black;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Adaptive color sampling failed: {ex.Message}");
        }
    }

    private static double RelativeLuminance(byte red, byte green, byte blue)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(red) + 0.7152 * Linear(green) + 0.0722 * Linear(blue);
    }

    private static byte LerpByte(byte from, byte to, double amount)
    {
        if (from == to)
            return from;

        int value = (int)Math.Round(from + (to - from) * amount);
        if (value == from)
            value += Math.Sign(to - from);
        return (byte)Math.Clamp(value, 0, 255);
    }

    private void RenderClock(double sw, double sh)
    {
        _clockText = new TextBlock
        {
            FontFamily = new FontFamily(_clockConfig.GetRenderFontFamily()),
            FontSize = _clockConfig.FontSize,
            Foreground = new SolidColorBrush(Color.FromArgb(
                RenderHelper.OpacityToByte(_clockConfig.Opacity),
                _clockConfig.GetColor().R,
                _clockConfig.GetColor().G,
                _clockConfig.GetColor().B)),
            // Nearly-invisible background (alpha=1) so the entire TextBlock bounding box
            // is hit-testable by the OS compositor on AllowsTransparency windows.
            // Without this, only the text glyphs themselves receive mouse clicks.
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Padding = new Thickness(8)
        };

        // Apply built-in outline effect when the Outline pseudo-font is selected
        if (_clockConfig.IsOutlineFont)
        {
            _clockText.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 1
            };
        }

        Canvas.SetLeft(_clockText, _clockConfig.PositionX);
        Canvas.SetTop(_clockText, _clockConfig.PositionY);
        OverlayCanvas.Children.Add(_clockText);

        UpdateClock();
    }

    private void UpdateClock()
    {
        if (_clockText == null || !_clockConfig.IsVisible) return;

        var now = DateTime.Now;
        var text = _clockConfig.Format switch
        {
            ClockFormat.HHmm => now.ToString("HH:mm"),
            ClockFormat.HHmmss => now.ToString("HH:mm:ss"),
            // am/pm h:mm — 12-hour clock, midnight/noon shown as 12
            ClockFormat.HhMmAmPm => $"{now:tt} {now:hh}:{now:mm}",
            _ => now.ToString("HH:mm")
        };
        _clockText.Text = text;
    }

    /// <summary>
    /// Enable clock dragging via cursor tracking.
    /// The clock follows the mouse cursor; left-click confirms the position.
    /// The overlay stays fully click-through — no UI is blocked.
    /// </summary>
    public void EnableClockDrag()
    {
        _isClockDragging = true;
        _wasLeftButtonDown = true; // ignore the initial click that started dragging

        // Set offset to zero so the clock teleports to the cursor position
        _clockDragOffset = new Point(0, 0);

        // Immediately move the clock to the current cursor position
        if (_clockText != null && Win32Interop.GetCursorPos(out var pt))
        {
            double x = pt.X, y = pt.Y;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var m = source.CompositionTarget.TransformFromDevice;
                var logical = m.Transform(new Point(pt.X, pt.Y));
                x = logical.X;
                y = logical.Y;
            }
            _clockConfig.PositionX = (int)x;
            _clockConfig.PositionY = (int)y;
            Canvas.SetLeft(_clockText, _clockConfig.PositionX);
            Canvas.SetTop(_clockText, _clockConfig.PositionY);
        }

        _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _dragTimer.Tick += DragTimer_Tick;
        _dragTimer.Start();
    }

    private void DragTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isClockDragging || _clockText == null) return;

        // Check for left mouse button click to confirm
        bool leftDown = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_LBUTTON) & 0x8000) != 0;
        if (!leftDown && _wasLeftButtonDown)
        {
            // Left button was released → this is a click → confirm position
            _wasLeftButtonDown = false;
            // Slight delay to avoid immediate re-trigger
            return;
        }
        if (leftDown && !_wasLeftButtonDown)
        {
            // New left click → confirm
            DisableClockDrag();
            return;
        }
        _wasLeftButtonDown = leftDown;

        // Check Escape to cancel
        if ((Win32Interop.GetAsyncKeyState(Win32Interop.VK_ESCAPE) & 0x8000) != 0)
        {
            DisableClockDrag();
            return;
        }

        // Move clock to follow cursor
        if (Win32Interop.GetCursorPos(out var pt))
        {
            double x = pt.X, y = pt.Y;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var m = source.CompositionTarget.TransformFromDevice;
                var logical = m.Transform(new Point(pt.X, pt.Y));
                x = logical.X;
                y = logical.Y;
            }
            _clockConfig.PositionX = (int)(x - _clockDragOffset.X);
            _clockConfig.PositionY = (int)(y - _clockDragOffset.Y);
            Canvas.SetLeft(_clockText, _clockConfig.PositionX);
            Canvas.SetTop(_clockText, _clockConfig.PositionY);
        }
    }

    /// <summary>Disable clock dragging and stop the tracking timer.</summary>
    public void DisableClockDrag()
    {
        _isClockDragging = false;
        _dragTimer?.Stop();
        _dragTimer = null;

        // Notify the MainWindow to restore the ClockPage button state
        App.MainWin?.NotifyClockDragConfirmed();
    }

    /// <summary>Called when the screen resolution may have changed.</summary>
    public void OnScreenResolutionChanged()
    {
        UpdateScreenBounds();
        Render();
    }
}
