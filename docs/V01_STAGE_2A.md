# V0.1 第 2A 阶段：Desktop Host 基础运行层

## 范围

第 2A 只建立不依赖旧 WPF UI 的 Windows Desktop Host。它负责初始化本地任务存储、稳定的本机 Device、授权 Project、启动恢复、取消服务和 Agent Connector 容器。

本阶段定义 `IAgentConnector`，但没有 Codex Connector、Codex SDK、Codex CLI 或任何真实 Agent 执行逻辑。

## 项目依赖

```text
ScreenGuide.DesktopHost
  ├─ ScreenGuide.Agent.Abstractions
  ├─ ScreenGuide.Core
  └─ ScreenGuide.Persistence ──────── ScreenGuide.Core

ScreenGuide.App ─ ScreenGuide.Core
```

`ScreenGuide.DesktopHost` 不引用 `ScreenGuide.App`、WPF、截图、语音或 UI Automation。

## 默认运行数据

默认数据目录：`%LOCALAPPDATA%\ScreenGuide\V01`

可使用环境变量 `SCREEN_GUIDE_DATA_DIRECTORY` 覆盖。SQLite 文件位于数据目录下的 `state\tasking.db`。测试和冒烟运行必须使用系统临时目录，不得在仓库中生成运行数据。

## 启动顺序

1. 初始化 SQLite Schema。
2. 读取第一个 `Local`、`WindowsHost` Device；不存在时创建，存在时更新最后在线时间。
3. 写入 `HostStarting`。
4. 加载 `Authorized` Project。
5. 将上次未正常结束的任务标记为 `Interrupted`，不自动重跑。
6. 初始化 Agent Connector 容器。第 2A 允许零连接器。
7. 写入 `HostStarted`。

正常关闭写入 `HostStopping` 和 `HostStopped`。启动失败写入 `HostStartupFailed`；无法使用 SQLite 时至少输出到标准错误。Host 进程未处理异常由入口记录 `HostUnhandledException`。

## IAgentConnector

接口只覆盖 V0.1 Codex 所需的启动、状态、事件、取消、用户决定和最终结果。`ExternalRunId` 用于后续保存 Codex Thread/Run 标识；第 2A 不创建真实 Run。

## 明确不做

- Codex Connector 或任何 Agent 进程。
- 手机、云端、推送和通知。
- 远程桌面、截图和输入控制。
- 语音、唤醒词和通话。
- Windows Service、隐藏启动、计划任务或自动持久化。
- 多 Agent 编排、Claude 适配和任务 DAG。
