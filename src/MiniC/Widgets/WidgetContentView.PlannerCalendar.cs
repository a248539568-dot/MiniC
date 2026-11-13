using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace MiniC.Widgets;

public sealed partial class WidgetContentView
{
    private DateTime? _plannerScheduleDay;
    private bool _plannerAlmanacExpanded;

    private void OpenPlannerDay(DateTime date)
    {
        _selectedDay = _plannerScheduleDay = date.Date;
        PlannerPage = "日程";
        _plannerReadingEntryId = null;
        _selectedEntry = null;
        RenderBody();
        Scroll.ScrollToTop();
    }

    private static string[] PlannerDayDescriptions(CalendarDayInfo info) =>
        new[] { $"农历 {info.LunarDate}", info.SolarTerm,
            info.OfficialStatus is null ? "" : $"中国放假调休：{info.OfficialName} {info.OfficialStatus}".Trim() }
        .Concat(info.Observances.Except(info.InternationalObservances).Where(name => name != info.OfficialName))
        .Concat(info.InternationalObservances.Select(name => $"国际纪念日：{name}"))
        .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct().ToArray();

    private Button PlannerCalendarDay(DateTime date, bool selected, int count)
    {
        var info = CalendarDayInfoProvider.Get(date);
        var foreground = selected ? PlannerAccentTextBrush : ForegroundBrush;
        var button = PlannerQuietButton("", (_, _) => OpenPlannerDay(date));
        button.Tag = date;
        button.Height = 51;
        button.MinHeight = 0;
        button.Margin = new Thickness(1, 1, 1, 3);
        button.Padding = new Thickness(1, 2, 1, 2);
        button.Foreground = foreground;
        button.Background = selected ? PlannerAccentBrush : Brushes.Transparent;
        button.BorderBrush = date == DateTime.Today && !selected ? PlannerAccentBrush : Brushes.Transparent;
        button.BorderThickness = new Thickness(1);
        button.Template = PlannerButtonTemplate(new CornerRadius(8));
        // Capture the calendar's selected background, rather than the quiet button's transparent idle state.
        var idle = button.Background;
        button.MouseLeave += (_, _) => button.Background = idle;
        button.ToolTip = string.Join("\n", new[] { date.ToString("yyyy年M月d日 dddd", PlannerCulture) }
            .Concat(PlannerDayDescriptions(info)).Append($"{count} 项待办或日程"));
        System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        var day = Text(date.Day.ToString(CultureInfo.InvariantCulture), 13, foreground);
        day.TextAlignment = TextAlignment.Center;
        var number = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        number.Children.Add(day);
        content.Children.Add(number);
        if (info.OfficialStatus is not null)
        {
            var badge = Text(info.OfficialStatus == "放假" ? "休" : "班", 8, foreground);
            badge.Margin = new Thickness(1, 0, 0, 0);
            badge.VerticalAlignment = VerticalAlignment.Top;
            number.Children.Add(badge);
        }
        var caption = !string.IsNullOrEmpty(info.SolarTerm) ? info.SolarTerm
            : info.Observances.FirstOrDefault() ?? info.Lunar;
        var label = Text(caption, 9, selected ? foreground :
            !string.IsNullOrEmpty(info.SolarTerm) || info.Observances.Count > 0 ? PlannerAccentBrush : PlannerMutedBrush);
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.TextAlignment = TextAlignment.Center;
        Grid.SetRow(label, 1);
        content.Children.Add(label);
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 3, Height = 3, Fill = foreground,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Visibility = count > 0 ? Visibility.Visible : Visibility.Hidden
        };
        Grid.SetRow(dot, 2);
        content.Children.Add(dot);
        button.Content = content;
        return button;
    }

    private WidgetEntry[] PlannerScheduleEntries() => _store.Entries(State.Id)
        .Where(entry => !entry.Completed && (_plannerScheduleDay is { } day
            ? entry.DueUtc?.ToLocalTime().Date == day
            : entry.EntryKind == "Schedule"))
        .OrderBy(entry => entry.DueUtc).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();

    private void RenderPlannerSchedules()
    {
        var date = _plannerScheduleDay;
        var header = new Grid { Margin = new Thickness(8, 4, 8, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Text(date?.ToString("M月d日 dddd", PlannerCulture) ?? "日程", 18);
        title.FontWeight = FontWeights.Light;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(title);
        var add = PlannerNewEntryButton("Schedule");
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        _body.Children.Add(header);
        if (date is { } day)
        {
            var all = PlannerQuietButton("全部日程", (_, _) =>
            {
                _plannerScheduleDay = null;
                _selectedDay = null;
                RenderBody();
                Scroll.ScrollToTop();
            });
            var controls = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            controls.Children.Add(PlannerIconButton("\uE787", "返回日历", (_, _) =>
            {
                _shownMonth = day;
                PlannerPage = "日历";
                RenderBody();
                Scroll.ScrollToTop();
            }));
            controls.Children.Add(PlannerIconButton("\uE76B", "前一天", (_, _) => OpenPlannerDay(day.AddDays(-1))));
            controls.Children.Add(PlannerQuietButton("今天", (_, _) => OpenPlannerDay(DateTime.Today)));
            controls.Children.Add(PlannerIconButton("\uE76C", "后一天", (_, _) => OpenPlannerDay(day.AddDays(1))));
            _body.Children.Add(new WidgetResponsiveRow(all, controls, 240) { Margin = new Thickness(8, 0, 8, 8) });
            RenderPlannerAlmanac(day);
        }
        var entries = PlannerScheduleEntries();
        foreach (var entry in entries)
        {
            var caption = entry.EntryKind == "Schedule" ? "日程" : "待办";
            if (date is null) caption = entry.DueUtc?.ToLocalTime().ToString("yyyy年M月d日", PlannerCulture) ?? "未设置日期";
            _body.Children.Add(CreatePlannerEntryRow(entry, caption,
                entry.EntryKind == "Schedule" ? PlannerAccentBrush : FrozenBrush(Color.FromRgb(244, 190, 116)),
                () => OpenPlannerReading(entry, PlannerScheduleEntries())));
        }
        if (entries.Length == 0) AddPlannerListEmptyState(overview: false);
    }

    private void RenderPlannerAlmanac(DateTime day)
    {
        var info = CalendarDayInfoProvider.Get(day);
        var panel = new StackPanel { Margin = new Thickness(8, 0, 8, 14) };
        panel.Children.Add(Text(string.Join(" · ", PlannerDayDescriptions(info)), 11, PlannerMutedBrush));
        var details = new StackPanel
        {
            Margin = new Thickness(0, 4, 0, 4),
            Visibility = _plannerAlmanacExpanded ? Visibility.Visible : Visibility.Collapsed
        };
        details.Children.Add(Text(info.Ganzhi, 11, PlannerMutedBrush));
        details.Children.Add(Text($"宜  {string.Join("、", info.Appropriate)}", 11));
        details.Children.Add(Text($"忌  {string.Join("、", info.Avoid)}", 11, PlannerMutedBrush));
        details.Children.Add(Text("民俗参考 · lunar-csharp", 10, PlannerMutedBrush));
        var toggle = PlannerQuietButton("", (_, _) => { });
        toggle.ToolTip = "展开或收起黄历";
        toggle.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        toggle.Padding = new Thickness(0, 4, 6, 4);
        var heading = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        heading.Children.Add(Text("黄历", 12));
        var chevron = WidgetFormStyles.Icon(_plannerAlmanacExpanded ? "\uE70E" : "\uE70D");
        chevron.Margin = new Thickness(7, 0, 0, 0);
        heading.Children.Add(chevron);
        toggle.Content = heading;
        toggle.Click += (_, _) =>
        {
            _plannerAlmanacExpanded = !_plannerAlmanacExpanded;
            chevron.Text = _plannerAlmanacExpanded ? "\uE70E" : "\uE70D";
            var expanded = _plannerAlmanacExpanded;
            var from = details.Visibility == Visibility.Collapsed ? 0 : details.Opacity;
            details.BeginAnimation(OpacityProperty, null);
            if (!SystemParameters.ClientAreaAnimation)
            {
                details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                details.Opacity = 1;
                return;
            }
            details.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(from, expanded ? 1 : 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) =>
            {
                if (!expanded && !_plannerAlmanacExpanded) details.Visibility = Visibility.Collapsed;
            };
            details.BeginAnimation(OpacityProperty, fade);
        };
        panel.Children.Add(toggle);
        panel.Children.Add(details);
        _body.Children.Add(panel);
    }
}
