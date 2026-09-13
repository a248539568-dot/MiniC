using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MiniC.Services;

/// <summary>开机登录时等待 Explorer 桌面宿主和 DWM 首次合成完成。</summary>
public static class DesktopStartupReadinessService
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);

    public static async Task WaitAsync(NativeDesktopService nativeDesktopService)
    {
        var stopwatch = Stopwatch.StartNew();
        while (nativeDesktopService.GetDesktopHostIdentity() == 0
               && ShouldContinueWaiting(stopwatch.Elapsed, MaximumWait))
            await Task.Delay(PollInterval);

        if (nativeDesktopService.GetDesktopHostIdentity() != 0) _ = DwmFlush();
    }

    internal static bool ValidateWaitPolicyForSmokeTest() =>
        ShouldContinueWaiting(TimeSpan.FromMilliseconds(200), MaximumWait)
        && !ShouldContinueWaiting(MaximumWait, MaximumWait);

    private static bool ShouldContinueWaiting(TimeSpan elapsed, TimeSpan maximumWait) => elapsed < maximumWait;

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
