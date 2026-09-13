using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace MiniC.Services;

/// <summary>在独立进程中监视主程序，主进程退出后恢复 Explorer 桌面图标层。</summary>
public static class DesktopFallbackWatchdog
{
    public const string Argument = "--desktop-restore-watchdog";
    // 保留旧互斥量名称，防止升级期间 MiniC 与历史版本同时接管 Explorer 桌面。
    public const string SingleInstanceMutexName = "Local\\DeskNest.SingleInstance.4E31D7B6";

    public static bool TryParse(IReadOnlyList<string> arguments, out int processId, out long startTimeUtcTicks)
    {
        processId = 0;
        startTimeUtcTicks = 0;
        var index = arguments.ToList().FindIndex(argument =>
            argument.Equals(Argument, StringComparison.OrdinalIgnoreCase));
        return index >= 0
               && index + 2 < arguments.Count
               && int.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out processId)
               && long.TryParse(arguments[index + 2], NumberStyles.None, CultureInfo.InvariantCulture,
                   out startTimeUtcTicks)
               && processId > 0
               && startTimeUtcTicks > 0;
    }

    public static void StartForCurrentProcess()
    {
        if (Environment.ProcessPath is not { Length: > 0 } executablePath) return;
        try
        {
            using var current = Process.GetCurrentProcess();
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } assemblyPath)
                startInfo.ArgumentList.Add(assemblyPath);
            startInfo.ArgumentList.Add(Argument);
            startInfo.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            Process.Start(startInfo)?.Dispose();
        }
        catch
        {
            // 守护启动失败不应阻断主程序，正常退出路径仍会恢复 Explorer。
        }
    }

    public static async Task<bool> WaitForExitAndRestoreAsync(int processId, long startTimeUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime().Ticks == startTimeUtcTicks)
                await process.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
            // 主进程在守护进程完成附加前已经退出。
        }
        catch (InvalidOperationException)
        {
            // 进程标识已失效，直接进入桌面恢复。
        }

        if (ReplacementInstanceIsRunning()) return true;

        var nativeDesktop = new NativeDesktopService();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (nativeDesktop.SetDesktopIconLayerVisible(true)
                && nativeDesktop.IsDesktopIconLayerVisible()) return true;
            await Task.Delay(250);
        }
        return false;
    }

    internal static bool ValidateArgumentsForSmokeTest()
    {
        const int expectedProcessId = 4321;
        const long expectedTicks = 638000000000000000;
        return TryParse([Argument, "4321", "638000000000000000"], out var processId, out var ticks)
               && processId == expectedProcessId
               && ticks == expectedTicks
               && !TryParse([Argument, "invalid", "0"], out _, out _);
    }

    private static bool ReplacementInstanceIsRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(SingleInstanceMutexName);
            try
            {
                if (!mutex.WaitOne(0)) return true;
                mutex.ReleaseMutex();
                return false;
            }
            catch (AbandonedMutexException)
            {
                mutex.ReleaseMutex();
                return false;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}
