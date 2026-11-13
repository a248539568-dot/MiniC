using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniC.Services;

/// <summary>Short idle capture polling without depending on the system's coarse timer tick.</summary>
internal sealed class GpuFrameWait : IDisposable
{
    private readonly SafeWaitHandle _timer = CreateWaitableTimerEx(0, null, 2, 0x001F0003);
    internal GpuFrameWait()
    {
        if (_timer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal void Wait(AutoResetEvent wake, GpuGlassSwapChain[]? frames = null, int pollMilliseconds = 4)
    {
        frames ??= [];
        var count = Math.Min(frames.Length, 62); // Win32 permits 64 handles, including wake and timer.
        var handles = new nint[count + 2];
        handles[0] = wake.SafeWaitHandle.DangerousGetHandle();
        handles[1] = _timer.DangerousGetHandle();
        for (var index = 0; index < count; index++) handles[index + 2] = frames[index].LatencyHandle;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pollMilliseconds);
        long due = -(long)pollMilliseconds * 10_000;
        if (!SetWaitableTimerEx(_timer, in due, 0, 0, 0, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = WaitForMultipleObjects((uint)handles.Length, handles, false, uint.MaxValue);
        if (result == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (result >= 2 && result < handles.Length) frames[result - 2].AcceptReadySignal();
    }
    public void Dispose() => _timer.Dispose();
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, in long due, int period, nint completion, nint argument, nint context, uint tolerance);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForMultipleObjects(uint count, nint[] handles, [MarshalAs(UnmanagedType.Bool)] bool all, uint timeout);
}
