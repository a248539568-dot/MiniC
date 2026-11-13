using System.Text.Json;
using System.IO;
using MiniC.Models;

namespace MiniC.Services;

/// <summary>
/// 负责布局 JSON 的读取与原子写入。保存时先写临时文件，再替换正式文件，避免异常退出损坏配置。
/// </summary>
public sealed class LayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly string _legacyFilePath;

    public LayoutStore()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    internal LayoutStore(string localAppData)
    {
        _filePath = Path.Combine(localAppData, "MiniC", "layout.json");
        _legacyFilePath = Path.Combine(localAppData, "DeskNest", "layout.json");
    }

    public async Task<LayoutState> LoadAsync()
    {
        try
        {
            var sourcePath = File.Exists(_filePath)
                ? _filePath
                : File.Exists(_legacyFilePath) ? _legacyFilePath : null;
            if (sourcePath is null)
            {
                return CreateDefault();
            }

            LayoutState state;
            await using (var stream = File.OpenRead(sourcePath))
                state = await JsonSerializer.DeserializeAsync<LayoutState>(stream, JsonOptions)
                    ?? throw new InvalidDataException($"布局文件没有有效内容：{sourcePath}");
            if (sourcePath.Equals(_legacyFilePath, StringComparison.OrdinalIgnoreCase))
            {
                try { await SaveAsync(state); }
                catch (Exception exception)
                {
                    MiniCLogger.Error(nameof(LayoutStore), exception, "Legacy layout migration will retry next time.");
                }
            }
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            MiniCLogger.Error(nameof(LayoutStore), exception, "Layout load failed; original file was not modified.");
            throw new InvalidDataException($"布局文件无法读取，已停止启动以防止覆盖原有数据：{_filePath}", exception);
        }
    }

    public async Task SaveAsync(LayoutState state)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions);
        }

        File.Move(tempPath, _filePath, true);
    }

    private static LayoutState CreateDefault()
    {
        var all = new GroupState
        {
            Id = "all",
            Name = "桌面项目",
            X = 36,
            Y = 104,
            Width = BoxSizePolicy.DefaultWidth,
            Height = BoxSizePolicy.DefaultHeight
        };
        var work = new GroupState
        {
            Id = "work",
            Name = "工作与文档",
            X = 490,
            Y = 104,
            Width = BoxSizePolicy.DefaultWidth,
            Height = BoxSizePolicy.DefaultHeight
        };
        return new LayoutState { Groups = [all, work] };
    }
}
