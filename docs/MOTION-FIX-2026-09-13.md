# 收纳盒动画流畅性修复

日期：2026-09-13。基于 0.58.7 源码修复，已构建为 0.58.8 安装包（dist/MiniC-Setup-v0.58.8.exe）；38/38 单元测试和发布版 9/9 隔离自检通过，未执行安装。

## 已确认原因与修复

- `GroupDragPreviewWindow.Follow` 每个鼠标事件重启 100ms PointAnimation，预览持续追赶而非直接跟手。改为 CompositionTarget.Rendering 合并一帧内输入，只提交最后位置；松手前刷新最后输入并解除逐帧订阅。
- 预览每次更新都重设透明窗口 Left/Top/Width/Height，反复分配绘制表面。改为带 128 DIP 缓冲、手势期间只扩不缩的视口；范围足够时不修改窗口尺寸。
- 原盒透明度随每个 MouseMove 重启动画，目标提示也反复覆盖内容动画。改为状态变化时才启动；整盒移动时隐藏原卡片绘制，避免与跟手预览出现双轮廓。
- 松手后无论是否已在最终位置都等待 260ms，再追加 260ms 内容滑入。已经到位的拆出快速交接，预览完成后不再追加内容位移；融合位置和遮罩采用相同缓动与时长。
- 合并/拆分关闭并重建全部收纳盒。改用现有差量宿主同步，只修复拓扑受影响的宿主，保留无关窗口；保留拓扑快照、校验和失败回滚。
- `PanelMotionAnimator` 每次排版清除变换并重新启动标签/图标位移动画。现在比较真实布局位置，位置未变时保留现有动画；换位时从当前可见位置衔接。

## 验证

- Release 构建零警告零错误；38/38 独立测试通过。
- 新增连续输入逐帧跟手、稳定视口、逐帧订阅清理、重复排版不中断动画回归测试；既有方向锁定、取消合并、跨窗口命中等测试继续通过。
- 收纳盒组织、多选布局、启动三项隔离自检通过。
- 对照程序直接加载旧发布版与当前构建的真实 GroupDragPreviewWindow，使用同一简单示例卡片和 90 点序列，在真实 WPF 渲染事件中采样；未启动第二个正式桌面接管实例。

| 本机隔离采样 | 0.58.7 发布版 | 修复构建 |
| --- | ---: | ---: |
| 帧采样位置平均落后（DIP） | 9.29 | 0 |
| 平均渲染事件间隔（ms） | 28.61 | 16.82 |
| P95 渲染事件间隔（ms） | 34.01 | 22.77 |
| 窗口 SizeChanged 次数 | 144 | 7 |
| 已到位后的收尾耗时（ms） | 300.07 | 17.05 |

原始结果与渲染截图：`.research/motion-smoothness-20260913/before.json`、`after.json`、`before.png`、`after.png`。对照程序位于同目录 `probe/`，修改前源码位于 `src/`。

序列由每帧推进，统计反映该机器该示例的渲染与交接开销，不等同于任意鼠标采样率下的输入延迟或所有桌面的稳定帧率保证。完整真实标签交互、大量图标、混合 DPI 多屏及安装版现场体验仍待验证。未替换运行实例或重建安装包。

## 官方依据

- [CompositionTarget 逐帧渲染](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-render-on-a-per-frame-interval-using-compositiontarget)
- [WPF 动画的时钟、属性优先级与移除](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/animation-tips-and-tricks)
