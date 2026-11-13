# 材质实现

更新：2026-10-18。材质、水纹及以下前景阴影调整已包含在 0.60.74。

| 菜单名称 | 配置标识 | 关闭透出后方窗口 | 开启透出后方窗口 | 透明度 |
| --- | --- | --- | --- | --- |
| 液态玻璃 | Frosted | 缓存桌面壁纸模糊 | 系统后景与高斯模糊 | 独立保存 |
| 折射玻璃 | Crystal | 缓存桌面壁纸水纹折射 | GPU 实时后景水纹折射 | 独立保存 |
| 纯色 | Solid | 完全不透明主题色 | 不支持，菜单禁用 | 固定为 1 |

桌面专用来源是静态壁纸，不包含桌面图标或动态壁纸。实时来源保留原有 DXGI / WGC 选择、资源共享和捕获设置；不可捕获区域仍遵守原有可用性规则。

## 状态与绘制

菜单通过 WindowAppearanceService 更新宿主配置，DeskGroup 负责绑定，GlassMaterialSurface 负责背景、主题薄色、边框与最终圆角遮罩。窗口内容位于独立前景，预览复用相同材质实现。

SolidOpacity 已删除，OpacityFor(Solid) 恒为 1，纯色透明度设置不生效；SolidMaterialSurface 即使收到带透明 alpha 的画刷或 UseWallpaper=true，也强制不透明并停用壁纸子层。透出来源偏好保留给玻璃，切回玻璃时恢复，不因选纯色被清空。

## 折射与水纹

CrystalGlass.hlsl 和 NativeCrystal.hlsl 使用相同配方：窄边缘弧面叠加覆盖整窗的两组连续周期水纹。位移按 DIP 计算，实时路径换算物理像素；WPF 将均匀系数预先计算为常量，以满足 ps_2_0 的指令预算，参数变化即时更新系数。

周期抛物线在周期边界处的值与一阶导数连续，替代旧 frac 锯齿波。一次清晰采样保留背景纹理细节与预乘 alpha，不用模糊冒充折射。强度为零时关闭所有折射，水纹强度为零时仍保留边缘弧面。

水纹是随背景取样变化的光学形变，没有独立时间轴或持续动画线程。前景文本、图标、输入框不会进入背景采样器。轮廓由 GlassSurfaceFrame 独立绘制，避免白色后景下丢失边界。

## 前景对比

GroupMaterialBrushes.ContentShadow 统一提供冻结的 WPF DropShadowEffect：5 DIP 模糊半径、1 DIP 向下偏移、60% 黑色阴影。文字和图标都使用这套柔和下投影，白色桌面或近白色玻璃背景下仍能看清白色前景，同时避免阴影贴边形成轮廓；阴影不改变前景颜色，也不把图标送入背景采样。旧 ContentContrastEffect、HLSL、字节码资源和编译步骤已删除。

组件按钮的字符串标签通过统一模板应用阴影，显式内容保护自己的文字或图标，避免重复处理。玻璃窗口自身用于识别边界的 GlassSurfaceFrame 边框保留。日程中心的选中、悬停和分隔语义颜色及表单独立底色保持原规则；修改效果不重建正文或清空草稿。

WPF 官方 API：[DropShadowEffect](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.effects.dropshadoweffect?view=windowsdesktop-10.0)。

## 验证

本次验证包含明暗背景、100%/200% DPI、阴影渐变与向下偏移、白底文字和白底图标阴影、前景像素保留及远处背景不变。已检查日程中心、天气明暗离屏图。此前材质修改的测试还包含合成纹理原生 GPU 检查；本次只改 WPF 前景效果，未重跑原生 GPU 捕获测试。

已查看 artifacts/material-rewrite、artifacts/planner-responsive 与 artifacts/weather-responsive 的渲染图。未显示测试盒子、启动应用或操作鼠标；未执行现场 WGC 采集或实测拖动帧率。
