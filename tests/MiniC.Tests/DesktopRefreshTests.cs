using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using MiniC.Services;
using MiniC.ViewModels;
using Xunit;

namespace MiniC.Tests;

public sealed class DesktopRefreshTests
{
    [Fact]
    public void SnapshotDetectsCreateOverwriteRenameAndDeleteWithoutConsumingBaseline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniC-Snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var initial = DesktopDirectorySnapshot.Capture(Directory.EnumerateFileSystemEntries(directory));
            var path = Path.Combine(directory, "image.png");
            File.WriteAllText(path, "first");
            var saved = DesktopDirectorySnapshot.Capture(Directory.EnumerateFileSystemEntries(directory));
            Assert.False(saved.Matches(initial));
            Assert.False(saved.Matches(initial)); // 检查差异不能提前消费刷新请求。
            Assert.True(saved.Matches(DesktopDirectorySnapshot.Capture(Directory.EnumerateFileSystemEntries(directory))));
            var lastWrite = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, "larger replacement");
            File.SetLastWriteTimeUtc(path, lastWrite);
            var overwritten = DesktopDirectorySnapshot.Capture(Directory.EnumerateFileSystemEntries(directory));
            Assert.False(overwritten.Matches(saved));
            var renamed = Path.Combine(directory, "final.png");
            File.Move(path, renamed);
            var replaced = DesktopDirectorySnapshot.Capture(Directory.EnumerateFileSystemEntries(directory));
            Assert.False(replaced.Matches(overwritten));
            File.Delete(renamed);
            Assert.True(DesktopDirectorySnapshot.Capture(Directory.EnumerateFileSystemEntries(directory)).Matches(initial));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DirectFileSavesAreObservedAlongsideShellRegistration()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "MiniC-Watcher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var source = new HwndSource(new HwndSourceParameters("MiniC-Watcher-Test")
                { Width = 0, Height = 0, ParentWindow = new IntPtr(-3) });
                using var service = new DesktopFileService(directory, directory);
                service.StartWatching(source.Handle, 0x8101);
                var changed = new TaskCompletionSource<DesktopChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                service.DesktopChanged += (_, args) => changed.TrySetResult(args);
                // 直接写临时文件并改名，不调用任何 SHChangeNotify，模拟外部程序保存。
                var temporary = Path.Combine(directory, "saving.tmp");
                var final = Path.Combine(directory, "export.png");
                await File.WriteAllTextAsync(temporary, "image bytes");
                File.Move(temporary, final);
                var args = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(args.HasFileChanges);
                Assert.True(File.Exists(final));
                Assert.Contains(args.Renames, rename => rename.OldPath == temporary && rename.NewPath == final);
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    [Fact]
    public async Task RecycleBinTracksFullEmptyNewModelsAndFailedQueries()
    {
        var count = 3L;
        var empty = new DrawingImage(); empty.Freeze();
        var full = new DrawingImage(); full.Freeze();
        var service = new RecycleBinIconService(() => Task.FromResult(count),
            isEmpty => Task.FromResult<ImageSource>(isEmpty ? empty : full));
        var item = new DesktopItem { Path = DesktopFileService.RecycleBinShellPath, Name = "回收站" };
        await service.RefreshAsync([item]);
        Assert.Same(full, item.Icon);
        count = 0;
        await service.RefreshAsync([item]);
        Assert.Same(empty, item.Icon);
        count = -1;
        await service.RefreshAsync([item]);
        Assert.Same(empty, item.Icon);
        count = 0;
        var replacement = new DesktopItem { Path = DesktopFileService.RecycleBinShellPath, Name = "回收站" };
        await service.RefreshAsync([replacement]);
        Assert.Same(empty, replacement.Icon);
        count = 2;
        await service.RefreshAsync([replacement]);
        Assert.Same(full, replacement.Icon);
    }

    [Fact]
    public async Task RecycleBinRetriesIconFailureAndStopsLateWrites()
    {
        var icon = new DrawingImage(); icon.Freeze();
        var attempts = 0;
        var service = new RecycleBinIconService(() => Task.FromResult(0L),
            _ => Task.FromResult<ImageSource>(++attempts == 1 ? null : icon));
        var item = new DesktopItem { Path = DesktopFileService.RecycleBinShellPath, Name = "回收站" };
        await service.RefreshAsync([item]);
        Assert.Null(item.Icon);
        await service.RefreshAsync([item]);
        Assert.Same(icon, item.Icon);
        var pending = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new RecycleBinIconService(() => pending.Task, _ => Task.FromResult<ImageSource>(icon));
        var untouched = new DesktopItem { Path = DesktopFileService.RecycleBinShellPath, Name = "回收站" };
        var refresh = stopped.RefreshAsync([untouched]);
        await stopped.RefreshAsync([untouched]); // 重叠轮询不能重复查询/排队。
        stopped.Stop();
        pending.SetResult(0);
        await refresh;
        Assert.Null(untouched.Icon);
    }
}
