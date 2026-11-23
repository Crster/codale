using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Codale.Browser;

/// <summary>One entry in the bell's list: a download, a saved password, a permission decision, a passkey.</summary>
public sealed record NotificationItem(string Title, string Body, DateTimeOffset Time)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>
/// The browser's notifications. Each one is kept in the bell's list; while the window is in the background
/// a notification can also raise a Windows toast, so a finished download is seen without looking at the window.
/// </summary>
public sealed class NotificationCenter
{
    private bool _toastsRegistered;

    public ObservableCollection<NotificationItem> Items { get; } = [];

    public int Count => Items.Count;

    /// <param name="title">Short headline.</param>
    /// <param name="body">Detail line.</param>
    /// <param name="toast">Also raise a Windows toast (use for events the user may want to see while the window is in the background).</param>
    public void Post(string title, string body, bool toast = false)
    {
        Items.Insert(0, new NotificationItem(title, body, DateTimeOffset.UtcNow));
        while (Items.Count > 100) Items.RemoveAt(Items.Count - 1);
        if (toast) ShowToast(title, body);
    }

    public void Clear() => Items.Clear();

    private void ShowToast(string title, string body)
    {
        try
        {
            if (!_toastsRegistered)
            {
                AppNotificationManager.Default.Register();
                _toastsRegistered = true;
            }

            AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            // Toasts are a nicety: the bell still lists the event.
            Debug.WriteLine(ex);
        }
    }
}
