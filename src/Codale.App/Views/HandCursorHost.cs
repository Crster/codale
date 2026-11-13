using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Views;

/// <summary>
/// A panel that shows the link hand while the pointer is over it. WinUI keeps the cursor
/// on a protected member, so anything that should read as clickable (attachment pills in a
/// message, for one) is wrapped in this host.
/// </summary>
public sealed partial class HandCursorHost : Grid
{
    public HandCursorHost()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}
