using MiniC.Models;

namespace MiniC.Services;

internal static class BoxTopologyService
{
    public static List<BoxHostState> Reconcile(
        IReadOnlyCollection<BoxHostState>? saved,
        IReadOnlyCollection<GroupState> groups,
        IReadOnlyCollection<(string Id, double X, double Y, double Width, double Height, bool IsCollapsed)> widgets)
    {
        var available = new HashSet<BoxTabRef>(groups.Select(group => new BoxTabRef(BoxContentKind.DesktopGroup, group.Id)));
        available.UnionWith(widgets.Select(widget => new BoxTabRef(BoxContentKind.Widget, widget.Id)));
        var seen = new HashSet<BoxTabRef>();
        var hosts = new List<BoxHostState>();
        var hostIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string UniqueHostId(string candidate)
        {
            if (hostIds.Add(candidate)) return candidate;
            string replacement;
            do { replacement = Guid.NewGuid().ToString("N"); }
            while (!hostIds.Add(replacement));
            return replacement;
        }

        foreach (var host in saved ?? [])
        {
            if (host is null || string.IsNullOrWhiteSpace(host.Id) || host.Tabs is null) continue;
            var tabs = host.Tabs.Where(tab => tab is not null && !string.IsNullOrWhiteSpace(tab.Id)
                && available.Contains(tab) && seen.Add(tab)).ToList();
            if (tabs.Count == 0) continue;
            hosts.Add(new BoxHostState
            {
                Id = UniqueHostId(host.Id),
                Tabs = tabs,
                ActiveTab = host.ActiveTab is not null && tabs.Contains(host.ActiveTab) ? host.ActiveTab : tabs[0],
                X = host.X, Y = host.Y, Width = host.Width, Height = host.Height, IsCollapsed = host.IsCollapsed,
                Appearance = host.Appearance, Theme = host.Theme, Material = host.Material, Opacity = host.Opacity
            });
        }

        // A saved topology takes precedence over legacy group clusters.
        foreach (var cluster in groups.Where(group => !seen.Contains(new BoxTabRef(BoxContentKind.DesktopGroup, group.Id)))
            .GroupBy(group => saved is null ? group.TabGroupId ?? group.Id : group.Id, StringComparer.OrdinalIgnoreCase))
        {
            var members = cluster.OrderBy(group => group.TabOrder).ToArray();
            var tabs = members.Select(group => new BoxTabRef(BoxContentKind.DesktopGroup, group.Id)).ToList();
            foreach (var tab in tabs) seen.Add(tab);
            hosts.Add(new BoxHostState
            {
                Id = UniqueHostId(cluster.Key), Tabs = tabs, ActiveTab = tabs[0],
                X = members[0].X, Y = members[0].Y, Width = members[0].Width,
                Height = members[0].Height, IsCollapsed = members[0].IsCollapsed
            });
        }

        foreach (var widget in widgets)
        {
            var tab = new BoxTabRef(BoxContentKind.Widget, widget.Id);
            if (!seen.Add(tab)) continue;
            hosts.Add(new BoxHostState
            {
                Id = UniqueHostId(widget.Id), Tabs = [tab], ActiveTab = tab,
                X = widget.X, Y = widget.Y, Width = widget.Width,
                Height = widget.Height, IsCollapsed = widget.IsCollapsed
            });
        }
        return hosts;
    }

    public static void Validate(IReadOnlyCollection<BoxHostState> hosts, IReadOnlyCollection<BoxTabRef> available)
    {
        var expected = available.ToHashSet();
        var seen = new HashSet<BoxTabRef>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            if (string.IsNullOrWhiteSpace(host.Id) || !ids.Add(host.Id) || host.Tabs.Count == 0
                || host.ActiveTab is null || !host.Tabs.Contains(host.ActiveTab))
                throw new InvalidOperationException("Box host identity or active tab is invalid.");
            foreach (var tab in host.Tabs)
                if (!expected.Contains(tab) || !seen.Add(tab))
                    throw new InvalidOperationException("Box tab is missing or duplicated.");
        }
        if (!seen.SetEquals(expected)) throw new InvalidOperationException("Box topology does not cover all content.");
    }

    public static void Merge(List<BoxHostState> hosts, BoxTabRef source, BoxTabRef target)
    {
        if (source == target) return;
        var origin = hosts.Single(host => host.Tabs.Contains(source));
        var destination = hosts.Single(host => host.Tabs.Contains(target));
        if (ReferenceEquals(origin, destination)) return;
        origin.Tabs.Remove(source);
        destination.Tabs.Add(source);
        destination.ActiveTab = source;
        if (origin.Tabs.Count == 0) hosts.Remove(origin);
        else if (origin.ActiveTab == source) origin.ActiveTab = origin.Tabs[0];
    }

    public static void MergeHost(List<BoxHostState> hosts, BoxTabRef source, BoxTabRef target)
    {
        var origin = hosts.Single(host => host.Tabs.Contains(source));
        var destination = hosts.Single(host => host.Tabs.Contains(target));
        if (ReferenceEquals(origin, destination)) return;
        if (origin.Tabs.Any(destination.Tabs.Contains))
            throw new InvalidOperationException("Box hosts contain duplicate content.");
        destination.Tabs.AddRange(origin.Tabs);
        destination.ActiveTab = source;
        hosts.Remove(origin);
    }

    public static void Reorder(BoxHostState host, IReadOnlyList<BoxTabRef> tabs)
    {
        if (tabs.Count != host.Tabs.Count || tabs.Distinct().Count() != tabs.Count
            || !tabs.ToHashSet().SetEquals(host.Tabs))
            throw new InvalidOperationException("Tab reorder must preserve host membership.");
        host.Tabs = tabs.ToList();
    }

    public static void Detach(List<BoxHostState> hosts, BoxTabRef tab, double x, double y,
        double width, double height, bool isCollapsed)
    {
        var origin = hosts.Single(host => host.Tabs.Contains(tab));
        if (origin.Tabs.Count == 1) return;
        var id = Guid.NewGuid().ToString("N");
        while (hosts.Any(host => host.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            id = Guid.NewGuid().ToString("N");
        origin.Tabs.Remove(tab);
        if (origin.ActiveTab == tab) origin.ActiveTab = origin.Tabs[0];
        hosts.Add(new BoxHostState
        {
            Id = id, Tabs = [tab], ActiveTab = tab, X = x, Y = y,
            Width = width, Height = height, IsCollapsed = isCollapsed,
            Appearance = origin.Appearance, Theme = origin.Theme,
            Material = origin.Material, Opacity = origin.Opacity
        });
    }

    public static void Remove(List<BoxHostState> hosts, BoxTabRef tab)
    {
        var host = hosts.SingleOrDefault(candidate => candidate.Tabs.Contains(tab));
        if (host is null) return;
        host.Tabs.Remove(tab);
        if (host.Tabs.Count == 0) hosts.Remove(host);
        else if (host.ActiveTab == tab) host.ActiveTab = host.Tabs[0];
    }
}
