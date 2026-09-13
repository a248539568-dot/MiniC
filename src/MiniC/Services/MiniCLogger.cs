using System.Text;
using System.IO;

namespace MiniC.Services;

/// <summary>为后台、Shell 和窗口过程提供不会反向抛出异常的本地诊断日志。</summary>
public static class MiniCLogger
{
    private static readonly object SyncRoot = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniC", "Logs");

    public static void Error(string area, Exception exception, string? detail = null) =>
        Write("ERROR", area, detail is null ? exception.ToString() : $"{detail}{Environment.NewLine}{exception}");

    public static void Warning(string area, string message) => Write("WARN", area, message);

    public static void Info(string area, string message) => Write("INFO", area, message);

    private static void Write(string level, string area, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"MiniC-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{area}] {message}{Environment.NewLine}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Logging must never become a second failure path.
        }
    }
}
