# MiniC — Windows 桌面整理与图标收纳工具

[English](README.en.md)

MiniC 是一款面向 Windows 10/11 的桌面整理软件（Windows desktop organizer），支持桌面图标分组、桌面收纳盒和标签式管理。它用独立桌面图标层和可组合的收纳盒整理快捷方式、文件与文件夹，同时让真实文件继续留在用户桌面或公共桌面中。

> 当前版本：`0.59.0`。支持 Windows 10 / Windows 11，x64。许可证状态见文末。

## 下载

[下载 Windows x64 安装包](https://github.com/a248539568-dot/MiniC/releases/download/v0.59.0/MiniC-Setup-v0.59.0.exe) · [更新说明与 SHA-256 校验文件](https://github.com/a248539568-dot/MiniC/releases/latest)

安装包自带 .NET 运行时，无需另外安装。开发者可按下方说明从源码构建。

## 适用场景

- 桌面图标太多：把工作文档、常用软件快捷方式和项目文件分组放入不同收纳盒。
- 希望节省桌面空间：将多个收纳盒合并为标签组，左右排序，上下拖动拆出或合并。
- 希望保留熟悉的文件操作：继续使用 Windows 右键菜单、复制粘贴、重命名和拖放，桌面已有文件不因视觉分组而迁入私有目录。

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

## 许可证状态

本仓库公开可见，但项目尚未指定开源许可证。在维护者选定并添加根目录 `LICENSE` 前，源代码仅供查看，不应默认视为已获得复制、修改或分发授权。

第三方参考与许可证声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。该文件不代表 MiniC 自身采用 MIT License。
