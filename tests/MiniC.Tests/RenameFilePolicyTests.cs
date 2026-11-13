using System;
using System.IO;
using System.Threading.Tasks;
using MiniC.Services;
using MiniC.ViewModels;
using Xunit;

namespace MiniC.Tests;

public sealed class RenameFilePolicyTests
{
    [Theory]
    [InlineData("report.txt", "report.txt", 6)]
    [InlineData("archive.tar.gz", "archive.tar.gz", 11)]
    [InlineData("README", "README", 6)]
    [InlineData(".env", ".env", 4)]
    [InlineData("App.v2.lnk", "App.v2", 6)]
    [InlineData("Site.v2.URL", "Site.v2", 7)]
    public void EditorShowsFullOrdinaryNamesAndSelectsOnlyTheirStem(string fileName, string expected, int selection)
    {
        var item = new DesktopItem { Path = Path.Combine(@"C:\Desktop", fileName), Name = "Shell display name" };
        var editName = DesktopFileService.GetRenameEditName(item);
        Assert.Equal(expected, editName);
        Assert.Equal(selection, DesktopFileService.GetRenameSelectionLength(item, editName));
    }

    [Theory]
    [InlineData("report.txt", "report.md", false, "report.md")]
    [InlineData("report.txt", "report", false, "report")]
    [InlineData("README", "README.txt", false, "README.txt")]
    [InlineData("archive.tar.gz", "archive.zip", false, "archive.zip")]
    [InlineData("App.lnk", "New App", false, "New App.lnk")]
    [InlineData("App.LNK", "New App.lnk", false, "New App.lnk")]
    [InlineData("Site.url", "New Site", false, "New Site.url")]
    [InlineData("Site.URL", "New Site.v2", false, "New Site.v2.URL")]
    [InlineData("Folder.lnk", "Other.txt", true, "Other.txt")]
    public void TargetsAllowOrdinaryExtensionChangesAndPreserveShortcutTypes(string source, string input, bool folder, string expected)
    {
        Assert.Equal(expected, DesktopFileService.GetRenameTargetFileName(source, input, folder));
    }

    [Fact]
    public void DottedFolderNameIsFullySelected()
    {
        var item = new DesktopItem { Path = @"C:\Desktop\Project.v2", Name = "Project.v2", IsDirectory = true };
        Assert.Equal("Project.v2", DesktopFileService.GetRenameEditName(item));
        Assert.Equal(10, DesktopFileService.GetRenameSelectionLength(item, "Project.v2"));
    }

    [Theory]
    [InlineData("original.txt", "changed.md", "changed.md")]
    [InlineData("original.txt", "changed", "changed")]
    [InlineData("original.lnk", "changed", "changed.lnk")]
    [InlineData("original.url", "changed", "changed.url")]
    public async Task RealRenameChangesTheExpectedPathWithoutChangingContents(string sourceName, string input, string targetName)
    {
        var root = Path.Combine(Path.GetTempPath(), "MiniC-Rename-Files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, sourceName);
            await File.WriteAllTextAsync(source, "original contents");
            using var service = new DesktopFileService(root, root);
            var result = await service.RenameItemAsync(source, input);
            var target = Path.Combine(root, targetName);
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(target, result.NewPath);
            Assert.False(File.Exists(source));
            Assert.Equal("original contents", await File.ReadAllTextAsync(target));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExtensionChangeCannotOverwriteAnExistingFileOrEscapeTheDesktop()
    {
        var root = Path.Combine(Path.GetTempPath(), "MiniC-Rename-Collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "report.txt");
            var existing = Path.Combine(root, "report.md");
            await File.WriteAllTextAsync(source, "source");
            await File.WriteAllTextAsync(existing, "existing");
            using var service = new DesktopFileService(root, root);
            Assert.False((await service.RenameItemAsync(source, "report.md")).Succeeded);
            Assert.False((await service.RenameItemAsync(source, @"..\escaped.md")).Succeeded);
            Assert.Equal("source", await File.ReadAllTextAsync(source));
            Assert.Equal("existing", await File.ReadAllTextAsync(existing));
        }
        finally { Directory.Delete(root, true); }
    }
}
