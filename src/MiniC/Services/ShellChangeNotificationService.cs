using System.Runtime.InteropServices;
using System.IO;

namespace MiniC.Services;

/// <summary>封装 Shell 桌面目录通知的注册、解锁和原生资源生命周期。</summary>
public sealed class ShellChangeNotificationService : IDisposable
{
    private const int ShellLevel = 0x0002;
    private const int NewDelivery = 0x8000;
    private const int AllEvents = unchecked((int)0x7FFFFFFF);
    private readonly List<uint> _registrationIds = [];
    private readonly List<IntPtr> _pidls = [];

    public bool Register(IntPtr notificationWindow, uint notificationMessage, IEnumerable<string> directories)
    {
        DisposeRegistrations();
        foreach (var path in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(path)
                || SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) < 0
                || pidl == IntPtr.Zero) continue;
            var entry = new ShellChangeNotifyEntry(pidl, true);
            var id = SHChangeNotifyRegister(notificationWindow, ShellLevel | NewDelivery,
                AllEvents, notificationMessage, 1, ref entry);
            if (id == 0)
            {
                Marshal.FreeCoTaskMem(pidl);
                continue;
            }
            _registrationIds.Add(id);
            _pidls.Add(pidl);
        }
        return _registrationIds.Count > 0;
    }

    public bool TryConsume(IntPtr changeHandle, IntPtr processId)
    {
        IntPtr lockHandle = IntPtr.Zero;
        try
        {
            lockHandle = SHChangeNotificationLock(changeHandle,
                unchecked((uint)processId.ToInt64()), out _, out _);
            return lockHandle != IntPtr.Zero;
        }
        catch (Exception exception)
        {
            MiniCLogger.Error(nameof(ShellChangeNotificationService), exception,
                "Unable to consume a Shell change notification.");
            return false;
        }
        finally
        {
            if (lockHandle != IntPtr.Zero)
            {
                try { _ = SHChangeNotificationUnlock(lockHandle); }
                catch (Exception exception) { MiniCLogger.Error(nameof(ShellChangeNotificationService), exception); }
            }
        }
    }

    public void Dispose() => DisposeRegistrations();

    private void DisposeRegistrations()
    {
        foreach (var id in _registrationIds) _ = SHChangeNotifyDeregister(id);
        _registrationIds.Clear();
        foreach (var pidl in _pidls) Marshal.FreeCoTaskMem(pidl);
        _pidls.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellChangeNotifyEntry(IntPtr pidl, bool recursive)
    {
        public IntPtr Pidl = pidl;
        [MarshalAs(UnmanagedType.Bool)] public bool Recursive = recursive;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext,
        out IntPtr itemIdList, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern uint SHChangeNotifyRegister(IntPtr windowHandle, int sources,
        int events, uint message, int entryCount, ref ShellChangeNotifyEntry entries);

    [DllImport("shell32.dll")]
    private static extern bool SHChangeNotifyDeregister(uint registrationId);

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotification_Lock")]
    private static extern IntPtr SHChangeNotificationLock(IntPtr changeHandle, uint processId,
        out IntPtr pidl, out int eventId);

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotification_Unlock")]
    private static extern bool SHChangeNotificationUnlock(IntPtr lockHandle);
}
