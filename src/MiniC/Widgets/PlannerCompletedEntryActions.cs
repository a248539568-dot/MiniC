using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace MiniC.Widgets;

/// <summary>Completed entries expose deletion and selection, without changing completion.</summary>
internal sealed class PlannerCompletedEntryActions : Grid
{
    private readonly StackPanel _actions;
    private readonly System.Windows.Shapes.Path _check;
    private bool _hovered;
    private Border? _row;
    internal StackPanel TimeDisplay { get; }
    internal Button DeleteButton { get; }
    internal Button SelectButton { get; }
    internal bool IsSelected { get; private set; }

    internal PlannerCompletedEntryActions(WidgetEntry entry, bool selected, Action delete, Action<bool> select)
    {
        Width = 64; Height = 36;
        VerticalAlignment = VerticalAlignment.Center;
        TimeDisplay = PlannerEntryStatus.CreateTimeDisplay(entry);
        Children.Add(TimeDisplay);
        _actions = new StackPanel
        {
            Opacity = 0,
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        DeleteButton = WidgetFormStyles.Button("", (_, e) => { e.Handled = true; delete(); });
        DeleteButton.Content = WidgetFormStyles.Icon("\uE74D", 14);
        DeleteButton.ToolTip = "删除此待办";
        DeleteButton.Opacity = 0;
        DeleteButton.Background = Brushes.Transparent;
        DeleteButton.BorderThickness = new Thickness(0);
        DeleteButton.Margin = new Thickness(0, 0, 4, 0);
        SelectButton = WidgetFormStyles.Button("", (_, e) =>
        {
            e.Handled = true;
            SetSelected(!IsSelected);
            select(IsSelected);
        });
        SelectButton.Background = new SolidColorBrush(Color.FromArgb(90, 28, 38, 49));
        SelectButton.BorderBrush = new SolidColorBrush(Color.FromArgb(215, 231, 242, 255));
        _check = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,7 L5.5,10.5 L12,3"),
            Stroke = Brushes.White, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Width = 14, Height = 14,
            Effect = Services.GroupMaterialBrushes.ContentShadow
        };
        SelectButton.Content = _check;
        SelectButton.Margin = new Thickness(0);
        foreach (var button in new[] { DeleteButton, SelectButton })
        {
            button.Width = button.Height = 28;
            button.MinHeight = 0;
            button.Padding = new Thickness(0);
            _actions.Children.Add(button);
        }
        System.Windows.Automation.AutomationProperties.SetName(DeleteButton, "删除此待办");
        Children.Add(_actions);
        SetSelected(selected);
    }

    internal void Attach(Border row)
    {
        _row = row;
        row.MouseEnter += (_, _) => { _hovered = true; Refresh(); };
        row.MouseLeave += (_, _) => { _hovered = false; Refresh(); };
        row.IsKeyboardFocusWithinChanged += (_, _) => Refresh();
    }

    private void SetSelected(bool selected)
    {
        IsSelected = selected;
        _check.Opacity = selected ? 1 : 0;
        SelectButton.ToolTip = selected ? "取消勾选" : "勾选待办";
        System.Windows.Automation.AutomationProperties.SetName(SelectButton, (string)SelectButton.ToolTip);
        System.Windows.Automation.AutomationProperties.SetItemStatus(SelectButton, selected ? "已勾选" : "未勾选");
        Refresh();
    }

    private void Refresh()
    {
        var reveal = _hovered || _row?.IsKeyboardFocusWithin == true;
        _actions.IsHitTestVisible = reveal || IsSelected;
        DeleteButton.IsHitTestVisible = reveal;
        PlannerEntryStatus.CrossFade(_actions, reveal || IsSelected ? 1 : 0);
        PlannerEntryStatus.CrossFade(TimeDisplay, reveal || IsSelected ? 0 : 1);
        PlannerEntryStatus.CrossFade(DeleteButton, reveal ? 1 : 0);
    }
}
