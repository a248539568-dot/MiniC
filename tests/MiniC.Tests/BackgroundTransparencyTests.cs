using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using MiniC.Models;
using MiniC.Services;
using MiniC.ViewModels;
using Xunit;

namespace MiniC.Tests;

public sealed class BackgroundTransparencyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.05)]
    [InlineData(0.22)]
    [InlineData(0.62)]
    [InlineData(0.92)]
    [InlineData(1)]
    public void TransparencyRoundTripsWithoutChangingLegacyStoredMeaning(double opacity)
    {
        var group = new DeskGroup { Opacity = opacity };
        Assert.Equal(opacity, group.Opacity);
        var json = JsonSerializer.Serialize(new GroupState { Opacity = group.Opacity });
        var restored = JsonSerializer.Deserialize<GroupState>(json);
        Assert.Equal(opacity, BackgroundTransparency.NormalizeOpacity(restored.Opacity));
        Assert.Equal(opacity, BackgroundTransparency.ToOpacity(BackgroundTransparency.ToTransparency(opacity)), 8);
    }

    [Fact]
    public void HighTransparencyRemovesFrostedColorWithoutChangingSource()
    {
        var previous = 255;
        for (var percent = 0; percent <= 100; percent++)
        {
            var opacity = BackgroundTransparency.ToOpacity(percent / 100d);
            var group = new DeskGroup { Material = "Frosted", Opacity = opacity };
            var alpha = ((SolidColorBrush)group.GlassBackground).Color.A;
            Assert.True(alpha <= previous);
            previous = alpha;
        }
        Assert.Equal(1, previous);
        var transparent = new DeskGroup { Material = "Frosted", Opacity = 0 };
        Assert.False(transparent.IsCrystal);
        transparent.ComposedBackdropActive = true;
        Assert.Equal(1, ((SolidColorBrush)transparent.GlassBackground).Color.A);
    }

}

