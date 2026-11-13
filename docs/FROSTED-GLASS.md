# 毛玻璃材质（0.60.62）

2026-10-18：当前菜单名称改为“液态玻璃”，保留此文描述的背景模糊路径，前景统一使用柔和阴影。现行三种材质规则见[材质实现](MATERIALS.md)。

## 显示链路

宿主配置 → FrostedGlassSurface → 背景扩散 → 主题薄色 → 柔和窄高光 → 完整圆角裁剪与遮罩。图标、文字、标签和控件位于独立前景，不参与模糊。

默认模糊半径 24 DIP，范围 0–40。主题薄色 alpha 为 0.18 × 不透明度，至少保留 1/255，保证透明窗口连续接收点击。材质透明度不降低原生模糊层 alpha，防止未模糊后景混入。

## 两种来源

关闭透出：WallpaperGlassSurface 按屏幕坐标绘制缓存壁纸，BlurEffect 只处理背景。输入外扩模糊半径，最终由公共 Clip / OpacityMask 裁剪，防止模糊内沿透明漏边。桌面来源是静态壁纸，不含图标或动态壁纸。

开启透出：CompositionBackdropHost 创建无激活、输入穿透的原生表面，放置在 WPF 前景后面。HostBackdropBrush 提供实际后景，CompositionGaussianBlur 增加系统高斯模糊，标准差按半径 / 3 和 DPI 缩放。WPF 背景子层停止绘制壁纸和应用 BlurEffect，薄色、边框和交互前景仍保持。

原生后景创建或对齐失败时保持正常透明，记录原因；不启动截图、不使用旧壁纸覆盖后景。此状态暂时缺少模糊，不声称材质完整。系统透明度设置及电源策略可能影响原生后景。

## 资源与预览

三种材质共用 GlassMaterialSurface 和 SeeThroughWindows。来源切换拒绝旧壁纸加载，删除捕获图像/坐标属性；隐藏、切换及关闭释放原生材质宿主、画刷和效果图。原生源画刷保留到效果释放。

HideFromCapture 与毛玻璃效果独立：DeskGroupWindow 统一管理前景标志，ComposedFrostedBackdrop 只管理自己的背景 HWND。关闭截图隐藏保留 HostBackdropBrush 和高斯图，不嵌套恢复前景标志；桌面来源和纯色也遵守该窗口策略。

拖出预览只快照前景，复制材质参数和来源；原生毛玻璃对齐实际卡片，跟随缩放与淡出，关闭释放资源。预览原生失败也保持直透，不回退捕获。

## 验证

无界面测试覆盖真实壁纸加载路径、三档 DPI 下的扩散、大色块保留、完整边缘、前景清晰、来源切换、预览和原生高斯效果图创建。使用不含 WS_VISIBLE 的隐藏渲染句柄，不自动启动应用或操作鼠标。实际系统后景、激活/拖动、多屏及帧率待人工验收。

## 官方依据

- [CreateHostBackdropBrush](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush)
- [Direct2D Gaussian blur](https://learn.microsoft.com/en-us/windows/win32/direct2d/gaussian-blur)
- [IGraphicsEffectD2D1Interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.effects.interop/nn-windows-graphics-effects-interop-igraphicseffectd2d1interop)
