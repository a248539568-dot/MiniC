using System.IO;
using System.Windows;
using System.Windows.Threading;
using MiniC.Controllers;
using MiniC.Services;

namespace MiniC;

/// <summary>
/// 应用程序入口，负责单实例互斥、第二实例唤醒以及应用生命周期清理。
/// </summary>
public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = DesktopFallbackWatchdog.SingleInstanceMutexName;
    private const string ActivateApplicationEventName = "Local\\DeskNest.Activate.4E31D7B6";
    private DesktopCoordinator? _coordinator;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateApplicationEvent;
    private volatile bool _isExiting;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MiniCLogger.Error(nameof(App), e.Exception, "Unhandled WPF dispatcher exception.");
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            MiniCLogger.Error(nameof(App), exception, "Unhandled application-domain exception.");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        MiniCLogger.Error(nameof(App), e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using var activateApplicationEvent = EventWaitHandle.OpenExisting(ActivateApplicationEventName);
                    activateApplicationEvent.Set();
                    break;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    if (attempt == 9) break;
                    Thread.Sleep(75);
                }
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DesktopFallbackWatchdog.StartForCurrentProcess();
        _activateApplicationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateApplicationEventName);
        var isAutoStart = e.Args.Any(argument => argument.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        var nativeDesktopService = new NativeDesktopService();
        if (isAutoStart) await DesktopStartupReadinessService.WaitAsync(nativeDesktopService);
        if (_isExiting) return;
        // 协调器创建自绘桌面图标层后，再隐藏 Explorer 的原生图标层。
        _coordinator = new DesktopCoordinator(
            new LayoutStore(),
            new DesktopFileService(),
            new LegacyStorageMigrationService(),
            new ShellContextMenuService(),
            nativeDesktopService,
            new DesktopLayerHostService(),
            () => Shutdown());
        try
        {
            await _coordinator.InitializeAsync();
        }
        catch (InvalidDataException exception)
        {
            MiniCLogger.Error(nameof(App), exception, "Desktop layout could not be loaded.");
            System.Windows.MessageBox.Show(exception.Message, "MiniC 布局恢复", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        if (!isAutoStart) _coordinator.ActivateBoxes();
        _ = Task.Run(WaitForActivationRequests);
    }

    private void WaitForActivationRequests()
    {
        while (!_isExiting && _activateApplicationEvent is not null)
        {
            try
            {
                _activateApplicationEvent.WaitOne();
                if (_isExiting) return;
                Dispatcher.BeginInvoke(() => _coordinator?.ActivateBoxes());
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        _coordinator?.Dispose();
        _coordinator = null;
        _activateApplicationEvent?.Set();
        _activateApplicationEvent?.Dispose();
        _activateApplicationEvent = null;
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

}
