using System;
using System.Collections.Generic;
using System.Linq;
using MiniC.Models;
using MiniC.Services;
using MiniC.ViewModels;
using MiniC.Views;
using Xunit;

namespace MiniC.Tests;

public sealed class BoxTopologyTests
{
    private static readonly BoxTabRef Group = new(BoxContentKind.DesktopGroup, "same-id");
    private static readonly BoxTabRef Widget = new(BoxContentKind.Widget, "same-id");

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    public void AnySourceMergesIntoChosenDestinationRegardlessOfHostOrder(int sourceIndex, int targetIndex)
    {
        foreach (var order in new[] { new[] { 0, 1, 2 }, [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0] })
        {
            var tabs = Enumerable.Range(0, 3).Select(index => new BoxTabRef(BoxContentKind.DesktopGroup, "group-" + index)).ToArray();
            var hosts = order.Select(index => new BoxHostState { Id = "host-" + index,
                Tabs = [tabs[index]], ActiveTab = tabs[index], X = 20 + index * 450, Y = 60,
                Width = 340 + index * 10, Height = 380, Theme = "Mint", Material = "Solid" }).ToList();
            var extra = new BoxTabRef(BoxContentKind.Widget, "nested-widget");
            hosts.Single(host => host.Tabs.Contains(tabs[sourceIndex])).Tabs.Add(extra);
            var destination = hosts.Single(host => host.Tabs.Contains(tabs[targetIndex]));
            var originalBounds = (destination.X, destination.Y, destination.Width, destination.Height);
            BoxTopologyService.MergeHost(hosts, tabs[sourceIndex], tabs[targetIndex]);
            Assert.Equal(2, hosts.Count);
            Assert.Contains(destination, hosts);
            Assert.Equal([tabs[targetIndex], tabs[sourceIndex], extra], destination.Tabs);
            Assert.Equal(tabs[sourceIndex], destination.ActiveTab);
            Assert.Equal(originalBounds, (destination.X, destination.Y, destination.Width, destination.Height));
            Assert.Equal("Solid", destination.Material);
            BoxTopologyService.Validate(hosts, tabs.Append(extra).ToArray());
        }
    }

    [Fact]
    public void LegacyGroupsAndWidgetsBecomeDistinctTabsEvenWithMatchingIds()
    {
        var hosts = BoxTopologyService.Reconcile(null,
            [new GroupState { Id = "same-id", TabGroupId = "group-cluster", TabOrder = 1 },
             new GroupState { Id = "second", TabGroupId = "group-cluster", TabOrder = 0 }],
            [("same-id", 20, 30, 300, 200, false)]);

        BoxTopologyService.Validate(hosts, [Group, Widget, new BoxTabRef(BoxContentKind.DesktopGroup, "second")]);
        Assert.Equal(2, hosts.Count);
        Assert.Equal("second", hosts[0].ActiveTab?.Id);
        Assert.Equal(2, hosts[0].Tabs.Count);
        Assert.Equal(Widget, Assert.Single(hosts[1].Tabs));
    }

    [Fact]
    public void MixedMergeAndDetachPreserveIdentityOrderAndIndividualBounds()
    {
        var hosts = BoxTopologyService.Reconcile(null,
            [new GroupState { Id = "same-id", Width = 430, Height = 320 }],
            [("same-id", 40, 50, 700, 500, false)]);
        BoxTopologyService.Merge(hosts, Widget, Group);
        var merged = Assert.Single(hosts);
        Assert.Equal([Group, Widget], merged.Tabs);
        Assert.Equal(Widget, merged.ActiveTab);

        BoxTopologyService.Detach(hosts, Widget, 40, 50, 700, 500, false);
        Assert.Equal(2, hosts.Count);
        Assert.Equal(Group, hosts[0].ActiveTab);
        Assert.Equal(700, hosts[1].Width);
        Assert.Equal(500, hosts[1].Height);
        BoxTopologyService.Validate(hosts, [Group, Widget]);
    }

    [Fact]
    public void ReconcileDropsStaleAndDuplicateTabsWithoutLosingValidContent()
    {
        var saved = new List<BoxHostState>
        {
            new() { Id = "host", Tabs = [Group, Group, new BoxTabRef(BoxContentKind.Widget, "missing")], ActiveTab = Group },
            new() { Id = "host", Tabs = [Widget, Group], ActiveTab = Widget }
        };
        var hosts = BoxTopologyService.Reconcile(saved,
            [new GroupState { Id = "same-id" }], [("same-id", 1, 2, 300, 200, false)]);
        BoxTopologyService.Validate(hosts, [Group, Widget]);
        Assert.NotEqual(hosts[0].Id, hosts[1].Id);
        Assert.Equal(2, hosts.SelectMany(host => host.Tabs).Count());
    }

    [Fact]
    public void ValidationRejectsOrphanedContent()
    {
        Assert.Throws<InvalidOperationException>(() => BoxTopologyService.Validate(
            [new BoxHostState { Tabs = [Group], ActiveTab = Group }], [Group, Widget]));
    }

    [Fact]
    public void SavedTopologyDoesNotRegroupNewContentUsingLegacyCluster()
    {
        var saved = new List<BoxHostState>
        {
            new() { Id = "cluster", Tabs = [Group], ActiveTab = Group }
        };
        var hosts = BoxTopologyService.Reconcile(saved,
            [new GroupState { Id = "same-id", TabGroupId = "cluster" },
             new GroupState { Id = "new", TabGroupId = "cluster" }], []);

        Assert.Equal(2, hosts.Count);
        Assert.Equal(new BoxTabRef(BoxContentKind.DesktopGroup, "new"), Assert.Single(hosts[1].Tabs));
    }

    [Fact]
    public void RemoveActiveTabSelectsRemainingAndAvoidsHostIdCollision()
    {
        var hosts = BoxTopologyService.Reconcile(null,
            [new GroupState { Id = "same-id" }],
            [("same-id", 20, 30, 300, 200, false)]);
        Assert.Equal(2, hosts.Select(host => host.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        BoxTopologyService.Merge(hosts, Widget, Group);
        BoxTopologyService.Remove(hosts, Widget);

        Assert.Equal(Group, Assert.Single(hosts).ActiveTab);
        BoxTopologyService.Validate(hosts, [Group]);
    }

    [Fact]
    public void MergeWholeHostAndReorderKeepAllContent()
    {
        var extra = new BoxTabRef(BoxContentKind.DesktopGroup, "extra");
        var hosts = BoxTopologyService.Reconcile(null,
            [new GroupState { Id = Group.Id }, new GroupState { Id = extra.Id }],
            [(Widget.Id, 10, 20, 300, 400, false)]);
        BoxTopologyService.Merge(hosts, extra, Group);
        BoxTopologyService.MergeHost(hosts, Group, Widget);
        var host = Assert.Single(hosts);
        Assert.Equal([Widget, Group, extra], host.Tabs);
        BoxTopologyService.Reorder(host, [extra, Widget, Group]);
        BoxTopologyService.Validate(hosts, [extra, Widget, Group]);
        Assert.Equal([extra, Widget, Group], host.Tabs);
        Assert.Throws<InvalidOperationException>(() => BoxTopologyService.Reorder(host, [extra, extra, Group]));
        Assert.Equal([extra, Widget, Group], host.Tabs);
    }

    [Fact]
    public void WholeHostMergeAndDragOutOneTabPreserveMixedContent()
    {
        var extra = new BoxTabRef(BoxContentKind.DesktopGroup, "extra");
        var hosts = BoxTopologyService.Reconcile(null,
            [new GroupState { Id = Group.Id }, new GroupState { Id = extra.Id }],
            [(Widget.Id, 20, 30, 380, 320, false)]);
        BoxTopologyService.Merge(hosts, Widget, Group);
        var sourceId = hosts.Single(host => host.Tabs.Contains(Group)).Id;
        var destination = hosts.Single(host => host.Tabs.Contains(extra));

        BoxTopologyService.MergeHost(hosts, Group, extra);
        Assert.Single(hosts);
        Assert.Equal(destination.Id, hosts[0].Id);
        Assert.Equal([extra, Group, Widget], hosts[0].Tabs);
        Assert.Equal(Group, hosts[0].ActiveTab);
        Assert.DoesNotContain(hosts, host => host.Id == sourceId);

        BoxTopologyService.Detach(hosts, Widget, 130, 210, 450, 340, false);
        BoxTopologyService.Validate(hosts, [extra, Group, Widget]);
        var detached = hosts.Single(host => host.Tabs.Contains(Widget));
        Assert.NotEqual(destination.Id, detached.Id);
        Assert.Equal(Widget, detached.ActiveTab);
        Assert.Equal((130d, 210d, 450d, 340d),
            (detached.X, detached.Y, detached.Width, detached.Height));
        Assert.Equal([extra, Group], destination.Tabs);
    }

    [Fact]
    public void ReconcilePreservesHostAppearanceAndActiveMixedTab()
    {
        var appearance = new MaterialPreferences { Radius = 12, CrystalOpacity = 0.48 };
        var saved = new BoxHostState
        {
            Id = "mixed", Tabs = [Group, Widget], ActiveTab = Widget,
            X = 80, Y = 90, Width = 620, Height = 440,
            Material = "Crystal", Theme = "Mint", Opacity = 0.48, Appearance = appearance
        };

        var restored = Assert.Single(BoxTopologyService.Reconcile([saved],
            [new GroupState { Id = Group.Id }], [(Widget.Id, 20, 30, 340, 380, false)]));

        Assert.Equal([Group, Widget], restored.Tabs);
        Assert.Equal(Widget, restored.ActiveTab);
        Assert.Equal(620, restored.Width);
        Assert.Equal("Crystal", restored.Material);
        Assert.Equal("Mint", restored.Theme);
        Assert.Equal(0.48, restored.Opacity);
        Assert.Same(appearance, restored.Appearance);
    }

    [Fact]
    public void MixedHostRoundTripPreservesSingleHostAndWidgetIdentity()
    {
        var groups = new[] { new GroupState { Id = Group.Id, Name = "Files", Width = 410, Height = 310 } };
        var widgets = new[] { (Widget.Id, 10d, 20d, 340d, 380d, false) };
        var hosts = BoxTopologyService.Reconcile(null, groups, widgets);
        BoxTopologyService.Merge(hosts, Widget, Group);
        var host = Assert.Single(hosts);
        host.ActiveTab = Widget;
        host.Width = 630;
        host.Height = 480;
        host.IsCollapsed = true;
        host.Theme = "Mint";
        host.Material = "Crystal";
        host.Appearance = new MaterialPreferences { CrystalOpacity = 0.43 };

        var json = System.Text.Json.JsonSerializer.Serialize(new LayoutState { Groups = groups.ToList(), BoxHosts = hosts });
        var restoredLayout = System.Text.Json.JsonSerializer.Deserialize<LayoutState>(json)!;
        var restored = Assert.Single(BoxTopologyService.Reconcile(restoredLayout.BoxHosts, restoredLayout.Groups, widgets));
        BoxTopologyService.Validate([restored], [Group, Widget]);
        Assert.Equal([Group, Widget], restored.Tabs);
        Assert.Equal(Widget, restored.ActiveTab);
        Assert.Equal(630, restored.Width);
        Assert.Equal(480, restored.Height);
        Assert.True(restored.IsCollapsed);
        Assert.Equal("Mint", restored.Theme);
        Assert.Equal("Crystal", restored.Material);
        Assert.Equal(0.43, restored.Appearance!.CrystalOpacity);
    }

    [Fact]
    public void DetachAndDeleteDoNotOrphanOtherContentOrChangeOwnership()
    {
        var assignments = new Dictionary<string, string> { ["C:/Desktop/file.txt"] = Group.Id };
        var hosts = BoxTopologyService.Reconcile(null, [new GroupState { Id = Group.Id }],
            [(Widget.Id, 10, 20, 340, 380, false)]);
        BoxTopologyService.Merge(hosts, Widget, Group);
        var appearance = new MaterialPreferences { Radius = 14 };
        hosts[0].Appearance = appearance;

        BoxTopologyService.Detach(hosts, Widget, 80, 90, 500, 420, false);
        Assert.Same(appearance, hosts.Single(host => host.Tabs.Contains(Widget)).Appearance);
        BoxTopologyService.Remove(hosts, Group);
        var remaining = Assert.Single(hosts);
        Assert.Equal(Widget, Assert.Single(remaining.Tabs));
        Assert.Equal(Widget, remaining.ActiveTab);
        Assert.Equal(Group.Id, assignments["C:/Desktop/file.txt"]);
        BoxTopologyService.Validate(hosts, [Widget]);
    }

    [Fact]
    public void ContentIdentityIsTypedAndPreferencesRemainPerContent()
    {
        Exception failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var group = new DeskGroup { Id = "shared", Name = "Files", Width = 420, Height = 320 };
                var surface = new System.Windows.Controls.ScrollViewer
                {
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalContentAlignment = System.Windows.VerticalAlignment.Top,
                    Content = new System.Windows.Controls.Border { Width = 100, Height = 40 }
                };
                IBoxContent files = new DeskGroupBoxContent(group, surface);
                var notes = new BoxTabRef(BoxContentKind.Widget, "shared");

                Assert.NotEqual(files.Tab, notes);
                Assert.Equal(new System.Windows.Size(420, 320), files.PreferredSize);
                Assert.Equal("Files", files.Title);
                Assert.Same(surface, ((DeskGroupBoxContent)files).Content);
                var content = (DeskGroupBoxContent)files;
                Assert.Equal(System.Windows.HorizontalAlignment.Stretch, content.HorizontalContentAlignment);
                Assert.Equal(System.Windows.VerticalAlignment.Stretch, content.VerticalContentAlignment);
                content.Measure(new System.Windows.Size(420, 320));
                content.Arrange(new System.Windows.Rect(0, 0, 420, 320));
                Assert.Equal(420, surface.ActualWidth);
                Assert.Equal(320, surface.ActualHeight);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
