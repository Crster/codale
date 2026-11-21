using Codale.App.ViewModels;
using Codale.App.Services;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Codale.App.Views;

/// <summary>A specialist conversation occupying the existing plan/document preview column.</summary>
public sealed partial class SpecialistPane : UserControl
{
    private readonly SpecialistItem _specialist;
    private bool _followEnd = true;

    public SpecialistPane(SpecialistItem specialist)
    {
        InitializeComponent();
        _specialist = specialist;
        DataContext = specialist;
        Loaded += (_, _) =>
        {
            specialist.Messages.CollectionChanged += OnMessagesChanged;
            specialist.PropertyChanged += OnSpecialistChanged;
            FollowEnd();
        };
        Unloaded += (_, _) =>
        {
            specialist.Messages.CollectionChanged -= OnMessagesChanged;
            specialist.PropertyChanged -= OnSpecialistChanged;
        };
    }

    private async void OnCopyMessageClick(object sender, RoutedEventArgs e)
    {
        var text = (sender as FrameworkElement)?.Tag switch
        {
            UserMessageItem user => user.Text,
            SpecialistAnswersItem answers => string.Join("\n\n", answers.Pairs.Select(p => $"{p.Question}\n→ {p.Answer}")),
            AssistantMessageItem assistant => assistant.Text,
            _ => "",
        };
        await ClipboardWriter.SetTextAsync(text);
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _specialist.IsRevising)
        {
            e.Handled = true;
            _specialist.CancelReviseCommand.Execute(null);
            return;
        }
        if (e.Key != VirtualKey.Enter || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        if (_specialist.CanSend && _specialist.SendInputCommand.CanExecute(null))
        {
            e.Handled = true;
            _specialist.SendInputCommand.Execute(null);
        }
    }

    private void OnConversationViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        _followEnd = ConversationScroller.ScrollableHeight - ConversationScroller.VerticalOffset < 32;

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => FollowEnd();
    private void OnSpecialistChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpecialistItem.HasQuestions)) FollowEnd();
        // Revise opens the input in place of the actions; put the caret there.
        if (e.PropertyName == nameof(SpecialistItem.IsRevising) && _specialist.IsRevising)
            DispatcherQueue.TryEnqueue(() => InputBox.Focus(FocusState.Programmatic));
    }
    private void FollowEnd()
    {
        if (_followEnd) DispatcherQueue.TryEnqueue(() =>
        {
            ConversationScroller.UpdateLayout();
            ConversationScroller.ChangeView(null, ConversationScroller.ScrollableHeight, null, true);
        });
    }
}
