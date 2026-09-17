# 开发指南

[English](en/DEVELOPMENT.md)

## 环境要求

- Windows 10 1809 或更高版本，推荐 Windows 11
- .NET 10 SDK
- 支持当前 .NET 10 SDK 的代码编辑器；本项目已验证本地 SDK 命令行构建
- 构建安装包时额外需要 Inno Setup 6

项目目标为 `net10.0-windows`，并同时启用 WPF 和 Windows Forms。由于依赖 Windows Shell 和 Win32 API，不能在非 Windows 环境完成有效运行验证。

## 获取和构建

源码仓库不包含 `.dotnet` SDK、`.nuget` 缓存、`.local` 归档或 `dist` 安装包。首次克隆请安装 .NET 10 SDK，使用下方系统 SDK 命令；本地开发可继续使用已有的仓库旁 SDK。

仓库包含本地 SDK 时：

```powershell
.\.dotnet\dotnet.exe restore .\MiniC.sln --configfile .\NuGet.Publish.Config
.\.dotnet\dotnet.exe build .\MiniC.sln -c Release --no-restore
```

使用系统 SDK 时：

```powershell
dotnet restore .\MiniC.sln --configfile .\NuGet.Publish.Config
dotnet build .\MiniC.sln -c Release --no-restore
```

`NuGet.Config` 已清空外部包源。主应用没有第三方 NuGet 包依赖，但测试项目使用 `Microsoft.NET.Test.Sdk 17.14.1`、`xunit 2.9.3` 和 `xunit.runner.visualstudio 3.1.4`。首次构建使用已有的 `NuGet.Publish.Config` 从 nuget.org 恢复依赖；恢复成功后才使用 `--no-restore`。SDK 仍需包含对应 Windows 桌面目标包。

2026-09-10 已在独立源码副本和空包缓存中验证恢复、Release 构建与测试，详细结果见 [项目接手记录](HANDOVER.md)。

## 运行

构建完成后直接运行：

```powershell
& .\src\MiniC\bin\Release\net10.0-windows\MiniC.exe
```

MiniC 会接管 Explorer 桌面图标显示。调试前关闭其他 MiniC 实例；结束调试后确认 Explorer 原生图标已经恢复。不要同时运行两个会接管桌面的构建。

## 代码入口

| 任务 | 入口 |
| --- | --- |
| 启动、单实例、自检 | `src/MiniC/App.xaml.cs` |
| 跨窗口业务协调 | `src/MiniC/Controllers/DesktopCoordinator.cs` |
| 布局模型 | `src/MiniC/Models/LayoutState.cs` |
| 布局读写 | `src/MiniC/Services/LayoutStore.cs` |
| 文件移动与重命名 | `src/MiniC/Services/DesktopFileService.cs` |
| Explorer 图标能力 | `src/MiniC/Services/NativeDesktopService.cs` |
| 系统右键菜单 | `src/MiniC/Services/ShellContextMenuService.cs` |
| 桌面窗口 | `src/MiniC/Views/DesktopSurfaceWindow.xaml(.cs)` |
| 收纳盒窗口 | `src/MiniC/Views/DeskGroupWindow.xaml(.cs)` |
| 异常回退 | `src/MiniC/Services/DesktopFallbackWatchdog.cs` |

## 修改原则

- 先确认 [架构不变量](ARCHITECTURE.md#核心不变量)，再修改相关模块。
- ViewModel 不执行真实文件操作；窗口通过回调把业务动作交给协调器。
- 不用 UI 状态代替事实状态。文件系统是文件存在性的事实来源，布局只保存视觉归属。
- 对公共桌面、同名文件、跨盘复制、扩展名设置、多显示器和异常退出进行关联检查。
- 删除被替代的旧实现，不保留两套并行逻辑。
- 用户可见变化更新 `CHANGELOG.md`；每次开发变更在 `development.log` 顶部添加简短记录。

## 命名规范

- 对外品牌、程序集、项目和根命名空间统一为 `MiniC`。
- 新代码和文档不得新增 `MiniC桌面`、`MiniCDesktop` 或以 `DeskNest` 表示当前产品。
- `DeskGroup`、`DesktopSurfaceWindow` 等领域名称描述桌面业务，不需要为了品牌统一而改名。
- 旧名称只允许保留在数据迁移、安装升级、单实例和历史进程清理等兼容入口，并在代码旁说明原因。

## 验证

`.github/workflows/build.yml` 在 GitHub 的 Windows 运行器上执行依赖恢复、Release 构建和独立测试。自动流程不运行会接管 Explorer 的正常主程序，也不代替安装和真实桌面交互验收。

修改后先执行 Release 构建和独立单元测试：

```powershell
.\.dotnet\dotnet.exe build .\MiniC.sln -c Release --no-restore
.\.dotnet\dotnet.exe test .\tests\MiniC.Tests\MiniC.Tests.csproj -c Release --no-build --no-restore
```

`Directory.Build.props` 启用 .NET 分析器、整数溢出检查和警告即错误。服务保持接口导向时，`CA1822`、`CA1859` 等纯风格或微性能建议不阻断发布；正确性和安全告警必须清零。

需要真实 Explorer、DWM 或窗口层的集成行为继续使用专项自检：

```powershell
.\.dotnet\dotnet.exe .\src\MiniC\bin\Release\net10.0-windows\MiniC.dll `
  --rename-policy-smoke-test
if ($LASTEXITCODE -ne 0) { throw "smoke test failed: $LASTEXITCODE" }
```

可用参数：

| 参数 | 覆盖范围 |
| --- | --- |
| `--multi-drag-layout-smoke-test` | 多选拖动布局与网格避让 |
| `--rename-policy-smoke-test` | 文件名、扩展名和默认选区 |
| `--rename-focus-smoke-test` | 桌面/收纳盒真实临时文件的失焦提交、Escape 取消、失败恢复与焦点保留 |
| `--file-transfer-smoke-test` | 文件移动、复制和投放 |
| `--group-organization-smoke-test` | 收纳盒排序、标签、外观和拓扑 |
| `--marquee-selection-smoke-test` | 框选和 `Ctrl` 反选 |
| `--drag-preview-smoke-test` | 拖动预览位置与窗口样式 |
| `--desktop-drop-target-smoke-test` | 桌面空白区域投放 |
| `--desktop-interactive-layer-smoke-test` | 桌面交互区域与 Z 序 |
| `--desktop-display-layer-smoke-test` | Explorer 图标层隐藏和恢复 |
| `--startup-smoke-test` | XAML、迁移、布局、守护和启动策略 |

`--desktop-display-layer-smoke-test` 会真实切换 Explorer 图标层，只能在可恢复的 Windows 桌面会话中运行。桌面层、Shell、拖放、真实文件和多显示器改动还需人工验证对应交互；测试文件必须是可恢复副本。

运行时诊断日志保存在 `%LOCALAPPDATA%\MiniC\Logs`。Shell、后台任务或 WPF 调度异常必须写入日志，禁止无上下文吞掉异常。

## 发布

日常开发不生成安装包。发布前同步以下版本号：

1. `src/MiniC/MiniC.csproj` 中的 `<Version>`
2. `installer/MiniC.iss` 中的 `AppVersion`
3. `installer/MiniC.iss` 中的 `PublishDir`

自包含发布：

```powershell
.\.dotnet\dotnet.exe restore .\src\MiniC\MiniC.csproj `
  -r win-x64 --configfile .\NuGet.Publish.Config
.\.dotnet\dotnet.exe publish .\src\MiniC\MiniC.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o .\dist\MiniC-v<version>-win-x64
```

生成安装包：

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" `
  ".\installer\MiniC.iss"
```

发布前确认根目录已有维护者选定的 `LICENSE`，并完成首次安装、旧版本升级、卸载、异常退出回退和布局保留验证。安装包应包含 `THIRD_PARTY_NOTICES.md`，对外发布时提供 SHA-256；正式分发建议完成代码签名。
