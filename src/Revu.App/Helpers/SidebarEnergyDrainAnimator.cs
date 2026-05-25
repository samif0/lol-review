#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Revu.App.Helpers;

/// <summary>
/// Draws subtle bezier energy trails from sidebar edges toward the active
/// nav button with gentle swirl arcs.
///
/// v2.17.4: rewritten to drive the per-trail pulse via Composition opacity
/// key-frame animations (GPU compositor thread) instead of the previous
/// per-frame UI-thread <c>CompositionTarget.Rendering</c> + <c>StrokeDashOffset</c>
/// path. The old approach forced D2D to re-tessellate dashed bezier strokes
/// every render tick on the UI thread, which contended with hover hit-testing
/// and made sidebar buttons feel laggy. Composition animations run on the
/// compositor thread independent of UI work, so hover stays responsive even
/// while trails are pulsing. The visual result is "energy breathing along the
/// trail" rather than "dashes flowing along it" — softer, but still alive.
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
    private readonly List<Path> _trails = new();
    private float _sidebarWidth;

    public SidebarEnergyDrainAnimator(Canvas canvas, float sidebarWidth = 72f)
    {
        _canvas = canvas;
        _sidebarWidth = sidebarWidth;
    }

    public void UpdateTarget(float targetY, float sidebarHeight)
    {
        Stop();

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

        // Per-trail definitions. `pulseSec` is the breathing period; `phaseSec`
        // staggers when each trail starts its cycle so they don't pulse in
        // lockstep. Values tuned so the overall sidebar looks like flowing
        // energy rather than a coordinated blink.
        var defs = new[]
        {
            // Left edge, from top — primary
            (edge: 0.0,           fromTop: true,  curvePull: 0.30, swirlDeg:  160.0, thick: 1.5, alpha: 0.40, pulseSec: 2.6, phaseSec: 0.0),
            // Left edge, from top — secondary
            (edge: 0.0,           fromTop: true,  curvePull: 0.50, swirlDeg:  110.0, thick: 1.0, alpha: 0.26, pulseSec: 3.1, phaseSec: 0.7),
            // Left edge, from bottom — primary
            (edge: 0.0,           fromTop: false, curvePull: 0.32, swirlDeg: -150.0, thick: 1.5, alpha: 0.36, pulseSec: 2.8, phaseSec: 1.3),
            // Left edge, from bottom — secondary
            (edge: 0.0,           fromTop: false, curvePull: 0.48, swirlDeg: -100.0, thick: 1.0, alpha: 0.24, pulseSec: 3.4, phaseSec: 1.9),

            // Right edge, from top — primary
            (edge: _sidebarWidth, fromTop: true,  curvePull: 0.30, swirlDeg: -160.0, thick: 1.5, alpha: 0.40, pulseSec: 2.7, phaseSec: 0.4),
            // Right edge, from top — secondary
            (edge: _sidebarWidth, fromTop: true,  curvePull: 0.45, swirlDeg: -110.0, thick: 1.0, alpha: 0.26, pulseSec: 3.0, phaseSec: 1.1),
            // Right edge, from bottom — primary
            (edge: _sidebarWidth, fromTop: false, curvePull: 0.35, swirlDeg:  150.0, thick: 1.5, alpha: 0.34, pulseSec: 2.9, phaseSec: 1.6),
            // Right edge, from bottom — secondary
            (edge: _sidebarWidth, fromTop: false, curvePull: 0.52, swirlDeg:  100.0, thick: 1.0, alpha: 0.22, pulseSec: 3.3, phaseSec: 2.2),
        };

        foreach (var d in defs)
        {
            var path = CreateTrailPath(d.edge, d.fromTop, cx, targetY, sidebarHeight,
                d.curvePull, d.swirlDeg, d.thick, d.alpha);
            _canvas.Children.Add(path);
            _trails.Add(path);
            AttachPulse(path, (float)d.alpha, d.pulseSec, d.phaseSec);
        }
    }

    public void Stop()
    {
        // Stopping a Composition animation explicitly is good hygiene but the
        // Visual is GC'd with the Path when we clear children, so it's mostly
        // belt-and-suspenders. The important part is the canvas clear, which
        // detaches every Path from the visual tree.
        foreach (var path in _trails)
        {
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(path);
                visual.StopAnimation(nameof(visual.Opacity));
            }
            catch
            {
                // Visual may not exist yet if Path never made it on-screen.
            }
        }

        _canvas.Children.Clear();
        _trails.Clear();
    }

    /// <summary>
    /// Drive opacity from <c>baseAlpha * 0.35</c> up to <c>baseAlpha * 1.15</c>
    /// and back, forever, on the GPU compositor thread. <paramref name="phaseSec"/>
    /// shifts the cycle start so eight trails don't pulse in unison.
    /// </summary>
    private static void AttachPulse(UIElement element, float baseAlpha, double pulseSec, double phaseSec)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            // Composition lacks a "start at offset" knob, but DelayTime + repeat
            // for the first cycle gives us the same staggered look. After the
            // first iteration the animation runs continuously.
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(0.0f, 1.0f);  // we modulate Opacity 0..1; the
            anim.InsertKeyFrame(0.5f, 0.35f); // base alpha lives in the brush
            anim.InsertKeyFrame(1.0f, 1.0f);  // color itself.
            anim.Duration = TimeSpan.FromSeconds(pulseSec);
            anim.IterationBehavior = AnimationIterationBehavior.Forever;
            if (phaseSec > 0)
            {
                anim.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                anim.DelayTime = TimeSpan.FromSeconds(phaseSec);
            }

            visual.StartAnimation(nameof(visual.Opacity), anim);
        }
        catch
        {
            // Composition can throw if the element isn't in the tree yet —
            // silently skip; the path will still render at its static alpha.
        }
    }

    private Path CreateTrailPath(
        double edgeX, bool fromTop, double centerX, double targetY,
        double sidebarHeight, double curvePull, double swirlDeg,
        double thickness, double alpha)
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

        return new Path
        {
            Data = new PathGeometry { Figures = figures },
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)(alpha * 255), Accent.R, Accent.G, Accent.B)),
            StrokeThickness = thickness,
            // v2.17.4: no StrokeDashArray. Solid stroke is dramatically cheaper
            // to render (no per-frame arc-length parameterization) and the
            // pulse comes from Composition opacity instead.
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
    }
}
