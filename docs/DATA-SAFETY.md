# 数据与恢复

[English](en/DATA-SAFETY.md)

## 文件在哪里

MiniC 当前架构不使用私有目录保存已收纳文件。真实文件始终位于：

- 当前用户桌面：`%USERPROFILE%\Desktop` 或系统重定向后的桌面路径
- 公共桌面：`%PUBLIC%\Desktop`

收纳盒只是视觉归属。文件选择器、浏览器上传框和资源管理器仍从标准桌面路径看到这些文件。

## 布局数据

布局文件位于：

```text
%LOCALAPPDATA%\MiniC\layout.json
```

它保存收纳盒尺寸和位置、标签关系、视觉归属及 MiniC 管理的图标坐标，不保存文件内容。`Assignments` 的键可能包含用户本机的完整路径，因此布局文件可能泄露用户名、目录名和文件名，不应公开上传。

`LayoutStore` 先写入 `layout.json.tmp`，成功后再替换正式文件。读取失败时程序使用默认布局，而不是尝试破坏性修复。

## 备份

重大布局迁移或现场修复前，将整个目录复制到安全位置：

```text
%LOCALAPPDATA%\MiniC
```

自动或人工生成的备份统一放在 `%LOCALAPPDATA%\MiniC\Backups`。备份仍包含本机文件路径，分享前应脱敏。

## 旧版本迁移

品牌统一后的首次启动会读取旧 `%LOCALAPPDATA%\DeskNest\layout.json`，并将有效布局复制到 `%LOCALAPPDATA%\MiniC\layout.json`。旧布局不会立即删除，可用于回退。

旧版本可能在以下位置保存过真实项目：

- `%LOCALAPPDATA%\DeskNest\Storage`
- 桌面 `.DeskNest` 目录

`LegacyStorageMigrationService` 启动时将遗留项目迁回真实桌面。同名目标不会覆盖，而是生成不冲突名称；迁移失败的项目保留原位，下次启动继续重试。其他组件不得自行读取或清理旧 Storage。

## 异常退出

MiniC 隐藏 Explorer 原生图标层后，由 `DesktopFallbackWatchdog` 监视主进程。主进程崩溃、被强制结束或异常退出时，守护进程应恢复 Explorer 图标层。正常退出和安装升级流程也会主动恢复。

如果桌面图标没有恢复：

1. 从系统托盘正常退出 MiniC，并等待守护进程恢复。
2. 主程序已经结束时，在任务管理器中重新启动“Windows 资源管理器”。
3. 仍未恢复时注销并重新登录 Windows。

不要通过删除桌面文件或布局文件修复显示问题。

## 布局恢复

1. 正常退出 MiniC，确认 Explorer 图标层已恢复。
2. 复制 `%LOCALAPPDATA%\MiniC` 保存故障现场。
3. 从 `Backups` 选择故障前的副本，只替换 `layout.json`。
4. 重新启动并核对收纳盒、标签和文件归属。

收纳盒或图标不可见时，先按 `Ctrl + Alt + D` 检查显示状态，再从真实桌面目录确认文件存在。报告问题时提供 Windows 版本、显示器和缩放比例、MiniC 版本、最短复现步骤及脱敏截图；不要公开原始 `layout.json`。

## 数据相关改动检查

涉及文件或布局的修改至少要验证：

- 同名文件不会被覆盖或重复生成
- 用户桌面与公共桌面路径都能正确处理
- 重命名后归属和位置跟随新路径
- 文件被外部移动或删除后，失效归属会被清理
- 标签合并和拆出前后，收纳盒 ID 及每盒文件计数不变
- 失败操作不会先提交视觉状态
- 异常结束后 Explorer 桌面可用
