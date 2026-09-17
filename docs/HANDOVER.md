# MiniC 项目接手记录

2026-09-17 [0.59.0 已发布](https://github.com/a248539568-dot/MiniC/releases/tag/v0.59.0)：[外部保存、回收站与标题栏激活](BUGFIX-2026-09-17.md)，并补齐桌面/收纳盒文件失焦自动改名。测试基线 49/49，发布代码提交 5ddfc17 的 GitHub Actions 通过；10 项发布自检完成，桌面层首轮失败后旧版对照与新版复测通过。安装包 50.21 MiB，Release 提供 SHA-256 文件。未自动安装，真实 Photoshop、多屏和安装升级仍需现场复核。

更新日期：2026-09-17。当前源码版本：0.59.0。本文件只保留当前维护入口；逐次变更与历史验证见根目录 development.log，用户可见变化见 CHANGELOG.md。

源码仓库：[a248539568-dot/MiniC](https://github.com/a248539568-dot/MiniC)，公开仓库，主分支 main。GitHub 保存源码、文档、测试和自动构建配置；本地归档、SDK 和安装包不包含在提交中。

搜索定位：Windows 桌面整理、桌面图标分组、桌面收纳盒；英文为 desktop organizer、desktop icons、icon organizer。仓库简介与 Topics 同步使用产品类别、Windows 平台及 WPF / C# / .NET 技术标签。公开可见不等于已经指定开源许可证。

## 当前基线

- 技术栈：C#、.NET 10、WPF、Windows Forms 托盘、Windows Shell COM / Win32。
- 正式项目：src/MiniC 与 tests/MiniC.Tests，统一由 MiniC.sln 构建。
- 当前测试基线：49 项独立测试，发布前运行 10 项隔离自检。
- 本地安装包：dist/MiniC-Setup-v0.59.0.exe，同目录有 SHA-256 校验文件；dist 不提交到 Git，安装包作为 GitHub Release 附件提供。
- 最新交互：左右拖标签只排序，上下拖用于抽离/合并；接收点限定在标签栏，独立盒使用名称区域。原配色和透明度不变，仅细边框提示接收。
- 动画：逐帧跟手、稳定预览视口、差量宿主同步；禁止恢复逐事件追赶动画、整盒缩放或全部窗口重建。
- 自动构建配置：.github/workflows/build.yml，在 Windows 环境恢复依赖、构建和运行独立测试；首次远端运行结果以上传后的 GitHub Actions 为准。

## 不可破坏的业务规则

1. 文件系统决定文件是否存在，布局只决定视觉归属与坐标；收纳盒不是磁盘目录。
2. 删除/解散收纳盒只解除归属，不删除真实文件。外部文件传输成功后才提交目标路径。
3. 同一项目只在桌面层或一个收纳盒显示；系统虚拟图标只留在桌面层。
4. 一个独立收纳盒或一个标签组对应一个宿主窗口；拓扑改变必须验证 ID、标签顺序与文件归属数量，并可回滚。
5. Explorer 原生图标只在接管完成后隐藏，正常退出和恢复守护都要保证原生桌面可用。
6. 不使用跨进程 SetParent 或永久 Topmost 替代桌面窗口策略。拖动穿透预览只在手势期间存在，退出时必须清理。

## 维护入口

路径相对 src/MiniC。

| 业务 | 入口 | 关联检查 |
| --- | --- | --- |
| 启动/退出 | App.xaml.cs、DesktopStartupReadinessService、DesktopFallbackWatchdog | 单实例、自启动、Explorer 就绪、正常保存与异常恢复 |
| 跨窗口协调 | Controllers/DesktopCoordinator.cs | 归属、标签拓扑、差量宿主同步、刷新与保存互斥 |
| 布局/兼容 | Models/LayoutState.cs、LayoutStore、LayoutRepairService、LegacyStorageMigrationService | 缺字段默认值、孤儿归属、旧目录迁回、同名不覆盖 |
| 文件操作 | DesktopFileService、DesktopFileServiceContracts | 桌面/公共桌面、跨盘、重命名扩展名、部分失败 |
| 系统桌面 | NativeDesktopService、ExplorerDesktopWindowService、DesktopLayerHostService | 宿主校验、物理像素/DIP、焦点、Z 序、多屏 |
| 右键/剪贴板 | ShellContextMenuService、ShellClipboardTransferService、DesktopClipboardCoordinator | COM 释放、系统新建落位、复制/剪切状态同步 |
| 刷新/落位 | DesktopItemRefreshService、DesktopIconPlacementService、DesktopAssignmentRetentionService | 保留对象与编辑状态、工作区边界、缺失宽限期 |
| 标签/动效 | Views/DeskGroupWindow、GroupDragPreviewWindow、Controls/GroupTransitionSurface、PanelMotionAnimator | 方向锁定、标签栏命中、取消、中断清理、保留颜色与透明度 |
| 手势策略 | TabDragPolicy、GroupDetachPlacement | 横向排序/纵向转移、预览与实际落点一致 |

接到改动时按“交互入口 → 视图回调 → 协调器 → 服务副作用 → 持久化/刷新 → 测试”追踪，不在视图中执行真实文件操作。

## 已知待处理事项

| 优先级 | 当前问题 | 后续验证 |
| --- | --- | --- |
| 高 | LayoutStore 读取失败后静默返回默认布局，初始化随后保存可能覆盖损坏现场 | 隔离目录复现、保留损坏副本、记录恢复状态 |
| 高 | IsPathInsideDirectory 未排除不同根路径，可能误判跨盘目录包含关系 | 跨盘目录传输与重解析点边界测试 |
| 中 | 安装升级按进程名强制结束旧实例，可能丢失延迟保存的布局变更 | 隔离系统验证正常退出、超时回退和升级布局保留 |
| 中 | 部分初始化异常采用静默 catch 或统一标记已处理 | 故障注入、受控退出与 Explorer 恢复 |

自动测试不等于真实桌面验收。完整鼠标操作、混合 DPI 多屏、Explorer 重启、登录自启动、强制退出、安装/升级/卸载仍需现场验证。项目没有 Web 服务、数据库或账号系统；当前安全边界以本地文件、Shell、布局与安装权限为主。

## 构建、数据与本地资料

- 使用已安装的 .NET 10 SDK，或本机保留的 .dotnet/dotnet.exe；首次恢复使用 NuGet.Publish.Config，详见 DEVELOPMENT.md。
- 用户布局在 %LOCALAPPDATA%\MiniC\layout.json，运行日志在 %LOCALAPPDATA%\MiniC\Logs。它们不得提交到仓库。
- 修复真实布局前正常退出并备份数据目录；详见 DATA-SAFETY.md。
- SDK、包缓存、研究快照、旧测试程序和安装包都不属于源码提交内容。
- 本次整理把 .research、失效的 src/MiniC.Watchdog 构建目录及 runtime-test.pid 移入 .local/archives/cleanup-20260913-135321/。历史文档中的 .research 路径指向该归档中的原相对位置，归档不上传 GitHub。
- 本地 Git 与远端上传状态以 git status / git remote 及 GitHub 仓库为准；不再将早期“无 Git 历史”的接手记录当作当前状态。

## 历史专题

- [桌面双击、重命名及任务栏边界修复](BUGFIX-2026-09-10.md)
- [动画冲突与流畅性修复](MOTION-FIX-2026-09-13.md)
- [交互约定](INTERACTIONS.md)
- [架构](ARCHITECTURE.md)

尚未选择根目录 LICENSE，不将源码上传行为表述为授予开源许可；第三方归属声明见 THIRD_PARTY_NOTICES.md。
