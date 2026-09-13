using Microsoft.Win32;

namespace MiniC.Services;

/// <summary>封装当前用户的 Windows 登录启动项读写。</summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MiniC";
    private static readonly string[] LegacyValueNames = ["MiniC桌面", "DeskNest"];

    public static bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey is not null
               && new[] { ValueName }.Concat(LegacyValueNames)
                   .Any(name => runKey.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value));
    }

    public static void SetEnabled(bool enabled)
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                           ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            foreach (var legacyValueName in LegacyValueNames)
                runKey.DeleteValue(legacyValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath
                             ?? throw new InvalidOperationException("无法确定 MiniC 程序路径。");
        var desiredValue = $"\"{executablePath}\" --autostart";
        foreach (var legacyValueName in LegacyValueNames)
            runKey.DeleteValue(legacyValueName, throwOnMissingValue: false);
        if (string.Equals(runKey.GetValue(ValueName) as string, desiredValue, StringComparison.OrdinalIgnoreCase)) return;
        runKey.SetValue(ValueName, desiredValue, RegistryValueKind.String);
    }
}
