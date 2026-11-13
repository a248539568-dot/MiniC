# 窗口层晶透折射调研

日期：2026-10-14。本次阅读微软官方文档、微软项目讨论及社区源码；没有运行第三方程序、创建可见测试窗口或修改应用渲染实现。

## 结论

可以在自定义窗口的原生 GPU 渲染层实现实时后景折射。已有社区原型使用 DXGI Desktop Duplication、Direct3D 11 像素着色器和 DirectComposition 完成该链路。

目前查阅的公开窗口材质接口没有提供一个直接开启液态玻璃折射的属性。正常透明、Acrylic 模糊、折射分别需要不同的处理。DirectComposition 可以承载和合成 GPU 输出，但不自动提供后方窗口纹理。

## 官方接口及边界

| 接口 | 已核对的能力 | 对晶透的意义 |
| --- | --- | --- |
| DWM_SYSTEMBACKDROP_TYPE | 系统 Mica、Desktop Acrylic、Mica Alt | 没有自定义折射类型 |
| CreateHostBackdropBrush | 窗口绘制之前的后景来源；应用不能读回像素 | 可用于受支持的系统效果，不能假定可以交给任意 shader |
| CompositionEffectBrush | 使用系统内建 shader 编译受支持效果；不能指定自定义 shader | 换成 Composition 并不自动获得任意光学处理能力 |
| Direct2D Displacement Map | 按第二张图的通道偏移第一张图的采样位置 | 提供折射基础处理，不负责取得后方画面 |
| DXGI Desktop Duplication | 提供桌面输出的 GPU surface | 是公开的背景纹理来源之一，仍属于采样 |
| CreateSwapChainForComposition | 把 Direct3D 输出送入 DirectComposition，在窗口中合成 | 可用于原生透明折射背景层 |

微软 WindowsCompositionSamples 的 #294 中，贡献者明确解释：BackdropBrush 使用特定优化，无法保证变换结果，因此禁止该组合。这与本项目上一轮实际效果图工厂返回 `Backdrop brushes cannot be transformed` 一致；它限定的是这条接口路径，不是 Windows 上所有自定义 GPU 渲染。

## 原生折射原型

[TomLo-FStack/win-liquid-glass](https://github.com/TomLo-FStack/win-liquid-glass) 明确标注为 Prototype。

已阅读 README 和 [liquid_glass.cpp](https://github.com/TomLo-FStack/win-liquid-glass/blob/master/liquid_glass.cpp)，源码包含：

- DuplicateOutput / AcquireNextFrame 取得桌面 GPU 图像。
- CopyResource 将背景留在可供 shader 采样的 GPU 纹理中；该路径不做背景 CPU 像素回读。
- Direct3D 11 像素着色器执行位移采样、RGB 色散和高光，输出预乘 alpha。
- CreateSwapChainForComposition、DCompositionCreateDevice、SetContent 将结果合成到透明窗口。
- WDA_EXCLUDEFROMCAPTURE 排除自身窗口，避免重复采样产生视觉反馈。

该例证实了原生渲染实现路线，不证明 MiniC 已经支持它，也不证明目标机器上的帧率。原型说明尚有 DPI 对齐、多屏及整屏 GPU 复制的问题；源码中也有过时的局部复制注释，实际读取路径使用整屏 CopyResource，不应据注释宣称只复制小区域。

截图排除是实际产品取舍：官方说明 WDA_EXCLUDEFROMCAPTURE 会让窗口不出现在相关捕获结果中。因此不能直接照搬后再假定用户截图、录屏仍能看到盒子。必须在背景来源设计时处理这一点。

## 更靠近 DWM 的候选

[selastingeorge/Win32-Acrylic-Effect](https://github.com/selastingeorge/Win32-Acrylic-Effect) 的 AcrylicCompositor.h / .cpp 使用共享窗口及桌面视觉：

- DwmpCreateSharedThumbnailVisual，经 dwmapi.dll 序号 147 获取。
- DwmpCreateSharedMultiWindowVisual / DwmpUpdateSharedMultiWindowVisual，经序号 163 / 164 获取。
- 在 DirectComposition 视觉树中应用高斯模糊、饱和度和坐标变换。

它提供了不依靠应用逐帧 CPU 截图来取得后景视觉的线索，但使用未公开的 DWM 接口。已阅读的实现是 Acrylic，并没有证实任意非线性折射或自定义 shader 输入。即使只面向当前用户的 Windows 版本，也应先做独立实验核对接口和真实输出。

公开 DwmRegisterThumbnail / DWM_THUMBNAIL_PROPERTIES 支持来源裁切、矩形缩放与透明度，文档没有提供任意位移着色器入口。CreateSurfaceFromHwnd 针对 layered window 自身的光栅内容，不是任意后方场景的纹理获取接口。

DWMBlurGlass 的 README 明确说明自定义模糊来自逆向 DWM；其 Aero reflection / parallax 名称也不能证明对实时后景做了液态玻璃位移。检索到的 Liquid_Glass_UI_For_Window 实际是 DWMBlurGlass 使用说明，不能将名称当作另一个折射实现。

## MiniC 的建议边界

保持 WPF 前景负责图标、文字、标签、输入与文件业务。独立原生 GPU 背景层负责晶透折射、最终圆角遮罩和透明合成；背景源可分别接入桌面壁纸与实时后景。折射仅处理背景，不能模糊或扭曲前景。

公开路线需要先解决 GPU 背景来源、自身反馈、截图行为、多屏/DPI、合成 alpha 和资源生命周期。未公开 DWM 路线单独验证，不能根据毛玻璃示例假定液态玻璃已经可用。先验证原型，再接入生产窗口与拖出预览；不重新采用 GPU→CPU→WPF 的背景位图循环。

遮罩限定效果的形状和范围，不能单独改变后方像素的采样位置。增加线程可以避免阻塞 UI，但不能让缺少背景输入的 shader 自动获得折射。

## 后续实现（0.60.59）

已独立实现 DXGI Desktop Duplication → GPU 纹理 → D3D11 refraction shader → Composition swap chain，并接入共享盒子宿主和拆出预览。没有恢复旧 GPU→CPU→WPF 循环，也没有使用未公开 DWM 序号接口。通过本机 Windows SDK 的 windows.ui.composition.interop.h 核对 ICompositorInterop ABI，隐藏 HWND 已实际接入交换链；原生 shader 的位移、alpha、三档缩放及旋转坐标已做真实 GPU 像素检查。

实际 DXGI 隐藏客户端测试验证一个输出会话服务两个表面，显示拓扑重建后恢复、关闭后释放。WDA_EXCLUDEFROMCAPTURE 已作为明确产品限制写入 README，相关截图/录屏以及其他实时晶透盒子的采样将排除这些窗口。没有执行可见桌面测试；真实观感、连续拖动及多屏 FPS 尚未测量。实现细节和跨显卡限制见 [晶透链路](CRYSTAL-WALLPAPER.md)。

## 引用链接

- [系统窗口材质枚举](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [HostBackdropBrush](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush)
- [CompositionEffectBrush：不能指定自定义 shader](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositioneffectbrush)
- [微软关于 BackdropBrush 变换限制的回复](https://github.com/microsoft/WindowsCompositionSamples/issues/294)
- [DirectComposition 基本概念：不提供位图内容](https://learn.microsoft.com/en-us/windows/win32/directcomp/basic-concepts)
- [Direct2D Displacement Map](https://learn.microsoft.com/en-us/windows/win32/direct2d/displacement-map)
- [Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api)
- [CreateSwapChainForComposition](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforcomposition)
- [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
- [DWM_THUMBNAIL_PROPERTIES](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ns-dwmapi-dwm_thumbnail_properties)
- [CreateSurfaceFromHwnd](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-idcompositiondevice-createsurfacefromhwnd)
