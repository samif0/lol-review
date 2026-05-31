#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Revu.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Revu.App.Controls;

/// <summary>v2.16.1: rotates through a list of <see cref="IntelCard"/>s on a
/// timer with a short crossfade. Used on PreGamePage to surface passive
/// learning context (priority objective, last game, matchup notes, enemy
/// abilities) during champ select dead time. Pauses on hover so the user
/// can finish reading a card.
/// </summary>
public sealed partial class IntelRotatorControl : UserControl
{
    private readonly DispatcherTimer _rotationTimer;
    private readonly TimeSpan _rotationInterval = TimeSpan.FromSeconds(7);
    private int _currentIndex = -1;
    private bool _paused;

    // v2.17.5: once the user steers with the arrow keys, auto-rotation goes
    // away for the rest of this control's lifetime. The intent is "I want to
    // dwell on this card" — re-engaging auto-rotation 7s later would just
    // yank them off it again. Reset on Unload so the next time PreGamePage
    // is shown we start fresh in auto mode.
    private bool _userTookControl;

    public IntelRotatorControl()
    {
        InitializeComponent();
        _rotationTimer = new DispatcherTimer { Interval = _rotationInterval };
        _rotationTimer.Tick += (_, _) => Advance();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PointerEntered += (_, _) => _paused = true;
        PointerExited += (_, _) => _paused = false;

        // v2.17.5: enable focus + arrow-key driving. UserControl is not a
        // focusable element by default; flipping IsTabStop lets the framework
        // route key events to us once a click or Tab lands focus here.
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        KeyDown += OnKeyDown;
        // Clicking anywhere on the card grabs focus so subsequent arrows work
        // without the user having to Tab through the page first.
        Tapped += (_, _) => Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ItemsSource is null || ItemsSource.Count <= 1) return;

        if (e.Key == VirtualKey.Left)
        {
            StepBy(-1);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Right)
        {
            StepBy(+1);
            e.Handled = true;
        }
    }

    private void StepBy(int delta)
    {
        if (ItemsSource is null || ItemsSource.Count == 0) return;

        _userTookControl = true;
        _rotationTimer.Stop();

        var count = ItemsSource.Count;
        var next = (_currentIndex + delta) % count;
        if (next < 0) next += count;
        _currentIndex = next;
        ApplyCurrentCard(animate: true);
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IReadOnlyList<IntelCard>),
            typeof(IntelRotatorControl),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public IReadOnlyList<IntelCard>? ItemsSource
    {
        get => (IReadOnlyList<IntelCard>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not IntelRotatorControl c) return;

        if (e.OldValue is INotifyCollectionChanged oldNotify)
            oldNotify.CollectionChanged -= c.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNotify)
            newNotify.CollectionChanged += c.OnCollectionChanged;

        c.RebuildDots();
        c.JumpToFirst();

        // Start or stop the timer based on whether there is more than one card.
        // Guard with IsLoaded so we don't start the timer before OnLoaded fires.
        if (c.IsLoaded && !c._userTookControl)
        {
            if (c.ItemsSource is not null && c.ItemsSource.Count > 1)
                c._rotationTimer.Start();
            else
                c._rotationTimer.Stop();
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildDots();
        // If the deck repopulated and we don't have a valid index, restart.
        if (ItemsSource is not null && (_currentIndex < 0 || _currentIndex >= ItemsSource.Count))
            JumpToFirst();
        // Keep timer in sync with item count.
        if (!_userTookControl)
        {
            if (ItemsSource is not null && ItemsSource.Count > 1)
                _rotationTimer.Start();
            else
                _rotationTimer.Stop();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ItemsSource is INotifyCollectionChanged notify)
            notify.CollectionChanged += OnCollectionChanged;
        // Reset on re-load so the next visit to PreGamePage starts in auto mode.
        _userTookControl = false;
        JumpToFirst();
        // Only start rotation when there is more than one card to rotate through.
        if (ItemsSource is not null && ItemsSource.Count > 1)
            _rotationTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _rotationTimer.Stop();
        _userTookControl = false;
        if (ItemsSource is INotifyCollectionChanged notify)
            notify.CollectionChanged -= OnCollectionChanged;
    }

    private void JumpToFirst()
    {
        if (ItemsSource is null || ItemsSource.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            CardSurface.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyHint.Visibility = Visibility.Collapsed;
        CardSurface.Visibility = Visibility.Visible;
        _currentIndex = 0;
        ApplyCurrentCard(animate: false);
    }

    private void Advance()
    {
        if (_paused) return;
        if (_userTookControl) return; // belt-and-suspenders; timer is also Stop()ped
        if (ItemsSource is null || ItemsSource.Count <= 1) return;
        _currentIndex = (_currentIndex + 1) % ItemsSource.Count;
        ApplyCurrentCard(animate: true);
    }

    private void ApplyCurrentCard(bool animate)
    {
        if (ItemsSource is null || _currentIndex < 0 || _currentIndex >= ItemsSource.Count) return;
        var card = ItemsSource[_currentIndex];

        if (animate)
        {
            // Quick fade-out → swap text → fade-in. Composition opacity on
            // the whole card surface is cheap and the rest of the layout
            // doesn't need to recompute.
            var fadeOut = new DoubleAnimation
            {
                From = 1.0, To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(fadeOut, CardSurface);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            var sbOut = new Storyboard();
            sbOut.Children.Add(fadeOut);
            sbOut.Completed += (_, _) =>
            {
                SetCardText(card);
                var fadeIn = new DoubleAnimation
                {
                    From = 0.0, To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(fadeIn, CardSurface);
                Storyboard.SetTargetProperty(fadeIn, "Opacity");
                var sbIn = new Storyboard();
                sbIn.Children.Add(fadeIn);
                sbIn.Begin();
            };
            sbOut.Begin();
        }
        else
        {
            SetCardText(card);
            CardSurface.Opacity = 1.0;
        }

        UpdateDots();
    }

    private void SetCardText(IntelCard card)
    {
        EyebrowText.Text = card.Eyebrow;
        EyebrowText.Visibility = string.IsNullOrEmpty(card.Eyebrow) ? Visibility.Collapsed : Visibility.Visible;
        HeadlineText.Text = card.Headline;
        BodyText.Text = card.Body;
        BodyText.Visibility = string.IsNullOrEmpty(card.Body) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RebuildDots()
    {
        DotsRow.Children.Clear();
        if (ItemsSource is null || ItemsSource.Count <= 1) return;
        for (int i = 0; i < ItemsSource.Count; i++)
        {
            DotsRow.Children.Add(new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = (Brush)Application.Current.Resources["MutedTextBrush"],
                Opacity = 0.5,
            });
        }
        UpdateDots();
    }

    private void UpdateDots()
    {
        if (DotsRow.Children.Count == 0) return;
        var active = (Brush)Application.Current.Resources["AccentGoldBrush"];
        var dim = (Brush)Application.Current.Resources["MutedTextBrush"];
        for (int i = 0; i < DotsRow.Children.Count; i++)
        {
            if (DotsRow.Children[i] is Ellipse dot)
            {
                dot.Fill = i == _currentIndex ? active : dim;
                dot.Opacity = i == _currentIndex ? 1.0 : 0.5;
            }
        }
    }
}
