#nullable enable

using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Views;

/// <summary>Tilt check page — guided multi-step exercise to assess and reset mental state.</summary>
public sealed partial class TiltCheckPage : Page
{
    public TiltCheckViewModel ViewModel { get; }

    public TiltCheckPage()
    {
        ViewModel = App.GetService<TiltCheckViewModel>();
        InitializeComponent();
    }

    /// <summary>Helper for x:Bind — returns Visible when currentStep matches target.</summary>
    public Visibility IsStepVisible(int currentStep, int targetStep)
        => currentStep == targetStep ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Helper for x:Bind — returns Visible when text is non-empty.</summary>
    public Visibility HasText(string? text)
        => !string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
}
