using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Views;

/// <summary>
/// A RichEditBox that can show the link hand: WinUI keeps the cursor on a protected member, so
/// the composer's attachment pills ask this subclass to switch it while the pointer is over one.
/// </summary>
public sealed partial class PillRichEditBox : RichEditBox
{
    private bool _hand;

    public void SetHandCursor(bool hand)
    {
        if (_hand == hand)
        {
            return;
        }

        _hand = hand;
        ProtectedCursor = hand ? InputSystemCursor.Create(InputSystemCursorShape.Hand) : null;
    }
}
