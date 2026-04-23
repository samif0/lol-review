#nullable enable

using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Revu.App.Helpers;

namespace Revu.App.Controls;

/// <summary>
/// Monospace section header: pulsing dot + label + extending fade line.
/// Mirrors the .card-t and similar headings in mockups/app-mockup.html.
/// </summary>
public sealed partial class SectionTitle : UserControl
{
    public SectionTitle()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(SectionTitle),
            new PropertyMetadata("", (d, e) => ((SectionTitle)d).TitleTextBlock.Text = e.NewValue?.ToString() ?? ""));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimationHelper.AttachPulseOpacity(PulseDot, 0.4, 1.0, 2.0);
        AttachBreathingScale(PulseDot, 0.7f, 1.3f, 2.0);
        AttachSpin(PulseDot, 4.0);
    }

    private static void AttachBreathingScale(UIElement element, float minScale, float maxScale, double durationSec)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            // Center the scale around the element's midpoint
            if (element is FrameworkElement fe)
            {
                visual.CenterPoint = new System.Numerics.Vector3((float)(fe.Width / 2), (float)(fe.Height / 2), 0);
            }

            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(0.0f, new System.Numerics.Vector3(maxScale, maxScale, 1f));
            scaleAnim.InsertKeyFrame(0.5f, new System.Numerics.Vector3(minScale, minScale, 1f));
            scaleAnim.InsertKeyFrame(1.0f, new System.Numerics.Vector3(maxScale, maxScale, 1f));
            scaleAnim.Duration = TimeSpan.FromSeconds(durationSec);
            scaleAnim.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.StartAnimation(nameof(visual.Scale), scaleAnim);
        }
        catch { }
    }

    private static void AttachSpin(UIElement element, double durationSec)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            if (element is FrameworkElement fe)
            {
                visual.CenterPoint = new System.Numerics.Vector3((float)(fe.Width / 2), (float)(fe.Height / 2), 0);
            }

            var rotAnim = compositor.CreateScalarKeyFrameAnimation();
            rotAnim.InsertKeyFrame(0.0f, 0f);
            rotAnim.InsertKeyFrame(1.0f, 360f);
            rotAnim.Duration = TimeSpan.FromSeconds(durationSec);
            rotAnim.IterationBehavior = AnimationIterationBehavior.Forever;

            visual.StartAnimation(nameof(visual.RotationAngleInDegrees), rotAnim);
        }
        catch { }
    }
}
