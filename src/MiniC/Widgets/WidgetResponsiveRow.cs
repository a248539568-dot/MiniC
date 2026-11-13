using System.Windows;
using System.Windows.Controls;

namespace MiniC.Widgets;

/// <summary>Reflows existing children during layout without replacing input or timer state.</summary>
internal sealed class WidgetResponsiveRow : Grid
{
    private readonly double _inlineWidth;
    private readonly FrameworkElement _secondary;
    private readonly System.Windows.HorizontalAlignment _stackedAlignment;
    private readonly bool _stretchSecondary;
    private bool _stacked;

    internal WidgetResponsiveRow(FrameworkElement primary, FrameworkElement secondary, double inlineWidth,
        System.Windows.HorizontalAlignment stackedAlignment = System.Windows.HorizontalAlignment.Left,
        bool stretchSecondary = false)
    {
        _inlineWidth = inlineWidth;
        _secondary = secondary;
        _stackedAlignment = stackedAlignment;
        _stretchSecondary = stretchSecondary;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = stretchSecondary ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(primary);
        Children.Add(secondary);
        SetColumn(secondary, 2);
        secondary.HorizontalAlignment = stretchSecondary
            ? System.Windows.HorizontalAlignment.Stretch : System.Windows.HorizontalAlignment.Right;
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        var stacked = constraint.Width < _inlineWidth;
        if (_stacked != stacked)
        {
            _stacked = stacked;
            SetColumnSpan(Children[0], stacked ? 3 : 1);
            SetColumn(_secondary, stacked ? 0 : 2);
            SetColumnSpan(_secondary, stacked ? 3 : 1);
            SetRow(_secondary, stacked ? 2 : 0);
            ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 8);
            ColumnDefinitions[2].Width = stacked ? new GridLength(0)
                : _stretchSecondary ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            RowDefinitions[1].Height = new GridLength(stacked ? 6 : 0);
            _secondary.HorizontalAlignment = stacked
                ? _stackedAlignment
                : _stretchSecondary ? System.Windows.HorizontalAlignment.Stretch : System.Windows.HorizontalAlignment.Right;
        }
        return base.MeasureOverride(constraint);
    }
}
