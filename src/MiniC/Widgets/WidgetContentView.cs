using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MiniC.Models;
using MiniC.Views;

namespace MiniC.Widgets;

/// <summary>Persistent visual content for a widget, independent of its window chrome.</summary>
public sealed partial class WidgetContentView : System.Windows.Controls.UserControl, IBoxContent
{
    private readonly WidgetState _state;
    private readonly WidgetStore _store;
    private readonly WidgetWeatherService _weather;
    private readonly Action _save;
    private readonly Action<WidgetKind> _createWidget;
    private bool _closing;
    private WidgetState State => _state;
    private StackPanel _body => Body;
    private WidgetEntry? _selectedEntry { get => SelectedEntry; set => SelectedEntry = value; }
    private DateTime _shownMonth { get => ShownMonth; set => ShownMonth = value; }
    private DateTime? _selectedDay { get => SelectedDay; set => SelectedDay = value; }
    private readonly LatestRequest _weatherRequests = new();
    public BoxTabRef Tab => new(BoxContentKind.Widget, _state.Id);
    public string Title => _state.Title;
    public System.Windows.Size PreferredSize => new(_state.Width, _state.Height);
    public StackPanel Body { get; } = new();
    public Grid ContentRoot { get; } = new();
    public ScrollViewer Scroll { get; }
    private Border? _plannerNavigationLayer;
    private WidgetEntry? SelectedEntry { get; set; }
    private DateTime ShownMonth { get; set; } = DateTime.Today;
    private DateTime? SelectedDay { get; set; }
    private WidgetKind? CalendarDraftKind { get; set; }
    private DatedWidgetEntry? CalendarEditingEntry { get; set; }
    private string PlannerPage { get; set; } = "概览";
    private string PlannerTaskFilter { get; set; } = "待办";
    private TimeSpan FocusRemaining { get; set; } = TimeSpan.FromMinutes(25);
    private TimeSpan FocusDuration { get; set; } = TimeSpan.FromMinutes(25);
    private DispatcherTimer? _focusTimer;
    private TextBlock? _focusClock;
    private TextBlock? _focusStatus;
    private DateTime _focusDeadline;
    private DispatcherTimer? _plannerClockTimer;
    private TextBlock? _plannerClock;
    private TextBlock? _plannerDate;
    public void StopWeather() => _weatherRequests.Cancel();
    public void Resume()
    {
        if (State.Kind is WidgetKind.Weather or WidgetKind.Calendar or WidgetKind.Planner) RenderBody();
    }

    public void ResetWeather()
    {
        _weather.ResetLocation();
        State.City = "";
        SaveSoon();
        RenderBody();
    }

    public WidgetContentView(WidgetState state, WidgetStore store, WidgetWeatherService weather,
        Action save, Action<WidgetKind> createWidget)
    {
        _state = state;
        _store = store;
        _weather = weather;
        _save = save;
        _createWidget = createWidget;
        Scroll = new ScrollViewer
        {
            Content = Body,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        ContentRoot.Children.Add(Scroll);
        Content = ContentRoot;
        if (state.Kind is WidgetKind.Planner or WidgetKind.Weather)
        {
            Body.MaxWidth = 760;
            Body.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            // Reserve the host's thin scrollbar gutter even when the page fits.
            Scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        }
    }

    internal void ClearPlannerNavigation()
    {
        if (_plannerNavigationLayer is not null)
            ContentRoot.Children.Remove(_plannerNavigationLayer);
        _plannerNavigationLayer = null;
        Scroll.Padding = new Thickness(0);
    }

    internal void ShowPlannerNavigation(UIElement navigation)
    {
        ClearPlannerNavigation();
        Scroll.Padding = new Thickness(0, 0, 0, 64);
        _plannerNavigationLayer = new Border
        {
            Child = navigation,
            MaxWidth = 420,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 8, 8),
            IsHitTestVisible = true
        };
        System.Windows.Controls.Panel.SetZIndex(_plannerNavigationLayer, 10);
        ContentRoot.Children.Add(_plannerNavigationLayer);
    }

    private void SaveSoon() => _save();
    public void Stop()
    {
        _closing = true; _weatherRequests.Stop();
        _focusTimer?.Stop();
        _plannerClockTimer?.Stop();
        ClearPlannerNavigation();
    }
}
