# 收纳盒原生毛玻璃接入

日期：2026-09-18。已构建本地 0.59.3 安装包，沿用 Frosted 设置标识，无需迁移用户布局。安装包与校验文件位于 dist，未安装或推送 GitHub。

## 实现

- NativeGlassBackdrop 封装已在本机验证的 SetWindowCompositionAttribute / Accent Acrylic 兼容路径。Windows 11 Build 22000+、系统透明效果开启且非高对比度时尝试启用，失败回退到原有雾面画刷；该兼容 API 没有公开稳定性承诺，需要继续做版本兼容验证。
- 应用到真实 DeskGroupWindow：活动标签的材质、主题和透明度发生变化时更新原生效果；原生成功时只叠很薄的主题色，避免白色底板盖住模糊。取消原生状态后恢复完整雾面颜色。运行时状态不序列化。
- 稳定 HWND 和色调复用已应用状态，位置改变不会重新调用 Accent。隐藏、退出毛玻璃样式、切换标签和关闭窗口同步释放或更新。系统设置/合成改变时重新核对。
- 原生 Accent Acrylic 在分层 WPF HWND 上不可靠地遵守 WPF 内容 Clip 或 SetWindowRgn，真实截图确认会产生矩形光晕；正式毛玻璃路径已移除该渲染依赖，改为窗口内部壁纸模糊层，避免圆角外溢。
- 原生预览在同一渲染事件内更新抓取点、比例和 HWND 尺寸，跟随合并裁剪缩小。返回源标签时同步停用原生效果；完整盒拖动关闭原源盒模糊并隐藏内容，防止背景残留和回退底板闪现。

0.59.4 更新：预览的跟手、缩放及玻璃更新统一为一个渲染事件；原生位置和大小使用单次 SetWindowPos 提交，静止后停止空转。晶透使用稳定窗口和固定尺寸的壁纸着色器层，通过 RenderTransform 缩放，避免每帧重新分配绘制表面。Windows 原生毛玻璃外轮廓仍受系统圆角策略影响，WPF 外壳、快照裁剪和晶透着色器统一以原始 18 DIP 圆角为基准。

0.59.5 历史实现：按共享 18 DIP 设置 HWND region，但 2026-09-19 实际截图确认 Acrylic 忽略该视觉裁剪。GetWindowRgn / EqualRgn 通过只能证明 region 已设置，不能证明背景圆角正确。标题和文件名去除额外 BlurRadius=3 的阴影，按 Display/Grayscale 清晰绘制；按下按钮时不再异步转移键盘焦点。

## 验证

- Release 构建零警告零错误，65 项测试通过。包含调用去重、切换关闭、失败回退、Dispose、原生/回退颜色、原生预览范围及隐藏后的清理。
- 收纳盒组织、拖动预览、文件失焦改名及启动自检通过。
- `.local/glass-integration/` 引用当前实际 DeskGroupWindow，使用虚拟名称和示例图标，不连接真实文件业务、不接管桌面。Windows.Graphics.Capture 检查确认背景栏杆与文字被模糊、前景清晰、纯色切换清理、恢复毛玻璃及小预览不模糊外围区域。
- 已在 Windows 11 Build 26100 检查。完整多屏混合 DPI、不同 GPU/系统版本、节能状态和长期拖动性能仍待现场回归。圆角由系统合成器参与绘制，与 WPF 模拟材质可能存在细微差异。
- 已生成本地 0.59.3 安装包；没有更换运行中的正式安装版或推送 GitHub，也未实现清透边缘折射与色散。

前置资料见 [玻璃调研](GLASS-RESEARCH-2026-09-18.md) 和 [独立试验结果](GLASS-EXPERIMENT-2026-09-18.md)。

## 2026-09-19 后续修复（未打包）

- 恢复 DWMWCP_ROUND，避免 DONOTROUND 让 Acrylic 完全直角。当前实际效果是系统小圆角，尚未与其余材质的 18 DIP 对齐，明确保留为未解决项。
- WPF 背景及透明标题栏保留最低 1/255 alpha，避免 layered HWND 在透明像素处鼠标穿透；背景透明度仍不影响文字/图标透明度。
- 数量徽标独立使用高对比前景及底色，随主题切换通知更新。
- 隔离实际窗口验证数量 4 清晰显示，90% 与 100% 透明度均能从标题空白处拖动，100% 时折叠按钮可用。91/91 测试、收纳盒组织与拖动预览自检通过。
- 已试验 DwmEnableBlurBehindWindow 区域（导致白色实底）、非分层 HWND、Accent blur 状态、取消 frame 扩展；均未实现一致圆角，试验代码未保留。
- 官方依据：https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners 与 https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features 。

## 2026-09-19 圆角统一方案

不再尝试用 HRGN 裁剪 Acrylic。三种材质统一使用通用圆角，毛玻璃由内部模糊层、晶透由壁纸折射层、纯色由纯色层绘制；WPF 外壳、标题、背景层和预览基础几何共享 GroupGeometry。

## 材质参数补充

托盘外观面板支持实时圆角（0-32 px）、按材质独立透明度、晶透透出窗口开关、折射强度和折射角度。晶透透出模式会隐藏壁纸折射层并移除有色底层，仍保留最低 alpha 作为窗口命中面；关闭后恢复壁纸折射。参数存入 LayoutState.Appearance，兼容旧布局默认值。

## 2026-09-20：替换遗留 Accent 路径

以上原生 Acrylic 的描述仅记录历史试验。实际隔离收纳盒截图证实：打开透出后，Accent 在分层 WPF HWND 上绘制矩形，即使 WPF Clip 与 HRGN 已设为 24 DIP 圆角。当前产品和拖动预览已移除 `NativeGlassBackdrop`、原生调色与其独立背景层。

现在关闭透出时绘制盒内壁纸模糊；开启时用 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 暂时排除当前窗口，以 `CopyFromScreen` 读取背后的合成图像，再立即恢复捕获状态。图像只在内存里更新，由 WPF `BlurEffect` 与父层同一圆角裁剪；雾面渐变、纹理、透明度和标题控件保持一致。系统不支持排除或采样失败时回退到壁纸模糊。当前在 Windows 11 26100 的真实窗口上验证了开关、拖动、列表及折叠；不同显卡、DPI、远程桌面和屏幕共享场景仍需兼容性检查。采样频率为 120 ms，背后动态内容更新不会像系统 Acrylic 那样逐帧连续。

官方 API：[SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)。

## 2026-09-20 合成遮罩验证（未打包）

独立真实收纳盒在 Windows 11 Build 26100 验证：宿主背景画刷 `CreateHostBackdropBrush` 位于独立的无重定向位图窗口内，`DWMWA_USE_HOSTBACKDROPBRUSH` 明确打开；`CompositionRoundedRectangleGeometry` 对背景源自身作动态圆角裁剪。前景仍由现有分层 WPF 收纳盒负责，文字、图标保持清晰。拖动、切换纯色/晶透再回毛玻璃、背景切换均已实窗观察，没有旧 Acrylic 绕过裁剪的矩形角。普通 `SetWindowRgn` + Acrylic 即使成功返回也仍出现方角，已在隔离窗口排除。

默认模糊度 18 使用系统合成路径，不做 `CopyFromScreen`；用户修改模糊度后切回原有 WPF 图像模糊取样，保证 0–40 的参数行为。Windows 11 以下或合成接口失败也回退到取样。合成状态仅存在窗口实例中，不写布局配置；三材质依然各自保存参数。当前版本号 0.60.18 的安装包仍为此前构建，**不含本段改动**。

依据：[DWMWA_USE_HOSTBACKDROPBRUSH](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)、[CreateHostBackdropBrush](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush)、[CompositionRoundedRectangleGeometry](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositionroundedrectanglegeometry)、[SetWindowRgn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowrgn)。

最终验证补充：2026-09-20 自包含发布目录重新构建，91 项测试和收纳盒组织、拖动预览、启动自检全部通过。隔离真实窗口观察了圆角、拖动和材质切换；创建失败补齐目标释放和接口缺失回退，重新激活时重新对齐背景层。发布目录位于 .local/publish-mask，尚未生成本次安装包。

0.60.19 已构建自包含安装包 dist/MiniC-Setup-v0.60.19.exe，发布版 91 项测试与三项自检通过；未自动安装或推送。
