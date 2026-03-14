using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Controls;

public class ResizeGripper : Grid
{
    public ResizeGripper()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}
