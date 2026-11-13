using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiniC.Widgets;
using Xunit;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace MiniC.Tests;

public sealed class PlannerLayoutTests
{
    private static readonly string[] Pages = ["概览", "专注", "日历", "日程", "待办"];
    private static readonly double[] EditorWidths = [234, 464, 784, 324, 234];
    private static readonly double[] PickerWidths = [180, 234, 276, 280, 284, 324, 464, 760, 234];
    private static readonly double[] CalendarWidths = [176, 238, 300];

    [Fact]
    public async Task CalendarOpensScopedDayListAndAlmanacWithoutInlineDetails()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var date = new DateTime(2026, 2, 17);
            var task = new WidgetEntry { WidgetId = fixture.State.Id, Title = "当天待办",
                DueUtc = new DateTimeOffset(date.AddHours(9)) };
            var schedule = new WidgetEntry { WidgetId = fixture.State.Id, Title = "当天日程", EntryKind = "Schedule",
                DueUtc = new DateTimeOffset(date.AddHours(10)) };
            var tomorrow = new WidgetEntry { WidgetId = fixture.State.Id, Title = "隔天日程", EntryKind = "Schedule",
                DueUtc = new DateTimeOffset(date.AddDays(1).AddHours(10)) };
            var completed = new WidgetEntry { WidgetId = fixture.State.Id, Title = "已完成日程", EntryKind = "Schedule",
                DueUtc = schedule.DueUtc, Completed = true };
            var other = new WidgetState { Kind = WidgetKind.Planner };
            fixture.Store.Save(other);
            foreach (var entry in new[] { task, schedule, tomorrow, completed,
                new WidgetEntry { WidgetId = other.Id, Title = "其他盒子的日程", EntryKind = "Schedule", DueUtc = schedule.DueUtc } })
                fixture.Store.Save(entry);
            var view = fixture.View;
            ShowCalendarMonth(view, date);
            Layout(view, 324, 420);
            Assert.DoesNotContain(view.Body.Children.OfType<Border>(), row => row.Tag is WidgetEntry);
            Assert.DoesNotContain(Descendants(view.Body).OfType<TextBlock>(), text =>
                text.Text is "当天安排" or "当天没有安排" or "黄历");
            Assert.DoesNotContain(Descendants(view.Body).OfType<Button>(), button => Equals(button.ToolTip, "新建日程"));
            CalendarDate(view, date).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 420);
            Assert.Equal(new[] { task.Id, schedule.Id }, view.Body.Children.OfType<Border>()
                .Where(row => row.Tag is WidgetEntry).Select(row => ((WidgetEntry)row.Tag).Id));
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), text => text.Text.Contains("正月初一"));
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), text => text.Text.Contains("春节 放假"));
            DetailButton(view, "展开或收起黄历").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(160);
            foreach (var width in EditorWidths)
            {
                Layout(view, width, 420);
                foreach (var element in Descendants(view.Body).OfType<FrameworkElement>()
                    .Where(element => element is TextBlock or Button && element.Visibility == Visibility.Visible && element.ActualWidth > 0))
                {
                    var bounds = element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
                    Assert.True(bounds.Left >= -1 && bounds.Right <= width + 1, $"{element}: {bounds}");
                }
                SavePreview(view, $"日期日程-黄历-{width:0}.png");
            }
            var info = CalendarDayInfoProvider.Get(date);
            var ganzhi = Descendants(view.Body).OfType<TextBlock>().Single(text => text.Text == info.Ganzhi);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)ganzhi.Parent).Visibility);
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), text => text.Text.StartsWith("宜  "));
            DetailButton(view, "后一天").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(tomorrow.Id, ((WidgetEntry)view.Body.Children.OfType<Border>().Single(row => row.Tag is WidgetEntry).Tag).Id);
            DetailButton(view, "前一天").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 420);
            var row = view.Body.Children.OfType<Border>().Single(item => item.Tag is WidgetEntry entry && entry.Id == schedule.Id);
            ClickRow(row);
            Layout(view, 324, 420);
            DetailButton(view, "返回日程").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DetailButton(view, "返回日历").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 420);
            Assert.NotNull(CalendarDate(view, date));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ScheduleTabReadsAllDatesAndCompletesEntriesWithoutLeakingTasks()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var first = new WidgetEntry { WidgetId = fixture.State.Id, EntryKind = "Schedule",
                Title = "先发生", DueUtc = DateTimeOffset.Now.AddDays(1) };
            var second = new WidgetEntry { WidgetId = fixture.State.Id, EntryKind = "Schedule",
                Title = "后发生", DueUtc = DateTimeOffset.Now.AddDays(2) };
            var task = new WidgetEntry { WidgetId = fixture.State.Id, Title = "保留待办", DueUtc = first.DueUtc };
            foreach (var entry in new[] { second, task, first }) fixture.Store.Save(entry);
            var view = fixture.View;
            SelectTab(view, "日程");
            Layout(view, 324, 300);
            var rows = view.Body.Children.OfType<Border>().Where(row => row.Tag is WidgetEntry).ToArray();
            Assert.Equal(new[] { first.Id, second.Id }, rows.Select(row => ((WidgetEntry)row.Tag).Id));
            ClickRow(rows[0]);
            DetailButton(view, "下一条").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), text => text.Text == second.Title);
            DetailButton(view, "返回日程").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 300);
            foreach (var row in view.Body.Children.OfType<Border>().Where(row => row.Tag is WidgetEntry).ToArray())
                Descendants(row).OfType<PlannerEntryStatus>().Single().CompleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(400);
            Layout(view, 324, 300);
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), text => text.Text == "暂无日程");
            Assert.False(fixture.Store.Entries(fixture.State.Id).Single(entry => entry.Id == task.Id).Completed);
            Assert.All(fixture.Store.Entries(fixture.State.Id).Where(entry => entry.EntryKind == "Schedule"), entry => Assert.True(entry.Completed));
            ShowCalendarMonth(view, first.DueUtc!.Value.LocalDateTime);
            CalendarDate(view, first.DueUtc.Value.LocalDateTime.Date).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Descendants(view.Body).OfType<Button>().Single(button => Equals(button.Content, "全部日程"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DetailButton(view, "新建日程").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var editor = Descendants(view.Body).OfType<PlannerEntryEditor>().Single();
            Assert.True(editor.Picker.TryGetUtc(out var due));
            Assert.Equal(DateTime.Today, due!.Value.LocalDateTime.Date);
        });
    }

    [Fact]
    public async Task CalendarCellsShowFestivalsTermsAndInternationalObservances()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var view = fixture.View;
            foreach (var (date, caption, tooltip) in new[]
            {
                (new DateTime(2026, 2, 17), "春节", "放假"),
                (new DateTime(2026, 2, 28), "十二", "调休上班"),
                (new DateTime(2026, 9, 21), "国际和平日", "国际纪念日"),
                (new DateTime(2026, 9, 23), "秋分", "秋分"),
                (new DateTime(2026, 9, 25), "中秋节", "放假")
            })
            {
                ShowCalendarMonth(view, date);
                Layout(view, 234, 420);
                var button = CalendarDate(view, date);
                Assert.Contains(Descendants(button).OfType<TextBlock>(), text => text.Text == caption);
                Assert.Contains(tooltip, (string)button.ToolTip);
                SavePreview(view, $"节日月历-{date:yyyy-MM}.png");
            }
            await Task.CompletedTask;
        });
    }

    private static Button CalendarDate(WidgetContentView view, DateTime date) =>
        Descendants(view.Body).OfType<Button>().Single(button => button.Tag is DateTime day && day == date.Date);

    private static void ShowCalendarMonth(WidgetContentView view, DateTime date)
    {
        SelectTab(view, "日历");
        for (var index = 0; index < 120; index++)
        {
            var current = (DateTime)Descendants(view.Body).OfType<Button>().First(button => button.Tag is DateTime).Tag;
            if (current.Year == date.Year && current.Month == date.Month) return;
            DetailButton(view, current < date ? "下个月" : "上个月").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        throw new InvalidOperationException("Cannot reach calendar month.");
    }

    [Fact]
    public async Task CompletedRowsSelectForDeletionWithoutRestoringAndCleanTheirAttachments()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var first = new WidgetEntry { WidgetId = fixture.State.Id, Title = "完成的第一项", Completed = true };
            var second = new WidgetEntry { WidgetId = fixture.State.Id, Title = "完成的第二项", Completed = true };
            var pending = new WidgetEntry { WidgetId = fixture.State.Id, Title = "不能删掉的待办" };
            foreach (var entry in new[] { first, second, pending }) fixture.Store.Save(entry);
            var source = Path.GetTempFileName();
            try
            {
                var attachment = fixture.Store.Import(fixture.State.Id, first.Id, source);
                var path = fixture.Store.AttachmentPath(attachment);
                var view = fixture.View;
                SelectTab(view, "待办");
                Descendants(view.Body).OfType<Button>().Single(button => Equals(button.Content, "完成"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(view, 324, 420);
                var deleteSelected = Descendants(view.Body).OfType<Button>().Single(button => Equals(button.ToolTip, "删除勾选项"));
                Assert.False(deleteSelected.IsEnabled);
                var rows = view.Body.Children.OfType<Border>().Where(row => row.Tag is WidgetEntry).ToArray();
                Assert.Equal(2, rows.Length);
                Assert.Empty(Descendants(view.Body).OfType<PlannerEntryStatus>());
                foreach (var row in rows)
                {
                    var actions = Descendants(row).OfType<PlannerCompletedEntryActions>().Single();
                    Assert.False(actions.IsSelected);
                    Assert.False(actions.SelectButton.IsHitTestVisible);
                    Assert.Contains(Descendants(row).OfType<TextBlock>(), text => text.TextDecorations.Count > 0);
                    row.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = UIElement.MouseEnterEvent });
                    await Task.Delay(170);
                    Assert.True(actions.DeleteButton.IsHitTestVisible);
                    SavePreview(view, "完成-悬停删除勾选-324.png");
                    actions.SelectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(actions.IsSelected);
                    Assert.True(fixture.Store.Entries(fixture.State.Id).Single(entry => entry.Id == ((WidgetEntry)row.Tag).Id).Completed);
                    row.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = UIElement.MouseLeaveEvent });
                    await Task.Delay(170);
                    Assert.False(actions.DeleteButton.IsHitTestVisible);
                    Assert.True(actions.SelectButton.IsHitTestVisible);
                }
                Assert.Empty(Descendants(view.Body).OfType<TextBox>());
                Assert.True(deleteSelected.IsEnabled);
                deleteSelected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(view, 324, 420);
                Assert.Equal(3, fixture.Store.Entries(fixture.State.Id).Count);
                SavePreview(view, "完成-删除勾选确认-324.png");
                Descendants(view.Body).OfType<Button>().Single(button => Equals(button.ToolTip, "取消删除"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(deleteSelected.IsEnabled);
                deleteSelected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Descendants(view.Body).OfType<Button>().Single(button => Equals(button.ToolTip, "确认删除"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(File.Exists(path));
                Assert.Empty(fixture.Store.Attachments(fixture.State.Id));
                Assert.Equal(pending.Id, Assert.Single(fixture.Store.Entries(fixture.State.Id)).Id);
                await Task.Delay(400);
                Layout(view, 324, 420);
                Assert.DoesNotContain(view.Body.Children.OfType<Border>(), row => row.Tag is WidgetEntry);
                Assert.False(deleteSelected.IsEnabled);
                Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), text => text.Text == "暂无已完成待办");
            }
            finally { File.Delete(source); }
        });
    }

    [Fact]
    public async Task ClearCompletedAndRowDeletionStayScopedAndOldConfirmationCannotDeleteAfterSwitching()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var done = new WidgetEntry { WidgetId = fixture.State.Id, Title = "可以清空的完成项", Completed = true };
            var pending = new WidgetEntry { WidgetId = fixture.State.Id, Title = "保留未完成" };
            var schedule = new WidgetEntry { WidgetId = fixture.State.Id, Title = "保留已完成日程", EntryKind = "Schedule", Completed = true };
            var otherState = new WidgetState { Kind = WidgetKind.Planner, Title = "另一个日程中心" };
            fixture.Store.Save(otherState);
            var other = new WidgetEntry { WidgetId = otherState.Id, Title = "保留另一个盒子的完成项", Completed = true };
            foreach (var entry in new[] { done, pending, schedule, other }) fixture.Store.Save(entry);
            var view = fixture.View;
            SelectTab(view, "待办");
            Descendants(view.Body).OfType<Button>().Single(button => Equals(button.Content, "完成"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 234, 420);
            var row = view.Body.Children.OfType<Border>().Single(item => item.Tag is WidgetEntry);
            Descendants(row).OfType<PlannerCompletedEntryActions>().Single().DeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var oldConfirm = Descendants(view.Body).OfType<Button>().Single(button => Equals(button.ToolTip, "确认删除"));
            SelectTab(view, "专注");
            oldConfirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(4, fixture.Store.Entries().Count);
            SelectTab(view, "待办");
            Layout(view, 234, 420);
            Descendants(view.Body).OfType<Button>().Single(button => Equals(button.ToolTip, "清空已完成待办"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 234, 420);
            SavePreview(view, "完成-清空确认-234.png");
            var confirm = Descendants(view.Body).OfType<Button>().Single(button => Equals(button.ToolTip, "确认删除"));
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(400);
            Assert.DoesNotContain(fixture.Store.Entries(), entry => entry.Id == done.Id);
            Assert.Contains(fixture.Store.Entries(), entry => entry.Id == pending.Id && !entry.Completed);
            Assert.Contains(fixture.Store.Entries(), entry => entry.Id == schedule.Id && entry.Completed);
            Assert.Contains(fixture.Store.Entries(), entry => entry.Id == other.Id && entry.Completed);
            Assert.False(Descendants(view.Body).OfType<Button>().Single(button => Equals(button.ToolTip, "清空已完成待办")).IsEnabled);
        });
    }

    [Theory]
    [InlineData("概览")]
    [InlineData("待办")]
    public async Task RowTimeAndHoverCompletionShareACenteredStableSlot(string page)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            fixture.Store.Save(new WidgetEntry { WidgetId = fixture.State.Id, Title = "没有时间也保留时间入口" });
            fixture.Store.Save(new WidgetEntry { WidgetId = fixture.State.Id, Title = "这是较长的待办内容，需要换行但不能挤占时间位置",
                DueUtc = DateTimeOffset.Now.AddDays(1) });
            var view = fixture.View;
            SelectTab(view, page);
            foreach (var width in EditorWidths)
            {
                Layout(view, width, 420);
                foreach (var row in view.Body.Children.OfType<Border>().Where(item => item.Tag is WidgetEntry))
                {
                    var status = Descendants(row).OfType<PlannerEntryStatus>().Single();
                    Assert.True(status.CompleteButton.Focusable);
                    Assert.Equal("标记为已完成", System.Windows.Automation.AutomationProperties.GetName(status.CompleteButton));
                    Assert.Equal(0, status.CompleteButton.Opacity, 3);
                    Assert.False(status.CompleteButton.IsHitTestVisible);
                    var content = (Grid)row.Child;
                    var centre = content.ActualHeight / 2;
                    var timeCentre = status.TimeDisplay.TranslatePoint(new System.Windows.Point(0, status.TimeDisplay.ActualHeight / 2), content).Y;
                    var buttonCentre = status.CompleteButton.TranslatePoint(new System.Windows.Point(0, status.CompleteButton.ActualHeight / 2), content).Y;
                    Assert.Equal(centre, timeCentre, 1);
                    Assert.Equal(centre, buttonCentre, 1);
                    Assert.Equal(status.CompleteButton.ActualWidth, status.CompleteButton.ActualHeight);
                    if (((WidgetEntry)row.Tag).DueUtc is null)
                        Assert.Contains(status.TimeDisplay.Children.OfType<TextBlock>(), text => text.Text == "未设时间");
                    var bounds = row.RenderSize;
                    row.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = UIElement.MouseEnterEvent });
                    await Task.Delay(160);
                    view.UpdateLayout();
                    Assert.True(status.IsRevealed);
                    Assert.True(status.CompleteButton.IsHitTestVisible);
                    Assert.Equal(1, status.CompleteButton.Opacity, 3);
                    Assert.Equal(0, status.TimeDisplay.Opacity, 3);
                    Assert.Equal(bounds, row.RenderSize);
                    if (width == 324) SavePreview(view, $"{page}-悬停完成-324.png");
                    row.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                        { RoutedEvent = UIElement.MouseLeaveEvent });
                    await Task.Delay(160);
                    Assert.False(status.IsRevealed);
                    Assert.False(status.CompleteButton.IsHitTestVisible);
                    Assert.Equal(0, status.CompleteButton.Opacity, 3);
                    Assert.Equal(1, status.TimeDisplay.Opacity, 3);
                    Assert.Equal(bounds, row.RenderSize);
                }
            }
        });
    }

    [Fact]
    public async Task OverviewCompletionPersistsOnceAndAnimatesRowsWithoutRebuildingTheirSiblings()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var task = new WidgetEntry { WidgetId = fixture.State.Id, Title = "完成这个待办", DueUtc = DateTimeOffset.Now.AddDays(1) };
            var schedule = new WidgetEntry { WidgetId = fixture.State.Id, Title = "完成这个日程", EntryKind = "Schedule",
                DueUtc = DateTimeOffset.Now.AddDays(2) };
            var remaining = new WidgetEntry { WidgetId = fixture.State.Id, Title = "保留原来的行" };
            foreach (var entry in new[] { task, schedule, remaining }) fixture.Store.Save(entry);
            var view = fixture.View;
            SelectTab(view, "概览");
            Layout(view, 324, 420);
            var rows = view.Body.Children.OfType<Border>().Where(row => row.Tag is WidgetEntry).ToArray();
            var sibling = rows.Single(row => ((WidgetEntry)row.Tag).Id == remaining.Id);
            var siblingY = sibling.TranslatePoint(new System.Windows.Point(), view).Y;
            // Simulate another operation updating the entry after the list was rendered.
            task.Body = "完成操作不能覆盖更新后的正文";
            fixture.Store.Save(task);
            foreach (var row in rows.Where(row => row != sibling))
            {
                var status = Descendants(row).OfType<PlannerEntryStatus>().Single();
                var click = new RoutedEventArgs(Button.ClickEvent);
                status.CompleteButton.RaiseEvent(click);
                Assert.True(click.Handled);
                status.CompleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(fixture.Store.Entries(fixture.State.Id).Single(entry => entry.Id == ((WidgetEntry)row.Tag).Id).Completed);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.Same(view.Body, row.Parent);
                    Assert.False(row.IsHitTestVisible);
                    Assert.False(status.CompleteButton.IsEnabled);
                    Assert.True(row.HasAnimatedProperties);
                }
            }
            Assert.Empty(Descendants(view.Body).OfType<TextBox>());
            await Task.Delay(80);
            view.UpdateLayout();
            SavePreview(view, "概览-完成动画中-324.png");
            await Task.Delay(350);
            view.UpdateLayout();
            Assert.Same(sibling, view.Body.Children.OfType<Border>().Single(row => row.Tag is WidgetEntry));
            Assert.True(sibling.TranslatePoint(new System.Windows.Point(), view).Y < siblingY);
            Assert.Equal("完成操作不能覆盖更新后的正文", fixture.Store.Entries(fixture.State.Id).Single(entry => entry.Id == task.Id).Body);
            var originalHeader = view.Body.Children[0];
            Descendants(sibling).OfType<PlannerEntryStatus>().Single().CompleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(400);
            Assert.Same(originalHeader, view.Body.Children[0]);
            Assert.DoesNotContain(view.Body.Children.OfType<Border>(), row => row.Tag is WidgetEntry);
            Assert.Single(view.Body.Children.OfType<TextBlock>(), text => text.Text == "暂无待办或日程");
        });
    }

    [Fact]
    public async Task CompletionAnimationCannotReplaceANewPageOrItsDraft()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var entry = new WidgetEntry { WidgetId = fixture.State.Id, Title = "完成后立即切页" };
            fixture.Store.Save(entry);
            var view = fixture.View;
            SelectTab(view, "概览");
            Layout(view, 324, 420);
            var row = view.Body.Children.OfType<Border>().Single(item => item.Tag is WidgetEntry);
            var button = Descendants(row).OfType<PlannerEntryStatus>().Single().CompleteButton;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SelectTab(view, "待办");
            Descendants(view.Body).OfType<Button>().Single(item => Equals(item.ToolTip, "新建待办"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 420);
            var editor = Descendants(view.Body).OfType<PlannerEntryEditor>().Single();
            editor.Input.Text = "切页后的草稿必须保留";
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(400);
            Assert.Same(editor, Descendants(view.Body).OfType<PlannerEntryEditor>().Single());
            Assert.Equal("切页后的草稿必须保留", editor.Input.Text);
            Assert.True(fixture.Store.Entries(fixture.State.Id).Single().Completed);
        });
    }

    [Fact]
    public async Task OverviewSortsAllPendingEntriesAndOpensPagedReadingWithoutCountPlaceholders()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var now = DateTimeOffset.Now;
            var task = new WidgetEntry { WidgetId = fixture.State.Id, Title = "明天的待办", DueUtc = now.AddDays(1) };
            var schedule = new WidgetEntry { WidgetId = fixture.State.Id, Title = "后天的日程", EntryKind = "Schedule",
                DueUtc = now.AddDays(2), Body = "阅读面板里的完整说明\n第二行说明" };
            var old = new WidgetEntry { WidgetId = fixture.State.Id, Title = "昨天的待办", DueUtc = now.AddDays(-1) };
            var undated = new WidgetEntry { WidgetId = fixture.State.Id, Title = "不设时间的待办" };
            var done = new WidgetEntry { WidgetId = fixture.State.Id, Title = "已完成不进入概览", Completed = true, DueUtc = now.AddDays(3) };
            foreach (var entry in new[] { task, undated, old, done, schedule }) fixture.Store.Save(entry);
            var view = fixture.View;
            SelectTab(view, "概览");
            Layout(view, 324, 300);
            var rows = view.Body.Children.OfType<Border>().Where(row => row.Tag is WidgetEntry).ToArray();
            Assert.Equal(new[] { schedule.Id, task.Id, old.Id, undated.Id }, rows.Select(row => ((WidgetEntry)row.Tag).Id));
            Assert.DoesNotContain(Descendants(view.Body).OfType<TextBlock>(), t =>
                t.Text.Contains(" 项", StringComparison.Ordinal) || t.Text is "暂无待办" or "暂无日程");
            Assert.True(view.Scroll.ScrollableHeight > 0);
            SavePreview(view, "概览时间倒序-324.png");
            view.Scroll.ScrollToVerticalOffset(45);
            view.UpdateLayout();
            var offset = view.Scroll.VerticalOffset;
            ClickRow(rows[0]);
            Layout(view, 324, 300);
            Assert.Empty(Descendants(view.Body).OfType<TextBox>());
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), t => t.Text == schedule.Body);
            Assert.False(DetailButton(view, "上一条").IsEnabled);
            Assert.True(DetailButton(view, "下一条").IsEnabled);
            SavePreview(view, "阅读面板-324.png");
            DetailButton(view, "下一条").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 300);
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), t => t.Text == task.Title);
            DetailButton(view, "下一条").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 300);
            DetailButton(view, "下一条").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 300);
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), t => t.Text == undated.Title);
            Assert.False(DetailButton(view, "下一条").IsEnabled);
            DetailButton(view, "上一条").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 300);
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), t => t.Text == old.Title);
            DetailButton(view, "返回概览").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 300);
            Assert.Equal(offset, view.Scroll.VerticalOffset, 1);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ReadingEditCombinesContentPreservesDatesAndReturnsToTheSameEntry()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var entry = new WidgetEntry { WidgetId = fixture.State.Id, Title = "原先的摘要",
                Body = "原有说明\n下一行", DueUtc = DateTimeOffset.Now.AddDays(1) };
            fixture.Store.Save(entry);
            var view = fixture.View;
            SelectTab(view, "概览");
            Layout(view, 234, 240);
            ClickRow(view.Body.Children.OfType<Border>().Single(row => row.Tag is WidgetEntry));
            Layout(view, 234, 240);
            DetailButton(view, "编辑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 234, 240);
            var composer = Descendants(view.Body).OfType<PlannerEntryEditor>().Single();
            Assert.Single(Descendants(view.Body).OfType<TextBox>());
            Assert.Equal("原先的摘要\n原有说明\n下一行", composer.Input.Text.Replace("\r\n", "\n"));
            Assert.DoesNotContain(Descendants(view.Body).OfType<TextBlock>(), t => t.Text is "标题" or "说明（选填）" or "编辑待办");
            composer.Input.Text = "更新的第一行\n完整正文保持换行\n最后一行";
            DetailButton(view, "保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 234, 240);
            var saved = Assert.Single(fixture.Store.Entries(fixture.State.Id));
            Assert.Equal("更新的第一行", saved.Title);
            Assert.Equal("完整正文保持换行\n最后一行", saved.Body);
            Assert.Equal(entry.DueUtc, saved.DueUtc);
            Assert.Empty(Descendants(view.Body).OfType<TextBox>());
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), t => t.Text == saved.Body);
            Assert.False(DetailButton(view, "上一条").IsEnabled);
            Assert.False(DetailButton(view, "下一条").IsEnabled);
            SavePreview(view, "阅读编辑返回-234.png");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FloatingTimeLabelTracksChangesAndTheEditorScrollsLongContentInsideItsOwnBounds()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var view = fixture.View;
            SelectTab(view, "待办");
            Layout(view, 324, 300);
            DetailButton(view, "新建待办").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 300);
            var composer = Descendants(view.Body).OfType<PlannerEntryEditor>().Single();
            Assert.True(composer.Picker.TryGetUtc(out var initial));
            Assert.Null(initial);
            composer.Input.Text = string.Join('\n', Enumerable.Range(0, 80).Select(i => $"第 {i} 行：可以独立上下滚动的完整事项"));
            Descendants(composer.Picker).OfType<Button>().Single(button => Equals(button.Content, "明天"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(composer.Picker.TryGetUtc(out var due));
            Assert.NotNull(due);
            Layout(view, 324, 300);
            Assert.Contains(Descendants(composer.TimeButton).OfType<TextBlock>(), text => text.Text.Contains('月'));
            foreach (var width in EditorWidths)
            {
                Layout(view, width, 300);
                Assert.Same(composer, Descendants(view.Body).OfType<PlannerEntryEditor>().Single());
                composer.Input.ScrollToEnd();
                composer.Input.UpdateLayout();
                Assert.True(composer.Input.GetLastVisibleLineIndex() > 60);
                var floating = composer.TimeButton.TransformToAncestor(composer).TransformBounds(new Rect(composer.TimeButton.RenderSize));
                Assert.True(floating.Bottom <= composer.ActualHeight - 7);
                Assert.True(floating.Right <= composer.ActualWidth);
                var inputBounds = composer.TransformToAncestor(view).TransformBounds(new Rect(composer.RenderSize));
                var navigation = view.ContentRoot.Children.OfType<Border>().Single();
                Assert.True(inputBounds.Bottom <= navigation.TranslatePoint(new System.Windows.Point(), view).Y);
                SavePreview(view, $"单输入区-{width:0}.png");
            }
            Descendants(composer.Picker).OfType<System.Windows.Controls.CheckBox>().Single().IsChecked = true;
            Layout(view, 324, 300);
            Assert.Contains(Descendants(composer.TimeButton).OfType<TextBlock>(), text => text.Text == "设置时间");
            DetailButton(view, "保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = Assert.Single(fixture.Store.Entries(fixture.State.Id));
            Assert.Null(saved.DueUtc);
            Assert.Contains("第 79 行", saved.Body);
            await Task.CompletedTask;
        });
    }

    private static Button DetailButton(WidgetContentView view, string tooltip) =>
        Descendants(view.Body).OfType<Button>().Single(button => Equals(button.ToolTip, tooltip));

    private static void ClickRow(Border row) => row.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
        System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    [Theory]
    [InlineData(234, 110)]
    [InlineData(234, 420)]
    [InlineData(324, 300)]
    [InlineData(464, 420)]
    [InlineData(784, 540)]
    public async Task PagesFitViewportAndFloatingTabsStayReachable(double width, double height)
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture();
            var view = fixture.View;
            foreach (var page in Pages)
            {
                SelectTab(view, page);
                Layout(view, width, height);
                Assert.DoesNotContain(Descendants(view.Body).OfType<TextBlock>(), t => t.Text == "日程中心");
                Assert.DoesNotContain(Descendants(view.Body).OfType<Button>(), b => Equals(b.ToolTip, "刷新") || Equals(b.ToolTip, "新建待办或日程"));
                if (page == "待办")
                {
                    Assert.Equal(14, Descendants(view.Body).OfType<TextBlock>().Count(t => t.Text.StartsWith("任务 ", StringComparison.Ordinal)));
                    Assert.DoesNotContain(Descendants(view.Body).OfType<Button>(), button => Equals(button.Content, "更新"));
                    var segment = Descendants(view.Body).OfType<Border>().Single(border => border.Name == "PlannerTaskFilters");
                    Assert.True(segment.ActualWidth <= 240);
                    var midpoint = segment.TranslatePoint(new System.Windows.Point(segment.ActualWidth / 2, 0), view.Body).X;
                    Assert.Equal(view.Body.ActualWidth / 2, midpoint, 1);
                }
                foreach (var element in Descendants(view.Body).OfType<FrameworkElement>()
                    .Where(e => e is Button or TextBlock && e.ActualWidth > 0 && e.IsVisible))
                {
                    var bounds = element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
                    Assert.True(bounds.Left >= -1 && bounds.Right <= width + 1,
                        $"{page}, {width}: {element.GetType().Name} extends outside viewport: {bounds}");
                }

                var navigation = view.ContentRoot.Children.OfType<Border>().Single();
                var tabs = Descendants(navigation).OfType<Button>().ToArray();
                Assert.Equal(5, tabs.Length);
                var before = navigation.TranslatePoint(new System.Windows.Point(), view);
                Assert.InRange(before.Y + navigation.ActualHeight, height - 10, height);
                SavePreview(view, $"{page}-{width:0}x{height:0}.png");
                view.Scroll.ScrollToEnd();
                view.UpdateLayout();
                Assert.Equal(before, navigation.TranslatePoint(new System.Windows.Point(), view));
                Assert.True(view.Scroll.ScrollableWidth <= 0.1);
                SavePreview(view, $"{page}-{width:0}x{height:0}-end.png");
            }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FocusAndEmptyPagesKeepTheirStateWhileResizing()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var view = fixture.View;
            foreach (var page in Pages)
            {
                SelectTab(view, page);
                foreach (var width in EditorWidths) Layout(view, width, 300);
                Assert.True(view.Scroll.ScrollableWidth <= 0.1);
            }
            SelectTab(view, "专注");
            Layout(view, 324, 300);
            Descendants(view.Body).OfType<Button>().Single(b => b.Content is string text && text.Contains("开始专注"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var clock = Descendants(view.Body).OfType<TextBlock>().Single(t => t.Text == "00:25:00");
            foreach (var width in EditorWidths)
            {
                Layout(view, width, 300);
                Assert.Contains(clock, Descendants(view.Body));
                Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), t => t.Text == "专注中");
            }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EditorReflowsWithoutReplacingDraftOrDateAndCanScrollAboveNavigation()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture();
            var view = fixture.View;
            SelectTab(view, "待办");
            Layout(view, 324, 300);
            Descendants(view.Body).OfType<Button>().Single(b => Equals(b.ToolTip, "新建待办"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 300);
            var title = Descendants(view.Body).OfType<TextBox>().First();
            title.Text = "缩放期间保留的草稿：包含一段很长的标题用于检查自动重排";
            var composer = Descendants(view.Body).OfType<PlannerEntryEditor>().Single();
            var picker = composer.Picker;
            Assert.True(picker.TryGetUtc(out var originalDue));
            foreach (var width in EditorWidths)
            {
                Layout(view, width, 240);
                Assert.Same(title, Descendants(view.Body).OfType<TextBox>().First());
                Assert.StartsWith("缩放期间保留", title.Text);
                Assert.True(picker.TryGetUtc(out var due));
                Assert.Equal(originalDue, due);
                var timeBounds = composer.TimeButton.TransformToAncestor(composer).TransformBounds(new Rect(composer.TimeButton.RenderSize));
                Assert.InRange(timeBounds.Left, 0, composer.ActualWidth);
                Assert.True(timeBounds.Right <= composer.ActualWidth);
                Assert.True(timeBounds.Bottom <= composer.ActualHeight - 7);
                Assert.True(composer.Input.Padding.Bottom >= composer.TimeButton.ActualHeight + 16);
                view.Scroll.ScrollToEnd();
                view.UpdateLayout();
                var save = Descendants(view.Body).OfType<Button>().Single(b => Equals(b.ToolTip, "保存"));
                var navigation = view.ContentRoot.Children.OfType<Border>().Single();
                Assert.True(save.TranslatePoint(new System.Windows.Point(0, save.ActualHeight), view).Y
                    <= navigation.TranslatePoint(new System.Windows.Point(), view).Y);
                SavePreview(view, $"编辑-{width:0}.png");
            }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DateAndTimeControlsStretchAndAlignAcrossTheReflowThreshold()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var original = new DateTimeOffset(2026, 10, 18, 15, 31, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 18)));
            var picker = new WidgetDateTimePicker(original, optional: true)
                { Background = new SolidColorBrush(Color.FromRgb(50, 58, 64)) };
            foreach (var width in PickerWidths)
            {
                picker.Measure(new System.Windows.Size(width, 240));
                picker.Arrange(new Rect(0, 0, width, picker.DesiredSize.Height));
                picker.UpdateLayout();
                var date = Descendants(picker).OfType<Button>().Single(b => Equals(b.ToolTip, "选择日期"));
                var hour = Descendants(picker).OfType<System.Windows.Controls.ComboBox>().Single(b => Equals(b.ToolTip, "小时"));
                var minute = Descendants(picker).OfType<System.Windows.Controls.ComboBox>().Single(b => Equals(b.ToolTip, "分钟"));
                var dateBounds = date.TransformToAncestor(picker).TransformBounds(new Rect(date.RenderSize));
                var hourBounds = hour.TransformToAncestor(picker).TransformBounds(new Rect(hour.RenderSize));
                var minuteBounds = minute.TransformToAncestor(picker).TransformBounds(new Rect(minute.RenderSize));
                Assert.InRange(Math.Abs(dateBounds.Left), 0, 0.1);
                Assert.InRange(Math.Abs(minuteBounds.Right - width), 0, 0.1);
                Assert.InRange(Math.Abs(hour.ActualWidth - minute.ActualWidth), 0, 0.1);
                Assert.Equal(36, date.ActualHeight);
                Assert.Equal(date.ActualHeight, hour.ActualHeight);
                Assert.Equal(date.ActualHeight, minute.ActualHeight);
                if (width >= 280)
                {
                    Assert.Equal(dateBounds.Top, hourBounds.Top);
                    Assert.InRange(Math.Abs(dateBounds.Right + 8 - hourBounds.Left), 0, 0.1);
                }
                else
                {
                    Assert.InRange(Math.Abs(dateBounds.Right - width), 0, 0.1);
                    Assert.InRange(Math.Abs(hourBounds.Left), 0, 0.1);
                    Assert.InRange(Math.Abs(dateBounds.Bottom + 6 - hourBounds.Top), 0, 0.1);
                }
                Assert.True(picker.TryGetUtc(out var due));
                Assert.Equal(original.ToUniversalTime(), due);
                SaveElement(picker, $"日期时间对齐-{width}.png");
            }
            var calendar = new WidgetDateCalendar(original.Date);
            foreach (var width in CalendarWidths)
            {
                calendar.Measure(new System.Windows.Size(width, 300));
                calendar.Arrange(new Rect(0, 0, width, calendar.DesiredSize.Height));
                calendar.UpdateLayout();
                Assert.Equal(width, calendar.ActualWidth);
                foreach (var button in Descendants(calendar).OfType<Button>())
                {
                    var bounds = button.TransformToAncestor(calendar).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(bounds.Left >= -0.1 && bounds.Right <= width + 0.1);
                }
            }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TaskAndCalendarCreationUseTheSameStyledEditorAndPersistSelections()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var view = fixture.View;
            SelectTab(view, "待办");
            Layout(view, 324, 420);
            var add = Descendants(view.Body).OfType<Button>().Single(b => Equals(b.ToolTip, "新建待办"));
            Assert.Null(add.ContextMenu);
            var icon = Assert.IsType<System.Windows.Shapes.Path>(add.Content);
            Assert.True(icon.StrokeThickness >= 2.5);
            Assert.Equal(0, add.BorderThickness.Left);
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(add.Background).Color.A);
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 540);
            var fields = Descendants(view.Body).OfType<TextBox>().ToArray();
            Assert.Single(fields);
            Assert.All(fields, field =>
            {
                Assert.NotNull(field.Style);
                Assert.NotNull(field.Template.FindName("PART_ContentHost", field));
            });
            Descendants(view.Body).OfType<Button>().Single(b => Equals(b.ToolTip, "保存"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 540);
            Assert.Contains(Descendants(view.Body).OfType<TextBlock>(), t => t.Text == "请填写事项内容，最多 10000 字。");
            Assert.Empty(fixture.Store.Entries(fixture.State.Id));
            fields[0].Text = "按计划完成待办\n保留说明和选择的截止时间";
            var composer = Descendants(view.Body).OfType<PlannerEntryEditor>().Single();
            var picker = composer.Picker;
            composer.TimePanel.Width = 280;
            composer.TimePanel.Measure(new System.Windows.Size(280, 400));
            composer.TimePanel.Arrange(new Rect(0, 0, 280, composer.TimePanel.DesiredSize.Height));
            composer.TimePanel.UpdateLayout();
            var combos = Descendants(picker).OfType<System.Windows.Controls.ComboBox>().ToArray();
            Assert.Equal(2, combos.Length);
            foreach (var combo in combos)
            {
                Assert.NotNull(combo.Template.FindName("PART_Popup", combo));
                Assert.NotNull(combo.ItemContainerStyle);
            }
            var dropdown = Assert.IsType<Popup>(combos[0].Template.FindName("PART_Popup", combos[0]));
            var options = Assert.IsType<Border>(dropdown.Child);
            options.Measure(new System.Windows.Size(90, 240));
            options.Arrange(new Rect(0, 0, 90, options.DesiredSize.Height));
            options.UpdateLayout();
            Assert.True(options.ActualHeight > 150);
            Assert.All(Descendants(options).OfType<System.Windows.Controls.Primitives.ScrollBar>(), bar => Assert.True(bar.ActualWidth <= 6));
            SaveElement(options, "时间下拉.png");
            combos[0].SelectedIndex = 9;
            combos[1].SelectedIndex = 35;
            Descendants(picker).OfType<Button>().Single(b => Equals(b.Content, "明天"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(picker.TryGetUtc(out var due));
            Assert.Equal(DateTime.Today.AddDays(1).AddHours(9).AddMinutes(35), due.Value.LocalDateTime);
            composer.TimePanel.UpdateLayout();
            SaveElement(composer.TimePanel, "浮动时间面板-280.png");
            Layout(view, 324, 540);
            SavePreview(view, "新建待办-324.png");
            Descendants(view.Body).OfType<Button>().Single(b => Equals(b.ToolTip, "保存"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var entry = Assert.Single(fixture.Store.Entries(fixture.State.Id));
            Assert.Equal("按计划完成待办", entry.Title);
            Assert.Equal("保留说明和选择的截止时间", entry.Body);
            Assert.Equal(due, entry.DueUtc);
            Assert.Equal("Task", entry.EntryKind);

            SelectTab(view, "待办");
            Layout(view, 324, 300);
            Assert.DoesNotContain(Descendants(view.Body).OfType<Button>(), b => (b.ToolTip as string)?.Contains("搜索") == true);
            SelectTab(view, "日历");
            Layout(view, 324, 420);
            Descendants(view.Body).OfType<Button>().Single(b =>
                    b.Tag is DateTime date && date.Day == 15)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 420);
            Descendants(view.Body).OfType<Button>().Single(b => Equals(b.ToolTip, "新建日程"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 324, 540);
            picker = Descendants(view.Body).OfType<PlannerEntryEditor>().Single().Picker;
            Assert.True(picker.TryGetUtc(out var scheduleDue));
            Assert.Equal(15, scheduleDue.Value.LocalDateTime.Day);
            Assert.DoesNotContain(Descendants(picker).OfType<System.Windows.Controls.CheckBox>(), _ => true);
            Descendants(view.Body).OfType<TextBox>().First().Text = "日历选定日期的日程";
            Descendants(view.Body).OfType<Button>().Single(b => Equals(b.ToolTip, "保存"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(fixture.Store.Entries(fixture.State.Id), e => e.EntryKind == "Schedule" && e.DueUtc == scheduleDue);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RestyledTaskRowsKeepCompletionAndEditingActions()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            using var fixture = new PlannerFixture(populate: false);
            var entry = new WidgetEntry { WidgetId = fixture.State.Id, Title = "核对列表排版", EntryKind = "Task",
                DueUtc = DateTimeOffset.Now.AddDays(-1) };
            fixture.Store.Save(entry);
            var view = fixture.View;
            SelectTab(view, "待办");
            Layout(view, 234, 300);
            var row = view.Body.Children.OfType<Border>().Single(b => b.Tag is WidgetEntry);
            Assert.Contains(Descendants(row).OfType<TextBlock>(), t => t.Text == "已逾期");
            Assert.Contains(Descendants(row).OfType<Border>(), b => b.Width == 3 && b.Height == 42);
            Descendants(row).OfType<Button>().Single(b => Equals(b.ToolTip, "标记为已完成"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(fixture.Store.Entries(fixture.State.Id).Single().Completed);
            await Task.Delay(400);
            Layout(view, 234, 300);
            Assert.DoesNotContain(view.Body.Children.OfType<Border>(), b => b.Tag is WidgetEntry);
            Descendants(view.Body).OfType<Button>().Single(b => Equals(b.Content, "完成"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(view, 234, 300);
            row = view.Body.Children.OfType<Border>().Single(b => b.Tag is WidgetEntry);
            Assert.Contains(Descendants(row).OfType<TextBlock>(), t => t.Text == "已完成");
            Assert.Contains(Descendants(row).OfType<TextBlock>(), t => t.Text == entry.Title && t.TextDecorations.Count > 0);
            SavePreview(view, "待办完成-234.png");
            row.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Layout(view, 234, 420);
            var title = Descendants(view.Body).OfType<TextBox>().First();
            Assert.Equal(entry.Title, title.Text);
            title.Text = "更新后的待办";
            Descendants(view.Body).OfType<Button>().Single(b => Equals(b.ToolTip, "保存"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = fixture.Store.Entries(fixture.State.Id).Single();
            Assert.Equal("更新后的待办", saved.Title);
            Assert.True(saved.Completed);
            Layout(view, 234, 300);
            row = view.Body.Children.OfType<Border>().Single(b => b.Tag is WidgetEntry);
            Assert.Empty(Descendants(row).OfType<PlannerEntryStatus>());
            Assert.Single(Descendants(row).OfType<PlannerCompletedEntryActions>());
            ClickRow(row);
            Layout(view, 234, 420);
            var composer = Descendants(view.Body).OfType<PlannerEntryEditor>().Single();
            var hours = Descendants(composer.Picker).OfType<System.Windows.Controls.ComboBox>().First();
            hours.SelectedIndex = (hours.SelectedIndex + 1) % 24;
            Descendants(view.Body).OfType<Button>().Single(b => Equals(b.ToolTip, "保存"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(fixture.Store.Entries(fixture.State.Id).Single().Completed);
            Layout(view, 234, 300);
            Assert.Contains(view.Body.Children.OfType<Border>(), item => item.Tag is WidgetEntry task && task.Id == entry.Id);
            Assert.DoesNotContain(Descendants(view.Body).OfType<TextBlock>(), text => text.Text == "已完成");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task CustomDateCalendarNavigatesMonthAndOptionalTimeCanBeCleared()
    {
        await GroupMotionTests.OnUiThread(async () =>
        {
            var calendar = new WidgetDateCalendar(new DateTime(2026, 12, 31));
            DateTime? selected = null;
            calendar.DateSelected += date => selected = date;
            Descendants(calendar).OfType<Button>().Single(b => Equals(b.ToolTip, "下个月"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            calendar.Measure(new System.Windows.Size(238, 300));
            calendar.Arrange(new Rect(0, 0, 238, calendar.DesiredSize.Height));
            Assert.Contains(Descendants(calendar).OfType<TextBlock>(), t => t.Text == "2027年1月");
            Descendants(calendar).OfType<Button>().Single(b => Equals(b.Tag, new DateTime(2027, 1, 4)))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new DateTime(2027, 1, 4), selected);
            calendar.Measure(new System.Windows.Size(238, 300));
            calendar.Arrange(new Rect(0, 0, 238, calendar.DesiredSize.Height));
            var frame = new Border { Background = new SolidColorBrush(Color.FromRgb(45, 52, 60)), Padding = new Thickness(10), Child = calendar };
            frame.Measure(new System.Windows.Size(258, 320));
            frame.Arrange(new Rect(0, 0, 258, frame.DesiredSize.Height));
            SaveElement(frame, "日期面板.png");
            var picker = new WidgetDateTimePicker(null, optional: true);
            picker.Measure(new System.Windows.Size(280, 200));
            picker.Arrange(new Rect(0, 0, 280, picker.DesiredSize.Height));
            var none = Descendants(picker).OfType<System.Windows.Controls.CheckBox>().Single();
            Assert.NotNull(none.Template);
            Assert.True(picker.TryGetUtc(out var due));
            Assert.Null(due);
            var combos = Descendants(picker).OfType<System.Windows.Controls.ComboBox>().ToArray();
            Assert.All(combos, combo => Assert.False(combo.IsEnabled));
            Descendants(picker).OfType<Button>().Single(b => Equals(b.Content, "今天"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.All(combos, combo => Assert.True(combo.IsEnabled));
            Assert.True(picker.TryGetUtc(out due));
            Assert.Equal(DateTime.Today, due.Value.LocalDateTime.Date);
            none.IsChecked = true;
            Assert.True(picker.TryGetUtc(out due));
            Assert.Null(due);
            await Task.CompletedTask;
        });
    }

    private static void SelectTab(WidgetContentView view, string label) =>
        Descendants(view.ContentRoot.Children.OfType<Border>().Single()).OfType<Button>()
            .Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Layout(WidgetContentView view, double width, double height)
    {
        // Finish page transitions so geometry and screenshots describe the settled page.
        foreach (UIElement child in view.Body.Children)
        {
            child.BeginAnimation(UIElement.OpacityProperty, null);
            child.Opacity = 1;
            child.RenderTransform = Transform.Identity;
        }
        view.Measure(new System.Windows.Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void SavePreview(WidgetContentView view, string name)
    {
        SaveElement(view, name);
        if (view.ActualWidth != 324 || view.ActualHeight != 300) return;
        var background = view.ContentRoot.Background;
        try
        {
            view.ContentRoot.Background = Brushes.White;
            view.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            SaveElement(view, "white-" + name);
            view.ContentRoot.Background = Brushes.Black;
            view.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            SaveElement(view, "black-" + name);
        }
        finally { view.ContentRoot.Background = background; }
    }

    private static void SaveElement(FrameworkElement view, string name)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "planner-responsive");
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name));
        encoder.Save(file);
    }

    private sealed class PlannerFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "MiniC-Planner-Layout-" + Guid.NewGuid().ToString("N"));
        internal WidgetContentView View { get; }
        internal WidgetStore Store { get; }
        internal WidgetState State { get; }

        internal PlannerFixture(bool populate = true)
        {
            var store = new WidgetStore(_root);
            var state = new WidgetState { Kind = WidgetKind.Planner, Title = "日程中心" };
            Store = store;
            State = state;
            store.Save(state);
            for (var index = 0; populate && index < 15; index++)
                store.Save(new WidgetEntry { WidgetId = state.Id,
                    Title = $"任务 {index + 1}：这是一条较长的事项标题，用于检查换行和时间标签是否相互挤压",
                    DueUtc = DateTimeOffset.Now.AddMinutes(index * 5), EntryKind = index == 0 ? "Schedule" : "Task" });
            View = new WidgetContentView(state, store, new WidgetWeatherService(null, false), () => { }, _ => { })
                { Background = new SolidColorBrush(Color.FromRgb(50, 58, 64)) };
            View.RenderBody();
        }

        public void Dispose()
        {
            View.Stop();
            Directory.Delete(_root, true);
        }
    }
}
