# MiniC

[English](README.en.md)

MiniC 是一款面向 Windows 10/11 的桌面图标收纳工具。它用独立桌面图标层和可组合的收纳盒管理桌面视觉布局，同时让真实文件继续留在用户桌面或公共桌面中。

> 当前版本：`0.58.9`。项目尚未声明开源许可证；在维护者选定许可证前，源代码仅供查看，不应默认视为已获得复制、修改或分发授权。

## 为什么这样设计

- 文件仍是普通 Desktop 文件，上传、选择和资源管理器访问路径不变。
- 收纳只改变视觉归属，不把文件迁入私有目录。
- 每个独立收纳盒或标签组使用自己的顶层窗口，不覆盖整个桌面。
- 主程序异常退出后，守护进程恢复 Explorer 原生桌面图标层。

## 主要能力

- 桌面与收纳盒图标的选择、框选、拖动、排序和原位重命名
- 收纳盒合并为标签组、标签换序与拆出、折叠及列表预览
- 每个收纳盒独立颜色和透明度，以及模拟液态玻璃视觉
- Windows Shell 文件右键菜单、文件夹投放和回收站投放
- 复制项目显示蓝色双页标识，剪切项目显示半透明剪刀标识，并随系统剪贴板同步
- 新增桌面文件增量落位，不打乱已有图标位置
- 文件扩展名、图片缩略图和拖放语义尽量遵循 Windows 设置与行为

完整交互约定见 [交互行为](docs/INTERACTIONS.md)。

## 技术栈

| 范围 | 技术 |
| --- | --- |
| 应用 | C#、.NET 10、WPF |
| 系统集成 | Windows Shell COM、Win32 API |
| 辅助界面 | Windows Forms 系统托盘 |
| 目标系统 | Windows 10 1809+ / Windows 11，x64 |
| 发布 | `win-x64` 自包含发布 |
| 安装 | Inno Setup 6 |

主应用没有第三方 NuGet 包依赖；独立测试项目依赖 Microsoft.NET.Test.Sdk、xUnit 和测试适配器。默认 `NuGet.Config` 清空包源，首次构建须按开发指南使用 `NuGet.Publish.Config` 恢复依赖。

## 快速开始

开发环境需要 Windows 10/11 和 .NET 10 SDK。克隆源码后，使用已安装的 SDK 执行：

```powershell
dotnet restore .\MiniC.sln --configfile .\NuGet.Publish.Config
dotnet build .\MiniC.sln -c Release --no-restore
```

本机若保留 `.dotnet` SDK，可将 `dotnet` 替换为 `.\.dotnet\dotnet.exe`；SDK、构建缓存和安装包均不提交到源码仓库。构建、运行和调试说明见 [开发指南](docs/DEVELOPMENT.md)。

## 项目结构

```text
src/MiniC/
  Controllers/   桌面与收纳盒业务协调
  Controls/      选择、布局和动画控件
  Models/        布局持久化模型
  Services/      文件、Shell、Explorer 和系统能力
  ViewModels/    可绑定的界面状态
  Views/         桌面层、收纳盒和托盘界面
installer/       Inno Setup 安装脚本
docs/            架构、开发、测试和维护文档
```

`DesktopCoordinator` 是唯一业务协调器。真实文件位置、视觉归属和窗口拓扑之间的关系见 [架构说明](docs/ARCHITECTURE.md)。

## 数据安全

- 布局保存在 `%LOCALAPPDATA%\MiniC\layout.json`，不保存文件内容。
- 配置写入采用临时文件替换，降低异常中断导致的损坏风险。
- 旧版本私有收纳目录会迁回桌面；同名时生成不冲突名称，不覆盖现有文件。
- 正常退出、异常退出和卸载流程都应恢复 Explorer 原生桌面。

操作布局文件前请先阅读 [数据与恢复](docs/DATA-SAFETY.md)。

## 文档

- [项目接手记录](docs/HANDOVER.md)：源码地图、验证基线、已知问题和后续维护入口
- [架构说明](docs/ARCHITECTURE.md)
- [开发指南](docs/DEVELOPMENT.md)：环境、构建、自检与发布
- [交互行为](docs/INTERACTIONS.md)
- [数据与恢复](docs/DATA-SAFETY.md)
- [贡献指南](CONTRIBUTING.md)
- [安全政策](SECURITY.md)
- [版本记录](CHANGELOG.md)

Agent 或自动化工具应先阅读 [AGENTS.md](AGENTS.md)，再根据任务进入对应专题文档。

## 开源状态

第三方参考与许可证声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。该文件不代表 MiniC 自身采用 MIT License。正式发布到 GitHub 前，维护者仍需选择并添加根目录 `LICENSE`。
