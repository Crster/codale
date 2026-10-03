using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Views;

/// <summary>
/// The transcript is a heterogeneous list: user prompts, turn timelines, answers,
/// the odd standalone tool call (a plan lifted out of its turn) and notices each get
/// their own shape rather than being flattened into one bubble style.
/// </summary>
public sealed partial class ChatItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? ToolTemplate { get; set; }
    public DataTemplate? ActivityTemplate { get; set; }
    public DataTemplate? NoticeTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        UserMessageItem => UserTemplate,
        AssistantMessageItem => AssistantTemplate,
        ToolCallItem => ToolTemplate,
        TurnActivityItem => ActivityTemplate,
        NoticeItem => NoticeTemplate,
        _ => base.SelectTemplateCore(item),
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}

/// <summary>
/// The steps inside a turn's timeline: each sits on the rail with a dot in its own
/// colour - narration, a tool call, an explore group, the task-list row.
/// </summary>
public sealed partial class StepTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NarrationTemplate { get; set; }
    public DataTemplate? ToolTemplate { get; set; }
    public DataTemplate? ExploreTemplate { get; set; }
    public DataTemplate? TodoTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        AssistantMessageItem => NarrationTemplate,
        ToolCallItem => ToolTemplate,
        ExploreGroupStep => ExploreTemplate,
        TodoStepItem => TodoTemplate,
        _ => base.SelectTemplateCore(item),
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
