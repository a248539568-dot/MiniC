using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;

namespace MiniC.Widgets;

internal sealed class WidgetDateTimePicker : StackPanel
{
    internal event Action? ValueChanged;
    private readonly WidgetDateCalendar _calendar;
    private readonly Button _dateButton;
    private readonly Popup _datePopup;
    private readonly ComboBox _hour;
    private readonly ComboBox _minute;
    private readonly CheckBox? _none;
    private readonly DateTimeOffset? _initialValue;

    public WidgetDateTimePicker(DateTimeOffset? value, bool optional, DateTime? initialDate = null)
    {
        _initialValue = value;
        var local = value?.ToLocalTime() ?? DateTimeOffset.Now;
        var hasDate = value is not null || initialDate is not null;
        _calendar = new WidgetDateCalendar(initialDate?.Date ?? local.Date);
        _dateButton = WidgetFormStyles.Button(_calendar.SelectedDate.ToString("yyyy-MM-dd"), (_, _) => { });
        _dateButton.Height = 36;
        _dateButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        _dateButton.Margin = new Thickness(0);
        _dateButton.ToolTip = "选择日期";
        _datePopup = new Popup
        {
            PlacementTarget = _dateButton, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(248, 45, 52, 60)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 0), Child = _calendar
            }
        };
        _dateButton.Click += (_, _) =>
        {
            ResizeDatePopup();
            _datePopup.IsOpen = !_datePopup.IsOpen;
        };
        SizeChanged += (_, _) => { if (_datePopup.IsOpen) ResizeDatePopup(); };
        _calendar.DateSelected += selected =>
        {
            _dateButton.Content = selected.ToString("yyyy-MM-dd");
            _datePopup.IsOpen = false;
            ValueChanged?.Invoke();
        };
        _hour = TimeChoice(24, local.Hour, "小时");
        _minute = TimeChoice(60, local.Minute, "分钟");
        _hour.SelectionChanged += (_, _) => ValueChanged?.Invoke();
        _minute.SelectionChanged += (_, _) => ValueChanged?.Invoke();
        if (optional)
        {
            _none = new CheckBox { Content = "不设置时间", IsChecked = !hasDate,
                Margin = new Thickness(0, 0, 0, 6) };
            WidgetFormStyles.Apply(_none, "FormCheck");
            _none.Checked += (_, _) => { SetEnabled(false); ValueChanged?.Invoke(); };
            _none.Unchecked += (_, _) => { SetEnabled(true); ValueChanged?.Invoke(); };
            Children.Add(_none);
        }
        var shortcuts = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (caption, day) in new[] { ("今天", DateTime.Today), ("明天", DateTime.Today.AddDays(1)) })
        {
            var button = WidgetFormStyles.Button(caption, (_, _) =>
            {
                _calendar.Select(day);
                if (_none is { } none) none.IsChecked = false;
            });
            button.Padding = new Thickness(12, 5, 12, 5);
            button.ToolTip = $"选择{caption}";
            shortcuts.Children.Add(button);
        }
        Children.Add(shortcuts);
        var timeRow = new Grid();
        timeRow.ColumnDefinitions.Add(new ColumnDefinition());
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        timeRow.ColumnDefinitions.Add(new ColumnDefinition());
        timeRow.Children.Add(_hour);
        var separator = WidgetContentControls.Text(":", 14);
        separator.TextAlignment = TextAlignment.Center;
        Grid.SetColumn(separator, 1);
        timeRow.Children.Add(separator);
        Grid.SetColumn(_minute, 2);
        timeRow.Children.Add(_minute);
        Children.Add(new WidgetResponsiveRow(_dateButton, timeRow, 280,
            System.Windows.HorizontalAlignment.Stretch, stretchSecondary: true));
        SetEnabled(_none?.IsChecked != true);
        Unloaded += (_, _) =>
        {
            _datePopup.IsOpen = false;
            _hour.IsDropDownOpen = false;
            _minute.IsDropDownOpen = false;
        };
    }

    private static ComboBox TimeChoice(int count, int selected, string tooltip)
    {
        var combo = new ComboBox { MinWidth = 52, Height = 36,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ItemsSource = Enumerable.Range(0, count).Select(i => i.ToString("00")).ToArray(),
            SelectedIndex = selected, ToolTip = tooltip };
        WidgetFormStyles.Apply(combo, "FormChoice");
        return combo;
    }

    private void ResizeDatePopup()
    {
        if (_datePopup.Child is Border surface)
            surface.Width = Math.Clamp(ActualWidth, 196, 320);
    }

    private void SetEnabled(bool enabled)
    {
        _dateButton.IsEnabled = enabled;
        _hour.IsEnabled = enabled;
        _minute.IsEnabled = enabled;
        if (!enabled) _datePopup.IsOpen = false;
    }

    public bool TryGetUtc(out DateTimeOffset? utc)
    {
        utc = null;
        if (_none?.IsChecked == true) return true;
        if (_hour.SelectedIndex < 0 || _minute.SelectedIndex < 0) return false;
        if (_initialValue is { } original)
        {
            var local = original.ToLocalTime();
            if (_calendar.SelectedDate == local.Date && _hour.SelectedIndex == local.Hour && _minute.SelectedIndex == local.Minute)
            {
                utc = original.ToUniversalTime();
                return true;
            }
        }
        return WidgetDateTime.TryToUtc(_calendar.SelectedDate, _hour.SelectedIndex, _minute.SelectedIndex, out utc);
    }
}
