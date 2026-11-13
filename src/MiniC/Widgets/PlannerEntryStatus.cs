using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace MiniC.Widgets;

/// <summary>A stable time slot that reveals completion on hover or keyboard focus.</summary>
internal sealed class PlannerEntryStatus : Grid
{
    private readonly System.Windows.Shapes.Path _check;
    private bool _hovered;
    internal StackPanel TimeDisplay { get; }
    internal Button CompleteButton { get; }
    internal bool IsRevealed { get; private set; }

    internal PlannerEntryStatus(WidgetEntry entry, RoutedEventHandler complete)
    {
        Width = 64;
        Height = 36;
        VerticalAlignment = VerticalAlignment.Center;
        TimeDisplay = CreateTimeDisplay(entry);
        Children.Add(TimeDisplay);

        CompleteButton = WidgetFormStyles.Button("", complete);
        CompleteButton.Width = CompleteButton.Height = 28;
        CompleteButton.MinHeight = 0;
        CompleteButton.Padding = new Thickness(0);
        CompleteButton.Margin = new Thickness(0);
        CompleteButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        CompleteButton.VerticalAlignment = VerticalAlignment.Center;
        CompleteButton.Background = new SolidColorBrush(Color.FromArgb(90, 28, 38, 49));
        CompleteButton.BorderBrush = new SolidColorBrush(Color.FromArgb(215, 231, 242, 255));
        CompleteButton.BorderThickness = new Thickness(1.2);
        CompleteButton.Template = WidgetFormStyles.ButtonTemplate(new CornerRadius(14));
        CompleteButton.Opacity = 0;
        CompleteButton.IsHitTestVisible = false;
        _check = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,7 L5.5,10.5 L12,3"),
            Stroke = Brushes.White, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Width = 14, Height = 14,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = Services.GroupMaterialBrushes.ContentShadow
        };
        CompleteButton.Content = _check;
        _check.Opacity = 0;
        CompleteButton.ToolTip = "标记为已完成";
        System.Windows.Automation.AutomationProperties.SetName(CompleteButton, "标记为已完成");
        Children.Add(CompleteButton);
    }

    internal static StackPanel CreateTimeDisplay(WidgetEntry entry)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        if (entry.DueUtc is { } due)
        {
            var local = due.ToLocalTime();
            if (local.Date != DateTime.Today)
                AddTime(local.ToString("M月d日", System.Globalization.CultureInfo.GetCultureInfo("zh-CN")), 10);
            AddTime(local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), 12);
        }
        else AddTime("未设时间", 11);
        return panel;

        void AddTime(string value, int size)
        {
            var text = WidgetContentControls.Text(value, size);
            text.TextWrapping = TextWrapping.NoWrap;
            text.TextAlignment = TextAlignment.Right;
            panel.Children.Add(text);
        }
    }

    internal void Attach(Border row)
    {
        row.MouseEnter += (_, _) => { _hovered = true; Reveal(true); };
        row.MouseLeave += (_, _) => { _hovered = false; Reveal(row.IsKeyboardFocusWithin); };
        row.IsKeyboardFocusWithinChanged += (_, _) => Reveal(_hovered || row.IsKeyboardFocusWithin);
    }

    internal void ShowCompleted() => _check.Opacity = 1;

    private void Reveal(bool show)
    {
        IsRevealed = show;
        CompleteButton.IsHitTestVisible = show;
        CrossFade(TimeDisplay, show ? 0 : 1);
        CrossFade(CompleteButton, show ? 1 : 0);
    }

    internal static void CrossFade(UIElement target, double opacity)
    {
        var from = target.Opacity;
        target.BeginAnimation(OpacityProperty, null);
        target.Opacity = opacity;
        if (SystemParameters.ClientAreaAnimation)
            target.BeginAnimation(OpacityProperty, new DoubleAnimation(from, opacity, TimeSpan.FromMilliseconds(120))
            {
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }
}
