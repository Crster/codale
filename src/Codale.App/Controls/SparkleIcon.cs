using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Codale.App.Controls;

/// <summary>
/// The sparkle icon: a large four-point star with a small one at its top right. Segoe Fluent
/// Icons has no sparkle (E945, often borrowed for it, is a lightning bolt), so it is drawn.
/// Size it with Width/Height; it is filled with <see cref="Control.Foreground"/>, like a FontIcon.
/// </summary>
public sealed partial class SparkleIcon : UserControl
{
    /// <summary>The outline on a 16×16 grid.</summary>
    public const string PathData =
        "M6.5,3 C6.9,6.6 9.4,9.1 13,9.5 C9.4,9.9 6.9,12.4 6.5,16 C6.1,12.4 3.6,9.9 0,9.5 C3.6,9.1 6.1,6.6 6.5,3 Z " +
        "M13,0 C13.2,1.6 14.4,2.8 16,3 C14.4,3.2 13.2,4.4 13,6 C12.8,4.4 11.6,3.2 10,3 C11.6,2.8 12.8,1.6 13,0 Z";

    public SparkleIcon()
    {
        IsTabStop = false;
        var path = new Path { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), PathData) };
        path.SetBinding(Shape.FillProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = path };
    }
}
