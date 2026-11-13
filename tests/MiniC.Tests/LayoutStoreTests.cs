using System;
using System.IO;
using System.Threading.Tasks;
using MiniC.Services;
using Xunit;

namespace MiniC.Tests;

public sealed class LayoutStoreTests
{
    [Fact]
    public async Task InvalidLayoutIsNotReplacedByDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "MiniC-Layout-Test-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "MiniC");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "layout.json");
        const string invalid = "{invalid layout";
        try
        {
            await File.WriteAllTextAsync(path, invalid);
            var store = new LayoutStore(root);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
            Assert.Equal(invalid, await File.ReadAllTextAsync(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingLayoutStillStartsWithDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "MiniC-Layout-Test-" + Guid.NewGuid().ToString("N"));
        var state = await new LayoutStore(root).LoadAsync();
        Assert.NotEmpty(state.Groups);
        Assert.False(Directory.Exists(root));
    }
}
