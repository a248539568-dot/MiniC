using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace MiniC.Widgets;

/// <summary>A date selection surface using the same controls as the widget, without a native calendar skin.</summary>
internal sealed class WidgetDateCalendar : StackPanel
{
    private DateTime _month;
    internal DateTime SelectedDate { get; private set; }
    internal event Action<DateTime>? DateSelected;

    internal WidgetDateCalendar(DateTime selected)
    {
        SelectedDate = selected.Date;
        _month = new DateTime(selected.Year, selected.Month, 1);
        MinWidth = 168;
        RenderMonth();
    }

    internal void Select(DateTime date)
    {
        SelectedDate = date.Date;
        _month = new DateTime(date.Year, date.Month, 1);
        RenderMonth();
        DateSelected?.Invoke(SelectedDate);
    }

    private void RenderMonth()
    {
        Children.Clear();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        foreach (var (step, column, icon, tooltip) in new[]
            { (-1, 0, "\uE76B", "上个月"), (1, 2, "\uE76C", "下个月") })
        {
            var button = WidgetFormStyles.Button("", (_, _) => { _month = _month.AddMonths(step); RenderMonth(); });
            button.Content = WidgetFormStyles.Icon(icon, 12);
            button.ToolTip = tooltip;
            button.Margin = new Thickness(0);
            button.Padding = new Thickness(0);
            button.Height = 30;
            Grid.SetColumn(button, column);
            header.Children.Add(button);
        }
        var month = WidgetContentControls.Text(_month.ToString("yyyy年M月", CultureInfo.InvariantCulture));
        month.TextAlignment = TextAlignment.Center;
        Grid.SetColumn(month, 1);
        header.Children.Add(month);
        Children.Add(header);
        var weekdays = new UniformGrid { Columns = 7 };
        foreach (var day in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var text = WidgetContentControls.Text(day, 11);
            text.TextAlignment = TextAlignment.Center;
            text.Opacity = 0.6;
            text.Margin = new Thickness(0, 0, 0, 6);
            weekdays.Children.Add(text);
        }
        Children.Add(weekdays);
        var grid = new UniformGrid { Columns = 7, Rows = 6 };
        var first = _month.AddDays(-((int)_month.DayOfWeek + 6) % 7);
        for (var index = 0; index < 42; index++)
        {
            var date = first.AddDays(index);
            var button = WidgetFormStyles.Button(date.Day.ToString(CultureInfo.InvariantCulture), (_, _) => Select(date));
            button.Margin = new Thickness(1);
            button.Padding = new Thickness(0);
            button.MinHeight = 0;
            button.Height = 30;
            button.Tag = date;
            button.ToolTip = date.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);
            button.Background = date == SelectedDate ? new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)) : Brushes.Transparent;
            button.BorderBrush = date == SelectedDate || date == DateTime.Today
                ? new SolidColorBrush(Color.FromArgb(150, 218, 237, 243)) : Brushes.Transparent;
            button.Opacity = date.Month == _month.Month ? 1 : 0.4;
            grid.Children.Add(button);
        }
        Children.Add(grid);
    }
}
