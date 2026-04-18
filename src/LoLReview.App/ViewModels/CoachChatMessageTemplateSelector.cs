#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.ViewModels;

/// <summary>
/// Selects user vs assistant bubble templates in the Coach chat list.
/// </summary>
public sealed class CoachChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is CoachChatMessageViewModel m && m.IsUser)
            return UserTemplate;
        return AssistantTemplate;
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
