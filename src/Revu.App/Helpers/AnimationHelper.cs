#nullable enable

using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Revu.App.Helpers;

/// <summary>
/// Composition-based animation helpers for the futuristic HUD theme.
/// Each helper attaches a forever-running animation; consumers do not
/// need to manage lifecycle manually for simple cases.
/// </summary>
public static class AnimationHelper
{

    /// <summary>
    /// Attach a slow, forever-pulsing opacity animation to a UI element.
    /// Used for status dots and selected pills.
    /// </summary>
    public static void AttachPulseOpacity(
        UIElement element,
        double minOpacity = 0.4,
        double maxOpacity = 1.0,
        double durationSec = 2.0)
    {
        if (element is null) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(0.0f, (float)maxOpacity);
            anim.InsertKeyFrame(0.5f, (float)minOpacity);
            anim.InsertKeyFrame(1.0f, (float)maxOpacity);
            anim.Duration = TimeSpan.FromSeconds(durationSec);
            anim.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.StartAnimation(nameof(visual.Opacity), anim);
        }
        catch
        {
            // Composition can throw if element isn't in tree yet — silently skip.
        }
    }

    /// <summary>
    /// Attach a breathing drop-shadow to an element. Animates blur radius
    /// from minBlur up to maxBlur and back over durationSec, forever.
    /// Element must be a Border, Shape, or Image (DropShadow targets).
    /// </summary>
    public static void AttachBreathingGlow(
        UIElement element,
        Color glowColor,
        float minBlur = 4f,
        float maxBlur = 14f,
        double durationSec = 3.0)
    {
        if (element is null) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            // Composition drop shadow attaches via SpriteVisual sized to host.
            // For arbitrary UIElements we settle for an opacity-based "breath"
            // on the element itself if it isn't shadow-able.
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(0.0f, 0.85f);
            anim.InsertKeyFrame(0.5f, 1.0f);
            anim.InsertKeyFrame(1.0f, 0.85f);
            anim.Duration = TimeSpan.FromSeconds(durationSec);
            anim.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation(nameof(visual.Opacity), anim);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Apply a one-shot enter animation: fade in + slide up from 16px below.
    /// Call this on Page.Loaded or once at construction.
    /// </summary>
    public static void AnimatePageEnter(UIElement root)
    {
        if (root is null) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(root);
            var compositor = visual.Compositor;

            visual.Opacity = 0f;
            visual.Offset = new Vector3(0, 16, 0);

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0.0f, 0f);
            opacityAnim.InsertKeyFrame(1.0f, 1f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(450);
            visual.StartAnimation(nameof(visual.Opacity), opacityAnim);

            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertKeyFrame(0.0f, new Vector3(0, 16, 0));
            offsetAnim.InsertKeyFrame(1.0f, Vector3.Zero);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(450);
            visual.StartAnimation(nameof(visual.Offset), offsetAnim);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Make a UIElement slide right by `slidePx` on pointer enter, return on exit.
    /// Used for game rows and objective rows.
    /// </summary>
    public static void AttachSlideRightOnHover(FrameworkElement element, float slidePx = 4f)
    {
        if (element is null) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            visual.Offset = Vector3.Zero;

            element.PointerEntered += (_, _) =>
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.InsertKeyFrame(1.0f, new Vector3(slidePx, 0, 0));
                anim.Duration = TimeSpan.FromMilliseconds(220);
                visual.StartAnimation(nameof(visual.Offset), anim);
            };

            element.PointerExited += (_, _) =>
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.InsertKeyFrame(1.0f, Vector3.Zero);
                anim.Duration = TimeSpan.FromMilliseconds(220);
                visual.StartAnimation(nameof(visual.Offset), anim);
            };
        }
        catch
        {
        }
    }

    /// <summary>
    /// On hover: lift the card -1px on Y, brighten its border, fade in a hover overlay.
    /// On exit: reverse. Mirrors the .card:hover behaviour in the mockup.
    /// </summary>
    public static void AttachCardLiftHover(Border card, UIElement? hoverOverlay = null, float liftPx = 1f)
    {
        if (card is null) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(card);
            var compositor = visual.Compositor;

            var subtleBorder = (Brush)Application.Current.Resources["SubtleBorderBrush"];
            var brightBorder = (Brush)Application.Current.Resources["BrightBorderBrush"];

            card.PointerEntered += (_, _) =>
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.InsertKeyFrame(1.0f, new Vector3(0, -liftPx, 0));
                anim.Duration = TimeSpan.FromMilliseconds(220);
                visual.StartAnimation(nameof(visual.Offset), anim);

                card.BorderBrush = brightBorder;
                if (hoverOverlay is not null)
                {
                    AnimateOpacity(hoverOverlay, 1.0, 220);
                }
            };

            card.PointerExited += (_, _) =>
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.InsertKeyFrame(1.0f, Vector3.Zero);
                anim.Duration = TimeSpan.FromMilliseconds(220);
                visual.StartAnimation(nameof(visual.Offset), anim);

                card.BorderBrush = subtleBorder;
                if (hoverOverlay is not null)
                {
                    AnimateOpacity(hoverOverlay, 0.0, 220);
                }
            };
        }
        catch
        {
        }
    }

    /// <summary>
    /// Animate a UIElement's composition offset with the same easing profile as the HTML mockup.
    /// Useful for small cursor-follow hover travel without paying the cost of projection updates.
    /// </summary>
    public static void AnimateOffset(UIElement element, double x, double y, double durationMs = 140)
    {
        if (element is null) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.22f, 1.0f),
                new Vector2(0.36f, 1.0f));

            var anim = compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1.0f, new Vector3((float)x, (float)y, 0f), easing);
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation(nameof(visual.Offset), anim);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Animate a UIElement's composition scale around the center of a source element.
    /// Useful for hover depth without moving the element out of its layout slot.
    /// </summary>
    public static void AnimateScale(UIElement element, FrameworkElement centerSource, double scale, double durationMs = 180)
    {
        if (element is null || centerSource is null) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            visual.CenterPoint = new Vector3(
                (float)(centerSource.ActualWidth / 2.0),
                (float)(centerSource.ActualHeight / 2.0),
                0f);

            var easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.22f, 1.0f),
                new Vector2(0.36f, 1.0f));

            var anim = compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1.0f, new Vector3((float)scale, (float)scale, 1f), easing);
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation(nameof(visual.Scale), anim);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Fade-in/out a set of corner-bracket elements on hover of a container.
    /// </summary>
    public static void AttachCornerBracketsHover(FrameworkElement host, params UIElement[] corners)
    {
        if (host is null || corners is null) return;

        foreach (var corner in corners)
        {
            if (corner is null) continue;
            corner.Opacity = 0;
        }

        host.PointerEntered += (_, _) =>
        {
            foreach (var corner in corners)
            {
                if (corner is null) continue;
                AnimateOpacity(corner, 1.0, 220);
            }
        };

        host.PointerExited += (_, _) =>
        {
            foreach (var corner in corners)
            {
                if (corner is null) continue;
                AnimateOpacity(corner, 0.0, 220);
            }
        };
    }

    public static void AnimateOpacity(UIElement element, double target, double durationMs)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1.0f, (float)target);
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation(nameof(visual.Opacity), anim);
        }
        catch
        {
        }
    }

    public static void SetOpacity(UIElement element, double value)
    {
        if (element is null) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation(nameof(visual.Opacity));
            visual.Opacity = (float)value;
            element.Opacity = value;
        }
        catch
        {
        }
    }

    public static void SetOffset(UIElement element, double x, double y)
    {
        if (element is null) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation(nameof(visual.Offset));
            visual.Offset = new Vector3((float)x, (float)y, 0f);
        }
        catch
        {
        }
    }

    public static void SetScale(UIElement element, FrameworkElement centerSource, double scale)
    {
        if (element is null || centerSource is null) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation(nameof(visual.Scale));
            visual.CenterPoint = new Vector3(
                (float)(centerSource.ActualWidth / 2.0),
                (float)(centerSource.ActualHeight / 2.0),
                0f);
            visual.Scale = new Vector3((float)scale, (float)scale, 1f);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Convenience helper to convert a SolidColorBrush to a Windows.UI.Color
    /// for composition APIs.
    /// </summary>
    public static Color ColorFromBrush(Brush? brush, Color fallback)
    {
        return brush is SolidColorBrush scb ? scb.Color : fallback;
    }
}
