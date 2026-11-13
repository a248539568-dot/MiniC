using System.Windows.Controls.Primitives;

namespace MiniC.Widgets;

/// <summary>Balances hourly forecasts across rows while preserving readable tile widths.</summary>
internal sealed class WeatherHourlyGrid : UniformGrid
{
    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        var count = Children.Count;
        if (count > 0)
        {
            const double minimumTileWidth = 72;
            var capacity = double.IsPositiveInfinity(constraint.Width)
                ? count : (int)Math.Clamp(Math.Floor(constraint.Width / minimumTileWidth), 1, count);
            var rowCount = (int)Math.Ceiling((double)count / capacity);
            Columns = (int)Math.Ceiling((double)count / rowCount);
        }
        return base.MeasureOverride(constraint);
    }
}
