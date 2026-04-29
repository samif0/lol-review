#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Revu.App.Helpers;

/// <summary>
/// Draws subtle bezier energy trails from sidebar edges toward the active
/// nav button with gentle swirl arcs. Uses per-frame StrokeDashOffset
/// animation for smooth, continuous flow.
/// </summary>
public sealed class SidebarEnergyDrainAnimator
{
    private static readonly Windows.UI.Color Accent = Windows.UI.Color.FromArgb(255, 167, 139, 250);

    /// <summary>
    /// v2.15.0: global gate. When false, <see cref="UpdateTarget"/> clears the
    /// canvas and refuses to spawn new trails. Set by App from IConfigService
    /// at startup and re-applied from SettingsViewModel when the user toggles.
    /// Default true to preserve the existing look for users who haven't touched
    /// the toggle.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            EnabledChanged?.Invoke();
        }
    }
    private static bool _enabled = true;

    /// <summary>
    /// Fires when <see cref="Enabled"/> flips. ShellPage subscribes so it can
    /// immediately clear or rebuild the trails without waiting for a nav event.
    /// </summary>
    public static event Action? EnabledChanged;

    private readonly Canvas _canvas;
    private readonly List<TrailInfo> _trails = new();
    private bool _running;
    private float _sidebarWidth;
    private readonly Stopwatch _stopwatch = new();
    private long _lastTick;
    private int _frameCounter; // throttle to ~20fps (render 1 of every 3 frames)

    private sealed class TrailInfo
    {
        public required Path Path;
        public double DashOffset;
        public double Speed;       // px per second (not per frame)
        public double DashPeriod;
    }

    public SidebarEnergyDrainAnimator(Canvas canvas, float sidebarWidth = 72f)
    {
        _canvas = canvas;
        _sidebarWidth = sidebarWidth;
    }

    public void UpdateTarget(float targetY, float sidebarHeight)
    {
        Stop();

        _canvas.Children.Clear();
        _trails.Clear();

        // v2.15.0: bail after clearing if the user disabled sidebar animation.
        // Clearing first so any currently-drawn trails vanish immediately when
        // the toggle flips off.
        if (!Enabled) return;

        // Clip to sidebar bounds
        _canvas.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, _sidebarWidth, sidebarHeight)
        };

        var cx = _sidebarWidth / 2.0;

        var defs = new[]
        {
            // Left edge, from top — primary                                          speed = px/sec
            (edge: 0.0,   fromTop: true,  curvePull: 0.30, swirlDeg: 160.0,  thick: 1.5, alpha: 0.35, spd: 120.0, dashOn: 80.0, dashOff: 40.0),
            // Left edge, from top — secondary
            (edge: 0.0,   fromTop: true,  curvePull: 0.50, swirlDeg: 110.0,  thick: 1.0, alpha: 0.22, spd: 96.0,  dashOn: 60.0, dashOff: 50.0),
            // Left edge, from bottom — primary
            (edge: 0.0,   fromTop: false, curvePull: 0.32, swirlDeg: -150.0, thick: 1.5, alpha: 0.32, spd: 108.0, dashOn: 75.0, dashOff: 42.0),
            // Left edge, from bottom — secondary
            (edge: 0.0,   fromTop: false, curvePull: 0.48, swirlDeg: -100.0, thick: 1.0, alpha: 0.20, spd: 84.0,  dashOn: 55.0, dashOff: 55.0),

            // Right edge, from top — primary
            (edge: _sidebarWidth, fromTop: true,  curvePull: 0.30, swirlDeg: -160.0, thick: 1.5, alpha: 0.35, spd: 114.0, dashOn: 78.0, dashOff: 38.0),
            // Right edge, from top — secondary
            (edge: _sidebarWidth, fromTop: true,  curvePull: 0.45, swirlDeg: -110.0, thick: 1.0, alpha: 0.22, spd: 90.0,  dashOn: 58.0, dashOff: 48.0),
            // Right edge, from bottom — primary
            (edge: _sidebarWidth, fromTop: false, curvePull: 0.35, swirlDeg: 150.0,  thick: 1.5, alpha: 0.30, spd: 102.0, dashOn: 72.0, dashOff: 44.0),
            // Right edge, from bottom — secondary
            (edge: _sidebarWidth, fromTop: false, curvePull: 0.52, swirlDeg: 100.0,  thick: 1.0, alpha: 0.18, spd: 78.0,  dashOn: 52.0, dashOff: 52.0),
        };

        foreach (var d in defs)
        {
            var path = CreateTrailPath(d.edge, d.fromTop, cx, targetY, sidebarHeight,
                d.curvePull, d.swirlDeg, d.thick, d.alpha, d.dashOn, d.dashOff);
            _canvas.Children.Add(path);

            var period = d.dashOn + d.dashOff;
            _trails.Add(new TrailInfo
            {
                Path = path,
                DashOffset = 0,
                Speed = d.spd,
                DashPeriod = period,
            });
        }

        _running = true;
        _stopwatch.Restart();
        _lastTick = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running) return;
        CompositionTarget.Rendering -= OnRendering;
        _running = false;
        _canvas.Children.Clear();
        _trails.Clear();
    }

    private void OnRendering(object? sender, object e)
    {
        // v2.17: render 1 of every 3 frames (~20fps). Dash drift is dt-based
        // (Speed is px/sec * elapsed-real-time), so visual speed stays
        // identical — we just update fewer times per second. Active-
        // animation CPU dropped from ~21% one-core to ~18% one-core on a
        // 9800X3D. Smaller than the projected 33% because trail rendering
        // wasn't the dominant cost during the first ~110s post-nav window
        // (layout / composition warmup is most of it). Still a free win
        // for 4-core laptops.
        _frameCounter = (_frameCounter + 1) % 3;
        if (_frameCounter != 0) return;

        var now = _stopwatch.ElapsedMilliseconds;
        var dtSec = (now - _lastTick) / 1000.0;
        _lastTick = now;

        if (dtSec > 0.1) dtSec = 0.05;

        foreach (var t in _trails)
        {
            t.DashOffset -= t.Speed * dtSec;
            if (t.DashOffset < -t.DashPeriod * 10)
                t.DashOffset += t.DashPeriod * 10;
            t.Path.StrokeDashOffset = t.DashOffset;
        }
    }

    private Path CreateTrailPath(
        double edgeX, bool fromTop, double centerX, double targetY,
        double sidebarHeight, double curvePull, double swirlDeg,
        double thickness, double alpha, double dashOn, double dashOff)
    {
        var startY = fromTop ? 0.0 : sidebarHeight;
        var travelDist = Math.Abs(targetY - startY);

        // Control points: stay near edge initially, then curve toward button orbit
        var orbitR = 18.0; // orbit radius around button
        var isLeft = edgeX < centerX;

        // Approach point: arrive at the orbit edge, not the center
        var approachAngle = fromTop ? -Math.PI / 2 : Math.PI / 2; // arrive from above/below
        if (isLeft) approachAngle += 0.3; else approachAngle -= 0.3; // slight offset based on side
        var arriveX = centerX + orbitR * Math.Cos(approachAngle);
        var arriveY = targetY + orbitR * Math.Sin(approachAngle);

        double cp1X, cp1Y, cp2X, cp2Y;

        if (fromTop)
        {
            cp1X = edgeX;
            cp1Y = startY + travelDist * (1.0 - curvePull);
            cp2X = edgeX + (arriveX - edgeX) * curvePull * 0.8;
            cp2Y = arriveY - travelDist * 0.06;
        }
        else
        {
            cp1X = edgeX;
            cp1Y = startY - travelDist * (1.0 - curvePull);
            cp2X = edgeX + (arriveX - edgeX) * curvePull * 0.8;
            cp2Y = arriveY + travelDist * 0.06;
        }

        var mainFigure = new PathFigure
        {
            StartPoint = new Point(edgeX, startY),
            IsClosed = false,
            IsFilled = false,
        };

        // Bezier from edge to orbit entry point
        mainFigure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(cp1X, cp1Y),
            Point2 = new Point(cp2X, cp2Y),
            Point3 = new Point(arriveX, arriveY),
        });

        // Swirl arc(s) orbiting around the button
        if (Math.Abs(swirlDeg) > 10)
        {
            var swirlRad = swirlDeg * Math.PI / 180.0;
            // End point on the orbit circle
            var endAngle = approachAngle + swirlRad;
            var endX = centerX + orbitR * Math.Cos(endAngle);
            var endY = targetY + orbitR * Math.Sin(endAngle);

            endX = Math.Max(2, Math.Min(_sidebarWidth - 2, endX));
            endY = Math.Max(2, Math.Min(sidebarHeight - 2, endY));

            mainFigure.Segments.Add(new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(orbitR, orbitR),
                SweepDirection = swirlDeg > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                IsLargeArc = Math.Abs(swirlDeg) > 180,
                RotationAngle = 0,
            });

            // Second smaller arc spiraling inward
            var innerR = orbitR * 0.55;
            var innerAngle = endAngle + (swirlRad > 0 ? 1.2 : -1.2);
            var innerX = centerX + innerR * Math.Cos(innerAngle);
            var innerY = targetY + innerR * Math.Sin(innerAngle);
            innerX = Math.Max(2, Math.Min(_sidebarWidth - 2, innerX));
            innerY = Math.Max(2, Math.Min(sidebarHeight - 2, innerY));

            mainFigure.Segments.Add(new ArcSegment
            {
                Point = new Point(innerX, innerY),
                Size = new Size(innerR, innerR),
                SweepDirection = swirlDeg > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                IsLargeArc = false,
                RotationAngle = 0,
            });
        }

        var figures = new PathFigureCollection();
        figures.Add(mainFigure);

        var dashArray = new DoubleCollection { dashOn / thickness, dashOff / thickness };

        return new Path
        {
            Data = new PathGeometry { Figures = figures },
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)(alpha * 255), Accent.R, Accent.G, Accent.B)),
            StrokeThickness = thickness,
            StrokeDashArray = dashArray,
            StrokeDashCap = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
    }
}
