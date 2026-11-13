# 晶透材质与后景（0.60.64）

2026-10-18：当前名称为“折射玻璃”，光学已更新为整窗连续水纹与边缘弧面，单次清晰采样不再混合 RGB 色散；前景使用柔和阴影。纯色固定不透明；新盒子默认允许截屏显示。以下包含历史实现记录，现行约束见[材质实现](MATERIALS.md)。

## 来源与样式

宿主配置 → WindowAppearanceService → DeskGroup → UsesWallpaper → CrystalGlassSurface → 背景、主题薄色、GlassSurfaceFrame → 圆角 Clip / OpacityMask → 独立前景。

SeeThroughWindows 是三种材质共用的来源设置。开启表示实际后景，包括应用窗口和桌面；关闭表示只显示桌面来源。材质切换保留来源、主题、透明度、圆角和光学参数。

开启时 WallpaperGlassSurface 不加载壁纸、不绘制背景、Effect 为 null；实际背景由独立原生 GPU 表面折射。ComposedCrystalBackdrop 管理位置、缩放、透明度、原生宿主捕获标志及 UI 侧 Composition 宿主。GpuGlassRenderService 在一个 MTA 工作线程中为所有盒子共享 device/context：所有晶透都隐藏于截图时共享 Desktop Duplication；存在允许截图的晶透时，所有晶透改用 GpuWindowBackdropSource，防止可见晶透污染其他盒子的整屏输入。

逐窗口路径通过 BackdropWindowCatalog 的 Z 序快照，仅选取宿主后方、与外扩采样区相交且不属于 MiniC 的窗口。GpuCapturedWindow 使用 CreateForWindow 和 CreateFreeThreaded 帧池，在 GPU 中复制纹理；同显卡按 HWND 共享，离开需求集合后释放。GpuBackdropCompositor 按远到近的顺序合成预乘 alpha，桌面底图来自系统壁纸、上层叠加实际后方窗口（包括可捕获的 Explorer 桌面）。未就绪、拒绝捕获或尺寸不匹配的窗口区域挖成透明，不能误显示更深层窗口或壁纸。

激活盒子会改变后方窗口集合。0.60.64 的现场只读诊断发现 NVIDIA 全屏覆盖层使用 LWA_ALPHA=0，在屏幕上不可见，WGC 却返回 RGB=0、alpha=255 的纹理；此前它进入后方集合后覆盖整个背景。现读取 WS_EX_LAYERED 窗口的 GetLayeredWindowAttributes：全局透明度为零时不建立捕获会话、不绘制空缺；非零透明度同时乘到预乘 RGB 和 alpha，透明色键在双线性过滤前处理。读取失败保留像素 alpha，适用于 UpdateLayeredWindow；不按程序名、NOACTIVATE、黑色像素或分层样式一概排除。版本计算包含透明度、是否启用色键及色键值，避免透明属性变化而纹理版本不变时跳过重绘。切回 DXGI 的首次捕获超时同样保留已呈现画面。

两条路径都使用同一个 GpuGlassPipeline 执行位移和色散，再由 CreateSwapChainForComposition 与 ICompositorInterop.CreateCompositionSurfaceForSwapChain 输出。没有逐帧背景 CPU 回读、WPF 背景位图或 PrintWindow；壁纸只在变更时进行一次 CPU 解码和 GPU 上传。

关闭时 WallpaperTextureService 在后台读取并缓存 IDesktopWallpaper 壁纸；按物理坐标、DPI 和壁纸显示方式绘制。CrystalGlassEffect 对背景执行弧面折射、轻微色散和水波，前景图标文字不进入 shader。桌面来源是静态壁纸，不含桌面图标或动态壁纸。

## 光学与遮罩

NativeCrystal.hlsl 和桌面 CrystalGlass.hlsl 使用相同的 32 DIP 弧面、平滑有符号法线、角度、水波和 RGB 色散配方，中心保持清晰。实时 shader 以物理坐标取样，以 Scale 换算光学参数，因此预览缩放和 DPI 不拉伸玻璃边缘。捕获纹理的 alpha 未定义，必须由圆角 SDF 产生最终预乘 alpha，不能直接继承捕获 alpha 导致黑色。薄色、高光及明暗轮廓只由 WPF 前景绘制一次，不在原生 shader 叠加白底。

完整圆角遮罩限定绘制范围；GlassSurfaceFrame 提供方向高光和细暗轮廓，避免白色后景上边界消失。遮罩、轮廓与折射分别负责形状、识别及采样位移。系统 HostBackdropBrush 仍不能变换，本实现不再尝试把它作为自定义 shader 输入。

## 捕获与性能边界

- 实时晶透前景及原生背景使用 WDA_EXCLUDEFROMCAPTURE，防止下一帧采到自身产生反馈。相关截图、录屏可能不包含这些窗口；多个实时晶透盒子重叠时，彼此也不会进入对方背景采样。这是公开采样路线的明确代价，不代表普通透明窗口具备此限制。
- 每个收纳盒或组件的 `MaterialPreferences.HideFromCapture` 控制排除，默认值为 `true`。前景由 DeskGroupWindow 管理，覆盖三种材质和两种来源；原生背景只管理自己的标志，避免嵌套恢复把关闭开关重新改成隐藏。预览继承源宿主的配置。
- 关闭截图隐藏时保留现有宿主、交换链和捕获排除，后台切换到逐窗口来源，等待桌面底图和所需窗口的有效帧，绘制并提交后由 UI 回调恢复背景与前景可捕获。仅注册来源不代表已就绪。开启时先排除前景和背景，再更新后台策略；晶透和毛玻璃均不因截图开关重建材质宿主。策略回调带请求身份，快速往返和关闭会废弃旧请求。WindowCaptureExclusion 保存并恢复原标志，窗口隐藏或关闭时释放；只切换材质不取消前景的截图设置。
- 新建晶透的原生宿主在首帧提交前不显示；来源切换或尺寸变化尚未得到有效帧时不清空或发布空场景，保留已呈现的画面。显示/隐藏不改激活状态和 Z 序。
- 后方窗口持续 2 秒不给匹配尺寸的帧时释放该会话，按不可用区域直透并进入重试退避，避免截图开关无限等待；不把不可采集内容替换成黑底或更深层窗口。
- 逐窗口来源首次申请 GraphicsCaptureAccessKind.Borderless。未获系统许可时保留捕获提示边框，不能静默承诺无边框；不绕过受保护内容。此路径始终排除 MiniC 自身，多个盒子的前景不会递归进入晶透采样。
- 同一输出只创建一个采样会话并共享 GPU 副本。静态画面不复制指针更新帧，也不重复呈现；移动、缩放或光学参数变化时，用保留的 GPU 纹理重新采样。
- GpuGlassSwapChain 使用 FRAME_LATENCY_WAITABLE_OBJECT 和 MaximumFrameLatency=1，包括首帧在内先等就绪再绘制，ResizeBuffers 保留标志，结束关闭等待句柄。工作线程等鼠标唤醒、待绘制交换链的就绪信号，以及 WGC 的新帧事件；DXGI 来源保留 4 ms 检查，WGC 来源仅用 100 ms 超时复核窗口和重试，不使用固定 60 Hz 或逐窗口 DwmFlush。这些期限不是 FPS 声明。
- 标题拖动按物理坐标一次提交 X/Y；实时晶透先用完整的不可变位置快照绘制，随后同一次 UI 回调批量移动前景/背景 HWND 并 Present。不是跨 WPF 与 DWM 的原子显示保证；避免原来的两个坐标更新和位置/画面各自定时提交。UI 移动待完成时，不覆盖该表面的在途缓冲；新输入仅保留最新目标，松开/取消/关闭使旧移动失效。
- 同一显卡的 DXGI 捕获、D3D11 即时上下文绘制与 UI Present 使用同一互斥锁，符合 DXGI 不可与即时上下文并发调用的限制；GPU 工作线程不等待 UI 回调，UI 不做捕获、shader 绘制或等待垂直同步。
- 输出纹理在 GPU 内整屏复制，未宣称只复制盒内小区域。不同盒子仅渲染自身范围；没有背景像素 CPU 回读。
- 同一显卡多输出按物理区域绘制，支持负坐标和显示旋转；采样跨输出边缘时使用该输出的 clamp。跨显卡移动重建交换链；同时跨两块显卡的窗口只有主覆盖显卡区域折射，其余正常直透，未实现跨显卡纹理共享。
- 输出拓扑改变、锁屏/解锁、访问失效和设备移除会撤掉旧表面并退避重建。GPU 不可用时保留正常透明、薄色和边框，不用旧壁纸或黑色占位回退。受保护内容无法绕过 Windows 的捕获限制。

## 切换与验证

来源切换清空 WPF 壁纸场景、增加加载代际，已在途的旧结果不得回流到透明模式。加载失败保留已有有效桌面场景；逐窗口 GPU 合成独立取得桌面底图，不向 WPF 背景返回壁纸。预览多态复制材质参数与来源，前景只快照一次；真实盒和预览使用相同原生晶透服务。整盒 RenderTransform 的动画变化同步原生位置/大小，淡出同步原生 alpha。

208/208 Release 检查通过，覆盖 GPU 折射/DPI/旋转/拖动/取消/设备恢复、捕获标志、后方窗口排序、混合策略、GPU 图层/预乘 alpha/空缺区域、负坐标壁纸上传、原生纹理互操作及 HTTP 直连。包含真实已有桌面窗口的 WGC 会话与合成/折射像素验证、三轮捕获开关往返且不替换交换链、旧策略和关闭回调失效，以及隐藏 HWND 的分层透明属性、两个盒子激活前后来源选择、多个表面的全局透明度/像素 alpha/色键过滤。GPU 像素读取在单线程测试中完成；依赖共享渲染服务和壁纸缓存的测试隔离运行。不创建可见测试盒子、不操作鼠标、不更改用户窗口或配置；真实桌面像素仅在内存中检查、不保存截图。MINIC_RENDER_OUTPUT 仍仅导出合成图案。现场截图/录屏、黑窗修复观感、拖动帧率和多屏仍需人工验收，不能以测试通过宣称现场问题已经验证消失。

## 官方依据

2026-10-22 后台性能调整：`BackdropWindowSnapshot` 将窗口层级与像素帧率分开管理，静止时 100 ms 缓存、拖动时 16 ms 缓存。普通带标题窗口的客户区按 DPI 内缩 32 DIP 后可作为保守遮挡范围；分层、透明、自定义区域、无重定向位图和系统后景材质不参与。只在单个上方窗口内部完全覆盖盒子时暂停渲染，多个部分遮挡不推测覆盖。未找到宿主意味着隐藏/最小化/虚拟桌面隐藏，停止后景工作；移动请求绕过静态遮挡判定，避免停止拖动提交。

暂停时保留已提交的交换链画面和外观，释放无需求的 WGC/DXGI 来源，不把画面清黑；恢复时使纹理版本失效并等待有效来源后再呈现，防止会话重建后的版本号重复导致保留旧画面。后方窗口的普通不透明内部已覆盖整个外扩采样区时，停止枚举更深层来源，仍保留更近的透明窗口及远到近的合成顺序。

`GpuCapturedWindow` 的 CreateFreeThreaded 帧池通过 FrameArrived 回调唤醒 GPU 线程，回调本身不操作 D3D 上下文；一次最多取帧池中最新的两帧并丢弃旧帧，释放时取消事件订阅。没有改变折射 shader、窗口样式或帧率上限，也没有特判 VMware 进程名。回归不等同于真实 VMware 帧率验证。

- [FrameArrived 新帧通知](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.framearrived)
- [GetWindowRgn 自定义窗口区域](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrgn)
- [WPF 窗口透明](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency)
- [WPF OpacityMask](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/opacity-masks-overview)
- [HostBackdropBrush](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush)：系统后景像素不能读回。
- [Composition effects](https://learn.microsoft.com/en-us/windows/uwp/composition/composition-effects)：效果支持需同时核对源画刷约束；本次通过真实工厂验证识别限制。
- [Desktop Duplication](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api)
- [Composition swap chain](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforcomposition)
- [ICompositorInterop.CreateCompositionSurfaceForSwapChain](https://learn.microsoft.com/en-us/windows/win32/api/windows.ui.composition.interop/nf-windows-ui-composition-interop-icompositorinterop-createcompositionsurfaceforswapchain)
- [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
- [GetLayeredWindowAttributes](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlayeredwindowattributes)
- [SetLayeredWindowAttributes](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes)
- [IGraphicsCaptureItemInterop.CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)
- [CreateFreeThreaded](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded)
- [IsBorderRequired 与系统无边框授权](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired)
- [DeferWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-deferwindowpos)
- [GetFrameLatencyWaitableObject](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-getframelatencywaitableobject)
- [SetMaximumFrameLatency](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-setmaximumframelatency)
- [Direct3D 11 与 DXGI 多线程约束](https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-render-multi-thread-intro)

修改 shader 源后运行 tools/Compile-CrystalShader.ps1，同时更新 WPF ps_2_0 和原生 vs_5_0 / ps_5_0 字节码。原生 .cso 以明确 LogicalName、WithCulture=false 嵌入，避免 ps 文件名被构建系统当成语言资源。
