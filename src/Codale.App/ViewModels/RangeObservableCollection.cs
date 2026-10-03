using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can swap its whole content in one
/// notification. Clear-then-add loops raise a change per item, and a bound list
/// re-measures after each; <see cref="ReplaceAll"/> raises a single Reset instead.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces the content with <paramref name="items"/>; may be a view over this collection.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        // Materialised first: the source can be a query over this very collection.
        var fresh = items.ToList();

        CheckReentrancy();

        if (fresh.Count == 0 && Count == 0)
        {
            return;
        }

        Items.Clear();
        foreach (var item in fresh)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
