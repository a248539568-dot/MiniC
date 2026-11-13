using MiniC.Models;
using MiniC.Services;
using Xunit;

namespace MiniC.Tests;

public sealed class BoxSizePolicyTests
{
    [Fact]
    public void NewBoxUsesTheFirstValidHostSize()
    {
        var hosts = new[]
        {
            new BoxHostState { Width = 512, Height = 286 },
            new BoxHostState { Width = 760, Height = 520 }
        };

        Assert.Equal(new BoxSize(512, 286), BoxSizePolicy.GetNewBoxSize(hosts));
    }

    [Fact]
    public void NewBoxSkipsInvalidFirstHostAndUsesTheNextValidHost()
    {
        var hosts = new[]
        {
            new BoxHostState { Width = 0, Height = 0 },
            new BoxHostState { Width = 420, Height = 360 }
        };

        Assert.Equal(new BoxSize(420, 360), BoxSizePolicy.GetNewBoxSize(hosts));
    }

    [Fact]
    public void NewBoxUsesTheUnifiedSmallFallbackWhenNoHostExists()
    {
        Assert.Equal(new BoxSize(BoxSizePolicy.DefaultWidth, BoxSizePolicy.DefaultHeight),
            BoxSizePolicy.GetNewBoxSize([]));
    }
}
