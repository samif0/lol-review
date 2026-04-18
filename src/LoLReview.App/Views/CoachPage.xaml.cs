#nullable enable

using LoLReview.App.Helpers;
using LoLReview.App.Services;
using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace LoLReview.App.Views;

public sealed partial class CoachPage : Page
{
    public CoachChatViewModel ViewModel { get; }

    public CoachPage()
    {
        ViewModel = App.GetService<CoachChatViewModel>();
        InitializeComponent();
        ViewModel.Messages.CollectionChanged += (_, _) => ScrollToBottom();
        Loaded += (_, _) =>
        {
            AnimationHelper.AnimatePageEnter(RootGrid);
            // Hook KeyDown with handledEventsToo so we beat TextBox's internal
            // Enter handling even when AcceptsReturn=True.
            InputBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnInputKeyDown), handledEventsToo: true);
            InputBox.Focus(FocusState.Programmatic);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Accept a pre-scoped navigation argument. Shape: CoachScopeArgs.
        if (e.Parameter is CoachScopeArgs args)
        {
            ViewModel.NewConversationCommand.Execute(null);
            ViewModel.PinScope(args.Scope, args.Label);
            if (!string.IsNullOrWhiteSpace(args.SeedQuestion))
            {
                ViewModel.InputText = args.SeedQuestion;
            }
        }
    }

    private void ScrollToBottom()
    {
        // Defer to let the ItemsControl layout first.
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            ChatScroll.UpdateLayout();
            ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, disableAnimation: false);
        });
    }

    private void OnClearScopeClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearScope();
    }

    private void OnThinkingRootLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Parent is FrameworkElement parent)
        {
            // The Grid ancestor owns the Storyboard resource named
            // "ThinkingStoryboard" inside its Resources dictionary.
            if (parent.Resources.TryGetValue("ThinkingStoryboard", out var resource)
                && resource is Microsoft.UI.Xaml.Media.Animation.Storyboard sb)
            {
                try { sb.Begin(); } catch { }
            }
        }
    }

    private void OnThinkingRootUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Parent is FrameworkElement parent)
        {
            if (parent.Resources.TryGetValue("ThinkingStoryboard", out var resource)
                && resource is Microsoft.UI.Xaml.Media.Animation.Storyboard sb)
            {
                try { sb.Stop(); } catch { }
            }
        }
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl+Enter / Shift+Enter inserts newline, plain Enter sends.
        if (e.Key != Windows.System.VirtualKey.Enter) return;

        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (shift || ctrl)
        {
            // Let the newline insert.
            return;
        }

        // Flush current text from the TextBox into the VM in case the
        // x:Bind PropertyChanged trigger hasn't landed yet, then send.
        if (sender is TextBox tb)
        {
            ViewModel.InputText = tb.Text;
        }

        e.Handled = true;

        if (ViewModel.SendCommand.CanExecute(null))
        {
            ViewModel.SendCommand.Execute(null);
        }
    }
}

/// <summary>Navigation argument for deep-linking from other pages.</summary>
public sealed record CoachScopeArgs(CoachScope Scope, string Label, string? SeedQuestion = null);
