using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using WinMessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace MiniC.Widgets;

public sealed partial class WidgetContentView
{
    private static readonly Brush ForegroundBrush = Brushes.White;
    private static readonly Brush MutedBrush = FrozenBrush(Color.FromRgb(217, 225, 238));
    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    private static TextBlock Text(string content, int size = 13, Brush? color = null) => WidgetContentControls.Text(content, size, color);
    private static Border Card(UIElement child, Thickness? padding = null) => WidgetContentControls.Card(child, padding);
    private static TextBox Input(string? text = null, bool multiline = false) => WidgetContentControls.Input(text, multiline);
    private static Button Action(string caption, RoutedEventHandler click, bool accent = false) => WidgetContentControls.Action(caption, click, accent);
    private static StackPanel Row(params UIElement[] elements) => WidgetContentControls.Row(elements);
    private static bool FromInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button or TextBox) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    public void RenderBody()
    {
        _weatherRequests.Cancel();
        if (State.Kind != WidgetKind.Planner) _plannerClockTimer?.Stop();
        ClearPlannerNavigation();
        _plannerDeleteConfirmation = null;
        _plannerTaskCount = null;
        _plannerDeleteSelectedButton = _plannerClearCompletedButton = null;
        _body.Children.Clear();
        _body.Visibility = Visibility.Visible;
        switch (State.Kind)
        {
            case WidgetKind.Note: RenderNote(); break;
            case WidgetKind.Tasks: RenderTasks(); break;
            case WidgetKind.Schedule: RenderSchedule(); break;
            case WidgetKind.Calendar: RenderCalendar(); break;
            case WidgetKind.Weather: RenderWeather(); break;
            case WidgetKind.Planner: RenderPlanner(); break;
        }
        if (State.Kind is WidgetKind.Note or WidgetKind.Schedule ||
            State.Kind == WidgetKind.Tasks && (_selectedEntry is not null || _store.Attachments(State.Id).Any(a => a.EntryId is null)))
            RenderAttachments();
    }
    private void RenderNote()
    {
        var editor = Input(State.Body, multiline: true); editor.MinHeight = 195;
        editor.TextChanged += (_, _) => { State.Body = editor.Text; SaveSoon(); };
        var preview = new StackPanel { Visibility = Visibility.Collapsed };
        _body.Children.Add(Row(Action("编辑", (_, _) => { editor.Visibility = Visibility.Visible; preview.Visibility = Visibility.Collapsed; }),
            Action("预览", (_, _) => { RenderMarkdown(preview); editor.Visibility = Visibility.Collapsed; preview.Visibility = Visibility.Visible; })));
        _body.Children.Add(editor); _body.Children.Add(preview);
        var reminder = Input(State.ReminderUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        reminder.Width = 164;
        _body.Children.Add(Card(new StackPanel { Children = { Text("提醒时间（年-月-日 时:分）", 12, MutedBrush),
            Row(reminder, Action("设置", (_, _) => { if (TryLocalTime(reminder.Text, out var date))
                { State.ReminderUtc = date; State.NotifiedUtc = null; SaveSoon(); } else ShowDateError(); }),
                Action("清除", (_, _) => { State.ReminderUtc = null; reminder.Text = ""; SaveSoon(); })) } }, new Thickness(10)));
    }
    private void RenderMarkdown(StackPanel preview)
    {
        preview.Children.Clear();
        var attachments = _store.Attachments(State.Id).ToDictionary(a => a.Id);
        foreach (var line in State.Body.Split('\n'))
        {
            var text = line.TrimEnd('\r'); var size = 13;
            if (text.StartsWith("### ")) { size = 16; text = text[4..]; }
            else if (text.StartsWith("## ")) { size = 19; text = text[3..]; }
            else if (text.StartsWith("# ")) { size = 23; text = text[2..]; }
            else if (text.StartsWith("- ")) text = "•  " + text[2..];
            else if (text.StartsWith("> ")) text = "│  " + text[2..];
            var image = Regex.Match(text, @"^!\[(.*?)\]\(attachment://([a-f0-9]{32})\)$");
            if (image.Success && attachments.TryGetValue(image.Groups[2].Value, out var file)
                && File.Exists(_store.AttachmentPath(file)))
            {
                try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(_store.AttachmentPath(file));
                    bitmap.DecodePixelWidth = 600; bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
                    preview.Children.Add(new Image { Source = bitmap, MaxHeight = 240, Stretch = Stretch.Uniform,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 9) }); }
                catch (Exception) { preview.Children.Add(Text(file.Name)); }
                continue;
            }
            var block = Text("", size); block.Margin = new Thickness(0, 2, 0, 4);
            foreach (Match token in Regex.Matches(text, @"(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`|\[[^]]+\]\(https?://[^)]+\))|[^*`\[]+|."))
            {
                var value = token.Value;
                var run = new System.Windows.Documents.Run(value);
                if (value.StartsWith("**") && value.EndsWith("**")) { run.Text = value[2..^2]; run.FontWeight = FontWeights.SemiBold; }
                else if (value.StartsWith('*') && value.EndsWith('*') && value.Length > 2) { run.Text = value[1..^1]; run.FontStyle = FontStyles.Italic; }
                else if (value.StartsWith('`') && value.EndsWith('`')) { run.Text = value[1..^1]; run.Background = new SolidColorBrush(Color.FromArgb(100, 18, 27, 40)); }
                else if (value.StartsWith('[')) { var match = Regex.Match(value, @"^\[([^]]+)\]\((https?://[^)]+)\)$");
                    if (match.Success && Uri.TryCreate(match.Groups[2].Value, UriKind.Absolute, out var url))
                    { var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(match.Groups[1].Value));
                        link.Click += (_, _) => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                        block.Inlines.Add(link); continue; } }
                block.Inlines.Add(run);
            }
            preview.Children.Add(block);
        }
    }
    private static bool TryLocalTime(string text, out DateTimeOffset? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local)) return false;
        if (TimeZoneInfo.Local.IsInvalidTime(local)) return false;
        date = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime(); return true;
    }
    private static void ShowDateError() => WinMessageBox.Show("请选择有效日期和时间。", "时间格式", MessageBoxButton.OK, MessageBoxImage.Information);
    private void RedrawTasks(bool showDetails = false)
    {
        var offset = Scroll.VerticalOffset;
        RenderBody();
        Dispatcher.BeginInvoke(() => {
            if (showDetails && _selectedEntry is not null)
                _body.Children.OfType<Border>().FirstOrDefault(border => Equals(border.Tag, "TaskDetails"))?.BringIntoView();
            else Scroll.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.Loaded);
    }
    private void RenderTasks()
    {
        var tasks = _store.Entries(State.Id);
        var today = DateTime.Today;
        var todaysTasks = tasks.Count(entry => !entry.Completed && entry.DueUtc?.ToLocalTime().Date == today);
        _body.Children.Add(Text($"今天 · {todaysTasks} 项", 15));
        var remaining = tasks.Count(entry => !entry.Completed);
        _body.Children.Add(Text($"{remaining} 项待办  ·  {tasks.Count - remaining} 项已完成", 12, MutedBrush));
        var quick = Input(); quick.ToolTip = "输入待办名称，按回车添加";
        quick.MinWidth = 110;
        void AddTask()
        {
            var name = quick.Text.Trim();
            if (name.Length == 0) return;
            if (name.Length > 200) { WinMessageBox.Show("待办名称请控制在 200 字以内。", "待办"); return; }
            _store.Save(new WidgetEntry { WidgetId = State.Id, Title = name });
            _selectedEntry = null; RedrawTasks();
        }
        quick.KeyDown += (_, e) => { if (e.Key != Key.Enter) return; e.Handled = true; AddTask(); };
        var add = Action("+", (_, _) => AddTask(), true); add.ToolTip = "新增待办";
        add.Width = 32; add.Margin = new Thickness(7, 0, 0, 0);
        var inputRow = new Grid { Margin = new Thickness(0, 0, 0, 11) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(quick); Grid.SetColumn(add, 1); inputRow.Children.Add(add);
        _body.Children.Add(inputRow);

        if (tasks.Count == 0)
        {
            var hint = Text("还没有待办，先记下第一件事。", 13, MutedBrush);
            hint.Margin = new Thickness(3, 18, 0, 0); _body.Children.Add(hint); return;
        }
        var list = new StackPanel();
        var ordered = tasks.OrderBy(entry => entry.Completed)
            .ThenBy(entry => entry.Completed ? 3 : entry.DueUtc?.ToLocalTime().Date < today ? 0
                : entry.DueUtc?.ToLocalTime().Date == today ? 1 : 2)
            .ThenBy(entry => entry.DueUtc).ToList();
        var section = "";
        foreach (var entry in ordered)
        {
            var group = entry.Completed ? "已完成" : entry.DueUtc?.ToLocalTime().Date < today ? "已逾期"
                : entry.DueUtc?.ToLocalTime().Date == today ? "今天" : "稍后 / 未定时间";
            if (section != group)
            {
                var label = Text(group, 11, MutedBrush);
                label.Margin = new Thickness(7, list.Children.Count == 0 ? 3 : 14, 0, 7);
                list.Children.Add(label);
                section = group;
            }
            var selected = _selectedEntry?.Id == entry.Id;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 2), MinHeight = 47 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            var toggle = Action("", (_, _) => {
                entry.Completed = !entry.Completed; _store.Save(entry); RedrawTasks();
            });
            toggle.Width = 36; toggle.Height = 36; toggle.Padding = new Thickness(9);
            toggle.Margin = new Thickness(0);
            toggle.Background = Brushes.Transparent;
            toggle.ToolTip = entry.Completed ? "标记未完成" : "标记完成";
            toggle.Content = new System.Windows.Shapes.Path {
                Effect = MiniC.Services.GroupMaterialBrushes.ContentShadow,
                Data = entry.Completed ? Geometry.Parse("M3,9 L7,13 L14,4") : Geometry.Empty,
                Stroke = entry.Completed ? Brushes.White : MutedBrush,
                StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = 13, Height = 13, Stretch = Stretch.Uniform
            };
            var checkSurface = new Border { Width = 21, Height = 21, CornerRadius = new CornerRadius(11),
                Effect = MiniC.Services.GroupMaterialBrushes.ContentShadow,
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(130, 233, 241, 252)),
                Background = entry.Completed ? new SolidColorBrush(Color.FromRgb(72, 132, 208)) : Brushes.Transparent,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false };
            var check = new Grid(); check.Children.Add(checkSurface); check.Children.Add(toggle);
            row.Children.Add(check);
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 6, 0, 6) };
            var title = Text(entry.Title, 13, entry.Completed ? MutedBrush : ForegroundBrush);
            if (entry.Completed) title.TextDecorations = TextDecorations.Strikethrough;
            title.TextTrimming = TextTrimming.CharacterEllipsis; title.TextWrapping = TextWrapping.NoWrap;
            content.Children.Add(title);
            if (!string.IsNullOrWhiteSpace(entry.Body))
            {
                var summary = Text(entry.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "", 11, MutedBrush);
                summary.TextTrimming = TextTrimming.CharacterEllipsis;
                summary.TextWrapping = TextWrapping.NoWrap;
                content.Children.Add(summary);
            }
            if (entry.DueUtc is { } due)
            {
                var date = Text(due.ToLocalTime().ToString("M月d日 HH:mm", CultureInfo.InvariantCulture), 11, MutedBrush);
                date.Cursor = Cursors.Hand;
                date.ToolTip = "修改截止时间";
                date.MouseLeftButtonUp += (_, e) => { e.Handled = true; _selectedEntry = entry; RedrawTasks(showDetails: true); };
                content.Children.Add(date);
            }
            Grid.SetColumn(content, 1); row.Children.Add(content);
            var arrow = new System.Windows.Shapes.Path { Data = Geometry.Parse("M6,3 L11,8 L6,13"),
                Effect = MiniC.Services.GroupMaterialBrushes.ContentShadow,
                Stroke = MutedBrush, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, Width = 12, Height = 12, Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(arrow, 2); row.Children.Add(arrow);
            var surface = new Border { CornerRadius = new CornerRadius(9), Child = row, Cursor = Cursors.Hand,
                Background = selected ? new SolidColorBrush(Color.FromArgb(85, 70, 117, 172)) : Brushes.Transparent };
            surface.MouseEnter += (_, _) => { if (!selected) surface.Background = new SolidColorBrush(Color.FromArgb(48, 140, 170, 205)); };
            surface.MouseLeave += (_, _) => { if (!selected) surface.Background = Brushes.Transparent; };
            surface.MouseLeftButtonUp += (_, e) => {
                if (FromInteractive(e.OriginalSource as DependencyObject)) return;
                _selectedEntry = selected ? null : entry; RedrawTasks(showDetails: !selected);
            };
            list.Children.Add(surface);
        }
        _body.Children.Add(Card(list, new Thickness(7, 6, 7, 8)));
        if (_selectedEntry is { } selectedEntry) RenderTaskDetails(selectedEntry);
    }
    private void RenderTaskDetails(WidgetEntry entry)
    {
        var title = Input(entry.Title); title.Margin = new Thickness(0, 3, 0, 9);
        var details = Input(entry.Body, multiline: true); details.MinHeight = 56;
        details.Margin = new Thickness(0, 3, 0, 9);
        var when = new WidgetDateTimePicker(entry.DueUtc, optional: true)
        { Margin = new Thickness(0, 3, 0, 9) };
        var remind = Input(entry.ReminderUtc is null || entry.DueUtc is null ? "" :
            Math.Max(0, (int)(entry.DueUtc.Value - entry.ReminderUtc.Value).TotalMinutes).ToString(CultureInfo.InvariantCulture));
        remind.Width = 59; remind.Margin = new Thickness(0, 3, 0, 11);
        var editor = new StackPanel();
        editor.Children.Add(Text("待办详情", 14));
        editor.Children.Add(Text("名称", 11, MutedBrush)); editor.Children.Add(title);
        editor.Children.Add(Text("说明", 11, MutedBrush)); editor.Children.Add(details);
        editor.Children.Add(Text("截止时间", 11, MutedBrush)); editor.Children.Add(when);
        editor.Children.Add(Text("提前提醒（分钟，留空不提醒）", 11, MutedBrush)); editor.Children.Add(remind);
        editor.Children.Add(Row(Action("保存", (_, _) => {
            var name = title.Text.Trim();
            if (name.Length is < 1 or > 200) { WinMessageBox.Show("请输入 1–200 字的待办名称。", "待办"); return; }
            if (!when.TryGetUtc(out var due)) { ShowDateError(); return; }
            if (!string.IsNullOrWhiteSpace(remind.Text) && (!int.TryParse(remind.Text, out var minutes) || minutes is < 0 or > 10080))
            { WinMessageBox.Show("提醒分钟数须在 0–10080 之间。", "待办"); return; }
            var advance = string.IsNullOrWhiteSpace(remind.Text) ? (int?)null : int.Parse(remind.Text, CultureInfo.InvariantCulture);
            entry.Title = name; entry.Body = details.Text; entry.DueUtc = due;
            entry.ReminderUtc = due?.AddMinutes(-(advance ?? 0));
            if (advance is null) entry.ReminderUtc = null;
            entry.NotifiedUtc = null; _store.Save(entry); _selectedEntry = null; RedrawTasks();
        }, true), Action("取消", (_, _) => { _selectedEntry = null; RedrawTasks(); }),
            Action("删除", (_, _) => { _store.DeleteEntry(entry.Id); _selectedEntry = null; RedrawTasks(); })));
        var detailCard = Card(editor); detailCard.Tag = "TaskDetails";
        _body.Children.Add(detailCard);
    }
    private void RenderSchedule()
    {
        var selected = _selectedEntry;
        var title = Input(selected?.Title); title.Width = 158;
        var when = new WidgetDateTimePicker(selected?.DueUtc ?? DateTimeOffset.Now.AddHours(1), optional: false);
        var remind = Input(selected?.ReminderUtc is { } reminder && selected.DueUtc is { } selectedDue
            ? Math.Max(0, (int)(selectedDue - reminder).TotalMinutes).ToString(CultureInfo.InvariantCulture)
            : selected is null ? "10" : "");
        remind.Width = 50;
        var details = Input(selected?.Body, multiline: true); details.MinHeight = 58;
        _body.Children.Add(Card(new StackPanel { Children = { Text(selected is null ? "新建日程" : "编辑日程", 15),
            Row(title), Text("说明（可选）", 11, MutedBrush), Row(details),
            Text("开始时间", 11, MutedBrush), when,
            Text("提前提醒（分钟，留空则不提醒）", 11, MutedBrush), Row(remind),
            Row(Action("保存", (_, _) => {
                if (title.Text.Trim().Length is < 1 or > 200)
                { WinMessageBox.Show("请输入 1–200 字的日程名称。", "日程"); return; }
                if (!when.TryGetUtc(out var date)) { ShowDateError(); return; }
                if (date is null) { ShowDateError(); return; }
                if (!string.IsNullOrWhiteSpace(remind.Text) && (!int.TryParse(remind.Text, out var minutes) || minutes is < 0 or > 10080))
                { WinMessageBox.Show("提醒分钟数须在 0–10080 之间。"); return; }
                var advance = string.IsNullOrWhiteSpace(remind.Text) ? (int?)null : int.Parse(remind.Text);
                var entry = selected ?? new WidgetEntry { WidgetId = State.Id };
                entry.Title = title.Text.Trim(); entry.Body = details.Text; entry.DueUtc = date;
                entry.ReminderUtc = date?.AddMinutes(-(advance ?? 0));
                if (advance is null) entry.ReminderUtc = null;
                entry.NotifiedUtc = null; _store.Save(entry); _selectedEntry = null; RenderBody();
            }, true), Action("取消编辑", (_, _) => { _selectedEntry = null; RenderBody(); }))
        } }));
        var entries = _store.Entries(State.Id);
        if (entries.Count == 0) _body.Children.Add(Text("还没有日程", 12, MutedBrush));
        foreach (var entry in entries.OrderBy(e => e.DueUtc))
        {
            var row = new DockPanel();
            var select = Action("查看", (_, _) => { _selectedEntry = entry; RenderBody(); });
            DockPanel.SetDock(select, Dock.Left); row.Children.Add(select);
            var edit = Action("编辑", (_, _) => { _selectedEntry = entry; RenderBody(); }); DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            var info = new StackPanel(); info.Children.Add(Text(entry.Title, 13));
            if (entry.DueUtc is { } due) info.Children.Add(Text(due.ToLocalTime().ToString("M月d日 HH:mm"), 11, MutedBrush));
            if (!string.IsNullOrWhiteSpace(entry.Body)) info.Children.Add(Text(entry.Body, 11, MutedBrush));
            row.Children.Add(info);
            _body.Children.Add(Card(row));
        }
        if (selected is not null)
        {
            _body.Children.Add(Row(Action("删除当前条目", (_, _) => { _store.DeleteEntry(selected.Id); _selectedEntry = null; RenderBody(); })));
        }
    }
    private void RenderCalendar()
    {
        var selected = _selectedDay?.Date ?? DateTime.Today;
        var heading = Text(_shownMonth.ToString("yyyy 年 M 月"), 16);
        _body.Children.Add(Row(Action("‹", (_, _) => { _shownMonth = _shownMonth.AddMonths(-1); _selectedDay = new DateTime(_shownMonth.Year, _shownMonth.Month, 1); RenderBody(); }),
            heading, Action("›", (_, _) => { _shownMonth = _shownMonth.AddMonths(1); _selectedDay = new DateTime(_shownMonth.Year, _shownMonth.Month, 1); RenderBody(); }),
            Action("今天", (_, _) => { _shownMonth = DateTime.Today; _selectedDay = DateTime.Today; RenderBody(); })));
        var entries = _store.DatedEntries();
        var byDay = entries.GroupBy(item => item.Entry.DueUtc!.Value.ToLocalTime().Date)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var grid = new UniformGrid { Columns = 7 };
        foreach (var label in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var weekday = Text(label, 11, MutedBrush);
            weekday.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            grid.Children.Add(weekday);
        }
        var first = new DateTime(_shownMonth.Year, _shownMonth.Month, 1);
        for (var pad = 0; pad < ((int)first.DayOfWeek + 6) % 7; pad++) grid.Children.Add(new Border());
        for (var day = 1; day <= DateTime.DaysInMonth(first.Year, first.Month); day++)
        {
            var value = first.AddDays(day - 1);
            byDay.TryGetValue(value, out var daily);
            var count = daily?.Count(item => !item.Entry.Completed) ?? 0;
            var dayInfo = CalendarDayInfoProvider.Get(value);
            var caption = dayInfo.OfficialStatus == "调休上班" ? "班"
                : dayInfo.OfficialStatus == "放假" ? "休" : null;
            var marker = !string.IsNullOrEmpty(dayInfo.SolarTerm) ? dayInfo.SolarTerm
                : dayInfo.Observances.FirstOrDefault() ?? dayInfo.Lunar;
            var button = Action($"{day}{(count > 0 ? " ·" : "")}{(caption is null ? "" : $" {caption}")}",
                (_, _) => { _selectedDay = value; RenderBody(); });
            var markerLabel = Text(marker, 10, MutedBrush);
            markerLabel.TextWrapping = TextWrapping.NoWrap;
            markerLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            button.Content = new StackPanel { Children = { Text($"{day}{(count > 0 ? " ·" : "")}{(caption is null ? "" : $" {caption}")}", 12),
                markerLabel } };
            button.Margin = new Thickness(1); button.Padding = new Thickness(1); button.MinHeight = 42;
            if (value == selected) button.Background = new SolidColorBrush(Color.FromRgb(72, 132, 208));
            else if (value == DateTime.Today) button.BorderThickness = new Thickness(1);
            button.ToolTip = string.Join(" · ", new[] { value.ToString("M月d日"), dayInfo.Lunar,
                dayInfo.SolarTerm, dayInfo.OfficialName is null ? dayInfo.OfficialStatus : $"{dayInfo.OfficialName}{dayInfo.OfficialStatus}" }
                .Concat(dayInfo.Observances).Where(text => !string.IsNullOrWhiteSpace(text)));
            grid.Children.Add(button);
        }
        _body.Children.Add(Card(grid, new Thickness(5)));
        _body.Children.Add(Text(selected.ToString("M月d日 dddd"), 14));
        var selectedInfo = CalendarDayInfoProvider.Get(selected);
        var detail = new List<string> { $"农历 {selectedInfo.Lunar}" };
        if (selectedInfo.OfficialStatus is { } status)
            detail.Add($"{selectedInfo.OfficialName} {status}".Trim());
        if (!string.IsNullOrEmpty(selectedInfo.SolarTerm)) detail.Add(selectedInfo.SolarTerm);
        detail.AddRange(selectedInfo.Observances);
        _body.Children.Add(Text(string.Join("  ·  ", detail.Where(text => !string.IsNullOrWhiteSpace(text))), 11, MutedBrush));
        var almanac = new StackPanel { Margin = new Thickness(0, 6, 0, 9) };
        almanac.Children.Add(Text($"黄历 · {selectedInfo.Ganzhi}", 11, MutedBrush));
        almanac.Children.Add(Text($"宜  {string.Join("、", selectedInfo.Appropriate)}", 11, MutedBrush));
        almanac.Children.Add(Text($"忌  {string.Join("、", selectedInfo.Avoid)}", 11, MutedBrush));
        almanac.Children.Add(Text("宜忌仅供民俗参考 · 来源：lunar-csharp", 10, MutedBrush));
        _body.Children.Add(almanac);
        RenderCalendarWeather(selected);
        var selectedEntries = byDay.GetValueOrDefault(selected) ?? [];
        if (selectedEntries.Length == 0) _body.Children.Add(Text("这一天还没有安排", 12, MutedBrush));
        foreach (var item in selectedEntries.OrderBy(item => item.Entry.DueUtc).ThenBy(item => item.Kind))
        {
            var label = item.Kind == WidgetKind.Schedule ? "日程" : item.Entry.Completed ? "已完成" : "待办";
            var itemButton = Action($"{item.Entry.DueUtc!.Value.ToLocalTime():HH:mm}  {label} · {item.Entry.Title}",
                (_, _) =>
                {
                    CalendarEditingEntry = item;
                    CalendarDraftKind = null;
                    RenderBody();
                });
            itemButton.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
            itemButton.ToolTip = "查看并编辑";
            _body.Children.Add(itemButton);
        }
        _body.Children.Add(Row(Action("+ 待办", (_, _) => { CalendarEditingEntry = null; CalendarDraftKind = WidgetKind.Tasks; RenderBody(); }),
            Action("+ 日程", (_, _) => { CalendarEditingEntry = null; CalendarDraftKind = WidgetKind.Schedule; RenderBody(); })));
        if (CalendarEditingEntry is { } editing) RenderCalendarEditor(editing.Kind, selected, editing.Entry);
        else if (CalendarDraftKind is { } kind) RenderCalendarEditor(kind, selected);
    }

    private async void RenderCalendarWeather(DateTime selected)
    {
        if (selected.Date < DateTime.Today || selected.Date > DateTime.Today.AddDays(2)) return;
        var city = _store.Widgets().FirstOrDefault(widget => widget.Kind == WidgetKind.Weather)?.City;
        if (string.IsNullOrWhiteSpace(city)) return;
        var line = Text("天气加载中…", 11, MutedBrush);
        _body.Children.Add(line);
        var request = _weatherRequests.Begin();
        if (request is null) return;
        try
        {
            var weather = await _weather.GetAsync(city, token: request.Token);
            if (_closing || request.Token.IsCancellationRequested || (_selectedDay?.Date ?? DateTime.Today) != selected.Date) return;
            var forecast = weather.Forecast?.FirstOrDefault(day => day.Date == DateOnly.FromDateTime(selected));
            line.Text = forecast is null
                ? "所选日期暂无天气预报"
                : $"{weather.Place} · {forecast.Condition} · {forecast.Low:0}–{forecast.High:0}°  ({weather.Source})";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_closing && !request.Token.IsCancellationRequested) line.Text = "天气暂不可用"; }
        finally { _weatherRequests.Complete(request); }
    }

    private void RenderCalendarEditor(WidgetKind kind, DateTime date, WidgetEntry? existing = null)
    {
        var owner = _store.Widgets().FirstOrDefault(widget => widget.Id == existing?.WidgetId)
            ?? _store.Widgets().FirstOrDefault(widget => widget.Kind == kind);
        if (owner is null)
        {
            _body.Children.Add(Text($"尚无{(kind == WidgetKind.Tasks ? "待办" : "日程")}组件", 12, MutedBrush));
            _body.Children.Add(Action("创建日程中心", (_, _) => { CalendarDraftKind = null; _createWidget(WidgetKind.Planner); RenderBody(); }));
            return;
        }
        var title = Input(existing?.Title);
        var details = Input(existing?.Body, multiline: true); details.MinHeight = 52;
        var picker = new WidgetDateTimePicker(existing?.DueUtc,
            optional: kind == WidgetKind.Tasks, initialDate: existing is null ? date : null);
        var content = new StackPanel();
        content.Children.Add(Text($"{(existing is null ? "新建" : "编辑")}{(kind == WidgetKind.Tasks ? "待办" : "日程")}", 14));
        content.Children.Add(title);
        content.Children.Add(Text("说明", 11, MutedBrush)); content.Children.Add(details);
        content.Children.Add(Text("时间", 11, MutedBrush)); content.Children.Add(picker);
        System.Windows.Controls.CheckBox? completed = null;
        if (kind == WidgetKind.Tasks && existing is not null)
        {
            completed = new System.Windows.Controls.CheckBox { Content = "已完成", IsChecked = existing.Completed,
                Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 5) };
            content.Children.Add(completed);
        }
        content.Children.Add(Row(Action("保存", (_, _) =>
        {
            var name = title.Text.Trim();
            if (name.Length is < 1 or > 200) { WinMessageBox.Show("请输入 1–200 字的名称。", "新建事项"); return; }
            if (!picker.TryGetUtc(out var due) || kind == WidgetKind.Schedule && due is null) { ShowDateError(); return; }
            var entry = existing ?? new WidgetEntry { WidgetId = owner.Id };
            var dueChanged = existing is not null && existing.DueUtc != due;
            entry.Title = name; entry.Body = details.Text; entry.DueUtc = due;
            if (completed is not null) entry.Completed = completed.IsChecked == true;
            if (dueChanged) entry.NotifiedUtc = null;
            _store.Save(entry);
            if (due is { } scheduled)
            {
                _selectedDay = scheduled.ToLocalTime().Date;
                _shownMonth = _selectedDay.Value;
            }
            CalendarDraftKind = null;
            CalendarEditingEntry = null;
            RenderBody();
        }, true), Action("取消", (_, _) => { CalendarDraftKind = null; CalendarEditingEntry = null; RenderBody(); })));
        if (existing is not null)
            content.Children.Add(Action("删除", (_, _) =>
            {
                if (WinMessageBox.Show($"确定删除“{existing.Title}”？", "删除事项",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _store.DeleteEntry(existing.Id); CalendarEditingEntry = null; RenderBody();
            }));
        _body.Children.Add(Card(content));
    }

    private static readonly Brush PlannerMutedBrush = FrozenBrush(Color.FromArgb(190, 225, 232, 242));
    private static readonly Brush PlannerPanelBrush = FrozenBrush(Color.FromArgb(100, 28, 38, 49));
    private static readonly Brush PlannerPanelStrongBrush = FrozenBrush(Color.FromArgb(160, 40, 55, 72));
    private static readonly Brush PlannerLineBrush = FrozenBrush(Color.FromArgb(110, 96, 116, 140));
    private static readonly Brush PlannerAccentBrush = FrozenBrush(Color.FromRgb(231, 242, 255));
    private static readonly Brush PlannerAccentTextBrush = FrozenBrush(Color.FromRgb(30, 47, 68));
    private static readonly CultureInfo PlannerCulture = CultureInfo.GetCultureInfo("zh-CN");

    private static readonly Duration PlannerMotionDuration = new(TimeSpan.FromMilliseconds(220));

    private static void AnimatePlannerScale(Button button, double target)
    {
        if (button.RenderTransform is not ScaleTransform scale) return;
        var animation = new DoubleAnimation(target, PlannerMotionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void StartPlannerClock()
    {
        _plannerClockTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _plannerClockTimer.Tick -= PlannerClockTick;
        _plannerClockTimer.Tick += PlannerClockTick;
        PlannerClockTick(null, EventArgs.Empty);
        _plannerClockTimer.Start();
    }

    private void PlannerClockTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        if (_plannerClock is not null)
            _plannerClock.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (_plannerDate is not null)
            _plannerDate.Text = now.ToString("dddd，M月d日", PlannerCulture);
    }

    private void AnimatePlannerPage()
    {
        foreach (UIElement element in _body.Children)
        {
            element.Opacity = 0;
            if (element.RenderTransform is not TranslateTransform)
                element.RenderTransform = new TranslateTransform(0, 8);
            if (element.RenderTransform is TranslateTransform translate)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, PlannerMotionDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, PlannerMotionDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private static ControlTemplate PlannerButtonTemplate(CornerRadius radius)
        => WidgetFormStyles.ButtonTemplate(radius);

    private static Button PlannerAction(string caption, RoutedEventHandler click, bool accent = false,
        bool selected = false, CornerRadius? radius = null)
    {
        var button = Action(caption, click);
        button.Padding = new Thickness(10, 5, 10, 5);
        button.MinHeight = 28;
        button.Margin = new Thickness(0, 0, 5, 0);
        button.Foreground = ForegroundBrush;
        button.Background = accent || selected ? PlannerPanelStrongBrush : PlannerPanelBrush;
        button.BorderBrush = accent || selected ? new SolidColorBrush(Color.FromArgb(96, 255, 255, 255)) : Brushes.Transparent;
        button.BorderThickness = new Thickness(1);
        button.FocusVisualStyle = null;
        button.Template = PlannerButtonTemplate(radius ?? new CornerRadius(14));
        button.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        button.RenderTransform = new ScaleTransform(1, 1);
        var idleBackground = button.Background;
        var hoverBackground = PlannerPanelStrongBrush;
        button.MouseEnter += (_, _) =>
        {
            button.Background = hoverBackground;
            AnimatePlannerScale(button, 1.035);
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = idleBackground;
            AnimatePlannerScale(button, 1);
        };
        return button;
    }

    private static Button PlannerQuietButton(string caption, RoutedEventHandler click)
    {
        var button = Action(caption, click);
        button.Padding = new Thickness(7, 4, 7, 4);
        button.MinHeight = 26;
        button.Foreground = PlannerMutedBrush;
        button.Background = Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Template = PlannerButtonTemplate(new CornerRadius(8));
        button.Cursor = Cursors.Hand;
        var idleBackground = button.Background;
        button.MouseEnter += (_, _) => button.Background = PlannerPanelBrush;
        button.MouseLeave += (_, _) => button.Background = idleBackground;
        return button;
    }

    private static Button PlannerIconButton(string glyph, string tooltip, RoutedEventHandler click)
    {
        var button = PlannerQuietButton("", click);
        button.Content = WidgetFormStyles.Icon(glyph);
        button.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Width = 30;
        button.Padding = new Thickness(0);
        return button;
    }

    private static Button PlannerTabButton(string page, bool selected, RoutedEventHandler click)
    {
        var button = Action(page, click);
        button.Padding = new Thickness(2, 3, 2, 3);
        button.MinHeight = 32;
        button.FontSize = 11;
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        button.Foreground = selected ? ForegroundBrush : PlannerMutedBrush;
        button.Background = selected ? PlannerPanelStrongBrush : Brushes.Transparent;
        button.BorderBrush = selected ? new SolidColorBrush(Color.FromArgb(96, 255, 255, 255)) : Brushes.Transparent;
        button.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
        button.Template = PlannerButtonTemplate(new CornerRadius(17));
        button.Cursor = Cursors.Hand;
        return button;
    }

    private static Button PlannerChoiceButton(string caption, bool selected, RoutedEventHandler click)
    {
        var button = PlannerAction(caption, click, selected: selected, radius: new CornerRadius(9));
        button.Margin = new Thickness(2, 0, 2, 0);
        button.Padding = new Thickness(3, 4, 3, 4);
        return button;
    }

    private Border PlannerBottomNavigation()
    {
        var navigation = new Grid { Height = 38 };
        for (var index = 0; index < 5; index++)
            navigation.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (page, index) in new[] { ("概览", 0), ("专注", 1), ("日历", 2), ("日程", 3), ("待办", 4) })
        {
            var selected = page == PlannerPage;
            var button = PlannerTabButton(page, selected,
                (_, _) => { PlannerPage = page; _plannerReadingEntryId = null; _selectedEntry = null; RenderBody(); Scroll.ScrollToTop(); });
            button.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
            button.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
            button.Margin = new Thickness(1, 1, 1, 1);
            button.Padding = new Thickness(2, 2, 2, 2);
            button.ToolTip = page;
            Grid.SetColumn(button, index);
            navigation.Children.Add(button);
        }
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(78, 43, 53, 61)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(82, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(3, 3, 3, 3),
            Child = navigation
        };
    }

    private Button PlannerNewTaskButton() => PlannerNewEntryButton("Task");

    private Button PlannerNewEntryButton(string kind)
    {
        var button = Action("", (_, _) =>
        {
            _selectedEntry = null;
            if (kind == "Schedule" && PlannerPage == "日程")
                _selectedDay = _plannerScheduleDay ?? DateTime.Today;
            RenderPlannerEditor(kind);
        });
        button.ToolTip = kind == "Schedule" ? "新建日程" : "新建待办";
        System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
        button.Content = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M8,1 L8,15 M1,8 L15,8"), Stroke = ForegroundBrush,
            StrokeThickness = 2.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 16, Height = 16, Stretch = Stretch.Uniform,
            Effect = MiniC.Services.GroupMaterialBrushes.ContentShadow
        };
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Template = PlannerButtonTemplate(new CornerRadius(0));
        button.FocusVisualStyle = null;
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(0);
        button.Width = 32;
        button.Height = 32;
        return button;
    }

    private void RenderPlanner()
    {
        _plannerClock = null;
        _plannerDate = null;
        if (_plannerReadingEntryId is not null)
        {
            RenderPlannerReading();
            return;
        }
        switch (PlannerPage)
        {
            case "专注": RenderPlannerFocus(); break;
            case "日历": RenderPlannerCalendar(); break;
            case "日程": RenderPlannerSchedules(); break;
            case "待办": RenderPlannerTasks(); break;
            default: RenderPlannerOverview(); break;
        }
        ShowPlannerNavigation(PlannerBottomNavigation());
        StartPlannerClock();
        AnimatePlannerPage();
    }

    private void RenderPlannerOverview()
    {
        var entries = PlannerOverviewEntries();

        var now = Text(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), 44);
        _plannerClock = now;
        now.FontWeight = FontWeights.Light;
        now.VerticalAlignment = VerticalAlignment.Center;
        var side = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center };
        var weekday = Text(DateTime.Now.ToString("dddd，M月d日", PlannerCulture), 13);
        weekday.TextAlignment = TextAlignment.Right;
        _plannerDate = weekday;
        side.Children.Add(weekday);
        var hero = new WidgetResponsiveRow(now, side, 270) { Margin = new Thickness(8, 12, 8, 12) };
        _body.Children.Add(hero);

        if (entries.Length == 0) AddPlannerListEmptyState(overview: true);
        foreach (var entry in entries)
        {
            var schedule = entry.EntryKind == "Schedule";
            var row = CreatePlannerEntryRow(entry, schedule ? "日程" : "待办",
                schedule ? PlannerAccentBrush : FrozenBrush(Color.FromRgb(244, 190, 116)),
                () => OpenPlannerReading(entry, PlannerOverviewEntries()));
            _body.Children.Add(row);
        }
    }

    private static Border PlannerEntryLine(string caption, string title, Brush accent, UIElement trailing,
        bool muted = false, bool completed = false)
    {
        var content = new Grid { MinHeight = 58, Margin = new Thickness(8, 0, 8, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var marker = new Border { Width = 3, Height = 42, CornerRadius = new CornerRadius(2),
            Background = accent, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(marker);
        var text = new StackPanel { Margin = new Thickness(16, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(caption, 11, PlannerMutedBrush));
        var name = Text(title, 13, muted ? PlannerMutedBrush : ForegroundBrush);
        name.MaxHeight = 40;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        if (completed) name.TextDecorations = TextDecorations.Strikethrough;
        text.Children.Add(name);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        Grid.SetColumn(trailing, 2);
        content.Children.Add(trailing);
        var line = new Border { Child = content, BorderBrush = PlannerLineBrush, BorderThickness = new Thickness(0, 0, 0, 1) };
        return line;
    }

    private void RenderPlannerFocus()
    {
        _focusStatus = null;
        var focus = new StackPanel
        {
            MaxWidth = 440, Margin = new Thickness(8, 10, 8, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var hint = Text("把注意力留给眼前这一件事", 12, PlannerMutedBrush);
        hint.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        hint.Margin = new Thickness(0, 0, 0, 16);
        focus.Children.Add(hint);

        _focusClock = Text(FormatFocus(FocusRemaining), 40);
        _focusClock.FontWeight = FontWeights.Light;
        _focusClock.TextWrapping = TextWrapping.NoWrap;
        _focusClock.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        _focusClock.Margin = new Thickness(0, 0, 0, 18);
        focus.Children.Add(_focusClock);
        _focusStatus = Text(_focusTimer?.IsEnabled == true ? "专注中" : "准备开始", 10, PlannerMutedBrush);
        _focusStatus.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        _focusStatus.Margin = new Thickness(0, -12, 0, 13);
        focus.Children.Add(_focusStatus);

        var presets = new Grid { Height = 34, Margin = new Thickness(0, 0, 0, 19) };
        for (var index = 0; index < 4; index++) presets.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (minutes, index) in new[] { (15, 0), (25, 1), (45, 2), (60, 3) })
        {
            var button = PlannerChoiceButton(minutes.ToString(CultureInfo.InvariantCulture), FocusDuration == TimeSpan.FromMinutes(minutes), (_, _) =>
            {
                _focusTimer?.Stop();
                FocusDuration = TimeSpan.FromMinutes(minutes);
                FocusRemaining = FocusDuration;
                RenderBody();
            });
            button.Margin = new Thickness(index == 0 ? 0 : 2, 0, index == 3 ? 0 : 2, 0);
            button.Padding = new Thickness(2, 3, 2, 3);
            Grid.SetColumn(button, index);
            presets.Children.Add(button);
        }
        focus.Children.Add(presets);

        var mode = Text("倒计时专注", 12, PlannerMutedBrush);
        var start = PlannerAction(_focusTimer?.IsEnabled == true ? "专注中" : "开始专注",
            (_, _) => StartFocus(), true);
        start.Padding = new Thickness(13, 6, 13, 6);
        start.IsEnabled = _focusTimer?.IsEnabled != true;
        start.Margin = new Thickness(0);
        var controls = new WidgetResponsiveRow(mode, start, 270, System.Windows.HorizontalAlignment.Stretch)
            { Margin = new Thickness(0, 0, 0, 8) };
        focus.Children.Add(controls);
        var reset = PlannerQuietButton("重置", (_, _) =>
        {
            _focusTimer?.Stop();
            FocusDuration = TimeSpan.FromMinutes(25);
            FocusRemaining = FocusDuration;
            RenderBody();
        });
        reset.ToolTip = "重置专注时间";
        reset.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        focus.Children.Add(reset);
        _body.Children.Add(focus);
        UpdateFocusPresentation();
    }

    private void UpdateFocusPresentation()
    {
        if (_focusClock is not null) _focusClock.Text = FormatFocus(FocusRemaining);
        if (_focusStatus is not null)
            _focusStatus.Text = _focusTimer?.IsEnabled == true ? "专注中" : FocusRemaining <= TimeSpan.Zero ? "已完成" : "准备开始";
    }

    private static string FormatFocus(TimeSpan value) =>
        $"{Math.Max(0, (int)value.TotalHours):00}:{value.Minutes:00}:{value.Seconds:00}";

    private void StartFocus()
    {
        if (FocusDuration <= TimeSpan.Zero) FocusDuration = TimeSpan.FromMinutes(25);
        if (FocusRemaining <= TimeSpan.Zero) FocusRemaining = FocusDuration;
        _focusDeadline = DateTime.UtcNow + FocusRemaining;
        _focusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _focusTimer.Tick -= FocusTimerTick;
        _focusTimer.Tick += FocusTimerTick;
        _focusTimer.Start();
        UpdateFocusPresentation();
    }

    private void FocusTimerTick(object? sender, EventArgs e)
    {
        FocusRemaining = _focusDeadline - DateTime.UtcNow;
        if (FocusRemaining <= TimeSpan.Zero)
        {
            FocusRemaining = TimeSpan.Zero; _focusTimer?.Stop();
        }
        UpdateFocusPresentation();
    }

    private void RenderPlannerCalendar()
    {
        var selected = _selectedDay?.Date ?? DateTime.Today;
        var month = Text(_shownMonth.ToString("yyyy年M月", CultureInfo.InvariantCulture), 18);
        month.FontWeight = FontWeights.Light;
        var controls = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var today = PlannerQuietButton("今天", (_, _) => { _shownMonth = DateTime.Today; _selectedDay = DateTime.Today; RenderBody(); });
        today.Padding = new Thickness(8, 3, 8, 3);
        var previous = PlannerIconButton("\uE76B", "上个月", (_, _) => { _shownMonth = _shownMonth.AddMonths(-1); RenderBody(); });
        previous.ToolTip = "上个月"; previous.Width = 26; previous.Padding = new Thickness(2);
        var nextMonth = PlannerIconButton("\uE76C", "下个月", (_, _) => { _shownMonth = _shownMonth.AddMonths(1); RenderBody(); });
        nextMonth.ToolTip = "下个月"; nextMonth.Width = 26; nextMonth.Padding = new Thickness(2);
        controls.Children.Add(today); controls.Children.Add(previous); controls.Children.Add(nextMonth);
        var header = new WidgetResponsiveRow(month, controls, 260) { Margin = new Thickness(2, 4, 2, 10) };
        _body.Children.Add(header);

        var byDay = _store.Entries(State.Id).Where(entry => !entry.Completed && entry.DueUtc is not null)
            .GroupBy(entry => entry.DueUtc!.Value.ToLocalTime().Date)
            .ToDictionary(group => group.Key, group => group.Count());
        var grid = new UniformGrid { Columns = 7, Margin = new Thickness(3, 0, 3, 4) };
        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(3, 0, 3, 0) };
        foreach (var label in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var weekday = Text(label, 10, PlannerMutedBrush);
            weekday.TextAlignment = TextAlignment.Center;
            weekday.Margin = new Thickness(0, 0, 0, 7);
            weekdays.Children.Add(weekday);
        }
        var first = new DateTime(_shownMonth.Year, _shownMonth.Month, 1);
        for (var pad = 0; pad < ((int)first.DayOfWeek + 6) % 7; pad++) grid.Children.Add(new Border());
        for (var day = 1; day <= DateTime.DaysInMonth(first.Year, first.Month); day++)
        {
            var value = first.AddDays(day - 1);
            var button = PlannerCalendarDay(value, value == selected, byDay.GetValueOrDefault(value));
            grid.Children.Add(button);
        }
        _body.Children.Add(weekdays);
        _body.Children.Add(grid);
    }

    private void RenderPlannerTasks()
    {
        var entries = _store.Entries(State.Id).Where(entry => entry.EntryKind != "Schedule").ToArray();
        var active = entries.Where(entry => !entry.Completed).OrderBy(entry => entry.DueUtc).ToArray();
        var completed = entries.Where(entry => entry.Completed).OrderByDescending(entry => entry.DueUtc).ToArray();
        _plannerSelectedCompletedIds.IntersectWith(completed.Select(entry => entry.Id));
        var heading = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center };
        var taskTitle = Text(PlannerTaskFilter == "完成" ? "已完成" : "待办", 18);
        taskTitle.FontWeight = FontWeights.Light;
        heading.Children.Add(taskTitle);
        var taskCount = Text($"  {active.Length} 项待办", 10, PlannerMutedBrush);
        _plannerTaskCount = taskCount;
        taskCount.VerticalAlignment = VerticalAlignment.Bottom;
        taskCount.Margin = new Thickness(2, 0, 0, 2);
        heading.Children.Add(taskCount);
        UIElement add = PlannerTaskFilter == "完成" ? PlannerCompletedToolbar() : PlannerNewTaskButton();
        var actions = (FrameworkElement)add;
        actions.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        actions.Margin = new Thickness(0);
        var header = new Grid { Margin = new Thickness(8, 4, 8, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(heading);
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        _body.Children.Add(header);

        var filterGrid = new Grid { Margin = new Thickness(8, 0, 8, 8), Height = 32 };
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var segment = new Border
        {
            Name = "PlannerTaskFilters", MaxWidth = 240,
            Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
            BorderBrush = PlannerLineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(2)
        };
        var segmentGrid = new Grid();
        for (var index = 0; index < 2; index++) segmentGrid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (filter, index) in new[] { ("待办", 0), ("完成", 1) })
        {
            var button = PlannerTabButton(filter, PlannerTaskFilter == filter, (_, _) =>
            {
                PlannerTaskFilter = filter;
                _plannerSelectedCompletedIds.Clear();
                RenderBody();
            });
            button.MinHeight = 26;
            button.Margin = new Thickness(0);
            button.Padding = new Thickness(5, 1, 5, 1);
            Grid.SetColumn(button, index);
            segmentGrid.Children.Add(button);
        }
        segment.Child = segmentGrid;
        Grid.SetColumn(segment, 0);
        filterGrid.Children.Add(segment);
        _body.Children.Add(filterGrid);
        UpdatePlannerTaskHeader();

        var visible = PlannerTaskFilter == "完成" ? completed : active;

        if (visible.Length == 0)
        {
            AddPlannerListEmptyState(overview: false);
            return;
        }

        foreach (var entry in visible) _body.Children.Add(CreatePlannerTaskRow(entry));
    }

    private void RenderWeather()
    {
        var temperature = Text("—", 48);
        temperature.Margin = new Thickness(0, -4, 0, -2);
        temperature.TextWrapping = TextWrapping.NoWrap;
        var condition = Text(string.IsNullOrWhiteSpace(State.City) ? "正在估计所在城市…" : "正在更新天气…", 14);
        var details = Text("", 11, MutedBrush);
        var icon = new Image { Width = 68, Height = 68, Source = WeatherIcon.For(WeatherSymbol.PartlyCloudy),
            Effect = MiniC.Services.GroupMaterialBrushes.ContentShadow,
            Stretch = Stretch.Uniform, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        var reading = new Grid();
        reading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        reading.ColumnDefinitions.Add(new ColumnDefinition());
        reading.Children.Add(icon);
        Grid.SetColumn(temperature, 1);
        reading.Children.Add(temperature);
        var summary = new StackPanel { MaxWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(condition); summary.Children.Add(details);
        var current = new WidgetResponsiveRow(reading, summary, 420)
            { Margin = new Thickness(2, 2, 2, 12) };
        _body.Children.Add(current);

        var hourly = new StackPanel { Visibility = Visibility.Collapsed };
        hourly.Children.Add(new Border { Height = 1, Margin = new Thickness(4, 0, 4, 10),
            Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)) });
        hourly.Children.Add(Text("未来小时", 11, MutedBrush));
        _body.Children.Add(hourly);

        var forecast = new StackPanel { Visibility = Visibility.Collapsed };
        forecast.Children.Add(new Border { Height = 1, Margin = new Thickness(4, 0, 4, 11),
            Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)) });
        forecast.Children.Add(Text("未来几天", 11, MutedBrush));
        _body.Children.Add(forecast);
        if (!string.IsNullOrWhiteSpace(State.City))
            _ = RefreshWeatherAsync(temperature, condition, details, icon, forecast, hourly);
        else
            _ = LocateWeatherAsync(temperature, condition, details, icon, forecast, hourly);
    }

    private static string ForecastDate(DateOnly date)
    {
        var weekday = date.DayOfWeek switch { DayOfWeek.Monday => "周一", DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三", DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六", _ => "周日" };
        return $"{weekday}  {date:M/d}";
    }
    private static void RenderHourly(StackPanel host, IReadOnlyList<WeatherHourlyForecast>? periods)
    {
        while (host.Children.Count > 2) host.Children.RemoveAt(host.Children.Count - 1);
        if (periods is null || periods.Count == 0) { host.Visibility = Visibility.Collapsed; return; }
        var grid = new WeatherHourlyGrid { Margin = new Thickness(0, 7, 0, 10) };
        foreach (var period in periods.Take(6))
        {
            var content = new StackPanel();
            var time = Text(period.Time.ToString("HH:mm", CultureInfo.InvariantCulture), 11, MutedBrush);
            time.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            var icon = new Image { Source = WeatherIcon.For(period.Symbol), Width = 27, Height = 27,
                Effect = MiniC.Services.GroupMaterialBrushes.ContentShadow,
                Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 2) };
            var temperature = Text($"{period.Temperature:0}°", 13);
            temperature.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            var condition = Text(period.Condition, 10, MutedBrush);
            condition.TextAlignment = TextAlignment.Center;
            content.Children.Add(time); content.Children.Add(icon); content.Children.Add(temperature); content.Children.Add(condition);
            grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
                CornerRadius = new CornerRadius(8), Margin = new Thickness(2), Padding = new Thickness(4, 6, 4, 5),
                Child = content });
        }
        host.Children.Add(grid); host.Visibility = Visibility.Visible;
    }
    private static void RenderForecast(StackPanel host, IReadOnlyList<WeatherForecastDay>? days)
    {
        while (host.Children.Count > 2) host.Children.RemoveAt(host.Children.Count - 1);
        if (days is null || days.Count == 0) { host.Visibility = Visibility.Collapsed; return; }
        var grid = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        foreach (var day in days.Take(3))
        {
            var item = new Grid { Margin = new Thickness(3, 1, 3, 1), MinHeight = 38 };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            item.Children.Add(Text(ForecastDate(day.Date), 11, MutedBrush));
            var icon = new Image { Source = WeatherIcon.For(day.Symbol), Width = 28, Height = 28, Stretch = Stretch.Uniform,
                Effect = MiniC.Services.GroupMaterialBrushes.ContentShadow };
            Grid.SetColumn(icon, 1); item.Children.Add(icon);
            var condition = Text(day.Condition, 11);
            var range = Text($"{day.High:0}°  {day.Low:0}°", 11, MutedBrush);
            range.TextWrapping = TextWrapping.NoWrap;
            var description = new WidgetResponsiveRow(condition, range, 200) { Margin = new Thickness(4, 4, 0, 4) };
            Grid.SetColumn(description, 2); item.Children.Add(description);
            grid.Children.Add(item);
        }
        host.Children.Add(grid); host.Visibility = Visibility.Visible;
    }
    private async Task LocateWeatherAsync(TextBlock temperature, TextBlock condition, TextBlock details, Image icon,
        StackPanel forecast, StackPanel? hourly)
    {
        if (_closing) return;
        var request = _weatherRequests.Begin();
        if (request is null) return;
        var token = request.Token;
        try
        {
            var location = await _weather.LocateAsync(token);
            if (token.IsCancellationRequested || _closing || !string.IsNullOrWhiteSpace(State.City)) return;
            if (location is null) { condition.Text = "无法估计城市"; details.Text = "请手动输入城市。"; return; }
            State.City = location.City; SaveSoon();
            await RefreshWeatherAsync(temperature, condition, details, icon, forecast, hourly, location);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!token.IsCancellationRequested && !_closing) { condition.Text = "定位暂不可用"; details.Text = "请手动输入城市。"; } }
        finally
        {
            _weatherRequests.Complete(request);
        }
    }
    private async Task RefreshWeatherAsync(TextBlock temperature, TextBlock condition, TextBlock details, Image icon,
        StackPanel forecast, StackPanel? hourly, WeatherLocation? location = null)
    {
        if (_closing) return;
        var request = _weatherRequests.Begin();
        if (request is null) return;
        var token = request.Token;
        try
        {
            var weather = await _weather.GetAsync(State.City, location, token);
            if (token.IsCancellationRequested || _closing) return;
            temperature.Text = $"{weather.Temperature:0}°";
            condition.Text = $"{weather.Place} · {weather.Condition}";
            details.Text = $"最高 {weather.High:0}°  /  最低 {weather.Low:0}°\n{weather.Source} · {weather.FetchedAt.ToLocalTime():HH:mm}";
            icon.Source = WeatherIcon.For(weather.Symbol);
            if (hourly is not null) RenderHourly(hourly, weather.Hourly);
            RenderForecast(forecast, weather.Forecast);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!token.IsCancellationRequested && !_closing) { temperature.Text = "—";
            condition.Text = "天气暂不可用"; details.Text = "请检查网络或城市名称。";
            forecast.Visibility = Visibility.Collapsed; if (hourly is not null) hourly.Visibility = Visibility.Collapsed; } }
        finally
        {
            _weatherRequests.Complete(request);
        }
    }
    private void RenderAttachments()
    {
        var title = _selectedEntry is null ? "附件" : $"{_selectedEntry.Title} · 附件";
        var section = new StackPanel(); section.Children.Add(Row(Text(title, 14), Action("导入图片或文件", (_, _) => ImportFiles(), true)));
        foreach (var attachment in _store.Attachments(State.Id).Where(a => a.EntryId == _selectedEntry?.Id))
        {
            var row = new DockPanel();
            var remove = Action("移除", (_, _) => { _store.DeleteAttachment(attachment); RenderBody(); });
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var open = Action(attachment.Name, (_, _) => {
                var path = _store.AttachmentPath(attachment);
                if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }); row.Children.Add(open); section.Children.Add(row);
        }
        _body.Children.Add(Card(section));
    }
    private async void ImportFiles()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var file in dialog.FileNames)
        {
            try
            {
                var attachment = await Task.Run(() => _store.Import(State.Id, _selectedEntry?.Id, file));
                if (State.Kind == WidgetKind.Note && _selectedEntry is null &&
                    new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    State.Body += $"\n![{attachment.Name}](attachment://{attachment.Id})\n";
            }
            catch (Exception ex) { WinMessageBox.Show($"导入失败：{ex.Message}", "附件", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        _store.Save(State); RenderBody();
    }
}
