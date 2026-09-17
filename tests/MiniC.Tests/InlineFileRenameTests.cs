using System;
using System.Threading.Tasks;
using MiniC.Services;
using MiniC.ViewModels;
using Xunit;

namespace MiniC.Tests;

public sealed class InlineFileRenameTests
{
    [Fact]
    public async Task BlurAndDeactivationSubmitOnlyOnceWithCapturedName()
    {
        var item = Item();
        var session = new InlineFileRenameSession();
        var pending = new TaskCompletionSource<bool>();
        var calls = 0;
        string submitted = null;
        Task<bool> Rename(string name) { calls++; submitted = name; return pending.Task; }
        var first = session.CommitAsync(item, Rename, false);
        var second = session.CommitAsync(item, Rename, false);
        Assert.Same(first, second);
        Assert.False(session.Cancel(item));
        item.EditName = "later-input";
        pending.SetResult(true);
        Assert.True(await first);
        Assert.Equal(1, calls);
        Assert.Equal("changed", submitted);
        Assert.False(item.IsRenaming);
    }

    [Fact]
    public async Task EscapePreventsSubsequentBlurSubmission()
    {
        var item = Item();
        var session = new InlineFileRenameSession();
        Assert.True(session.Cancel(item));
        Assert.False(await session.CommitAsync(item, _ => throw new InvalidOperationException("Must not rename"), false));
        Assert.Equal(DesktopFileService.GetRenameEditName(item), item.EditName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureKeepsOriginalFileAndEndsBlurEditing(bool enter)
    {
        var item = Item();
        var path = item.Path;
        var session = new InlineFileRenameSession();
        Assert.False(await session.CommitAsync(item, _ => Task.FromResult(false), enter));
        Assert.Equal(path, item.Path);
        Assert.Equal(enter, item.IsRenaming);
        if (!enter) Assert.Equal(DesktopFileService.GetRenameEditName(item), item.EditName);
    }

    private static DesktopItem Item() => new()
    {
        Path = @"C:\Temp\original.txt", Name = "original", EditName = "changed", IsRenaming = true
    };
}
