using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Binding = System.Windows.Data.Binding;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Control = System.Windows.Controls.Control;

namespace MiniC.Widgets;

internal static class WidgetContentControls
{
    private static readonly Brush ForegroundBrush = Brushes.White;
    internal static TextBlock Text(string content, int size = 13, Brush? color = null) => new()
    {
        Text = content, FontSize = size, Foreground = color ?? ForegroundBrush,
        Effect = MiniC.Services.GroupMaterialBrushes.ContentShadow,
        FontWeight = FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    internal static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(82, 15, 24, 39)), CornerRadius = new CornerRadius(12),
        Padding = padding ?? new Thickness(12), Margin = new Thickness(0, 0, 0, 9), Child = child
    };
    internal static Button Button(string caption, RoutedEventHandler click, bool accent = false) => new()
    {
        Content = caption, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 7, 0),
        MinHeight = 30, Background = accent ? new SolidColorBrush(Color.FromRgb(72, 132, 208))
            : new SolidColorBrush(Color.FromArgb(105, 34, 50, 71)),
        Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        Template = RoundedButtonTemplate(),
        Tag = click
    };
    internal static ControlTemplate RoundedButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        ProtectButtonLabel(presenter);
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter); template.VisualTree = border; return template;
    }
    internal static Button Action(string caption, RoutedEventHandler click, bool accent = false)
    {
        var button = Button(caption, click, accent); button.Click += click; return button;
    }

    internal static void ProtectButtonLabel(FrameworkElementFactory presenter) => presenter.SetValue(
        ContentPresenter.ContentTemplateSelectorProperty, new ShadowLabelTemplateSelector());

    private sealed class ShadowLabelTemplateSelector : DataTemplateSelector
    {
        private readonly DataTemplate _label;
        internal ShadowLabelTemplateSelector()
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding());
            text.SetValue(UIElement.EffectProperty, MiniC.Services.GroupMaterialBrushes.ContentShadow);
            _label = new DataTemplate { VisualTree = text };
            _label.Seal();
        }
        // Explicit visual content already shadows its own text/icons.
        public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is string ? _label : null;
    }
    internal static TextBox Input(string? text = null, bool multiline = false) => new()
    {
        Text = text ?? "", Foreground = Brushes.White, CaretBrush = Brushes.White,
        Background = new SolidColorBrush(Color.FromArgb(175, 24, 35, 52)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(52, 255, 255, 255)), BorderThickness = new Thickness(1),
        Padding = new Thickness(9, 6, 9, 6), FontSize = 13,
        AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Template = RoundedInputTemplate(),
        VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
    };
    internal static ControlTemplate RoundedInputTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)); host.Name = "PART_ContentHost";
        border.AppendChild(host); template.VisualTree = border; return template;
    }
    internal static StackPanel Row(params UIElement[] elements)
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 9) };
        foreach (var element in elements) panel.Children.Add(element);
        return panel;
    }
}
