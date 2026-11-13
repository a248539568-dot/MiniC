using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace MiniC.Widgets;

public sealed partial class WidgetContentView
{
    private string? _plannerReadingEntryId;
    private string[] _plannerReadingOrder = [];
    private double _plannerListOffset;

    private WidgetEntry[] PlannerOverviewEntries() => _store.Entries(State.Id)
        .Where(entry => !entry.Completed)
        .OrderByDescending(entry => entry.DueUtc)
        .ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();

    private void OpenPlannerReading(WidgetEntry entry, WidgetEntry[] order)
    {
        _plannerListOffset = Scroll.VerticalOffset;
        _plannerReadingOrder = order.Select(item => item.Id).ToArray();
        _plannerReadingEntryId = entry.Id;
        RenderPlannerReading();
    }

    private void ReturnToPlannerList()
    {
        _plannerReadingEntryId = null;
        _selectedEntry = null;
        RenderBody();
        Scroll.ScrollToVerticalOffset(_plannerListOffset);
    }

    private void PreparePlannerDetail()
    {
        ClearPlannerNavigation();
        _plannerDeleteConfirmation = null;
        _plannerTaskCount = null;
        _plannerDeleteSelectedButton = _plannerClearCompletedButton = null;
        _body.Children.Clear();
        _plannerClock = _plannerDate = null;
        _plannerClockTimer?.Stop();
    }

    private void RenderPlannerReading()
    {
        var entries = _store.Entries(State.Id).ToDictionary(entry => entry.Id);
        _plannerReadingOrder = _plannerReadingOrder.Where(entries.ContainsKey).ToArray();
        if (_plannerReadingEntryId is null || !entries.TryGetValue(_plannerReadingEntryId, out var entry))
        {
            ReturnToPlannerList();
            return;
        }
        PreparePlannerDetail();
        var header = new Grid { Margin = new Thickness(8, 3, 8, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(PlannerIconButton("\uE72B", $"返回{PlannerPage}", (_, _) => ReturnToPlannerList()));
        var kind = Text(entry.EntryKind == "Schedule" ? "日程" : "待办", 12, PlannerMutedBrush);
        kind.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(kind, 1); header.Children.Add(kind);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        actions.Children.Add(PlannerIconButton("\uE70F", "编辑", (_, _) => RenderPlannerEditor(entry.EntryKind, entry)));
        var index = Array.IndexOf(_plannerReadingOrder, entry.Id);
        void Move(int step)
        {
            var target = index + step;
            if (target < 0 || target >= _plannerReadingOrder.Length) return;
            _plannerReadingEntryId = _plannerReadingOrder[target];
            RenderPlannerReading();
        }
        var previous = PlannerIconButton("\uE70E", "上一条", (_, _) => Move(-1));
        var next = PlannerIconButton("\uE70D", "下一条", (_, _) => Move(1));
        previous.IsEnabled = index > 0;
        next.IsEnabled = index >= 0 && index < _plannerReadingOrder.Length - 1;
        actions.Children.Add(previous); actions.Children.Add(next);
        Grid.SetColumn(actions, 2); header.Children.Add(actions);
        _body.Children.Add(header);
        var reading = new StackPanel
        {
            Margin = new Thickness(12, 6, 12, 16), MaxWidth = 560,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Focusable = true
        };
        var title = Text(entry.Title, 17);
        title.Margin = new Thickness(0, 0, 0, 10);
        reading.Children.Add(title);
        var time = Text(entry.DueUtc?.ToLocalTime().ToString("yyyy年M月d日 HH:mm", PlannerCulture) ?? "未设置时间", 11, PlannerMutedBrush);
        time.Margin = new Thickness(0, 0, 0, 18);
        reading.Children.Add(time);
        if (!string.IsNullOrWhiteSpace(entry.Body)) reading.Children.Add(Text(entry.Body, 14));
        reading.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.PageUp or Key.PageDown)) return;
            Move(e.Key == Key.PageUp ? -1 : 1);
            e.Handled = true;
        };
        _body.Children.Add(reading);
        reading.Loaded += (_, _) => reading.Focus();
        ShowPlannerNavigation(PlannerBottomNavigation());
        Scroll.ScrollToTop();
        reading.Focus();
        AnimatePlannerPage();
    }

    private void RenderPlannerEditor(string kind, WidgetEntry? existing = null)
    {
        PreparePlannerDetail();
        var content = existing is null ? "" : existing.Title + (string.IsNullOrEmpty(existing.Body) ? "" : "\n" + existing.Body);
        var composer = new PlannerEntryEditor(content, existing?.DueUtc, optional: kind != "Schedule",
            initialDate: existing is null ? (kind == "Schedule" ? _selectedDay ?? DateTime.Today : null) : null, this);
        var error = Text("", 11, FrozenBrush(Color.FromRgb(255, 204, 190)));
        error.Visibility = Visibility.Collapsed;
        error.Margin = new Thickness(8, 6, 8, 0);
        composer.Input.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;
        void Return()
        {
            _selectedEntry = null;
            if (_plannerReadingEntryId is not null) RenderPlannerReading();
            else RenderBody();
        }
        void Save()
        {
            var text = composer.Input.Text.Replace("\r\n", "\n").Trim();
            if (text.Length == 0 || (text.Length > PlannerEntryEditor.ContentLimit && text != content))
            {
                error.Text = $"请填写事项内容，最多 {PlannerEntryEditor.ContentLimit} 字。";
                error.Visibility = Visibility.Visible;
                composer.Input.Focus();
                return;
            }
            if (!composer.Picker.TryGetUtc(out var due) || kind == "Schedule" && due is null)
            {
                error.Text = "请选择有效的日期和时间。";
                error.Visibility = Visibility.Visible;
                return;
            }
            var newline = text.IndexOf('\n');
            var reopen = existing?.Completed == true && existing.DueUtc != due;
            var entry = existing ?? new WidgetEntry { WidgetId = State.Id };
            entry.Title = newline < 0 ? text : text[..newline];
            entry.Body = newline < 0 ? "" : text[(newline + 1)..];
            entry.DueUtc = due; entry.EntryKind = kind; entry.NotifiedUtc = null;
            if (reopen) entry.Completed = false;
            _store.Save(entry);
            if (reopen)
            {
                _plannerSelectedCompletedIds.Remove(entry.Id);
                PlannerTaskFilter = "待办";
                _plannerReadingEntryId = null;
            }
            if (_plannerReadingEntryId is not null)
                _plannerReadingOrder = (PlannerPage == "日程" ? PlannerScheduleEntries() : PlannerOverviewEntries())
                    .Select(item => item.Id).ToArray();
            Return();
        }
        var toolbar = new Grid { Margin = new Thickness(8, 2, 8, 6) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = PlannerIconButton("\uE72B", "返回", (_, _) => Return());
        back.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        toolbar.Children.Add(back);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        if (existing is not null)
            actions.Children.Add(PlannerIconButton("\uE74D", "删除", (_, _) =>
            {
                _store.DeleteEntry(existing.Id);
                _plannerReadingEntryId = null;
                Return();
            }));
        actions.Children.Add(PlannerIconButton("\uE74E", "保存", (_, _) => Save()));
        Grid.SetColumn(actions, 1); toolbar.Children.Add(actions);
        _body.Children.Add(toolbar);
        _body.Children.Add(composer);
        _body.Children.Add(error);
        composer.Input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            Save(); e.Handled = true;
        };
        ShowPlannerNavigation(PlannerBottomNavigation());
        Scroll.ScrollToTop();
        AnimatePlannerPage();
    }
}
