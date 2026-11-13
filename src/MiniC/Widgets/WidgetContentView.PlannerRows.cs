using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace MiniC.Widgets;

public sealed partial class WidgetContentView
{
    private TextBlock? _plannerTaskCount;

    private Border CreatePlannerEntryRow(WidgetEntry entry, string caption, Brush accent, Action open)
    {
        Border? row = null;
        var completing = false;
        var status = new PlannerEntryStatus(entry, (_, e) =>
        {
            e.Handled = true;
            if (completing || _closing || row?.Parent != _body) return;
            var current = _store.Entries(State.Id).FirstOrDefault(item => item.Id == entry.Id);
            if (current is null) return;
            current.Completed = true;
            _store.Save(current);
            completing = true;
            entry.Completed = current.Completed;
            var trailing = ((Grid)row.Child).Children.OfType<PlannerEntryStatus>().Single();
            trailing.ShowCompleted();
            AnimatePlannerEntryRemoval(row);
        });
        row = CreatePlannerRow(entry, caption, accent, status, open);
        status.Attach(row);
        return row;
    }

    private static Border CreatePlannerRow(WidgetEntry entry, string caption, Brush accent, UIElement trailing, Action open)
    {
        var row = PlannerEntryLine(caption, entry.Title, accent, trailing, muted: entry.Completed, completed: entry.Completed);
        row.Tag = entry;
        row.Background = Brushes.Transparent;
        row.Cursor = Cursors.Hand;
        row.MouseEnter += (_, _) => row.Background = PlannerPanelBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (!row.IsHitTestVisible || FromInteractive(e.OriginalSource as DependencyObject)) return;
            open();
        };
        return row;
    }

    private Border CreatePlannerTaskRow(WidgetEntry entry)
    {
        var caption = entry.Completed ? "已完成" : entry.DueUtc?.ToLocalTime().Date < DateTime.Today ? "已逾期" : "待办";
        if (entry.Completed)
        {
            var actions = new PlannerCompletedEntryActions(entry, _plannerSelectedCompletedIds.Contains(entry.Id),
                () => ConfirmPlannerCompletedDeletion([entry.Id], clearAll: false), selected =>
                {
                    if (selected) _plannerSelectedCompletedIds.Add(entry.Id);
                    else _plannerSelectedCompletedIds.Remove(entry.Id);
                    UpdatePlannerTaskHeader();
                });
            var completedRow = CreatePlannerRow(entry, caption, PlannerMutedBrush, actions,
                () => { _selectedEntry = entry; RenderPlannerEditor("Task", entry); });
            actions.Attach(completedRow);
            return completedRow;
        }
        return CreatePlannerEntryRow(entry, caption,
            entry.Completed ? PlannerMutedBrush : FrozenBrush(Color.FromRgb(244, 190, 116)),
            () => { _selectedEntry = entry; RenderPlannerEditor("Task", entry); });
    }

    private void AnimatePlannerEntryRemoval(Border row)
    {
        row.IsHitTestVisible = false;
        row.IsEnabled = false;
        void Finish()
        {
            if (_closing || row.Parent != _body) return;
            _body.Children.Remove(row);
            if (PlannerPage == "待办")
                UpdatePlannerTaskHeader();
            if (!_body.Children.OfType<Border>().Any(item => item.Tag is WidgetEntry))
                AddPlannerListEmptyState(PlannerPage == "概览");
        }
        if (!SystemParameters.ClientAreaAnimation || row.ActualHeight <= 0)
        {
            Finish();
            return;
        }
        row.ClipToBounds = true;
        row.Height = row.ActualHeight;
        var translate = row.RenderTransform as TranslateTransform ?? new TranslateTransform();
        row.RenderTransform = translate;
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 18, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(row.Opacity, 0, TimeSpan.FromMilliseconds(180)));
        var collapse = new DoubleAnimation(row.Height, 0, TimeSpan.FromMilliseconds(160))
        {
            BeginTime = TimeSpan.FromMilliseconds(100),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        collapse.Completed += (_, _) => Finish();
        row.BeginAnimation(HeightProperty, collapse);
    }

    private void AddPlannerListEmptyState(bool overview)
    {
        var empty = Text(overview ? "暂无待办或日程" : PlannerPage == "日程"
            ? _plannerScheduleDay is null ? "暂无日程" : "当天没有安排"
            : PlannerTaskFilter == "完成" ? "暂无已完成待办" : "暂无待办",
            12, PlannerMutedBrush);
        empty.Margin = overview ? new Thickness(8, 24, 8, 0) : new Thickness(0, 38, 0, 0);
        empty.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        _body.Children.Add(empty);
    }
}
