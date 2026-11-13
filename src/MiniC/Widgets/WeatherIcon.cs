using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace MiniC.Widgets;

/// <summary>读取项目内的 SVG path 并以 WPF 矢量 DrawingImage 绘制；只接受内置路径。</summary>
internal static class WeatherIcon
{
    private static readonly Dictionary<WeatherSymbol, ImageSource> Cache = new();
    public static ImageSource For(WeatherSymbol symbol)
    {
        if (Cache.TryGetValue(symbol, out var image)) return image;
        var uri = new Uri($"pack://application:,,,/MiniC;component/Widgets/WeatherIcons/{symbol}.svg");
        using var stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
        var svg = XDocument.Load(stream);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(System.Windows.Media.Brushes.Transparent, null, new RectangleGeometry(new System.Windows.Rect(0, 0, 64, 64))));
        foreach (var path in svg.Root!.Elements().Where(element => element.Name.LocalName == "path"))
        {
            var geometry = Geometry.Parse((string)path.Attribute("d")!);
            var fill = (string?)path.Attribute("fill");
            group.Children.Add(new GeometryDrawing(fill is null ? null : (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(fill)!, null, geometry));
        }
        group.Freeze();
        var result = new DrawingImage(group); result.Freeze(); Cache[symbol] = result; return result;
    }
}

