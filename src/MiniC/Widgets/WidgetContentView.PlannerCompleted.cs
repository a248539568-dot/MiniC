using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;

namespace MiniC.Widgets;

public sealed partial class WidgetContentView
{
    private readonly HashSet<string> _plannerSelectedCompletedIds = new(StringComparer.Ordinal);
    private Button? _plannerDeleteSelectedButton;
    private Button? _plannerClearCompletedButton;
    private Border? _plannerDeleteConfirmation;

    private WidgetEntry[] PlannerCompletedTasks() => _store.Entries(State.Id)
        .Where(entry => entry.Completed && entry.EntryKind != "Schedule").ToArray();

    private StackPanel PlannerCompletedToolbar()
    {
        var toolbar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _plannerDeleteSelectedButton = PlannerIconButton("\uE74D", "删除勾选项", (_, _) =>
            ConfirmPlannerCompletedDeletion(_plannerSelectedCompletedIds.ToArray(), clearAll: false));
        _plannerClearCompletedButton = PlannerIconButton("\uE75C", "清空已完成待办", (_, _) =>
            ConfirmPlannerCompletedDeletion(PlannerCompletedTasks().Select(entry => entry.Id).ToArray(), clearAll: true));
        toolbar.Children.Add(_plannerDeleteSelectedButton);
        toolbar.Children.Add(_plannerClearCompletedButton);
        return toolbar;
    }

    private void UpdatePlannerTaskHeader()
    {
        var tasks = _store.Entries(State.Id).Where(entry => entry.EntryKind != "Schedule").ToArray();
        var completed = tasks.Where(entry => entry.Completed).ToArray();
        _plannerSelectedCompletedIds.IntersectWith(completed.Select(entry => entry.Id));
        if (_plannerTaskCount is not null)
            _plannerTaskCount.Text = PlannerTaskFilter == "完成"
                ? _plannerSelectedCompletedIds.Count > 0 ? $"  已勾选 {_plannerSelectedCompletedIds.Count} 项" : $"  {completed.Length} 项完成"
                : $"  {tasks.Count(entry => !entry.Completed)} 项待办";
        if (_plannerDeleteSelectedButton is not null)
            _plannerDeleteSelectedButton.IsEnabled = _plannerSelectedCompletedIds.Count > 0;
        if (_plannerClearCompletedButton is not null)
            _plannerClearCompletedButton.IsEnabled = completed.Length > 0;
    }

    private void ConfirmPlannerCompletedDeletion(string[] requestedIds, bool clearAll)
    {
        if (_closing || PlannerPage != "待办" || PlannerTaskFilter != "完成") return;
        var requested = requestedIds.ToHashSet(StringComparer.Ordinal);
        var ids = PlannerCompletedTasks().Where(entry => requested.Contains(entry.Id)).Select(entry => entry.Id).ToArray();
        if (ids.Length == 0) return;
        if (_plannerDeleteConfirmation is { } previous) _body.Children.Remove(previous);
        var panel = new Border
        {
            Name = "PlannerDeleteConfirmation", Background = PlannerPanelBrush,
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(8, 0, 8, 8)
        };
        _plannerDeleteConfirmation = panel;
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        actions.Children.Add(PlannerIconButton("\uE711", "取消删除", (_, _) =>
        {
            _body.Children.Remove(panel);
            if (ReferenceEquals(_plannerDeleteConfirmation, panel)) _plannerDeleteConfirmation = null;
        }));
        actions.Children.Add(PlannerIconButton("\uE73E", "确认删除", (_, _) =>
        {
            if (_closing || panel.Parent != _body) return;
            var eligible = PlannerCompletedTasks().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in ids.Where(eligible.Contains))
            {
                _store.DeleteEntry(id);
                _plannerSelectedCompletedIds.Remove(id);
                var row = _body.Children.OfType<Border>().FirstOrDefault(item => item.Tag is WidgetEntry entry && entry.Id == id);
                if (row is not null) AnimatePlannerEntryRemoval(row);
            }
            _body.Children.Remove(panel);
            if (ReferenceEquals(_plannerDeleteConfirmation, panel)) _plannerDeleteConfirmation = null;
            UpdatePlannerTaskHeader();
        }));
        panel.Child = new WidgetResponsiveRow(Text(clearAll ? $"清空全部 {ids.Length} 项已完成待办？" : $"删除这 {ids.Length} 项待办？", 12), actions, 280);
        _body.Children.Insert(1, panel);
    }
}
