using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MotionStabilizer.Models;
using MotionStabilizer.Services;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using DXGIAlphaMode = Vortice.DXGI.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using static Vortice.Direct2D1.D2D1;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DirectComposition.DComp;

namespace MotionStabilizer.Overlay;

/// <summary>
/// Draws motion cues through Direct2D into a DirectComposition swap chain.
/// The native overlay is completely click-through and owns no WPF visuals.
/// </summary>
internal sealed class DirectCompositionMotionRenderer : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int GwlExStyle = -20;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;
    private const uint LwaAlpha = 0x00000002;
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;
    private const uint PowerThrottlingIgnoreTimerResolution = 0x4;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly System.Threading.Timer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<NativeDot> _dots = new();
    private readonly Random _random = new(0x51A8);

    private MotionOverlayNativeWindow? _window;
    private RawInputNativeWindow? _rawInputWindow;
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDXGISwapChain1? _swapChain;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private ID2D1Bitmap1? _targetBitmap;
    private ID2D1SolidColorBrush? _brush;
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionVisual;

    private OverlayConfig _config = new();
    private TimeSpan _lastFrame;
    private TimeSpan _lastPixelFrame;
    private TimeSpan _lastMotionCommit;
    private TimeSpan _lastMouseInput;
    private TimeSpan _lastInputSample;
    private TimeSpan _nextSpawnAt;
    private Vector2 _offset;
    private Vector2 _inputVelocity;
    private int _width;
    private int _height;
    private int _minimumDotCount;
    private int _maximumDotCount;
    private bool _visible;
    private bool _dirty;
    private bool _disposed;
    private int _layoutDotCount = -1;
    private SizePreset _layoutSize = (SizePreset)(-1);
    private double _frameIntervalMs = 1000.0 / 240.0;
    private double _motionIntervalMs = 1000.0 / 120.0;
    private double _pixelIntervalMs = 1000.0 / 60.0;
    private int _motionTimerPeriodMs = 8;
    private int _pixelTimerPeriodMs = 17;
    private int _currentTimerPeriodMs = 17;
    private int _timerEnabled;
    private int _tickQueued;
    private int _timerResolutionActive;
    private bool _powerThrottlingOverridden;
    private IntPtr _mmcssHandle;

    private sealed class NativeDot
    {
        public Vector2 Position;
        public float Radius;
        public EdgeSide Edge;
        public float Opacity = 1;
        public int FadeDirection;
        public float FadeSpeed = 3;
        public TimeSpan ExpiresAt;
        public bool RemoveAfterFade;
        public bool UsesLightColor = true;
        public Color4 CurrentColor = new(1, 1, 1, 1);
        public Color4 TargetColor = new(1, 1, 1, 1);
    }

    private sealed class MotionOverlayNativeWindow : System.Windows.Forms.NativeWindow, IDisposable
    {
        public MotionOverlayNativeWindow(int width, int height)
        {
            var parameters = new System.Windows.Forms.CreateParams
            {
                Caption = "MotionStabilizer.DirectComposition",
                X = 0,
                Y = 0,
                Width = width,
                Height = height,
                Style = WsPopup,
                ExStyle =
                    WsExTransparent |
                    WsExLayered |
                    WsExToolWindow |
                    WsExNoActivate |
                    WsExNoRedirectionBitmap
            };
            CreateHandle(parameters);
        }

        protected override void WndProc(ref System.Windows.Forms.Message message)
        {
            if (message.Msg == WmNcHitTest)
            {
                message.Result = new IntPtr(HtTransparent);
                return;
            }

            if (message.Msg == WmMouseActivate)
            {
                message.Result = new IntPtr(MaNoActivate);
                return;
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                DestroyHandle();
        }
    }

    private sealed class RawInputNativeWindow : System.Windows.Forms.NativeWindow, IDisposable
    {
        private static readonly IntPtr HwndMessage = new(-3);
        private readonly Action<int, int> _mouseDelta;

        public RawInputNativeWindow(Action<int, int> mouseDelta)
        {
            _mouseDelta = mouseDelta;
            CreateHandle(new System.Windows.Forms.CreateParams
            {
                Caption = "MotionStabilizer.RawInput",
                Parent = HwndMessage
            });
        }

        protected override void WndProc(ref System.Windows.Forms.Message message)
        {
            if (message.Msg == Win32Interop.WM_INPUT &&
                Win32Interop.TryGetRawMouseDelta(
                    message.LParam,
                    out int deltaX,
                    out int deltaY))
            {
                _mouseDelta(deltaX, deltaY);
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                DestroyHandle();
        }
    }

    public bool IsReady { get; private set; }
    public bool IsVisible => _visible && IsReady;

    public DirectCompositionMotionRenderer()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new System.Threading.Timer(
            QueueRenderTick,
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public bool TryInitialize(int width, int height)
    {
        if (IsReady)
            return true;

        try
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            CreateNativeWindow();
            CreateGraphicsResources();
            _rawInputWindow = new RawInputNativeWindow(OnMouseDelta);
            if (!Win32Interop.RegisterRawMouseInput(_rawInputWindow.Handle))
                throw new InvalidOperationException("Unable to register native raw mouse input.");
            IsReady = true;
            DrawAndPresent();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DirectComposition initialization failed: {ex}");
            DisposeGraphicsResources();
            _rawInputWindow?.Dispose();
            _rawInputWindow = null;
            _window?.Dispose();
            _window = null;
            IsReady = false;
            return false;
        }
    }

    public void Configure(OverlayConfig config, int width, int height)
    {
        if (!IsReady)
            return;

        bool sizeChanged = width != _width || height != _height;
        bool dotLayoutChanged =
            sizeChanged ||
            config.MotionDotCount != _layoutDotCount ||
            config.Size != _layoutSize;

        _config = config;
        int refreshRate = Math.Clamp(config.MotionRefreshRate, 30, 360);
        _frameIntervalMs = 1000.0 / refreshRate;
        _motionIntervalMs = Math.Max(_frameIntervalMs, 1000.0 / 120.0);
        // Opacity/color changes require redrawing the full transparent surface.
        // Keep those inexpensive while composition-only mouse motion remains
        // independently capped at 120 Hz for responsiveness.
        _pixelIntervalMs = Math.Max(_frameIntervalMs, 1000.0 / 30.0);
        _motionTimerPeriodMs = Math.Max(1, (int)Math.Round(_motionIntervalMs));
        _pixelTimerPeriodMs = Math.Max(1, (int)Math.Round(_pixelIntervalMs));
        _currentTimerPeriodMs = _pixelTimerPeriodMs;
        if (Volatile.Read(ref _timerEnabled) != 0)
            _timer.Change(_currentTimerPeriodMs, _currentTimerPeriodMs);
        if (sizeChanged)
            Resize(width, height);
        if (dotLayoutChanged || _dots.Count == 0)
            CreateDots();

        SetVisible(config.IsVisible && config.Shape == OverlayShape.MotionDots);
        _dirty = true;
        SetTimerCadence(motionActive: false, pixelsActive: true);
        EnsureTimer();
    }

    public void SetVisible(bool visible)
    {
        if (!IsReady || _window == null || _visible == visible)
            return;

        _visible = visible;
        if (visible)
        {
            DisableBackgroundThrottling();
            EnableMultimediaScheduling();
        }
        else
        {
            DisableMultimediaScheduling();
            RestoreSystemPowerThrottling();
        }
        EnsureClickThroughStyles(_window.Handle);
        ShowWindow(_window.Handle, visible ? SwShowNoActivate : SwHide);
        if (visible)
        {
            SetWindowPos(
                _window.Handle,
                HwndTopmost,
                0,
                0,
                _width,
                _height,
                SwpNoActivate | SwpShowWindow);
            _dirty = true;
            EnsureTimer();
        }
        else
        {
            StopTimer();
            _offset = Vector2.Zero;
            _inputVelocity = Vector2.Zero;
            ApplyCompositionOffset(force: true);
        }
    }

    public void OnMouseDelta(int deltaX, int deltaY)
    {
        if (!IsVisible)
            return;

        var delta = new Vector2(
            deltaX,
            _config.MotionInvertY ? -deltaY : deltaY);
        float sensitivity = (float)Math.Clamp(_config.MotionSensitivity, 0.05, 3.0);
        Vector2 scaledDelta = delta * sensitivity;
        TimeSpan now = _clock.Elapsed;
        double sampleSeconds = (now - _lastInputSample).TotalSeconds;
        if (sampleSeconds is > 0.0005 and < 0.1)
        {
            Vector2 instantaneousVelocity = scaledDelta / (float)sampleSeconds;
            _inputVelocity = Vector2.Lerp(_inputVelocity, instantaneousVelocity, 0.32f);
        }
        _lastInputSample = now;
        _lastMouseInput = now;
        _offset += scaledDelta;
        float maximum = (float)Math.Clamp(_config.MotionMaxOffset, 8, 240);
        float offsetLength = _offset.Length();
        if (offsetLength > maximum)
            _offset *= maximum / offsetLength;

        ApplyCompositionOffset();
        SetTimerCadence(motionActive: true, pixelsActive: false);
        EnsureTimer();
    }

    public void UpdateAdaptiveColors()
    {
        if (!IsVisible || !_config.MotionAdaptiveColor || _dots.Count == 0)
            return;

        try
        {
            var samplePoints = new List<(int X, int Y)>(4);
            for (int row = 0; row < 2; row++)
            {
                for (int column = 0; column < 2; column++)
                {
                    samplePoints.Add((
                        (int)Math.Round(_width * ((column + 0.5) / 2.0)),
                        (int)Math.Round(_height * ((row + 0.5) / 2.0))));
                }
            }

            var samples = Win32Interop.SampleScreenColors(samplePoints);
            foreach (NativeDot dot in _dots)
            {
                float centerX = dot.Position.X + _offset.X;
                float centerY = dot.Position.Y + _offset.Y;
                int column = Math.Clamp((int)(centerX / Math.Max(1, _width) * 2), 0, 1);
                int row = Math.Clamp((int)(centerY / Math.Max(1, _height) * 2), 0, 1);
                var sample = samples[row * 2 + column];
                if (!sample.HasValue)
                    continue;

                var color = sample.Value;
                double luminance = RelativeLuminance(color.R, color.G, color.B);
                if (dot.UsesLightColor && luminance > 0.64)
                    dot.UsesLightColor = false;
                else if (!dot.UsesLightColor && luminance < 0.36)
                    dot.UsesLightColor = true;
                dot.TargetColor = dot.UsesLightColor
                    ? new Color4(1, 1, 1, 1)
                    : new Color4(0, 0, 0, 1);
            }

            _dirty = true;
            EnsureTimer();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DirectComposition adaptive color failed: {ex.Message}");
        }
    }

    private void CreateNativeWindow()
    {
        _window = new MotionOverlayNativeWindow(_width, _height);
        EnsureClickThroughStyles(_window.Handle);
        SetLayeredWindowAttributes(_window.Handle, 0, 255, LwaAlpha);
    }

    private void CreateGraphicsResources()
    {
        D3DFeatureLevel[] featureLevels =
        [
            D3DFeatureLevel.Level_11_1,
            D3DFeatureLevel.Level_11_0,
            D3DFeatureLevel.Level_10_1,
            D3DFeatureLevel.Level_10_0
        ];

        D3D11CreateDevice(
            (IDXGIAdapter?)null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out _d3dDevice,
            out _,
            out _d3dContext).CheckError();

        using IDXGIDevice dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        var swapChainDescription = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = DXGIAlphaMode.Premultiplied,
            Flags = SwapChainFlags.None
        };
        _swapChain = factory.CreateSwapChainForComposition(
            _d3dDevice,
            swapChainDescription,
            null);

        _d2dFactory = D2D1CreateFactory<ID2D1Factory1>(
            FactoryType.SingleThreaded,
            DebugLevel.None);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        CreateDirect2DTarget();
        _brush = _d2dContext.CreateSolidColorBrush(new Color4(1, 1, 1, 1));

        _compositionDevice = DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _compositionDevice.CreateTargetForHwnd(
            _window!.Handle,
            true,
            out _compositionTarget).CheckError();
        _compositionDevice.CreateVisual(out _compositionVisual).CheckError();
        _compositionVisual.SetContent(_swapChain).CheckError();
        _compositionTarget.SetRoot(_compositionVisual).CheckError();
        _compositionDevice.Commit().CheckError();
    }

    private void CreateDirect2DTarget()
    {
        using IDXGISurface surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        var properties = new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
            96,
            96,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _targetBitmap = _d2dContext!.CreateBitmapFromDxgiSurface(surface, properties);
        _d2dContext.Target = _targetBitmap;
    }

    private void Resize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        if (_window != null)
        {
            SetWindowPos(
                _window.Handle,
                HwndTopmost,
                0,
                0,
                _width,
                _height,
                SwpNoActivate | (_visible ? SwpShowWindow : 0));
        }

        _d2dContext!.Target = null;
        _targetBitmap?.Dispose();
        _targetBitmap = null;
        _swapChain!.ResizeBuffers(
            2,
            (uint)_width,
            (uint)_height,
            Format.B8G8R8A8_UNorm,
            SwapChainFlags.None).CheckError();
        CreateDirect2DTarget();
    }

    private void CreateDots()
    {
        _dots.Clear();
        _offset = Vector2.Zero;
        _inputVelocity = Vector2.Zero;
        _layoutDotCount = _config.MotionDotCount;
        _layoutSize = _config.Size;

        int nominalCount = Math.Clamp(_config.MotionDotCount * 8, 24, 96);
        _minimumDotCount = Math.Max(12, (int)Math.Floor(nominalCount * 0.72));
        _maximumDotCount = Math.Min(112, (int)Math.Ceiling(nominalCount * 1.18));
        int initialCount = _random.Next(_minimumDotCount, _maximumDotCount + 1);
        int attempts = 0;
        while (_dots.Count < initialCount && attempts++ < initialCount * 16)
        {
            NativeDot? dot = CreateRandomDot(fadeIn: true);
            if (dot != null)
                _dots.Add(dot);
        }
        ScheduleNextSpawn(_clock.Elapsed);
    }

    private NativeDot? CreateRandomDot(bool fadeIn)
    {
        float baseDiameter = (float)(
            RenderHelper.MotionDotDiameter(_config.Size) *
            Win32Interop.GetDpiScale());
        float radius = baseDiameter * (float)(0.55 + _random.NextDouble() * 1.05) / 2;
        Vector2? position = FindSeparatedPosition(radius, null);
        if (!position.HasValue)
            return null;

        var dot = new NativeDot
        {
            Position = position.Value,
            Radius = radius,
            Edge = GetNearestEdge(position.Value + _offset),
            Opacity = fadeIn ? 0 : 1,
            FadeDirection = fadeIn ? 1 : 0,
            FadeSpeed = RandomFadeSpeed(),
            CurrentColor = ToColor4(_config.GetColor()),
            TargetColor = ToColor4(_config.GetColor())
        };
        ResetLifetime(dot, _clock.Elapsed);
        return dot;
    }

    private Vector2? FindSeparatedPosition(float radius, NativeDot? excluded)
    {
        float screenMarginX = Math.Max(radius + 20, _width * 0.04f);
        float screenMarginY = Math.Max(radius + 20, _height * 0.05f);
        float minimumX = Math.Max(radius + 2, screenMarginX - _offset.X);
        float maximumX = Math.Min(_width - radius - 2, _width - screenMarginX - _offset.X);
        float minimumY = Math.Max(radius + 2, screenMarginY - _offset.Y);
        float maximumY = Math.Min(_height - radius - 2, _height - screenMarginY - _offset.Y);
        if (maximumX <= minimumX || maximumY <= minimumY)
            return null;
        float extraGap = Math.Max(8, radius * 0.65f);

        for (int attempt = 0; attempt < 80; attempt++)
        {
            var candidate = new Vector2(
                minimumX + (float)_random.NextDouble() * (maximumX - minimumX),
                minimumY + (float)_random.NextDouble() * (maximumY - minimumY));
            bool overlaps = false;
            foreach (NativeDot existing in _dots)
            {
                if (ReferenceEquals(existing, excluded))
                    continue;
                float minimum = radius + existing.Radius + extraGap;
                if (Vector2.DistanceSquared(candidate, existing.Position) < minimum * minimum)
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

    private void Timer_Tick()
    {
        if (!IsVisible)
        {
            StopTimer();
            return;
        }

        TimeSpan now = _clock.Elapsed;
        float dt = (float)Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.05);
        _lastFrame = now;

        // Do not let return-to-center fight active input at the travel limit.
        // That clamp/decay loop was the source of the visible edge jitter.
        if ((now - _lastMouseInput).TotalMilliseconds >= 45)
        {
            float returnSeconds =
                (float)Math.Clamp(_config.MotionReturnMs, 80, 1200) / 1000f;
            _offset *= MathF.Exp(-dt / returnSeconds);
        }
        if (_offset.LengthSquared() < 0.0025f)
            _offset = Vector2.Zero;
        _inputVelocity *= MathF.Exp(-dt / 0.045f);
        if (_inputVelocity.LengthSquared() < 1)
            _inputVelocity = Vector2.Zero;
        ApplyCompositionOffset();

        StartEdgeFades();
        StartExpiredFades(now);
        bool spawnedDot = TrySpawnDot(now);

        bool motionActive =
            _offset != Vector2.Zero ||
            _inputVelocity != Vector2.Zero ||
            (now - _lastMouseInput).TotalMilliseconds < 80;
        bool pixelsChanging = spawnedDot;
        var removals = new List<NativeDot>();
        foreach (NativeDot dot in _dots)
        {
            if (dot.FadeDirection != 0)
            {
                dot.Opacity = Math.Clamp(
                    dot.Opacity + dot.FadeDirection * dt * dot.FadeSpeed,
                    0,
                    1);
                if (dot.FadeDirection < 0 && dot.Opacity <= 0)
                {
                    if (dot.RemoveAfterFade &&
                        _dots.Count - removals.Count > _minimumDotCount)
                    {
                        removals.Add(dot);
                    }
                    else if (Relocate(dot))
                    {
                        dot.RemoveAfterFade = false;
                        dot.FadeDirection = 1;
                        dot.FadeSpeed = RandomFadeSpeed();
                        ResetLifetime(dot, now);
                    }
                }
                else if (dot.FadeDirection > 0 && dot.Opacity >= 1)
                {
                    dot.FadeDirection = 0;
                }
                pixelsChanging = true;
            }

            Color4 target = _config.MotionAdaptiveColor
                ? dot.TargetColor
                : ToColor4(_config.GetColor());
            float follow = 1 - MathF.Exp(-dt / 0.14f);
            dot.CurrentColor = Lerp(dot.CurrentColor, target, follow);
            if (!ColorsClose(dot.CurrentColor, target))
                pixelsChanging = true;
        }

        if (removals.Count > 0)
        {
            foreach (NativeDot dot in removals)
                _dots.Remove(dot);
            pixelsChanging = true;
        }

        if ((_dirty || pixelsChanging) &&
            (now - _lastPixelFrame).TotalMilliseconds >= _pixelIntervalMs)
        {
            DrawAndPresent();
            _lastPixelFrame = now;
            _dirty = false;
        }
        SetTimerCadence(motionActive, pixelsChanging || _dirty);
    }

    private void StartExpiredFades(TimeSpan now)
    {
        foreach (NativeDot dot in _dots)
        {
            if (dot.FadeDirection == 0 && now >= dot.ExpiresAt)
            {
                dot.RemoveAfterFade =
                    _dots.Count > _minimumDotCount &&
                    _random.NextDouble() < 0.42;
                dot.FadeSpeed = RandomFadeSpeed();
                dot.FadeDirection = -1;
            }
        }
    }

    private bool TrySpawnDot(TimeSpan now)
    {
        if (now < _nextSpawnAt)
            return false;

        bool spawned = false;
        if (_dots.Count < _maximumDotCount &&
            (_dots.Count < _minimumDotCount || _random.NextDouble() < 0.68))
        {
            NativeDot? dot = CreateRandomDot(fadeIn: true);
            if (dot != null)
            {
                _dots.Add(dot);
                spawned = true;
            }
        }

        ScheduleNextSpawn(now);
        return spawned;
    }

    private void StartEdgeFades()
    {
        foreach (NativeDot dot in _dots)
        {
            if (dot.FadeDirection != 0)
                continue;

            Vector2 center = dot.Position + _offset;
            float protectedX = Math.Max(dot.Radius + 6, _width * 0.018f);
            float protectedY = Math.Max(dot.Radius + 6, _height * 0.022f);
            if (center.X < protectedX ||
                center.X > _width - protectedX ||
                center.Y < protectedY ||
                center.Y > _height - protectedY)
            {
                dot.RemoveAfterFade = false;
                dot.FadeSpeed = RandomFadeSpeed();
                dot.FadeDirection = -1;
            }
        }
    }

    private bool Relocate(NativeDot dot)
    {
        float baseDiameter = (float)(
            RenderHelper.MotionDotDiameter(_config.Size) *
            Win32Interop.GetDpiScale());
        float radius = baseDiameter * (float)(0.55 + _random.NextDouble() * 1.05) / 2;
        Vector2? position = FindSeparatedPosition(radius, dot);
        if (!position.HasValue)
            return false;

        dot.Position = position.Value;
        dot.Radius = radius;
        dot.Edge = GetNearestEdge(position.Value + _offset);
        return true;
    }

    private void ResetLifetime(NativeDot dot, TimeSpan now)
    {
        double lifetimeSeconds = 4.0 + _random.NextDouble() * 8.0;
        dot.ExpiresAt = now + TimeSpan.FromSeconds(lifetimeSeconds);
    }

    private float RandomFadeSpeed() =>
        (float)(1.8 + _random.NextDouble() * 1.4);

    private void ScheduleNextSpawn(TimeSpan now)
    {
        double delaySeconds = 0.8 + _random.NextDouble() * 1.7;
        _nextSpawnAt = now + TimeSpan.FromSeconds(delaySeconds);
    }

    private void DrawAndPresent()
    {
        if (_d2dContext == null || _brush == null || _swapChain == null)
            return;

        _d2dContext.BeginDraw();
        _d2dContext.Clear(new Color4(0, 0, 0, 0));
        foreach (NativeDot dot in _dots)
        {
            if (!_config.IsEdgeVisible(dot.Edge))
                continue;

            float edgeOpacity = _config.GetEdgeOpacity(dot.Edge) / 100f;
            _brush.Color = new Color4(
                dot.CurrentColor.R,
                dot.CurrentColor.G,
                dot.CurrentColor.B,
                1);
            _brush.Opacity = Math.Clamp(edgeOpacity * dot.Opacity, 0, 1);
            _d2dContext.FillEllipse(
                new Ellipse(dot.Position, dot.Radius, dot.Radius),
                _brush);
        }
        _d2dContext.EndDraw(out _, out _).CheckError();
        // DoNotWait silently drops frames when DWM applies stronger background
        // throttling. Let DXGI queue the frame so fades remain continuous even
        // while the settings window is hidden or another app has focus.
        _swapChain.Present(0, PresentFlags.None).CheckError();
    }

    private EdgeSide GetNearestEdge(Vector2 position)
    {
        float nearest = Math.Min(
            Math.Min(position.Y, _height - position.Y),
            Math.Min(position.X, _width - position.X));
        if (nearest == position.Y) return EdgeSide.Top;
        if (nearest == _height - position.Y) return EdgeSide.Bottom;
        if (nearest == position.X) return EdgeSide.Left;
        return EdgeSide.Right;
    }

    private void EnsureTimer()
    {
        if (Interlocked.Exchange(ref _timerEnabled, 1) != 0)
            return;
        if (Interlocked.Exchange(ref _timerResolutionActive, 1) == 0)
            TimeBeginPeriod(1);
        _lastFrame = _clock.Elapsed;
        _timer.Change(0, _currentTimerPeriodMs);
    }

    private void StopTimer()
    {
        if (Interlocked.Exchange(ref _timerEnabled, 0) == 0)
            return;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        if (Interlocked.Exchange(ref _timerResolutionActive, 0) != 0)
            TimeEndPeriod(1);
    }

    private void QueueRenderTick(object? state)
    {
        if (_disposed ||
            Volatile.Read(ref _timerEnabled) == 0 ||
            Interlocked.Exchange(ref _tickQueued, 1) != 0)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    Interlocked.Exchange(ref _tickQueued, 0);
                    if (!_disposed && Volatile.Read(ref _timerEnabled) != 0)
                        Timer_Tick();
                }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _tickQueued, 0);
            StopTimer();
        }
    }

    private void SetTimerCadence(bool motionActive, bool pixelsActive)
    {
        int period = motionActive
            ? _motionTimerPeriodMs
            : pixelsActive
                ? _pixelTimerPeriodMs
                : 33;
        if (_currentTimerPeriodMs != period)
        {
            _currentTimerPeriodMs = period;
            if (Volatile.Read(ref _timerEnabled) != 0)
                _timer.Change(period, period);
        }

        if (motionActive || pixelsActive)
        {
            if (Interlocked.Exchange(ref _timerResolutionActive, 1) == 0)
                TimeBeginPeriod(1);
        }
        else if (Interlocked.Exchange(ref _timerResolutionActive, 0) != 0)
        {
            TimeEndPeriod(1);
        }
    }

    private static void EnsureClickThroughStyles(IntPtr window)
    {
        long styles = GetWindowLongPtrCompat(window, GwlExStyle).ToInt64();
        styles |= WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate;
        SetWindowLongPtrCompat(window, GwlExStyle, new IntPtr(styles));
    }

    private void DisableBackgroundThrottling()
    {
        IntPtr process = GetCurrentProcess();

        var executionState = new ProcessPowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingExecutionSpeed,
            StateMask = 0
        };
        bool executionConfigured = SetProcessInformation(
            process,
            ProcessPowerThrottling,
            ref executionState,
            (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());

        var timerState = new ProcessPowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingIgnoreTimerResolution,
            StateMask = 0
        };
        bool timerConfigured = SetProcessInformation(
            process,
            ProcessPowerThrottling,
            ref timerState,
            (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());

        _powerThrottlingOverridden = executionConfigured || timerConfigured;
    }

    private void RestoreSystemPowerThrottling()
    {
        if (!_powerThrottlingOverridden)
            return;

        var state = new ProcessPowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = 0,
            StateMask = 0
        };
        SetProcessInformation(
            GetCurrentProcess(),
            ProcessPowerThrottling,
            ref state,
            (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
        _powerThrottlingOverridden = false;
    }

    private void EnableMultimediaScheduling()
    {
        if (_mmcssHandle != IntPtr.Zero)
            return;

        uint taskIndex = 0;
        _mmcssHandle = AvSetMmThreadCharacteristics("Games", ref taskIndex);
        if (_mmcssHandle != IntPtr.Zero)
            AvSetMmThreadPriority(_mmcssHandle, 1);
    }

    private void DisableMultimediaScheduling()
    {
        if (_mmcssHandle == IntPtr.Zero)
            return;

        AvRevertMmThreadCharacteristics(_mmcssHandle);
        _mmcssHandle = IntPtr.Zero;
    }

    private void ApplyCompositionOffset(bool force = false)
    {
        if (_compositionVisual == null || _compositionDevice == null)
            return;

        TimeSpan now = _clock.Elapsed;
        if (!force &&
            (now - _lastMotionCommit).TotalMilliseconds < _motionIntervalMs)
        {
            return;
        }

        Vector2 displayedOffset = _offset;
        if ((now - _lastMouseInput).TotalMilliseconds < 55)
        {
            Vector2 prediction = _inputVelocity * 0.012f;
            float predictionLength = prediction.Length();
            if (predictionLength > 18)
                prediction *= 18 / predictionLength;
            displayedOffset += prediction;
        }

        float maximum = (float)Math.Clamp(_config.MotionMaxOffset, 8, 240);
        float displayedLength = displayedOffset.Length();
        if (displayedLength > maximum)
            displayedOffset *= maximum / displayedLength;

        _compositionVisual.SetOffsetX(displayedOffset.X).CheckError();
        _compositionVisual.SetOffsetY(displayedOffset.Y).CheckError();
        _compositionDevice.Commit().CheckError();
        _lastMotionCommit = now;
    }

    private static Color4 ToColor4(System.Windows.Media.Color color) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f, 1);

    private static Color4 Lerp(Color4 current, Color4 target, float amount) =>
        new(
            current.R + (target.R - current.R) * amount,
            current.G + (target.G - current.G) * amount,
            current.B + (target.B - current.B) * amount,
            1);

    private static bool ColorsClose(Color4 first, Color4 second) =>
        Math.Abs(first.R - second.R) < 0.002f &&
        Math.Abs(first.G - second.G) < 0.002f &&
        Math.Abs(first.B - second.B) < 0.002f;

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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopTimer();
        _timer.Dispose();
        SetVisible(false);
        RestoreSystemPowerThrottling();
        DisposeGraphicsResources();
        _rawInputWindow?.Dispose();
        _rawInputWindow = null;
        _window?.Dispose();
        _window = null;
        GC.SuppressFinalize(this);
    }

    private void DisposeGraphicsResources()
    {
        if (_d2dContext != null)
            _d2dContext.Target = null;
        _brush?.Dispose();
        _targetBitmap?.Dispose();
        _d2dContext?.Dispose();
        _d2dDevice?.Dispose();
        _d2dFactory?.Dispose();
        _compositionVisual?.Dispose();
        _compositionTarget?.Dispose();
        _compositionDevice?.Dispose();
        _swapChain?.Dispose();
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();
        _brush = null;
        _targetBitmap = null;
        _d2dContext = null;
        _d2dDevice = null;
        _d2dFactory = null;
        _compositionVisual = null;
        _compositionTarget = null;
        _compositionDevice = null;
        _swapChain = null;
        _d3dContext = null;
        _d3dDevice = null;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int informationClass,
        ref ProcessPowerThrottlingState information,
        uint informationSize);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(
        string taskName,
        ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvSetMmThreadPriority(
        IntPtr avrtHandle,
        int priority);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr window,
        uint colorKey,
        byte alpha,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    private static IntPtr GetWindowLongPtrCompat(IntPtr window, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPtrCompat(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
