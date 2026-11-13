using System;
using System.Linq;
using System.IO;
using MiniC.Widgets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MiniC.Tests;

public sealed class WidgetStoreTests
{
    [Fact]
    public void CalendarReadsTheSameTaskAndScheduleEntriesWithoutDuplicatingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "MiniC-Widget-Calendar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new WidgetStore(root);
            var tasks = new WidgetState { Kind = WidgetKind.Tasks, Title = "待办" };
            var schedule = new WidgetState { Kind = WidgetKind.Schedule, Title = "日程" };
            var note = new WidgetState { Kind = WidgetKind.Note, Title = "备忘" };
            store.Save(tasks); store.Save(schedule); store.Save(note);
            var due = DateTimeOffset.UtcNow.AddDays(1);
            var task = new WidgetEntry { WidgetId = tasks.Id, Title = "准备材料", DueUtc = due };
            var meeting = new WidgetEntry { WidgetId = schedule.Id, Title = "开会", DueUtc = due };
            store.Save(task); store.Save(meeting);
            store.Save(new WidgetEntry { WidgetId = note.Id, Title = "不是日历事项", DueUtc = due });
            store.Save(new WidgetEntry { WidgetId = tasks.Id, Title = "未定时间" });

            var calendar = new WidgetStore(root).DatedEntries();
            Assert.Equal(2, calendar.Count);
            Assert.Contains(calendar, item => item.Entry.Id == task.Id && item.Kind == WidgetKind.Tasks);
            Assert.Contains(calendar, item => item.Entry.Id == meeting.Id && item.Kind == WidgetKind.Schedule);
            task.Completed = true; store.Save(task);
            Assert.True(new WidgetStore(root).DatedEntries().Single(item => item.Entry.Id == task.Id).Entry.Completed);
            store.DeleteEntry(meeting.Id);
            Assert.Single(new WidgetStore(root).DatedEntries());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PlannerKeepsTasksAndSchedulesInOneWidgetAndCalendarReadsBoth()
    {
        var root = Path.Combine(Path.GetTempPath(), "MiniC-Widget-Planner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new WidgetStore(root);
            var planner = new WidgetState { Kind = WidgetKind.Planner, Title = "日程中心", Width = 340, Height = 380 };
            store.Save(planner);
            store.Save(new WidgetEntry { WidgetId = planner.Id, Title = "整理桌面", EntryKind = "Task", DueUtc = DateTimeOffset.UtcNow.AddHours(1) });
            store.Save(new WidgetEntry { WidgetId = planner.Id, Title = "产品评审", EntryKind = "Schedule", DueUtc = DateTimeOffset.UtcNow.AddHours(2) });

            var reopened = new WidgetStore(root);
            Assert.Equal(2, reopened.Entries(planner.Id).Count);
            var dated = reopened.DatedEntries().Where(item => item.Entry.WidgetId == planner.Id).ToArray();
            Assert.Contains(dated, item => item.Entry.Title == "整理桌面" && item.Kind == WidgetKind.Tasks);
            Assert.Contains(dated, item => item.Entry.Title == "产品评审" && item.Kind == WidgetKind.Schedule);
            Assert.Equal(340, reopened.Widgets().Single().Width);
            Assert.Equal(380, reopened.Widgets().Single().Height);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DateTimeChoiceRejectsInvalidTimeAndPersistsUtc()
    {
        Assert.False(WidgetDateTime.TryToUtc(DateTime.Today, 24, 0, out _));
        Assert.False(WidgetDateTime.TryToUtc(DateTime.Today, 9, 60, out _));
        Assert.True(WidgetDateTime.TryToUtc(DateTime.Today, 9, 35, out var utc));
        Assert.Equal(DateTime.Today.AddHours(9).AddMinutes(35), utc!.Value.ToLocalTime().DateTime);
    }

    [Fact]
    public void CalendarDistinguishesLunarFestivalsFromOfficial2026Leave()
    {
        var spring = CalendarDayInfoProvider.Get(new DateTime(2026, 2, 17));
        Assert.Contains("春节", spring.Observances);
        Assert.Equal("正月初一", spring.LunarDate);
        Assert.Contains("除夕", CalendarDayInfoProvider.Get(new DateTime(2026, 2, 16)).Observances);
        Assert.Contains("清明节", CalendarDayInfoProvider.Get(new DateTime(2026, 4, 5)).Observances);
        Assert.Equal("放假", spring.OfficialStatus);
        Assert.Equal("春节", spring.OfficialName);
        var makeup = CalendarDayInfoProvider.Get(new DateTime(2026, 2, 28));
        Assert.Equal("调休上班", makeup.OfficialStatus);
        var festival = CalendarDayInfoProvider.Get(new DateTime(2026, 9, 25));
        Assert.Contains("中秋节", festival.Observances);
        Assert.Equal("放假", festival.OfficialStatus);
        Assert.Null(CalendarDayInfoProvider.Get(new DateTime(2027, 9, 25)).OfficialStatus);
    }

    [Theory]
    [InlineData(3, 22, "世界水日")]
    [InlineData(6, 5, "世界环境日")]
    [InlineData(9, 21, "国际和平日")]
    [InlineData(10, 24, "联合国日")]
    public void InternationalObservancesDoNotDeclareStatutoryLeave(int month, int day, string name)
    {
        var info = CalendarDayInfoProvider.Get(new DateTime(2026, month, day));
        Assert.Contains(name, info.InternationalObservances);
        Assert.Contains(name, info.Observances);
        Assert.Null(info.OfficialStatus);
        Assert.Null(info.OfficialName);
    }

    [Theory]
    [InlineData(2026, 2, 4, "立春")]
    [InlineData(2026, 9, 23, "秋分")]
    public void CalendarShowsPublishedSolarTermsAndAlmanac(int year, int month, int day, string term)
    {
        var info = CalendarDayInfoProvider.Get(new DateTime(year, month, day));
        Assert.Equal(term, info.SolarTerm);
        Assert.Contains("年", info.Ganzhi);
        Assert.NotEmpty(info.Appropriate);
        Assert.NotEmpty(info.Avoid);
    }

    [Theory]
    [InlineData("../outside", "file.txt")]
    [InlineData("valid", "../outside.txt")]
    [InlineData("valid", "C:outside.txt")]
    [InlineData("..", "file.txt")]
    public void AttachmentPathsCannotEscapeTheirDirectory(string widgetId, string fileName)
    {
        var root = Path.Combine(Path.GetTempPath(), "MiniC-Widget-Path-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WidgetStore(root);
            Assert.Throws<ArgumentException>(() => store.SafePath(widgetId, fileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ContentAttachmentsAndReminderSurviveRestartWithoutRepeating()
    {
        var root = Path.Combine(Path.GetTempPath(), "MiniC-Widget-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "photo.png"); File.WriteAllBytes(source, [1, 2, 3]);
            var store = new WidgetStore(root);
            var note = new WidgetState { Kind = WidgetKind.Note, Title = "备忘", Body = "# 标题", ReminderUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
            var tasks = new WidgetState { Kind = WidgetKind.Tasks, Title = "待办" };
            var weather = new WidgetState { Kind = WidgetKind.Weather, Title = "天气", ViewMode = "Large" };
            store.Save(note); store.Save(tasks); store.Save(weather);
            var item = new WidgetEntry { WidgetId = tasks.Id, Title = "任务", ReminderUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
            store.Save(item);
            var attachment = store.Import(note.Id, null, source);
            var taskFile = store.Import(tasks.Id, item.Id, source);
            var reopened = new WidgetStore(root);
            Assert.Equal("# 标题", reopened.Widgets().Single(w => w.Id == note.Id).Body);
            Assert.Equal("Large", reopened.Widgets().Single(w => w.Id == weather.Id).ViewMode);
            Assert.Equal("任务", reopened.Entries(tasks.Id).Single().Title);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(reopened.AttachmentPath(attachment)));
            var due = reopened.ClaimDue(DateTimeOffset.UtcNow);
            Assert.Equal(2, due.Count);
            Assert.Contains(due, reminder => reminder.WidgetId == tasks.Id && reminder.IsEntry);
            Assert.Empty(reopened.ClaimDue(DateTimeOffset.UtcNow));
            item.ReminderUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
            item.NotifiedUtc = null; reopened.Save(item);
            Assert.Single(reopened.ClaimDue(DateTimeOffset.UtcNow));
            reopened.DeleteEntry(item.Id);
            Assert.False(File.Exists(reopened.AttachmentPath(taskFile)));
            reopened.DeleteWidget(note.Id);
            Assert.Empty(reopened.Attachments(note.Id));
            Assert.False(File.Exists(reopened.AttachmentPath(attachment)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public void ExistingDatabaseAddsWeatherViewModeWithoutLosingWidgets()
    {
        var root = Path.Combine(Path.GetTempPath(), "MiniC-Widget-Migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "widgets.db");
            using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE widgets (
                      id TEXT PRIMARY KEY, kind INTEGER NOT NULL, title TEXT NOT NULL,
                      x REAL NOT NULL, y REAL NOT NULL, width REAL NOT NULL, height REAL NOT NULL,
                      collapsed INTEGER NOT NULL, material TEXT NOT NULL, theme TEXT NOT NULL,
                      opacity REAL NOT NULL, body TEXT NOT NULL, city TEXT NOT NULL,
                      reminder_utc TEXT, notified_utc TEXT);
                    INSERT INTO widgets
                      (id,kind,title,x,y,width,height,collapsed,material,theme,opacity,body,city,reminder_utc,notified_utc)
                    VALUES
                      ('11111111111111111111111111111111',4,'天气',80,120,340,370,0,'Solid','Graphite',0.75,'','上海',NULL,NULL);
                    """;
                command.ExecuteNonQuery();
            }

            var store = new WidgetStore(root);
            var migrated = Assert.Single(store.Widgets());
            Assert.Equal("天气", migrated.Title);
            Assert.Equal("", migrated.ViewMode);
            migrated.ViewMode = "Large";
            store.Save(migrated);
            Assert.Equal("Large", new WidgetStore(root).Widgets().Single().ViewMode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

}
