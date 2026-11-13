# 真实背景玻璃效果调研

后续：[独立窗口试验结果](GLASS-EXPERIMENT-2026-09-18.md)。本机系统 Acrylic 路径返回成功但显示实色，兼容 Accent 路径已观察到真实背景模糊；尚未接入正式收纳盒。

日期：2026-09-18。依据用户提供的两张玻璃小窗对比截图及 Microsoft / MDN 官方文档。本文是实现路线调研，尚未证明原生背景效果与现有收纳盒宿主兼容，不代表已完成产品实现。

## 参考效果

| 参考 | 可观察特征 | 应有的渲染处理 |
| --- | --- | --- |
| 上方清透玻璃 | 中心保留树林、栏杆的细节；圆角边缘扭曲背景线条，有窄高光 | 背景采样、边缘法线位移、少量模糊、克制的色散、边缘高光 |
| 下方磨砂玻璃 | 背后枝叶明显模糊，整体偏暗，文字和按钮清晰 | 背景模糊、色调/亮度控制、细微噪声、边缘高光；前景内容不参与模糊 |

截图中的“20px 倒角、21px 玻璃厚度、1px 模糊、0.04 色散、0.35 边缘光”可作为上方示例的视觉调参参考，不应直接当作 WPF 或系统 Acrylic 的同名参数；仅凭截图不能验证示例底层实现。

## 当前项目与差距

- `DeskGroupWindow` 使用 `AllowsTransparency=true` 的透明 WPF 顶层工具窗口。
- `GroupMaterialBrushes` 的 Frosted 是雾面底色与纹理；`GroupMaterial` 的 BlurEffect 只模糊自己的反光层，没有采样或模糊真实桌面。因此当前效果无法表现树林折射或真实背后内容的毛玻璃。
- `GroupDragPreviewWindow` 通过 RenderTargetBitmap 捕获 WPF 内容。原生合成器背景不一定会进入该快照，新材质接入后必须重新设计拖动预览的表现，防止拖动瞬间变色或变成不透明底板。
- 本机 Windows 版本为 10.0.26100，满足下面列出的 Windows 11 原生 API 最低版本；项目仍声明支持 Windows 10，不能把本机支持等同于所有用户支持。

## 技术路线比较

### 系统磨砂：Desktop Acrylic

Windows 11 Build 22621 起，`DwmSetWindowAttribute` 的 `DWMWA_SYSTEMBACKDROP_TYPE` 可选 `DWMSBT_TRANSIENTWINDOW`；Microsoft 当前将其映射为 Desktop Acrylic。该路线适合先验证下方的背景磨砂，但系统决定材料外观，不能保证像素级复刻截图或任意自定义模糊参数。

不要用 Mica 替代：官方说明 Mica 是结合壁纸和主题的**不透明**材质，不等同于透出实时窗口内容的磨砂玻璃。`DwmEnableBlurBehindWindow` 官方明确说明 Windows 8 起不再产生原来的模糊效果，不应作为 Windows 10/11 的可靠解法。

Acrylic 官方设计说明包含失活、高对比度、透明效果关闭、节电及设备能力导致的实色降级。不同宿主/API 路径的行为仍需实测，不能承诺桌面常驻且不激活的收纳盒会始终保持透明。

### 原生自定义：Windows Composition + HostBackdrop

`Compositor.CreateHostBackdropBrush` 提供窗口绘制之前的背景来源；Windows 11 Build 22000 起，`DWMWA_USE_HOSTBACKDROPBRUSH` 为非 UWP HWND 启用该能力。可作为背景模糊合成的候选路径。

需要重点验证：WPF 与 Composition 的 airspace、透明 HWND 的合成方式、非激活工具窗口、鼠标命中、圆角裁剪、多屏 DPI 和资源释放。Microsoft 的 WPF 接入例子使用 HwndHost；不能简单把子 HWND 盖在现有图标控件上，因为合成内容可能遮挡 WPF 内容。

HostBackdrop 的像素不能由应用读回，也受用户设置及电源策略控制。不能假定该画刷可以直接导出给 WPF PixelShader 当纹理。

### 清透折射：GPU 背景纹理 + 位移效果

上方效果需取得合法可用的背景纹理，再按圆角矩形的边缘距离/法线构造位移图：中心接近零位移，边缘逐渐增加偏移，随后叠加高光。Direct2D Displacement Map 提供双输入图像位移基础能力；RGB 分离采样可用于少量色散。Direct2D 位移本身不负责获取桌面背景。

若只采样壁纸，可通过壁纸纹理实现稳定折射，但不会反映其他窗口、桌面图标和动态壁纸。若使用屏幕捕获，需要处理自身窗口反馈、捕获排除、刷新频率、多屏以及 GPU 资源同步，不能每帧 CPU 截屏后上传。先明确采样范围再选后端，避免把壁纸模拟宣传为真实桌面折射。

### 网页 SVG / CSS

`backdrop-filter` 处理页面元素背后的内容，`feDisplacementMap` 按第二张图的通道位移采样输入图像，可以制作浏览器中的玻璃实验。它们不会自动得到浏览器或 WebView 外面的 Windows 桌面，因此引入 WebView 并不能独自解决本项目的真实背景玻璃需求。

## 建议实施顺序与验收

1. 独立原生材质试验窗：在当前系统分别检查 Acrylic 与 HostBackdrop，并比较激活/失活状态、桌面工具窗口样式和普通应用覆盖，记录 API 结果与真实合成截图。
2. 通过后封装背景渲染服务，保留现有文件业务与标签逻辑；材质回退继续使用纯色/渐变。前景文本、图标、选择框不参与模糊或折射。
3. 验证拖动、缩放、合并快照的交接，不能重新出现变白、尺寸闪烁、双层窗口或图标。
4. 清透折射单独做背景纹理来源与 GPU 位移原型，明确“实时桌面”还是“仅壁纸”。未完成采样和合成验证前，不接入正式默认材质。

优先完成真实背景磨砂再做清透折射更稳妥。两种材质应分别命名和设置，不把增加白色透明遮罩当作实现完成。

## 已查阅的官方资料

- [Acrylic：层次组成、失活与系统设置回退](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)
- [DWM_SYSTEMBACKDROP_TYPE 与最低系统版本](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [DWMWA_USE_HOSTBACKDROPBRUSH](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)
- [CreateHostBackdropBrush：采样范围与不能读回像素](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush?view=winrt-26100)
- [WPF 接入 Windows Composition](https://learn.microsoft.com/en-us/windows/uwp/composition/using-the-visual-layer-with-wpf)
- [桌面 Visual Layer 的 airspace 和 DPI 限制](https://learn.microsoft.com/en-us/windows/uwp/composition/visual-layer-in-desktop-apps)
- [DwmEnableBlurBehindWindow 的 Windows 8+ 限制](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmenableblurbehindwindow)
- [Mica 为不透明材料](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/apply-mica-win32)
- [Direct2D Displacement Map](https://learn.microsoft.com/en-us/windows/win32/direct2d/displacement-map)
- [MDN backdrop-filter](https://developer.mozilla.org/en-US/docs/Web/CSS/backdrop-filter)
- [MDN feDisplacementMap](https://developer.mozilla.org/en-US/docs/Web/SVG/Reference/Element/feDisplacementMap)
