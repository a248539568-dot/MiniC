# 贡献指南

感谢参与 MiniC。项目直接集成 Windows 桌面和真实文件操作，因此改动应优先保证数据安全、系统行为一致性和异常可恢复性。

English version: [Contributing Guide](docs/en/CONTRIBUTING.md)

## 开始之前

1. 阅读 [项目说明](README.md) 和 [架构不变量](docs/ARCHITECTURE.md#核心不变量)。
2. 在 Issue 中说明问题、复现步骤和目标行为；较大的交互或架构变化先讨论方案。
3. 不要在示例、日志或截图中提交真实用户名、桌面路径和私人文件名。

## 修改要求

- 一次提交聚焦一个问题，避免同时进行无关重构。
- 延续现有 C#、WPF 和服务分层，不让视图直接执行真实文件操作。
- 文件仍留在 Desktop；新增收纳能力不得引入不透明的私有存储。
- Windows 原生行为有公开 API 时优先复用，不仿冒无法公开调用的系统界面。
- 修改布局模型时保持旧版本 JSON 可读取，并提供默认值或迁移路径。
- 替换实现时删除已无用途的旧代码，同时检查调用方、持久化和异常清理。
- 用户可见变化写入 `CHANGELOG.md`，开发细节写入 `development.log` 顶部。

## 验证要求

至少完成警告即错误的 Release 构建、独立单元测试和与改动相关的专项自检。涉及桌面层、Shell、拖放、真实文件或多显示器时，还需按 [开发指南](docs/DEVELOPMENT.md#验证) 完成真实 Windows 验证。

提交说明应包含：

- 问题原因和行为变化
- 影响的模块与数据边界
- 已运行的命令和真实场景
- 尚未覆盖的风险

## Pull Request 检查

- 没有提交 `bin`、`obj`、`dist`、布局文件或本机日志
- 没有覆盖用户文件、产生重复副本或遗失视觉归属
- 正常退出和异常退出都能恢复 Explorer 桌面
- 标签合并与拆出前后，收纳盒和文件归属数量一致
- 文档、版本号和安装脚本在需要时同步更新
- `dotnet build MiniC.sln -c Release` 与 `dotnet test tests/MiniC.Tests/MiniC.Tests.csproj -c Release` 均通过

## 许可证

项目当前尚未声明开源许可证。在维护者添加根目录 `LICENSE` 前，外部贡献的授权方式也不明确；正式接受外部 Pull Request 前应先完成许可证选择，并在贡献流程中补充贡献者授权说明。
