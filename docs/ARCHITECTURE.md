# MiniC 架构说明

[English](en/ARCHITECTURE.md)

## 架构目标

MiniC 采用“真实 Desktop 文件 + 自绘桌面图标层 + 独立收纳盒窗口”架构。目标是在提供收纳体验的同时，不破坏 Windows 对桌面文件的标准访问方式。

## 核心不变量

以下规则是修改相关业务时必须保持的边界：

1. 真实文件始终位于用户桌面或公共桌面，收纳操作只改变视觉归属。
2. 同一项目只能显示在未收纳桌面层或一个收纳盒中，不能重复显示或消失。
3. `DesktopCoordinator` 是窗口、文件归属、布局保存和刷新操作的唯一业务协调器。
4. 一个独立收纳盒对应一个 `DeskGroupWindow`；相同 `TabGroupId` 的多个收纳盒共享一个窗口。
5. Explorer 原生图标层只在 MiniC 完成接管后隐藏，并在正常或异常退出时恢复。
6. 文件操作成功后才能提交视觉归属；跨盒拓扑修改必须可验证、可回滚。

## 组件关系

```text
App
 └─ DesktopCoordinator
     ├─ DesktopSurfaceWindow (1)
     ├─ DeskGroupWindow (0..n)
     ├─ ShellMenuOwnerWindow
     ├─ DesktopFileService / LayoutStore
     ├─ NativeDesktopService / ShellContextMenuService
     └─ DesktopLayerHostService
```

- `App`：单实例、启动就绪、诊断入口、依赖组装和退出清理。
- `DesktopCoordinator`：维护收纳盒集合、文件归属、窗口拓扑、布局保存和刷新。
- `DesktopSurfaceWindow`：绘制未收纳项目，处理桌面框选和桌面区域投放。
- `DeskGroupWindow`：显示一个独立收纳盒或标签组，通过窄回调接口提交业务动作。
- `Services`：封装文件系统、Shell、Explorer、布局和系统能力，不持有具体业务视图。`ShellChangeNotificationService` 独立拥有 Shell 通知注册资源，`ShellClipboardTransferService` 独立负责 OLE/IDropTarget 文件传输，`MiniCLogger` 提供不会反向抛错的运行时诊断。
- `ExplorerDesktopWindowService` 是桌面宿主与原生桌面列表的唯一发现入口；候选窗口必须属于 Shell 桌面进程且顶层类为 `Progman` 或 `WorkerW`，禁止把应用程序的打开或保存对话框识别为桌面。
- `ViewModels`：提供可绑定状态，不直接执行文件操作。

## 窗口分层

MiniC 使用独立顶层工具窗口，不通过跨进程 `SetParent` 挂入 WorkerW。收纳盒未激活时的 Z 序为：

```text
普通应用
收纳盒窗口
MiniC 桌面图标层
Explorer 桌面
```

`DesktopLayerHostService` 负责工具窗口样式、桌面层定位和激活后的 Z 序恢复。MiniC 窗口不进入任务栏或 `Alt+Tab`，也不应覆盖普通应用。多显示器命中和 Explorer 坐标统一使用物理像素。

收纳盒由用户点击激活时，按普通非 Topmost 窗口进入前台；失去激活后再回到上述桌面层级。全屏 `DesktopSurfaceWindow` 不采用此前台策略，始终压在普通应用下方。

## 文件与视觉归属

`LayoutState.NativeDesktop.Assignments` 使用完整路径记录项目所属收纳盒；`ManagedPositions` 记录 MiniC 管理的桌面坐标。收纳时只写入归属，拖回桌面时只删除归属。

外部文件进入桌面或收纳盒时，由 `DesktopFileService` 完成真实文件移动或复制，然后更新归属。文件夹和回收站图标是独立投放目标，同一次投放只能执行一次文件操作。

文件改名、移动或删除后，协调器通过差量刷新同步路径、归属和位置，不清空整个集合重建。详细规则见 [交互行为](INTERACTIONS.md)。

Shell 通知和文件系统监听并行工作，`DesktopDirectorySnapshot` 提供后台轻量元数据兜底；刷新锁忙碌时合并保留后续请求。已完成的快照才成为比较基线，外部改名排队到锁内处理。`RecycleBinIconService` 独立串行查询回收站计数并写入空/满图标，通用图标加载不处理回收站，避免异步结果相互覆盖。

## 收纳盒与标签拓扑

`GroupState.Id` 标识收纳盒，`TabGroupId` 标识共享宿主，`TabOrder` 保存标签顺序。标签合并和拆出通过协调器原子更新：

1. 保存操作前全部宿主属性和归属计数。
2. 修改目标标签关系并规范化顺序。
3. 验证收纳盒 ID 集合、文件归属计数和连续标签顺序。
4. 一次性重建受影响宿主；失败则恢复快照。

`EnsureGroupWindowCoverage` 会在启动、拓扑更新和周期同步时补建缺失宿主，避免收纳盒或其图标只在视觉上消失。

## 启动与退出

启动时先等待 Explorer 桌面宿主和 DWM 首次合成，再读取布局、创建桌面层和收纳盒。兼容迁移和完整文件元数据刷新在首帧之后继续，减少开机等待。

Explorer 原生图标层在 MiniC 完成初始化后隐藏。`DesktopFallbackWatchdog` 以辅助进程监视主进程 PID 和启动时间；主进程崩溃或被强制结束后，它会恢复 Explorer 图标层。检测到新实例已接管时会跳过恢复，避免升级和快速重启时闪烁。

## 持久化与兼容

布局由 `LayoutStore` 写入 `%LOCALAPPDATA%\MiniC\layout.json`，保存时先生成临时文件再替换正式文件。首次发现旧 `%LOCALAPPDATA%\DeskNest\layout.json` 时会安全复制到新目录；`LegacyStorageMigrationService` 是唯一允许读取更早期 Storage 目录的组件。迁移和恢复细节见 [数据与恢复](DATA-SAFETY.md)。

## 扩展边界

- 新的文件系统动作放入 `DesktopFileService` 或独立服务，不放进 ViewModel。
- 新的跨窗口业务动作由回调进入 `DesktopCoordinator`，视图之间不直接互相修改。
- 新的布局字段必须提供默认值，并考虑旧 JSON 缺字段的反序列化结果。
- 新的 Shell 能力优先使用公开 Windows API；无法公开调用的 Windows 11 Explorer 内部界面不应仿冒。
- 涉及真实文件、Explorer 图标层或标签拓扑的改动必须增加隔离自检，并进行真实 Windows 验证。
