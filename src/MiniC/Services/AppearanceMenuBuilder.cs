using System.Windows;
using Controls = global::System.Windows.Controls;
using MediaBrush = global::System.Windows.Media.Brush;
using MediaBrushes = global::System.Windows.Media.Brushes;
using MediaColor = global::System.Windows.Media.Color;
using MiniC.Models;

namespace MiniC.Services;

/// <summary>所有桌面窗口共用的外观菜单构建器；业务窗口只追加自己的功能菜单。</summary>
internal static class AppearanceMenuBuilder
{
    public sealed record Context(
        Func<WindowAppearanceService.State> ReadState,
        global::System.Windows.Style MenuStyle,
        global::System.Windows.Style ItemStyle,
        global::System.Windows.Style SeparatorStyle,
        Action<WindowAppearanceService.State> Apply);
    public static void AddTo(global::System.Windows.Controls.ContextMenu menu, Context context, string colorTitle, string materialTitle)
    {
        var initial = WindowAppearanceService.Normalize(context.ReadState());
        global::System.Windows.Controls.MenuItem Item(object header) => new global::System.Windows.Controls.MenuItem { Header = header, Style = context.ItemStyle, ItemContainerStyle = context.ItemStyle };
        global::System.Windows.Controls.Separator Separator() => new global::System.Windows.Controls.Separator { Style = context.SeparatorStyle };

        var colors = Item(colorTitle);
        foreach (var choice in ThemeChoices())
        {
            var child = Item(ColorHeader(choice.Name, choice.Color));
            child.IsCheckable = true;
            child.IsChecked = initial.Theme == choice.Id;
            child.Click += (_, _) => context.Apply(WindowAppearanceService.SetTheme(context.ReadState(), choice.Id));
            colors.Items.Add(child);
        }
        menu.Items.Add(colors);

        var materials = Item(materialTitle);
        Action? refreshMaterialOptions = null;
        foreach (var (id, name) in GroupMaterialBrushes.Choices)
        {
            var child = Item(name);
            child.IsCheckable = true;
            child.IsChecked = initial.Material == id;
            child.Click += (_, _) =>
            {
                context.Apply(WindowAppearanceService.SetMaterial(context.ReadState(), id));
                refreshMaterialOptions?.Invoke();
            };
            materials.Items.Add(child);
        }
        menu.Items.Add(materials);
        var opacity = OpacityItem(context, Item);
        opacity.IsEnabled = initial.Material != "Solid";
        menu.Items.Add(opacity);

        var isThrough = initial.Material != "Solid" && initial.Appearance.ThroughFor(initial.Material);
        var through = Item("透出后方窗口");
        through.IsCheckable = true;
        through.IsChecked = isThrough;
        through.IsEnabled = initial.Material != "Solid";
        through.Click += (_, _) =>
        {
            var current = context.ReadState();
            if (current.Material == "Solid") return;
            var enabled = current.Appearance.ThroughFor(current.Material);
            context.Apply(WindowAppearanceService.SetThrough(current, !enabled));
            var applied = context.ReadState();
            through.IsChecked = applied.Material != "Solid" && applied.Appearance.ThroughFor(applied.Material);
        };
        menu.Items.Add(through);
        refreshMaterialOptions = () =>
        {
            var state = context.ReadState();
            opacity.IsEnabled = through.IsEnabled = state.Material != "Solid";
            through.IsChecked = through.IsEnabled && state.Appearance.ThroughFor(state.Material);
            for (var i = 0; i < materials.Items.Count; i++)
                ((global::System.Windows.Controls.MenuItem)materials.Items[i]).IsChecked = GroupMaterialBrushes.Choices[i].Id == state.Material;
        };
        var capture = Item("截屏录屏时显示窗口");
        capture.IsCheckable = true;
        capture.IsChecked = !initial.Appearance.HideFromCapture;
        capture.Click += (_, _) =>
        {
            var current = context.ReadState();
            context.Apply(WindowAppearanceService.SetHideFromCapture(current, !current.Appearance.HideFromCapture));
            capture.IsChecked = !context.ReadState().Appearance.HideFromCapture;
        };
        menu.Items.Add(capture);
        menu.Items.Add(Separator());
    }

    private static global::System.Windows.Controls.MenuItem OpacityItem(Context context, Func<object, global::System.Windows.Controls.MenuItem> item)
    {
        var initial = WindowAppearanceService.Normalize(context.ReadState());
        var value = new global::System.Windows.Controls.TextBlock { Text = $"{BackgroundTransparency.ToTransparency(initial.Opacity):P0}", FontSize = 11,
            Foreground = initial.Material == "Solid" ? MediaBrushes.White : new global::System.Windows.Media.SolidColorBrush(MediaColor.FromArgb(220, 255, 255, 255)),
            HorizontalAlignment = global::System.Windows.HorizontalAlignment.Right };
        var slider = new global::System.Windows.Controls.Slider { Minimum = 0, Maximum = 1, Value = BackgroundTransparency.ToTransparency(initial.Opacity),
            TickFrequency = 0.02, IsSnapToTickEnabled = true, Margin = new Thickness(0, 7, 0, 0), Width = 190 };
        slider.ValueChanged += (_, e) => { value.Text = $"{e.NewValue:P0}"; context.Apply(WindowAppearanceService.SetOpacity(context.ReadState(), BackgroundTransparency.ToOpacity(e.NewValue))); };
        var panel = new global::System.Windows.Controls.Grid { Width = 206, Margin = new Thickness(0, 2, 0, 3) };
        panel.RowDefinitions.Add(new global::System.Windows.Controls.RowDefinition()); panel.RowDefinitions.Add(new global::System.Windows.Controls.RowDefinition());
        panel.Children.Add(new global::System.Windows.Controls.TextBlock { Text = "背景透明度", Foreground = MediaBrushes.White, FontSize = 12 });
        panel.Children.Add(value); global::System.Windows.Controls.Grid.SetColumn(value, 1);
        global::System.Windows.Controls.Grid.SetRow(slider, 1); panel.Children.Add(slider);
        var result = item(panel); result.StaysOpenOnClick = true; return result;
    }

    private static FrameworkElement ColorHeader(string name, string color) => new global::System.Windows.Controls.StackPanel
    {
        Orientation = global::System.Windows.Controls.Orientation.Horizontal,
        Children = { new global::System.Windows.Controls.Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            Background = (MediaBrush)new global::System.Windows.Media.BrushConverter().ConvertFromString(color)!, Margin = new Thickness(0, 0, 9, 0) },
            new global::System.Windows.Controls.TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center } }
    };

    public static IReadOnlyList<(string Id, string Name, string Color)> ThemeChoices() =>
    [
        ("White", "珍珠白", "#FFF7FAFF"), ("Blue", "冰川蓝", "#FF81B5F4"),
        ("Mint", "薄荷绿", "#FF74D3B3"), ("Purple", "淡雅紫", "#FFB799EC"),
        ("Amber", "暖阳橙", "#FFF5C075"), ("Graphite", "石墨黑", "#FF282C38")
    ];
}















