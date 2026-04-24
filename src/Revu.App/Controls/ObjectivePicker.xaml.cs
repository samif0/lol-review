#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Revu.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Revu.App.Controls;

/// <summary>
/// v2.15.7: reusable tag-search control. A TextBox + Popup + ListView
/// replaces WinUI's AutoSuggestBox so we avoid the two-click-to-select and
/// hover-confusion issues inherent in the AutoSuggest flyout lifecycle.
///
/// Source is a flat list of <see cref="TagOption"/>s — Objective headers
/// interleaved with their Prompt children. Filtering matches against
/// <see cref="TagOption.SearchText"/>, so typing "spells" matches a prompt
/// labelled "Key spells during trading" even though the visible Title shows
/// "Objective • Key spells during trading".
///
/// Optional <see cref="Payload"/> passes an arbitrary object through to the
/// event arg so parent code can distinguish instances (e.g. the BookmarkItem
/// that owns a per-clip picker).
/// </summary>
public sealed partial class ObjectivePicker : UserControl
{
    public ObjectivePicker()
    {
        InitializeComponent();
        SuggestionsList.ItemsSource = _visibleOptions;
        SizeChanged += (_, _) => SyncPopupWidth();

        // v2.15.7 fix: IsLightDismissEnabled on Popup is unreliable when the
        // popup is anchored to a control inside a ScrollViewer (which the
        // VodPlayer's sidebar is). Hook XamlRoot.Content's PointerPressed so
        // we can manually detect outside-clicks and close.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var root = XamlRoot?.Content as UIElement;
        if (root is not null)
        {
            root.AddHandler(PointerPressedEvent,
                new PointerEventHandler(OnRootPointerPressed),
                handledEventsToo: true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var root = XamlRoot?.Content as UIElement;
        if (root is not null)
        {
            root.RemoveHandler(PointerPressedEvent,
                new PointerEventHandler(OnRootPointerPressed));
        }
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!SuggestionPopup.IsOpen) return;
        var hit = e.OriginalSource as DependencyObject;
        if (hit is null) { SuggestionPopup.IsOpen = false; return; }

        if (IsDescendantOf(hit, SearchBox) || IsDescendantOf(hit, SuggestionPopupBorder))
        {
            return;
        }
        SuggestionPopup.IsOpen = false;
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // ── Dependency properties ───────────────────────────────────────

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(
            nameof(Source),
            typeof(ObservableCollection<TagOption>),
            typeof(ObjectivePicker),
            new PropertyMetadata(null, OnSourceChanged));

    public ObservableCollection<TagOption>? Source
    {
        get => (ObservableCollection<TagOption>?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ObjectivePicker picker) return;

        // v2.15.7 fix: TagOptions is populated asynchronously after the page
        // binds. Without a CollectionChanged subscription the picker would
        // only see the snapshot at bind-time (often empty), and prompt rows
        // added later would never appear in the dropdown.
        if (e.OldValue is INotifyCollectionChanged oldNotify)
        {
            oldNotify.CollectionChanged -= picker.OnSourceCollectionChanged;
        }
        if (e.NewValue is INotifyCollectionChanged newNotify)
        {
            newNotify.CollectionChanged += picker.OnSourceCollectionChanged;
        }
        picker.RecomputeVisibleOptions();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RecomputeVisibleOptions();
    }

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(
            nameof(PlaceholderText),
            typeof(string),
            typeof(ObjectivePicker),
            new PropertyMetadata("Type to search...", (d, e) =>
            {
                if (d is ObjectivePicker p)
                {
                    p.SearchBox.PlaceholderText = e.NewValue as string ?? "";
                }
            }));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public static readonly DependencyProperty PayloadProperty =
        DependencyProperty.Register(
            nameof(Payload),
            typeof(object),
            typeof(ObjectivePicker),
            new PropertyMetadata(null));

    /// <summary>Opaque caller-supplied payload (e.g. a BookmarkItem) echoed back
    /// on <see cref="TagChosen"/>.</summary>
    public object? Payload
    {
        get => GetValue(PayloadProperty);
        set => SetValue(PayloadProperty, value);
    }

    // v2.15.7: current-state display. The TextBox doubles as the
    // current-state indicator. Parents set this when their underlying state
    // changes (e.g. VM.SelectedObjectiveId / SelectedPromptId flips) so the
    // picker re-paints.
    public static readonly DependencyProperty SelectedTitleProperty =
        DependencyProperty.Register(
            nameof(SelectedTitle),
            typeof(string),
            typeof(ObjectivePicker),
            new PropertyMetadata("", OnSelectedTitleChanged));

    /// <summary>The title to show in the TextBox when no search is active.</summary>
    public string SelectedTitle
    {
        get => (string)GetValue(SelectedTitleProperty);
        set => SetValue(SelectedTitleProperty, value);
    }

    private static void OnSelectedTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ObjectivePicker p && !p._isEditingSearch)
        {
            p.SearchBox.Text = (string)(e.NewValue ?? "");
        }
    }

    // ── Events ──────────────────────────────────────────────────────

    public sealed class TagChosenEventArgs : EventArgs
    {
        public TagOption Option { get; }
        public object? Payload { get; }
        public TagChosenEventArgs(TagOption option, object? payload)
        {
            Option = option;
            Payload = payload;
        }
    }

    /// <summary>Fires immediately when the user picks a suggestion (click or Enter).</summary>
    public event EventHandler<TagChosenEventArgs>? TagChosen;

    // ── Internal state ──────────────────────────────────────────────

    private readonly ObservableCollection<TagOption> _visibleOptions = new();
    private bool _isEditingSearch;

    private void RecomputeVisibleOptions()
    {
        _visibleOptions.Clear();
        if (Source is null) return;

        var q = (SearchBox?.Text ?? "").Trim();
        IEnumerable<TagOption> filtered = Source;
        if (!string.IsNullOrEmpty(q))
        {
            filtered = filtered
                .Where(o => o.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(o => o.SearchText.StartsWith(q, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var o in filtered.Take(80))
        {
            _visibleOptions.Add(o);
        }
    }

    private void SyncPopupWidth()
    {
        if (SuggestionPopup.Child is FrameworkElement fe)
        {
            fe.Width = Math.Max(240, SearchBox.ActualWidth);
        }
    }

    private void OpenPopupAnchoredToTextBox()
    {
        if (Source is null || Source.Count == 0) return;
        SyncPopupWidth();
        SuggestionPopup.HorizontalOffset = 0;
        SuggestionPopup.VerticalOffset = SearchBox.ActualHeight + 2;
        SuggestionPopup.IsOpen = true;
    }

    // ── Event handlers ──────────────────────────────────────────────

    private void OnSearchGotFocus(object sender, RoutedEventArgs e)
    {
        _isEditingSearch = true;
        SearchBox.Text = "";
        RecomputeVisibleOptions();
        OpenPopupAnchoredToTextBox();
    }

    private void OnSearchLostFocus(object sender, RoutedEventArgs e)
    {
        _isEditingSearch = false;
        SearchBox.Text = SelectedTitle ?? "";
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isEditingSearch) return;
        RecomputeVisibleOptions();
        if (!SuggestionPopup.IsOpen) OpenPopupAnchoredToTextBox();
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Down)
        {
            if (_visibleOptions.Count > 0)
            {
                SuggestionsList.SelectedIndex = 0;
                var container = SuggestionsList.ContainerFromIndex(0) as Control;
                container?.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var chosen = SuggestionsList.SelectedItem as TagOption
                         ?? (_visibleOptions.Count == 1 ? _visibleOptions[0] : null);
            if (chosen is not null)
            {
                CommitChoice(chosen);
                e.Handled = true;
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            SuggestionPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnSuggestionItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TagOption opt)
        {
            CommitChoice(opt);
        }
    }

    private void OnPopupClosed(object? sender, object e)
    {
        SuggestionsList.SelectedItem = null;
    }

    private void OnPopupKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            SuggestionPopup.IsOpen = false;
            SearchBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (SuggestionsList.SelectedItem is TagOption opt)
            {
                CommitChoice(opt);
                e.Handled = true;
            }
        }
    }

    private void CommitChoice(TagOption opt)
    {
        SuggestionPopup.IsOpen = false;
        _isEditingSearch = false;
        SearchBox.Text = opt.Title;
        TagChosen?.Invoke(this, new TagChosenEventArgs(opt, Payload));
    }
}
