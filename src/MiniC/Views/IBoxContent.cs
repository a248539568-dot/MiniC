using MiniC.Models;

namespace MiniC.Views;

/// <summary>Identity and presentation preferences independent of host placement.</summary>
public interface IBoxContent
{
    BoxTabRef Tab { get; }
    string Title { get; }
    System.Windows.Size PreferredSize { get; }
}
