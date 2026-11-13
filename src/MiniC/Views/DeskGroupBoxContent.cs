using MiniC.Models;
using MiniC.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace MiniC.Views;

/// <summary>Mountable file surface and content identity for a desktop group.</summary>
public sealed class DeskGroupBoxContent : System.Windows.Controls.UserControl, IBoxContent
{
    private DeskGroup _group;

    public DeskGroupBoxContent(DeskGroup group, ScrollViewer fileSurface)
    {
        _group = group;
        Content = fileSurface;
        // The file surface owns the icon grid alignment. Keep the surface stretched
        // to the host so its fixed-size tiles start at the content area's top-left
        // instead of centering the whole grid as one block.
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        VerticalContentAlignment = System.Windows.VerticalAlignment.Stretch;
        DataContext = group;
    }

    public BoxTabRef Tab => new(BoxContentKind.DesktopGroup, _group.Id);
    public string Title => _group.Name;
    public System.Windows.Size PreferredSize => new(_group.Width, _group.Height);

    public void SetGroup(DeskGroup group)
    {
        _group = group;
        DataContext = group;
    }
}
