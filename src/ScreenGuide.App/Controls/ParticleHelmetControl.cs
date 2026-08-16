using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenGuide.App.Controls;

public enum ParticleHelmetState
{
    Offline,
    Waiting,
    Listening,
    Recognized,
    Thinking,
    Speaking,
    Alert
}

/// <summary>
/// Renders an original sci-fi helmet as a real-time point cloud. The control never
/// displays the branded bitmap: every visible pixel is generated from particles.
/// </summary>
public sealed class ParticleHelmetControl : FrameworkElement
{
    private const double UpperHelmetCompression = 0.86;
    private const double LowerHelmetCompression = 0.76;

    private enum ParticleKind
    {
        Armor,
        HotArmor,
        OuterContour,
        PanelSeam,
        Eye,
        Ambient
    }

    private sealed record Particle(
        double TargetX,
        double TargetY,
        ParticleKind Kind,
        double StartAngle,
        double StartRadius,
        double Delay,
        double Size,
        double Phase,
        double Spin);

    private readonly record struct PlateSample(int Offset, byte B, byte G, byte R, byte A);

    private static readonly Rgb ArmorRed = new(222, 31, 57);
    private static readonly Rgb HotOrange = new(255, 101, 54);
    private static readonly Rgb PanelGold = new(194, 140, 55);
    private static readonly Rgb PanelGoldLight = new(238, 198, 105);
    private static readonly Rgb ReactorCyan = new(72, 231, 255);
    private static readonly Rgb SignalSilver = new(205, 232, 244);
    private static readonly Rgb DeepArmor = new(24, 5, 11);

    private readonly Random _random = new(0x4A_52_56);
    private readonly List<Particle> _particles = [];
    private readonly List<PlateSample> _plateSamples = [];
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = [];
    private int _pixelWidth;
    private int _pixelHeight;
    private int _stride;
    private bool _isRendering;
    private TimeSpan _lastFrameTime;
    private DateTime _assemblyStartedUtc = DateTime.UtcNow;
    private double _assemblyDurationSeconds = 2.25;
    private DateTime _stateStartedUtc = DateTime.UtcNow;
    private DateTime _dissolveStartedUtc;
    private bool _isDissolving;
    private bool _assemblyFromScreenEdges;
    private double _renderResolutionScale = 1;
    private ParticleHelmetState _state = ParticleHelmetState.Offline;

    public int ParticleCount { get; set; } = 2600;

    public bool Compact { get; set; }

    public ParticleHelmetControl()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => StartRendering();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                TriggerAssembly(Compact ? 0.65 : 1.55);
            }
        };
        SizeChanged += (_, _) => RecreateBitmap();
    }

    public void SetState(ParticleHelmetState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetState(state));
            return;
        }

        var previous = _state;
        _state = state;
        if (previous != state)
        {
            _stateStartedUtc = DateTime.UtcNow;
        }
        _isDissolving = false;

        if (state == ParticleHelmetState.Listening && previous != ParticleHelmetState.Listening)
        {
            TriggerAssembly(1.05);
        }
        else if (state == ParticleHelmetState.Waiting && previous == ParticleHelmetState.Offline)
        {
            TriggerAssembly(1.65);
        }
    }

    public void TriggerAssembly(double durationSeconds = 2.25, bool fromScreenEdges = false)
    {
        _assemblyDurationSeconds = Math.Max(0.35, durationSeconds);
        _assemblyStartedUtc = DateTime.UtcNow;
        _assemblyFromScreenEdges = fromScreenEdges;
        _isDissolving = false;
    }

    public void PlayDissolve()
    {
        _dissolveStartedUtc = DateTime.UtcNow;
        _isDissolving = true;
    }

    public void CancelDissolve()
    {
        _isDissolving = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_bitmap is not null)
        {
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            drawingContext.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        }
    }

    private void StartRendering()
    {
        if (_isRendering)
        {
            return;
        }

        EnsureParticles();
        RecreateBitmap();
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        _isRendering = true;
    }

    private void StopRendering()
    {
        if (!_isRendering)
        {
            return;
        }

        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _isRendering = false;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (!IsVisible || ActualWidth < 2 || ActualHeight < 2)
        {
            return;
        }

        var renderingArgs = e as RenderingEventArgs;
        var renderingTime = renderingArgs?.RenderingTime ?? TimeSpan.FromTicks(DateTime.UtcNow.Ticks);
        var minimumFrame = Compact ? TimeSpan.FromMilliseconds(40) : TimeSpan.FromMilliseconds(28);
        if (renderingTime - _lastFrameTime < minimumFrame)
        {
            return;
        }

        _lastFrameTime = renderingTime;
        RenderFrame(DateTime.UtcNow);
    }

    private void EnsureParticles()
    {
        if (_particles.Count > 0)
        {
            return;
        }

        var fillCount = (int)(ParticleCount * 0.43);
        var contourCount = (int)(ParticleCount * 0.30);
        var eyeCount = (int)(ParticleCount * 0.07);
        var ambientCount = Math.Max(40, ParticleCount - fillCount - contourCount - eyeCount);

        AddHelmetFill(fillCount);
        AddHelmetContour(contourCount);
        AddEyes(eyeCount);
        AddAmbientParticles(ambientCount);
    }

    private void AddHelmetFill(int count)
    {
        while (count > 0)
        {
            var x = Next(-0.72, 0.72);
            var y = Next(-1.0, 1.0);
            if (!IsInsideHelmet(x, y))
            {
                continue;
            }

            var inEyeOpening = IsInsideEyeOpening(x, y);
            if (inEyeOpening && _random.NextDouble() < 0.88)
            {
                continue;
            }

            var faceplate = IsInsideGoldFaceplate(x, y);
            // The gold plate is deliberately solid. Particles belong to the red shell,
            // seams and eyes so the face still reads as armor after the animation settles.
            if (faceplate)
            {
                continue;
            }

            if (IsNearPanelSeam(x, y) && _random.NextDouble() < 0.82)
            {
                continue;
            }

            var kind = Math.Abs(x) > 0.49 || y > 0.70
                ? ParticleKind.HotArmor
                : ParticleKind.Armor;
            AddParticle(x, y, kind, Next(0.42, 1.02));
            count--;
        }
    }

    private void AddHelmetContour(int count)
    {
        var outer = new (double X, double Y)[]
        {
            (-0.37, -1.00), (-0.57, -0.84), (-0.66, -0.46), (-0.67, 0.22),
            (-0.54, 0.63), (-0.40, 0.86), (-0.30, 0.98), (0.30, 0.98), (0.40, 0.86),
            (0.54, 0.63), (0.67, 0.22), (0.66, -0.46), (0.57, -0.84), (0.37, -1.00)
        };
        AddPolyline(outer, (int)(count * 0.56), ParticleKind.OuterContour, 0.92, close: false);

        var panelLines = new (double X, double Y)[][]
        {
            [(-0.23, -0.77), (-0.43, -0.58), (-0.52, -0.18)],
            [(0.23, -0.77), (0.43, -0.58), (0.52, -0.18)],
            [(-0.52, 0.13), (-0.44, 0.49), (-0.30, 0.82)],
            [(0.52, 0.13), (0.44, 0.49), (0.30, 0.82)],
            [(-0.40, 0.67), (-0.34, 0.84), (-0.28, 0.91), (0.28, 0.91), (0.34, 0.84), (0.40, 0.67)],
            [(-0.42, -0.50), (0.00, -0.61), (0.42, -0.50)]
        };
        var remaining = Math.Max(1, (int)(count * 0.44) / panelLines.Length);
        foreach (var line in panelLines)
        {
            AddPolyline(line, remaining, ParticleKind.PanelSeam, 0.66, close: false);
        }
    }

    private void AddEyes(int count)
    {
        var eachEye = Math.Max(20, count / 2);
        AddEye(-1, eachEye);
        AddEye(1, eachEye);
    }

    private void AddEye(int side, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var t = _random.NextDouble();
            var innerX = 0.08;
            var outerX = 0.53;
            var x = side * (innerX + (outerX - innerX) * t);
            var centerY = -0.035 - 0.082 * t;
            var thickness = Next(-0.040, 0.040) * Math.Pow(Math.Sin(Math.PI * t), 0.65);
            AddParticle(x, centerY + thickness, ParticleKind.Eye, Next(0.78, 1.38));
        }
    }

    private void AddAmbientParticles(int count)
    {
        for (var index = 0; index < count; index++)
        {
            var angle = Next(0, Math.PI * 2);
            var radius = Next(0.82, 1.27);
            AddParticle(Math.Cos(angle) * radius, Math.Sin(angle) * radius, ParticleKind.Ambient, Next(0.55, 1.25));
        }
    }

    private void AddPolyline(
        IReadOnlyList<(double X, double Y)> points,
        int count,
        ParticleKind kind,
        double size,
        bool close)
    {
        if (points.Count < 2 || count <= 0)
        {
            return;
        }

        var segmentCount = close ? points.Count : points.Count - 1;
        for (var index = 0; index < count; index++)
        {
            var pathPosition = index / (double)Math.Max(1, count - 1) * segmentCount;
            var segment = Math.Min(segmentCount - 1, (int)pathPosition);
            var local = pathPosition - segment;
            var start = points[segment];
            var end = points[(segment + 1) % points.Count];
            var x = Lerp(start.X, end.X, local) + Next(-0.010, 0.010);
            var y = Lerp(start.Y, end.Y, local) + Next(-0.010, 0.010);
            AddParticle(x, y, kind, size * Next(0.72, 1.32));
        }
    }

    private void AddParticle(double x, double y, ParticleKind kind, double size)
    {
        _particles.Add(new Particle(
            x,
            y,
            kind,
            Next(0, Math.PI * 2),
            Next(0.72, 1.42),
            Next(0, 0.34),
            size,
            Next(0, Math.PI * 2),
            Next(-1.6, 1.6)));
    }

    private static bool IsInsideHelmet(double x, double y)
    {
        var absoluteX = Math.Abs(x);
        var halfWidth = y switch
        {
            < -0.84 => 0.37 + ((y + 1.0) / 0.16) * 0.20,
            < -0.25 => 0.57 + ((y + 0.84) / 0.59) * 0.10,
            < 0.24 => 0.67 - ((y + 0.25) / 0.49) * 0.03,
            < 0.68 => 0.64 - ((y - 0.24) / 0.44) * 0.16,
            _ => 0.48 - ((y - 0.68) / 0.32) * 0.14
        };
        return absoluteX <= halfWidth;
    }

    private static bool IsInsideGoldFaceplate(double x, double y)
    {
        var absoluteX = Math.Abs(x);
        var halfWidth = y switch
        {
            < -0.79 => 0.21 + ((y + 1.0) / 0.21) * 0.12,
            < -0.50 => 0.33 + ((y + 0.79) / 0.29) * 0.15,
            < 0.18 => 0.45 + ((y + 0.50) / 0.68) * 0.03,
            < 0.68 => 0.48 - ((y - 0.18) / 0.50) * 0.13,
            _ => 0.35 - ((y - 0.68) / 0.32) * 0.06
        };
        return absoluteX <= halfWidth;
    }

    private static bool IsInsideEyeOpening(double x, double y)
    {
        var absoluteX = Math.Abs(x);
        if (absoluteX is < 0.075 or > 0.545)
        {
            return false;
        }

        var t = (absoluteX - 0.075) / 0.47;
        var centerY = -0.035 - 0.082 * t;
        var halfHeight = 0.018 + 0.048 * Math.Pow(Math.Sin(Math.PI * t), 0.68);
        return Math.Abs(y - centerY) <= halfHeight;
    }

    private static bool IsInsideEyeGlow(double x, double y)
    {
        var absoluteX = Math.Abs(x);
        if (absoluteX is < 0.095 or > 0.525)
        {
            return false;
        }

        var t = (absoluteX - 0.095) / 0.43;
        var centerY = -0.035 - 0.082 * t;
        var halfHeight = 0.010 + 0.028 * Math.Pow(Math.Sin(Math.PI * t), 0.72);
        return Math.Abs(y - centerY) <= halfHeight;
    }

    private static bool IsNearPanelSeam(double x, double y)
    {
        const double tolerance = 0.018;
        return IsNearSegment(x, y, -0.42, -0.50, 0.00, -0.61, tolerance) ||
               IsNearSegment(x, y, 0.00, -0.61, 0.42, -0.50, tolerance) ||
               IsNearSegment(x, y, -0.52, 0.13, -0.44, 0.49, tolerance) ||
               IsNearSegment(x, y, -0.44, 0.49, -0.30, 0.82, tolerance) ||
               IsNearSegment(x, y, 0.52, 0.13, 0.44, 0.49, tolerance) ||
               IsNearSegment(x, y, 0.44, 0.49, 0.30, 0.82, tolerance) ||
               IsNearSegment(x, y, -0.40, 0.67, -0.28, 0.91, tolerance) ||
               IsNearSegment(x, y, -0.28, 0.91, 0.28, 0.91, tolerance) ||
               IsNearSegment(x, y, 0.40, 0.67, 0.28, 0.91, tolerance);
    }

    private static bool IsNearSegment(
        double x,
        double y,
        double startX,
        double startY,
        double endX,
        double endY,
        double tolerance)
    {
        var deltaX = endX - startX;
        var deltaY = endY - startY;
        var lengthSquared = deltaX * deltaX + deltaY * deltaY;
        if (lengthSquared <= double.Epsilon)
        {
            return false;
        }

        var amount = Math.Clamp(((x - startX) * deltaX + (y - startY) * deltaY) / lengthSquared, 0, 1);
        var nearestX = startX + deltaX * amount;
        var nearestY = startY + deltaY * amount;
        var distanceX = x - nearestX;
        var distanceY = y - nearestY;
        return distanceX * distanceX + distanceY * distanceY <= tolerance * tolerance;
    }

    private void RecreateBitmap()
    {
        var requestedWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var requestedHeight = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var renderResolutionScale = requestedWidth > 900 || requestedHeight > 700 ? 0.5 : 1.0;
        var width = Math.Max(1, (int)Math.Ceiling(requestedWidth * renderResolutionScale));
        var height = Math.Max(1, (int)Math.Ceiling(requestedHeight * renderResolutionScale));
        if (width == _pixelWidth && height == _pixelHeight &&
            Math.Abs(renderResolutionScale - _renderResolutionScale) < 0.001)
        {
            return;
        }

        _renderResolutionScale = renderResolutionScale;
        _pixelWidth = width;
        _pixelHeight = height;
        _stride = width * 4;
        _pixels = new byte[_stride * height];
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        BuildBasePlate();
        InvalidateVisual();
    }

    private void BuildBasePlate()
    {
        _plateSamples.Clear();
        if (_pixelWidth < 2 || _pixelHeight < 2)
        {
            return;
        }

        var scale = GetHelmetScale(_pixelWidth, _pixelHeight);
        var centerX = _pixelWidth * 0.5;
        var centerY = _pixelHeight * (Compact ? 0.49 : 0.47);
        var left = Math.Max(0, (int)Math.Floor(centerX - scale * 0.76));
        var right = Math.Min(_pixelWidth - 1, (int)Math.Ceiling(centerX + scale * 0.76));
        var top = Math.Max(0, (int)Math.Floor(centerY - scale * UpperHelmetCompression * 1.02));
        var bottom = Math.Min(_pixelHeight - 1, (int)Math.Ceiling(centerY + scale * LowerHelmetCompression * 1.02));

        for (var pixelY = top; pixelY <= bottom; pixelY++)
        {
            var normalizedY = UnwarpHelmetY((pixelY - centerY) / scale);
            for (var pixelX = left; pixelX <= right; pixelX++)
            {
                var normalizedX = (pixelX - centerX) / scale;
                if (!IsInsideHelmet(normalizedX, normalizedY))
                {
                    continue;
                }

                var absoluteX = Math.Abs(normalizedX);
                var faceplate = IsInsideGoldFaceplate(normalizedX, normalizedY);
                var eyeOpening = IsInsideEyeOpening(normalizedX, normalizedY);
                var eyeGlow = IsInsideEyeGlow(normalizedX, normalizedY);
                var panelSeam = IsNearPanelSeam(normalizedX, normalizedY);
                var foreheadInset = normalizedY is > -0.93 and < -0.57 &&
                                    absoluteX < 0.16 + (normalizedY + 0.93) * 0.36;
                var sideArmor = absoluteX > 0.48;
                var crown = normalizedY < -0.52;

                var color = faceplate
                    ? PanelGold
                    : crown
                        ? new Rgb(151, 18, 38)
                        : sideArmor
                            ? new Rgb(134, 14, 31)
                            : new Rgb(107, 10, 26);

                var light = faceplate
                    ? 0.92 + (1.0 - absoluteX) * 0.16 - normalizedX * 0.07 - normalizedY * 0.025
                    : 0.74 + (1.0 - absoluteX) * 0.20 - normalizedX * 0.08;
                color = color.Scale(Math.Clamp(light, faceplate ? 0.78 : 0.56, faceplate ? 1.14 : 1.08));

                if (faceplate && normalizedY is > 0.04 and < 0.68)
                {
                    var lowerFaceProgress = Clamp01((normalizedY - 0.04) / 0.64);
                    var centerWeight = 1.0 - Math.Min(1.0, absoluteX / 0.48);
                    color = color.Scale(1.0 - lowerFaceProgress * (0.14 + centerWeight * 0.14));
                }
                else if (faceplate && normalizedY >= 0.68)
                {
                    color = color.Scale(1.05);
                }

                if (foreheadInset)
                {
                    color = new Rgb(111, 11, 27).Scale(0.86 + (1.0 - absoluteX) * 0.20);
                }
                if (panelSeam)
                {
                    color = DeepArmor;
                }
                if (eyeOpening)
                {
                    color = new Rgb(1, 7, 11);
                }
                if (eyeGlow)
                {
                    color = new Rgb(178, 245, 255);
                }

                var alpha = Compact ? 238 : faceplate ? 232 : 220;
                if (panelSeam)
                {
                    alpha = 244;
                }
                if (eyeOpening)
                {
                    alpha = 250;
                }
                var offset = pixelY * _stride + pixelX * 4;
                _plateSamples.Add(new PlateSample(
                    offset,
                    (byte)(color.B * alpha / 255),
                    (byte)(color.G * alpha / 255),
                    (byte)(color.R * alpha / 255),
                    (byte)alpha));
            }
        }
    }

    private void RenderFrame(DateTime nowUtc)
    {
        if (_bitmap is null || _pixels.Length == 0)
        {
            RecreateBitmap();
            if (_bitmap is null)
            {
                return;
            }
        }

        Array.Clear(_pixels);

        var elapsed = (nowUtc - _assemblyStartedUtc).TotalSeconds;
        var stateElapsed = (nowUtc - _stateStartedUtc).TotalSeconds;
        var dissolve = _isDissolving
            ? SmoothStep(Clamp01((nowUtc - _dissolveStartedUtc).TotalSeconds / 0.34))
            : 0;
        var width = _pixelWidth;
        var height = _pixelHeight;
        var scale = GetHelmetScale(width, height);
        var centerX = width * 0.5;
        var centerY = height * (Compact ? 0.49 : 0.47);
        var breathing = 1.0 + Math.Sin(stateElapsed * 1.65) * (_state == ParticleHelmetState.Speaking ? 0.018 : 0.006);
        var plateProgress = SmoothStep(Clamp01(
            (elapsed - _assemblyDurationSeconds * 0.28) /
            Math.Max(0.1, _assemblyDurationSeconds * 0.58)));
        var plateStateOpacity = _state switch
        {
            ParticleHelmetState.Offline => 0.78,
            ParticleHelmetState.Waiting => 0.86,
            ParticleHelmetState.Listening => 0.92,
            ParticleHelmetState.Thinking => 0.94,
            ParticleHelmetState.Speaking => 0.90,
            ParticleHelmetState.Alert => 0.88,
            _ => 0.88
        };
        ApplyBasePlate(plateProgress * plateStateOpacity * (1.0 - dissolve));

        var scatterBase = _assemblyFromScreenEdges
            ? Math.Sqrt(width * (double)width + height * (double)height) * 0.53
            : scale;

        for (var index = 0; index < _particles.Count; index++)
        {
            var particle = _particles[index];
            var localProgress = Clamp01((elapsed - particle.Delay) / _assemblyDurationSeconds);
            var assembled = SmoothStep(localProgress);
            var angle = particle.StartAngle + assembled * (4.7 + particle.Spin);
            var scatterRadius = particle.StartRadius * scatterBase * (1.0 - assembled);
            var scatterX = centerX + Math.Cos(angle) * scatterRadius;
            var scatterY = centerY + Math.Sin(angle) * scatterRadius * 0.82;

            var targetX = centerX + particle.TargetX * scale * breathing;
            var targetY = centerY + WarpHelmetY(particle.TargetY) * scale * breathing;
            var turbulence = (1.0 - assembled) * (Compact ? 2.5 : 9.5);
            var x = Lerp(scatterX, targetX, assembled) + Math.Sin(stateElapsed * 2.1 + particle.Phase) * turbulence;
            var y = Lerp(scatterY, targetY, assembled) + Math.Cos(stateElapsed * 1.8 + particle.Phase) * turbulence;

            if (particle.Kind == ParticleKind.Ambient)
            {
                var orbitBoost = _state == ParticleHelmetState.Thinking ? 1.75 : 0.65;
                var orbitAngle = particle.StartAngle + stateElapsed * orbitBoost * (0.28 + Math.Abs(particle.Spin) * 0.12);
                var orbitRadius = scale * particle.StartRadius * (0.77 + Math.Sin(stateElapsed + particle.Phase) * 0.035);
                var orbitX = centerX + Math.Cos(orbitAngle) * orbitRadius;
                var orbitY = centerY + Math.Sin(orbitAngle) * orbitRadius * 0.78;
                x = Lerp(x, orbitX, assembled);
                y = Lerp(y, orbitY, assembled);
            }
            else if (_state == ParticleHelmetState.Thinking && index % 13 == 0)
            {
                x += Math.Sin(stateElapsed * 5.2 + particle.Phase) * 6.5;
                y += Math.Cos(stateElapsed * 4.6 + particle.Phase) * 4.0;
            }
            else if (_state == ParticleHelmetState.Speaking)
            {
                var voiceWave = Math.Sin(stateElapsed * 8.5 - particle.TargetY * 5.5 + particle.Phase) * 2.4;
                x += particle.TargetX * voiceWave;
                y += voiceWave * 0.34;
            }

            if (dissolve > 0)
            {
                var exitAngle = particle.StartAngle + dissolve * 3.2;
                var exitRadius = scale * (0.15 + particle.StartRadius * dissolve * 1.45);
                x = Lerp(x, centerX + Math.Cos(exitAngle) * exitRadius, dissolve);
                y = Lerp(y, centerY + Math.Sin(exitAngle) * exitRadius * 0.76, dissolve);
            }

            var color = ResolveColor(particle.Kind, assembled, stateElapsed, particle.Phase);
            var intensity = ResolveIntensity(particle.Kind, assembled, stateElapsed, particle.Phase) * (1.0 - dissolve);
            if (particle.Kind == ParticleKind.Ambient && assembled > 0.92 &&
                IsInsideHelmet((x - centerX) / scale, UnwarpHelmetY((y - centerY) / scale)))
            {
                continue;
            }
            if (intensity <= 0.01)
            {
                continue;
            }

            var kindScale = particle.Kind switch
            {
                ParticleKind.OuterContour => 0.84,
                ParticleKind.PanelSeam => 0.66,
                ParticleKind.Ambient => 0.72,
                ParticleKind.Eye => 0.96,
                _ => 0.88
            };
            var size = particle.Size * kindScale * (Compact ? 0.82 : 1.05);
            DrawParticle(x, y, size, color, intensity);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight), _pixels, _stride, 0);
        InvalidateVisual();
    }

    private double GetHelmetScale(int width, int height)
    {
        var proportional = Math.Min(width, height) * (Compact ? 0.405 : 0.385);
        var maximum = (Compact ? 50.0 : 172.0) * _renderResolutionScale;
        return Math.Min(proportional, maximum);
    }

    private static double WarpHelmetY(double normalizedY) =>
        normalizedY < 0
            ? normalizedY * UpperHelmetCompression
            : normalizedY * LowerHelmetCompression;

    private static double UnwarpHelmetY(double displayedY) =>
        displayedY < 0
            ? displayedY / UpperHelmetCompression
            : displayedY / LowerHelmetCompression;

    private void ApplyBasePlate(double opacity)
    {
        opacity = Clamp01(opacity);
        if (opacity <= 0.001)
        {
            return;
        }

        foreach (var sample in _plateSamples)
        {
            _pixels[sample.Offset] = (byte)Math.Round(sample.B * opacity);
            _pixels[sample.Offset + 1] = (byte)Math.Round(sample.G * opacity);
            _pixels[sample.Offset + 2] = (byte)Math.Round(sample.R * opacity);
            _pixels[sample.Offset + 3] = (byte)Math.Round(sample.A * opacity);
        }
    }

    private Rgb ResolveColor(ParticleKind kind, double assembled, double time, double phase)
    {
        var stable = kind switch
        {
            ParticleKind.Armor => ArmorRed,
            ParticleKind.HotArmor => HotOrange,
            ParticleKind.OuterContour => HotOrange,
            ParticleKind.PanelSeam => PanelGoldLight,
            ParticleKind.Eye => ReactorCyan,
            ParticleKind.Ambient => ReactorCyan,
            _ => SignalSilver
        };

        var assemblyBlue = (1.0 - assembled) * 0.72;
        stable = Rgb.Lerp(stable, ReactorCyan, assemblyBlue);

        return _state switch
        {
            ParticleHelmetState.Offline => Rgb.Lerp(stable, DeepArmor, 0.08),
            ParticleHelmetState.Listening when kind is not ParticleKind.Eye => Rgb.Lerp(stable, ReactorCyan, 0.34),
            ParticleHelmetState.Recognized => Rgb.Lerp(stable, SignalSilver, 0.22),
            ParticleHelmetState.Thinking => Rgb.Lerp(stable, HotOrange, 0.22 + 0.12 * Math.Sin(time * 3 + phase)),
            ParticleHelmetState.Alert => Rgb.Lerp(stable, ArmorRed, 0.55),
            _ => stable
        };
    }

    private double ResolveIntensity(ParticleKind kind, double assembled, double time, double phase)
    {
        var twinkle = 0.84 + 0.16 * Math.Sin(time * 2.3 + phase);
        var stateLevel = _state switch
        {
            ParticleHelmetState.Offline => 0.70,
            ParticleHelmetState.Waiting => 0.80,
            ParticleHelmetState.Listening => 0.92,
            ParticleHelmetState.Recognized => 0.82,
            ParticleHelmetState.Thinking => 0.88,
            ParticleHelmetState.Speaking => 0.84 + 0.16 * Math.Sin(time * 8.0 + phase),
            ParticleHelmetState.Alert => 0.72 + 0.20 * Math.Sin(time * 5.0),
            _ => 0.65
        };

        if (kind == ParticleKind.Eye)
        {
            var eyePulse = _state is ParticleHelmetState.Listening or ParticleHelmetState.Speaking
                ? 0.95 + 0.22 * Math.Sin(time * 5.5)
                : 0.92;
            return Clamp01(assembled * eyePulse);
        }

        if (kind == ParticleKind.Ambient)
        {
            stateLevel *= _state == ParticleHelmetState.Thinking ? 0.82 : 0.46;
        }
        else if (kind == ParticleKind.OuterContour)
        {
            stateLevel *= 0.92;
        }
        else if (kind == ParticleKind.PanelSeam)
        {
            stateLevel *= 0.72;
        }

        return Clamp01(assembled * stateLevel * twinkle);
    }

    private void DrawParticle(double x, double y, double size, Rgb color, double intensity)
    {
        var radius = Math.Clamp((int)Math.Ceiling(size * 2.45), 2, Compact ? 4 : 6);
        var centerX = (int)Math.Round(x);
        var centerY = (int)Math.Round(y);

        for (var offsetY = -radius; offsetY <= radius; offsetY++)
        {
            var pixelY = centerY + offsetY;
            if ((uint)pixelY >= (uint)_pixelHeight)
            {
                continue;
            }

            for (var offsetX = -radius; offsetX <= radius; offsetX++)
            {
                var pixelX = centerX + offsetX;
                if ((uint)pixelX >= (uint)_pixelWidth)
                {
                    continue;
                }

                var distanceSquared = offsetX * offsetX + offsetY * offsetY;
                var radiusSquared = radius * radius;
                if (distanceSquared > radiusSquared)
                {
                    continue;
                }

                var normalized = 1.0 - distanceSquared / (double)radiusSquared;
                var alpha = (int)(255 * intensity * normalized * normalized * 0.86);
                var coreRadius = Math.Max(0.85, size * 0.58);
                if (distanceSquared <= coreRadius * coreRadius)
                {
                    alpha = Math.Max(alpha, (int)(248 * intensity));
                }
                AddPixel(pixelX, pixelY, color, alpha);
            }
        }
    }

    private void AddPixel(int x, int y, Rgb color, int alpha)
    {
        alpha = Math.Clamp(alpha, 0, 255);
        if (alpha == 0)
        {
            return;
        }

        var offset = y * _stride + x * 4;
        _pixels[offset] = AddClamped(_pixels[offset], color.B * alpha / 255);
        _pixels[offset + 1] = AddClamped(_pixels[offset + 1], color.G * alpha / 255);
        _pixels[offset + 2] = AddClamped(_pixels[offset + 2], color.R * alpha / 255);
        _pixels[offset + 3] = AddClamped(_pixels[offset + 3], alpha);
    }

    private static byte AddClamped(byte current, int addition) =>
        (byte)Math.Min(255, current + addition);

    private double Next(double minimum, double maximum) =>
        minimum + _random.NextDouble() * (maximum - minimum);

    private static double Lerp(double start, double end, double amount) =>
        start + (end - start) * amount;

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private static double SmoothStep(double value)
    {
        value = Clamp01(value);
        return value * value * (3 - 2 * value);
    }

    private readonly record struct Rgb(byte R, byte G, byte B)
    {
        public Rgb Scale(double amount) => new(
            (byte)Math.Clamp(Math.Round(R * amount), 0, 255),
            (byte)Math.Clamp(Math.Round(G * amount), 0, 255),
            (byte)Math.Clamp(Math.Round(B * amount), 0, 255));

        public static Rgb Lerp(Rgb start, Rgb end, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return new Rgb(
                (byte)Math.Round(ParticleHelmetControl.Lerp(start.R, end.R, amount)),
                (byte)Math.Round(ParticleHelmetControl.Lerp(start.G, end.G, amount)),
                (byte)Math.Round(ParticleHelmetControl.Lerp(start.B, end.B, amount)));
        }
    }
}
