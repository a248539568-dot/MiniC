# MiniC 架构说明

[English](en/ARCHITECTURE.md)

## 架构目标

MiniC 采用“真实 Desktop 文件 + 自绘桌面图标层 + 独立收纳盒窗口”架构。目标是在提供收纳体验的同时，不破坏 Windows 对桌面文件的标准访问方式。

## 核心不变量

以下规则是修改相关业务时必须保持的边界：

1. 真实文件始终位于用户桌面或公共桌面，收纳操作只改变视觉归属。
2. 同一项目只能显示在未收纳桌面层或一个收纳盒中，不能重复显示或消失。
3. `DesktopCoordinator` 是窗口、文件归属、布局保存和刷新操作的唯一业务协调器。
4. 每个 `BoxHostState` 对应一个 `DeskGroupWindow`；收纳盒和组件以类型化标签共享宿主。
5. Explorer 原生图标层只在 MiniC 完成接管后隐藏，并在正常或异常退出时恢复。
6. 文件操作成功后才能提交视觉归属；跨盒拓扑修改必须可验证、可回滚。

## 组件关系

盒子前景由 WPF 绘制。`GlassMaterialSurface` 统一背景输入、主题薄色、边框、完整圆角 Clip 与 OpacityMask；`CrystalGlassSurface` 配置折射，`FrostedGlassSurface` 配置模糊，`SolidMaterialSurface` 保留主题薄色而不使用光学效果。`SeeThroughWindows` 是独立于材质的统一来源设置，三种材质都可通过当前盒子或组件自己的菜单修改；旧专属来源字段按当前材质迁移后清空。`UsesWallpaper` 同时绑定三种材质，切换来源只改变背景输入，保留各自参数、完整遮罩和独立前景。

0.60.59：关闭透出绘制缓存壁纸，晶透执行壁纸折射，毛玻璃执行背景模糊。开启透出时，`ComposedCrystalBackdrop` 管理 UI 侧原生宿主、捕获排除、屏幕坐标与透明度；`GpuGlassRenderService` 在共享 MTA 线程上持有 D3D11 device/context、按输出共享 DXGI Duplication、保留 GPU 纹理及每个表面的交换链；`GpuGlassPipeline` 只折射背景，再由 `CompositionBackdropHost` 接入透明合成。没有逐帧 CPU 图像回读、WPF 背景位图、PrintWindow 或旧 WGC 会话。预览与真实盒子使用同一服务；来源、隐藏和关闭释放会话，设备失效退避重建。静态背景保留纹理，位置/光学参数变化仍重新渲染。

0.60.60 拖动同步：标题输入形成物理坐标目标，工作线程每帧只读取一次不可变位置；在途帧不被覆盖，新输入合并。折射绘制后 UI 在一次回调中通过 `NativeWindowPlacement` 批量移动两个 HWND 并提交交换链，结束/取消使旧移动失效。`GpuGlassSwapChain` 以 DXGI 等待句柄控制单帧在途，`GpuFrameWait` 等输入、交换链信号及短时捕获期限，移除固定 60 Hz 时钟。每个显卡的 DXGI/D3D11 操作互斥，不让即时上下文与 UI Present 并发；捕获、复制和 shader 绘制仍在后台。不声称跨 WPF/DWM 原子显示。

0.60.62：只要存在可被截图的实时晶透，所有晶透改用 GpuWindowBackdropSource，按 HWND 共享后方窗口的 WGC GPU 纹理；BackdropWindowCatalog 排除 MiniC 自身与宿主上方窗口，GpuBackdropCompositor 按 Z 序合成预乘 alpha，仍交给相同折射 shader 和交换链。不能通过撤销捕获排除后继续使用整屏输入，否则会递归折射自身。尚未就绪/拒绝捕获/尺寸不符的区域保持直透。系统无边框许可首次由 UI 申请，未授权保留系统提示边框。旧 WGC CPU 回读路径没有恢复。

HideFromCapture 是独立窗口策略。DeskGroupWindow 管理前景捕获标志，覆盖全部材质和来源；原生背景只恢复自己的标志，预览继承源宿主配置。0.60.63 起切换不重建晶透/毛玻璃宿主：开启先排除前景和背景；关闭须等待安全来源的有效帧绘制、呈现及 UI 确认，再恢复可捕获，仅注册不足以判定安全。新原生晶透宿主首帧前保持隐藏，在途旧策略和关闭回调不得恢复状态。全部晶透都隐藏时保留 DXGI 路线。已验证真实已有桌面窗口的 WGC 合成/折射像素及连续策略切换；截图/录屏、桌面黑窗修复观感及性能仍需人工验收。

0.60.64 补齐外部分层窗口的桌面透明语义：BackdropWindowCatalog 读取全局 alpha/透明色键并排除 alpha=0 的来源；GpuWindowBackdropSource 将属性加入帧版本，GpuBackdropCompositor 在光学折射前处理色键、过滤及预乘透明度。读取失败保留采集帧的像素 alpha，不按应用名称或黑色纹理过滤。此职责留在来源目录和合成层，窗口激活只改变 Z 序，不重建材质。切回 DXGI 首帧未就绪时保留原有呈现。

天气服务使用显式 HttpClientHandler.UseProxy=false，IP 定位、地理编码以及各天气源共享直连客户端，不使用代理出口定位。系统级 VPN/TUN 路由仍由 Windows 控制。

材质模型只保留当前视图使用的状态与参数。水波配置直接绑定 `Appearance`，测试检查真实壁纸加载、透明像素、来源切换、生产窗口 XAML 和预览。不保留捕获图像或捕获坐标属性，旧位图不能残留到透明模式。

`AppearanceMenuBuilder` 每次操作读取最新宿主状态，`WindowAppearanceService` 保留各材质的独立参数并归一化。单盒和组件菜单统一进入 `DeskGroupWindow.ApplyAppearanceState`，一次性更新宿主、内容外观及组件存储，随后刷新裁剪和后景；全局设置也使用该入口。切换内容时统一管理 `GroupAppearanceChanged` 的订阅，周期配置不会先切回第一个收纳盒再恢复当前标签。材质链路详见 [晶透实现](CRYSTAL-WALLPAPER.md) 和 [毛玻璃实现](FROSTED-GLASS.md)。

拖动整盒在目标宿主中央 60% 区域释放时合并整个宿主，拖动标签则合并单个内容或离开原宿主拆出；高亮与命中使用同一中央区域，移动期间不逐帧写入组件状态。拓扑先验证并配置目标窗口，再关闭失效的源窗口；失败时尝试恢复拓扑和缺失窗口。

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
- `DeskGroupWindow`：每个 `BoxHostState` 唯一的顶层窗口，挂载收纳盒文件区或组件正文，通过类型化标签切换内容。
- `Services`：封装文件系统、Shell、Explorer、布局和系统能力，不持有具体业务视图。`ShellChangeNotificationService` 独立拥有 Shell 通知注册资源，`ShellClipboardTransferService` 独立负责 OLE/IDropTarget 文件传输，`MiniCLogger` 提供不会反向抛错的运行时诊断。
- `ExplorerDesktopWindowService` 是桌面宿主与原生桌面列表的唯一发现入口；候选窗口必须属于 Shell 桌面进程且顶层类为 `Progman` 或 `WorkerW`，禁止把应用程序的打开或保存对话框识别为桌面。
- `ViewModels`：提供可绑定状态，不直接执行文件操作。

## 窗口分层

`DesktopBoxWindow` 提供无边框透明工具窗口、首次点击激活和桌面层归位。运行时每个 `BoxHostState` 只创建一个 `DeskGroupWindow`；它统一尺寸、折叠、外观、标签和桌面层，收纳盒文件区由 `DeskGroupBoxContent` 挂载，组件正文由 `WidgetContentView` 挂载，组件数据仍保存在 `WidgetStore`。`LayoutState.BoxHosts` 保存混合标签顺序、活动标签、宿主边界和外观；旧 `GroupState.TabGroupId`、`TabOrder` 用于旧布局迁移，并由 `SyncLegacyGroupTopology` 同步。

日程中心是 `WidgetKind.Planner`，属于一个组件内容而不是四个窗口。`WidgetContentView` 在同一宿主内切换“待办 / 专注 / 日历 / 任务”页；四页共享一个 `WidgetState.Id` 和 `WidgetStore.entries`，条目通过 `entry_kind` 区分 Task 与 Schedule。日历页直接聚合同一 ID 的条目，专注计时器只管理内容状态，不改变宿主窗口尺寸和层级。默认 340×380 与小盒子组件统一，天气也按同一小盒子约束组织当前、小时和多日信息。

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

`BoxHostState.Id` 标识窗口宿主，`Tabs` 保存有序的 `BoxTabRef`，内容由类型及 ID 唯一定位；`ActiveTab` 指定当前内容。`BoxTopologyService` 负责合并、拆出、排序和拓扑校验，视图只提交操作。标签合并和拆出通过协调器的 `ChangeBoxTopology` 更新：

1. 保存操作前的宿主快照。
2. 修改宿主和标签关系，验证全部收纳盒及组件各出现一次，活动标签有效。
3. 同步旧布局字段，配置或补建目标窗口，再关闭失效的源窗口。
4. 激活操作后的标签并安排保存；失败时恢复宿主快照并尝试恢复窗口。

`EnsureGroupWindowCoverage` 会在启动、拓扑更新和周期同步时补建缺失宿主，避免收纳盒或其图标只在视觉上消失。

窗口只保留基于 `BoxTabRef` 的统一拖放路径；`TabDragPolicy` 负责方向锁定与拆出预览条件，`BoxDropGeometry` 负责中央投放命中。`GroupDragPreviewWindow` 只承担当前标签拆出预览，旧的纯收纳盒合并/拆分路径及融合收尾接口已删除。

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
