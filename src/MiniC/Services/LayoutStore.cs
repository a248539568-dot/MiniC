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
                state = await JsonSerializer.DeserializeAsync<LayoutState>(stream, JsonOptions) ?? CreateDefault();
            if (sourcePath.Equals(_legacyFilePath, StringComparison.OrdinalIgnoreCase))
            {
                try { await SaveAsync(state); }
                catch
                {
                    // 旧布局仍可正常使用；写入新品牌目录失败时下次启动继续迁移。
                }
            }
            return state;
        }
        catch
        {
            return CreateDefault();
        }
    }

    internal static async Task ValidateLegacyPathMigrationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MiniC-LayoutMigration-{Guid.NewGuid():N}");
        try
        {
            var legacyDirectory = Path.Combine(root, "DeskNest");
            Directory.CreateDirectory(legacyDirectory);
            await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "layout.json"),
                "{\"groups\":[{\"id\":\"legacy\",\"name\":\"Legacy\"}]}");
            var store = new LayoutStore(root);
            var state = await store.LoadAsync();
            if (state.Groups.SingleOrDefault()?.Id != "legacy"
                || !File.Exists(Path.Combine(root, "MiniC", "layout.json")))
                throw new InvalidOperationException("旧品牌布局目录迁移自检失败。");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch
            {
                // 临时自检目录清理失败不影响用户布局。
            }
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
            Width = 430,
            Height = 390
        };
        var work = new GroupState
        {
            Id = "work",
            Name = "工作与文档",
            X = 490,
            Y = 104,
            Width = 390,
            Height = 320
        };
        return new LayoutState { Groups = [all, work] };
    }
}
