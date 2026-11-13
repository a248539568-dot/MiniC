using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MiniC.Models;
using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Views;
using MiniC.Widgets;
using Xunit;

namespace MiniC.Tests;

public sealed class RenameRegressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedHostReconciliationPreservesRenameDraftAndItemSelection(bool title)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new GroupFixture();
            fixture.Configure();
            var group = fixture.Group;
            var item = Assert.Single(group.Items);
            if (title)
            {
                group.EditName = "收纳盒名称草稿";
                group.IsRenaming = true;
            }
            else
            {
                fixture.Window.BeginRename(item);
                item.EditName = "修改中的文件名.md";
                item.IsSelected = true;
            }
            var draft = title ? group.EditName : item.EditName;

            // Run the exact host configuration used by each one-second reconcile tick.
            for (var tick = 0; tick < 3; tick++)
            {
                await Task.Delay(1000);
                fixture.Configure();
                fixture.Window.RefreshDesktopPlacement();
                Assert.True(title ? group.IsRenaming : item.IsRenaming);
                Assert.Equal(draft, title ? group.EditName : item.EditName);
                if (!title) Assert.True(item.IsSelected);
                Assert.Equal(0, fixture.RenameCalls);
            }

            Assert.False(fixture.Window.IsVisible);
        });
    }

    [Fact]
    public async Task SwitchingToAnotherTabStillSubmitsTheFileDraftOnce()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new GroupFixture();
            fixture.Configure();
            var item = Assert.Single(fixture.Group.Items);
            item.EditName = "new-name.md";
            item.IsRenaming = true;
            fixture.Window.SwitchTab(fixture.Host.Tabs[1]);
            Assert.False(item.IsRenaming);
            Assert.Equal(1, fixture.RenameCalls);
            fixture.Configure();
            Assert.Equal(1, fixture.RenameCalls);
            await Task.CompletedTask;
        });
    }

    private sealed class GroupFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "MiniC-Rename-" + Guid.NewGuid().ToString("N"));
        private readonly WidgetStore _store;
        private readonly WidgetWeatherService _weather = new(null, false);
        private readonly Dictionary<string, DeskGroup> _groups;
        internal DeskGroup Group { get; } = new() { Name = "收纳盒", Material = "Solid", Theme = "Graphite", Width = 340, Height = 380 };
        internal DeskGroupWindow Window { get; }
        internal BoxHostState Host { get; }
        internal int RenameCalls { get; private set; }

        internal GroupFixture()
        {
            _store = new WidgetStore(_root);
            Group.Items.Add(new DesktopItem { Path = @"C:\Temp\original.txt", Name = "original" });
            var other = new DeskGroup { Name = "其他收纳盒" };
            _groups = new() { [Group.Id] = Group, [other.Id] = other };
            Host = new BoxHostState { Tabs = [new(BoxContentKind.DesktopGroup, Group.Id), new(BoxContentKind.DesktopGroup, other.Id)],
                ActiveTab = new(BoxContentKind.DesktopGroup, Group.Id), Width = 340, Height = 380, Material = "Solid", Theme = "Graphite" };
            var callbacks = new DeskGroupWindowCallbacks(_ => { }, (_, _, _) => Task.CompletedTask,
                (_, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => { RenameCalls++; return Task.FromResult(true); }, (_, _, _, _) => Task.CompletedTask,
                _ => { }, _ => { }, _ => { }, _ => { }, () => { });
            Window = new DeskGroupWindow(Group, callbacks, new DesktopLayerHostService(), new DesktopDragStateService());
        }

        internal void Configure() => Window.ConfigureHost(Host, _groups, new Dictionary<string, WidgetState>(), _store, _weather,
            _ => { }, _ => { }, () => { }, (_, _) => { }, (_, _) => { }, (_, _, _) => { }, (_, _) => { }, (_, _) => null, (_, _) => { });

        public void Dispose()
        {
            Window.ClosePermanently();
            Directory.Delete(_root, true);
        }
    }
}
