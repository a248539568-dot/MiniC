# 真实背景毛玻璃试验结果

日期：2026-09-18；系统 Windows 11 Build 26100。用户要求先试验，未修改正式收纳盒窗口或用户布局。

## 已完成的试验

独立 WPF 程序显示一个带高对比度栏杆、文字和色块的背景窗口，以及两个可拖动玻璃小窗：A 使用与 MiniC 类似的 AllowsTransparency 分层窗口，B 使用非分层窗口。两个小窗均可切换系统背景、实验磨砂和关闭背景，关闭对照窗口可同时结束所有小窗。

试验代码和构建位于 `.local/glass-lab/`，启动脚本为 `Start-GlassLab.ps1`，不进入产品构建或 Git 提交。此程序没有桌面接管、文件操作、布局迁移或进程终止逻辑。

## 观察结果

| 路径 | API 结果 | 真实窗口观察 |
| --- | --- | --- |
| DwmSetWindowAttribute + DWMSBT_TRANSIENTWINDOW | 两种 HWND 均返回 S_OK | 本机试验中显示不透明灰色；激活后也未获得预期背景磨砂，不判定可用 |
| 关闭系统背景 | 调用成功 | A 小窗透出清晰的栏杆及背景文字，确认背景对照本身可见 |
| SetWindowCompositionAttribute + ACCENT_ENABLE_ACRYLICBLURBEHIND | 两种 HWND 均返回 true | 栏杆和背景文字被模糊，小窗自己的文字与按钮清晰；绿色背景切换为紫红背景后玻璃同步变色 |
| 实验磨砂的激活/失活与移动 | 通过真实鼠标操作验证 | A 激活并拖动到另一块背景后，模糊颜色随位置变化；B 失活时仍保持磨砂，未退回不透明灰色 |

截图由 Windows.Graphics.Capture 实际窗口检查获取，已在当前任务中展示；不是 WPF RenderTargetBitmap 生成的静态材质模拟。仅观察了本机和简单对照窗口，未对帧率、不同显卡、系统版本、多屏缩放、节电/高对比度、实际收纳盒文件交互作完整验收。

## 技术范围与下一步

- 实验通过的兼容路径使用未公开稳定承诺的 Accent Policy 接口。仅作为能力验证，不能因本机成功就无回退地写入正式默认实现。
- 结构与调用参数核对 TranslucentTB 的现有实现：`Common/undoc/user32.hpp` 和 `TranslucentTB/taskbar/taskbarattributeworker.cpp`。试验独立编写 Win32 互操作，不复制其业务代码。
- 当前实现的是参考图下方的背景毛玻璃，不含上方的边缘折射和色散；透明基线也不等于液态折射玻璃。
- 正式接入前应独立封装材质生命周期，检测平台与调用失败回退，避免在每次移动中重设 Accent；协调透明圆角、失活桌面层级和拖动快照，防止方形边缘、变白或闪烁。
- 应先在真实收纳盒的隔离实例中验证该路径，再考虑版本发布。本次保留试验窗供用户比较，未生成新 MiniC 安装包或推送 GitHub。

## 资料

- [DWM_SYSTEMBACKDROP_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [DwmExtendFrameIntoClientArea](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea)
- [Acrylic](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)
- [TranslucentTB Win32 结构声明](https://github.com/TranslucentTB/TranslucentTB/blob/release/Common/undoc/user32.hpp)
- [TranslucentTB 合成调用](https://github.com/TranslucentTB/TranslucentTB/blob/release/TranslucentTB/taskbar/taskbarattributeworker.cpp)
