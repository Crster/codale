using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Codale.Browser;

/// <summary>The strip along the top of the Network/Console panel; it shows the up-down resize cursor and the window drags it.</summary>
public sealed partial class PanelSizer : Grid
{
    public PanelSizer()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}
