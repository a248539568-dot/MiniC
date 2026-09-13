using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MiniC.Views;

/// <summary>托盘左键打开的快捷操作面板，只负责界面转发，不直接处理业务数据。</summary>
public partial class TrayPanelWindow : Window
{
    private readonly Action _createGroup;
    private readonly Action<string> _applyTheme;
    private readonly Action<string> _applyMaterial;
    private readonly Action<double> _applyOpacity;
    private readonly Func<(string Theme, string Material, double Opacity)> _getUnifiedStyle;
    private readonly Action _toggleBoxes;
    private DateTime _keepVisibleUntilUtc;
    private bool _isSynchronizingStyle;

    public TrayPanelWindow(
        Action createGroup,
        Action<string> applyTheme,
        Action<string> applyMaterial,
        Action<double> applyOpacity,
        Func<(string Theme, string Material, double Opacity)> getUnifiedStyle,
        Action toggleBoxes)
    {
        _createGroup = createGroup;
        _applyTheme = applyTheme;
        _applyMaterial = applyMaterial;
        _applyOpacity = applyOpacity;
        _getUnifiedStyle = getUnifiedStyle;
        _toggleBoxes = toggleBoxes;
        // XAML 初始化 Slider 时会触发 ValueChanged，先屏蔽回调，避免首次打开面板重置用户透明度。
        _isSynchronizingStyle = true;
        try
        {
            InitializeComponent();
        }
        finally
        {
            _isSynchronizingStyle = false;
        }
    }

    public void ShowNearTray()
    {
        SynchronizeStyleControls();
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        Top = workArea.Bottom - Height - 16;
        if (!IsVisible) Show();
        Activate();
    }

    private void SynchronizeStyleControls()
    {
        var style = _getUnifiedStyle();
        _isSynchronizingStyle = true;
        try
        {
            SetCheckedByTag([ThemeWhite, ThemeBlue, ThemeMint, ThemePurple, ThemeAmber, ThemeGraphite], style.Theme);
            SetCheckedByTag([MaterialClear, MaterialFrosted, MaterialCrystal, MaterialSatin], style.Material);
            OpacitySlider.Value = style.Opacity;
            OpacityValue.Text = $"{style.Opacity:P0}";
        }
        finally
        {
            _isSynchronizingStyle = false;
        }
    }

    private static void SetCheckedByTag(IEnumerable<ToggleButton> buttons, string value)
    {
        foreach (var button in buttons) button.IsChecked = Equals(button.Tag, value);
    }

    private void CreateGroup_Click(object sender, RoutedEventArgs e) => RunAndKeepVisible(_createGroup);

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSynchronizingStyle && sender is FrameworkElement { Tag: string theme }) _applyTheme(theme);
    }

    private void Material_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSynchronizingStyle && sender is FrameworkElement { Tag: string material }) _applyMaterial(material);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue is null) return;
        OpacityValue.Text = $"{e.NewValue:P0}";
        if (!_isSynchronizingStyle) _applyOpacity(e.NewValue);
    }

    private void ToggleBoxes_Click(object sender, RoutedEventArgs e) => RunAndKeepVisible(_toggleBoxes);

    private void ClosePanel_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow >= _keepVisibleUntilUtc) Hide();
    }

    private void RunAndKeepVisible(Action action)
    {
        _keepVisibleUntilUtc = DateTime.UtcNow.AddSeconds(1.5);
        action();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Hide();
        e.Handled = true;
    }
}
