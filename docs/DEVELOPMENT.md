# 开发指南

[English](en/DEVELOPMENT.md)

## 环境要求

- Windows 11 Build 22000 或更高版本
- .NET 10 SDK
- 支持当前 .NET 10 SDK 的代码编辑器；本项目已验证本地 SDK 命令行构建
- 构建安装包时额外需要 Inno Setup 6

项目目标为 `net10.0-windows10.0.26100.0`，最低 Windows 11 Build 22000，并同时启用 WPF 和 Windows Forms。实时晶透通过 Vortice 的 DXGI/D3D11 GPU 纹理与 Composition 交换链合成，毛玻璃使用系统后景；没有逐帧 CPU/WPF 背景图像回读。由于依赖 Windows Shell 和 Win32 API，不能在非 Windows 环境完成有效运行验证。

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

`NuGet.Config` 已清空外部包源。主应用使用 `Microsoft.Data.Sqlite 10.0.12`、`lunar-csharp 1.6.8` 和 `Vortice.Direct3D11 3.8.3`（含 DXGI、DirectX、Mathematics 和 SharpGen 运行时依赖）；测试项目使用 `Microsoft.NET.Test.Sdk 17.14.1`、`xunit 2.9.3` 和 `xunit.runner.visualstudio 3.1.4`。首次构建使用已有的 `NuGet.Publish.Config` 从 nuget.org 恢复依赖；恢复成功后才使用 `--no-restore`。SDK 仍需包含对应 Windows 桌面目标包。

2026-09-10 已在独立源码副本和空包缓存中验证恢复、Release 构建与测试，详细结果见 [项目接手记录](HANDOVER.md)。

## 运行

构建完成后直接运行：

```powershell
& .\src\MiniC\bin\Release\net10.0-windows10.0.26100.0\MiniC.exe
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

晶透像素着色器源文件为 `src/MiniC/Effects/CrystalGlass.hlsl`，已编译的 `CrystalGlass.ps` 作为资源随源码提交，普通构建不需要额外 SDK。修改着色器后，在装有 Windows SDK 的机器运行 `tools/Compile-CrystalShader.ps1`（可用 `-Compiler` 指定 x64 fxc.exe），重新生成 ps_2_0 字节码并运行 CrystalGlassTests。不要把源码扩展名或字节码文件当作 PowerShell 脚本。

`.github/workflows/build.yml` 在 GitHub 的 Windows 运行器上执行依赖恢复、Release 构建和独立测试。自动流程不运行会接管 Explorer 的正常主程序，也不代替安装和真实桌面交互验收。

修改后先执行 Release 构建和独立单元测试：

```powershell
.\.dotnet\dotnet.exe build .\MiniC.sln -c Release --no-restore
.\.dotnet\dotnet.exe test .\tests\MiniC.Tests\MiniC.Tests.csproj -c Release --no-build --no-restore
```

`Directory.Build.props` 启用 .NET 分析器、整数溢出检查和警告即错误。服务保持接口导向时，`CA1822`、`CA1859` 等纯风格或微性能建议不阻断发布；正确性和安全告警必须清零。

不再通过启动参数运行会弹窗、模拟鼠标或切换 Explorer 图标层的自检。日常验证仅运行 Release 构建与不显示窗口的单元测试，覆盖拓扑、布局、文件规则、SQLite、天气解析和控件状态。真实桌面交互由维护者在单独的测试会话中人工验收，不应在用户正在使用的桌面上自动运行。

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

## 2026-09-20 毛玻璃移动

旧屏幕采样实现已删除。毛玻璃开启透出使用系统后景和高斯效果图，原生失败保持直透；切换、隐藏和关闭释放原生材质层。

## 2026-10-18 控制面板移除与外观初始化

控制面板窗口及其启动、托盘左键和单实例唤醒链路已删除。托盘左键改为激活已有收纳盒，外观设置只保留在当前收纳盒或组件自己的菜单中。首次启动执行一次外观初始化迁移，将旧宿主和组件恢复为统一的 `White + Crystal + 62%` 基础样式及默认光学参数，保留位置、尺寸、名称、标签、文件归属和组件内容；布局通过 `AppearanceInitializationVersion` 防止重复覆盖用户后续修改。

## 2026-09-20 合成背景开发依赖

项目与测试目标为 `net10.0-windows10.0.26100.0`，最低系统为 Windows 11 Build 22000。首次还原及自包含发布使用 `NuGet.Publish.Config` 取得 Windows SDK .NET 投影和 Vortice/SharpGen 依赖。0.60.59 保留 WGC/CPU 回读链路的删除结果，接入 GPU→GPU→Composition 原生晶透；安装脚本不再清理所需的 Vortice/SharpGen DLL。修改 shader 后执行 `tools/Compile-CrystalShader.ps1`，需 Windows SDK 的 fxc。无界面检查使用隐藏句柄、离屏 GPU 和实际 DXGI 会话，不启动应用或操作鼠标。

系统后景画刷与 Transform2D 组合仍不受支持，原生晶透使用独立 GPU 背景纹理。安装后需人工验证正常透出、毛玻璃、点击激活、连续拖动和多屏；特别注意 WDA_EXCLUDEFROMCAPTURE 对截图和重叠晶透盒子的影响。桌面来源是静态壁纸；跨显卡同时跨屏折射未实现，不宣称已测得帧率。细节见 [晶透链路](CRYSTAL-WALLPAPER.md)。

0.60.60 的拖动验证继续使用隐藏 HWND 和实际 DXGI 会话：检查双轴成对移动、不改变显隐/激活、单帧等待交换链的缩放和句柄释放、阻塞 UI 接收期间合并 200 次位置更新、取消/关闭丢弃旧提交，以及输入唤醒高精度捕获期限。195/195 Release 检查通过。shader 与材质配方未修改；实际拖动观感由用户验收，不运行可见窗口或鼠标测试。
