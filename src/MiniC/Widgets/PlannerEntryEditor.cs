using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

namespace MiniC.Widgets;

/// <summary>A single writing surface with an inset, floating time selector.</summary>
internal sealed class PlannerEntryEditor : Grid
{
    internal const int ContentLimit = 10000;
    internal TextBox Input { get; }
    internal WidgetDateTimePicker Picker { get; }
    internal Border TimePanel { get; }
    internal Button TimeButton { get; }
    private readonly Popup _timePopup;

    public static readonly DependencyProperty ViewportHeightProperty = DependencyProperty.Register(
        nameof(ViewportHeight), typeof(double), typeof(PlannerEntryEditor),
        new PropertyMetadata(0d, (target, args) =>
            ((PlannerEntryEditor)target).Height = Math.Max(120, (double)args.NewValue - 108)));
    public double ViewportHeight { get => (double)GetValue(ViewportHeightProperty); set => SetValue(ViewportHeightProperty, value); }

    internal PlannerEntryEditor(string content, DateTimeOffset? due, bool optional, DateTime? initialDate,
        FrameworkElement viewport)
    {
        Margin = new Thickness(0, 0, 0, 0);
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        MaxWidth = 560;
        Height = 120;
        void ResizeEditor()
        {
            if (viewport.ActualWidth <= 0) return;
            Width = Math.Max(180, Math.Min(MaxWidth, viewport.ActualWidth - 24));
        }
        viewport.SizeChanged += (_, _) => ResizeEditor();
        Loaded += (_, _) => ResizeEditor();
        SetBinding(ViewportHeightProperty, new System.Windows.Data.Binding(nameof(ActualHeight)) { Source = viewport });
        Input = new TextBox
        {
            Text = content, MaxLength = Math.Max(ContentLimit, content.Length), AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 12, 12, 60), VerticalContentAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(180, 28, 36, 46)), ToolTip = "事项内容"
        };
        WidgetFormStyles.Apply(Input, "FormInput");
        System.Windows.Automation.AutomationProperties.SetName(Input, "事项内容");
        Children.Add(Input);
        Picker = new WidgetDateTimePicker(due, optional, initialDate);
        TimeButton = WidgetFormStyles.Button("", (_, _) => ToggleTime());
        TimeButton.ToolTip = "设置时间";
        TimeButton.Margin = new Thickness(0);
        TimeButton.Padding = new Thickness(9, 5, 9, 5);
        TimeButton.MaxWidth = 260;
        TimeButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        var floating = new Border
        {
            Child = TimeButton, Background = new SolidColorBrush(Color.FromArgb(244, 37, 47, 59)),
            CornerRadius = new CornerRadius(8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8)
        };
        Children.Add(floating);
        TimePanel = new Border
        {
            Child = Picker, Padding = new Thickness(10), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(252, 37, 47, 59)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 190, 215, 235)), BorderThickness = new Thickness(1)
        };
        _timePopup = new Popup
        {
            Child = TimePanel, PlacementTarget = floating, Placement = PlacementMode.Top,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade
        };
        _timePopup.Closed += (_, _) => { if (IsLoaded) Input.Focus(); };
        Picker.ValueChanged += UpdateTimeLabel;
        SizeChanged += (_, _) =>
        {
            TimeButton.MaxWidth = Math.Max(120, ActualWidth - 20);
            if (_timePopup.IsOpen) ResizeTimePanel();
        };
        Unloaded += (_, _) => _timePopup.IsOpen = false;
        Loaded += (_, _) => Input.Focus();
        UpdateTimeLabel();
    }

    private void ToggleTime()
    {
        ResizeTimePanel();
        _timePopup.IsOpen = !_timePopup.IsOpen;
    }

    private void ResizeTimePanel() => TimePanel.Width = Math.Clamp(ActualWidth - 16, 180, 320);

    private void UpdateTimeLabel()
    {
        var label = Picker.TryGetUtc(out var due) && due is not null
            ? due.Value.ToLocalTime().ToString("M月d日 HH:mm") : "设置时间";
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(WidgetFormStyles.Icon("\uE787", 13));
        var text = WidgetContentControls.Text(label, 11);
        text.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(text);
        TimeButton.Content = row;
        System.Windows.Automation.AutomationProperties.SetName(TimeButton, label == "设置时间" ? label : $"设置时间，{label}");
    }
}
