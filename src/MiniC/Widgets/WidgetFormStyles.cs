using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Control = System.Windows.Controls.Control;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace MiniC.Widgets;

internal static class WidgetFormStyles
{
    // WPF styles belong to their dispatcher; tests and application views can use different STA threads.
    private static readonly ThreadLocal<ResourceDictionary> Resources = new(() => new ResourceDictionary
    {
        Source = new Uri("/MiniC;component/Widgets/WidgetFormStyles.xaml", UriKind.Relative)
    });

    internal static void Apply(Control control, string key) => control.Style = (Style)Resources.Value![key];

    internal static ControlTemplate ButtonTemplate(CornerRadius radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Surface");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, radius);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        WidgetContentControls.ProtectButtonLabel(content);
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "Surface"));
        template.Triggers.Add(hover);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.White, "Surface"));
        focus.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "Surface"));
        template.Triggers.Add(focus);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65, "Surface"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "Surface"));
        template.Triggers.Add(disabled);
        return template;
    }

    internal static Button Button(string text, RoutedEventHandler action)
    {
        var button = WidgetContentControls.Action(text, action);
        button.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        button.BorderBrush = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255));
        button.BorderThickness = new Thickness(1);
        button.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
        button.FocusVisualStyle = null;
        button.Template = ButtonTemplate(new CornerRadius(8));
        return button;
    }

    internal static TextBlock Icon(string glyph, int size = 14) => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size,
        Effect = MiniC.Services.GroupMaterialBrushes.ContentShadow,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
}
