# V0.1 第 2B-0 阶段：Codex 集成能力验证

日期：2026-08-17
结论状态：已完成本机实测；未开始正式 `CodexConnector`

## 1. 最终结论

V0.1 正式 `CodexConnector` 推荐采用：**Desktop Host 直接启动官方 `codex exec --json` 子进程，读取 JSONL 事件，并用 Codex Thread ID 映射现有 `ExternalRunId`**。

选择这一方案的原因：

- `codex exec --json` 是官方文档公开的非交互接口，当前本机 0.147.0 可用；
- 能启动新任务、产生稳定 Thread ID、续接原线程、输出结构化事件和 JSON Schema 约束的最终结果；
- Desktop Host 是 .NET，不需要为了 TypeScript SDK 再引入一个 Node 常驻桥接层；
- 官方 TypeScript SDK 的本机包源码显示，它本质上仍会启动 Codex CLI 并读取 JSONL；
- App Server 的生命周期能力最完整，但本机命令仍明确标注 `[experimental]`，暂时不应成为 V0.1 唯一生产依赖。

这个选择存在一个必须接受的 MVP 边界：**稳定 CLI 路线不能在同一 Turn 内可靠处理实时审批/任意用户问答。** V0.1 应把“需要用户决定”做成结构化终态 `action_required`，将 ScreenGuide Task 切到 `WaitingForUser`；用户回复后用同一个 Thread ID 执行 `codex exec resume`。这是同一 ScreenGuide Task、同一 Codex Thread 的下一 Turn，不是第二个任务。

如未来必须支持真正的同 Turn 审批等待，可在独立传输适配器后试用 App Server；在它脱离 experimental 或完成兼容性门禁前，不替换稳定 CLI 主路径。

## 2. 本机实际环境

| 项目 | 实测结果 |
|---|---|
| Windows | Windows 11 x64 |
| Node.js | 20.12.2 |
| npm | 10.5.0 |
| PATH 中可调用 Codex | npm 安装的 `@openai/codex` |
| Codex CLI | `codex-cli 0.147.0` |
| 官方 TypeScript SDK | 实验 Harness 局部安装 `@openai/codex-sdk 0.147.0` |
| 登录 | `codex doctor --json` 确认 ChatGPT 登录已配置、网络和 WebSocket 可达 |
| API Key | 未读取、未复制、未写入仓库；本轮复用 Codex 已有本机登录 |

Codex 桌面应用安装包内部也包含 `codex.exe`，但 WindowsApps 权限阻止普通外部进程直接启动它。正式 Host 不应依赖桌面应用的内部安装路径；应依赖可发现、可版本检查的 npm CLI，或以后由 ScreenGuide 明确管理自己的 Codex CLI 运行时。

官方依据：

- [Codex SDK](https://learn.chatgpt.com/docs/codex-sdk)
- [Codex 非交互模式](https://learn.chatgpt.com/docs/non-interactive-mode)
- [Codex App Server](https://learn.chatgpt.com/docs/app-server)

## 3. 三种官方调用方式的实测判断

### 3.1 `codex exec --json`：V0.1 推荐

新任务建议形态：

```text
codex exec --json --sandbox workspace-write --config approval_policy="never" --cd <authorized-project> --output-schema <schema.json> <instruction>
```

续接任务：

```text
codex exec resume --json <thread-id> <user-response-or-next-instruction>
```

实测事件包括：

```text
thread.started
turn.started
item.started
item.updated / item.completed
error
turn.completed / turn.failed
```

优点：官方公开、无 UI、容易从 .NET 重定向标准输入输出、可测试、可用 Thread ID 续接。缺点：一个 CLI 进程只承载一 Turn；没有公开的运行中状态查询端点，也没有稳定的同 Turn 审批回调。

### 3.2 官方 TypeScript SDK：能力已验证，但不作为 .NET 主路径

SDK 的 `startThread()`、`resumeThread()`、`runStreamed()`、`outputSchema` 和 `AbortSignal` 均已在本机实测。它要求 Node 18+，当前环境满足。

包内 README 和源码确认：SDK 会启动 `@openai/codex` CLI，通过 JSONL 交换事件。当前 0.147.0 SDK 内部使用 `codex exec --experimental-json`，而公开 CLI 提供 `--json`。对 .NET Desktop Host 来说，再加入 Node sidecar 没有带来足以抵消部署和进程管理成本的价值。

SDK 可继续作为协议行为的官方参考和交叉验证工具，不建议成为 V0.1 Desktop Host 的必需运行时。

### 3.3 App Server：能力最完整，但暂不作为稳定主路径

本机 App Server 已验证支持：

- `thread/start`、`thread/resume`、`thread/read`；
- 独立 Thread ID、Session ID、Turn ID；
- `thread/status/changed` 的真实运行状态；
- `waitingOnApproval`；
- 带请求 ID 的审批请求和原 Turn 内响应；
- `turn/interrupt` 及终态 `interrupted`；
- `turn/completed` 的 `completed`、`failed`、`interrupted`、`inProgress` 状态模型。

但是本机 `codex app-server --help`、类型生成命令均标注 experimental。协议很适合未来的完整 Connector，却有版本变化风险。因此建议：保留 `ICodexTransport` 边界，V0.1 使用 Exec JSONL 实现；App Server 只能作为后续受版本门禁控制的替代实现。

## 4. 实验结果

所有原始事件、临时工作区和状态文件都在 `%LOCALAPPDATA%\ScreenGuide\Experiments\2B0`，没有写入仓库数据库或日志。

| 实验 | 实际结果 | 结论 |
|---|---|---|
| SDK 新建 Thread | 收到 `thread.started → turn.started → item.completed → turn.completed` | 可获得稳定 Thread ID |
| SDK 独立进程续接 | 第二次 Node 进程用原 ID 恢复，并正确记住标记 | 程序重启后可续接已持久化线程 |
| SDK 结构化输出 | 最终消息符合给定 JSON Schema | 可用于稳定生成摘要和 `action_required` |
| 完成事件与进程返回 | `turn.completed` 到达后约 1.2 秒 SDK 调用才返回 | 不能把进程退出当作任务完成事件 |
| SDK AbortSignal | 运行长命令后取消；事件流抛出中止，长命令进程数为 0 | 可取消，但 SDK 不产生 `turn.failed`/`turn.completed` 终态 |
| CLI 明确失败 | 无效模型产生 `error`、`turn.failed`，进程退出码 1 | 失败应以协议终态为准，并保存退出码辅助诊断 |
| CLI 硬终止与恢复 | 在 `item.started` 后强制终止；旧 Turn 被持久化为 `interrupted`，同 Thread 新 Turn 成功完成 | Thread 可恢复；被中断 Turn 不应自动视为失败或成功 |
| CLI 硬终止的子进程 | 本次实测没有遗留长命令子进程 | 不能据单次结果取消 Job Object/进程树兜底 |
| App Server 完成后重启 | 更换 App Server PID 后 `thread/resume` 成功，同 ID 读取到 2 个完成 Turn | App Server 自身重启后可恢复已落盘线程 |
| App Server 审批等待 | 明确收到 `waitingOnApproval` 和审批请求；拒绝后原 Turn 继续并完成，待创建文件不存在 | 可可靠判断授权等待和响应原任务 |
| App Server 取消 | `turn/interrupt` 后收到 `turn.completed: interrupted`，长命令进程为 0，服务器继续运行 | 这是最明确的协议级取消语义 |
| App Server 通用用户提问 | 当前 Default mode 报告 `request_user_input is unavailable`，未发出用户输入请求 | 消息类型存在不代表当前模式实际可用 |

## 5. ID 生命周期和 `ExternalRunId`

不要把 Run、Thread、Session、Turn 和 OS PID 混为一个 ID。

| 标识 | 含义 | 生命周期 | V0.1 用法 |
|---|---|---|---|
| ScreenGuide Task ID | 产品自己的任务主键 | 永久 | 权威业务 ID |
| Codex Thread ID | 对话及其连续 Turn 的持久标识 | 跨 CLI/Host 进程重启 | 映射到现有 `ExternalRunId` |
| Codex Session ID | App Server 的线程树会话 ID | 根线程及 fork 树共享 | V0.1 不依赖；可选保存 |
| Codex Turn ID | 单次输入到终态的执行标识 | 单 Turn | 仅 App Server稳定暴露；Exec 路线使用本地 Attempt ID |
| 本地 Attempt ID | ScreenGuide 每次启动/续接子进程的 ID | 单次进程调用 | 必须新增并持久化 |
| OS PID | 当前 Codex 进程 | 单进程且可复用 | 只用于诊断和取消，绝不能充当 ExternalRunId |

本机实测中，根线程的 App Server `sessionId == thread.id`。官方协议说明 fork 后仍共享根 Session ID，因此正式代码必须读取 `sessionId`，不能永远自行假定两者相等。

## 6. 真实状态映射

V0.1 不允许用“终端多久没输出”判断状态。

| ScreenGuide 状态 | Exec JSONL 判定 |
|---|---|
| Starting | 子进程已创建，但尚未收到 `thread.started` |
| Running | 已收到 `turn.started`，尚无终态；`item.*` 只更新进度 |
| Succeeded | 收到 `turn.completed`，且结构化业务结果不是 `action_required` |
| Failed | 收到 `turn.failed`；或进程/协议错误明确不可恢复 |
| WaitingForUser | 最终结构化结果为 `action_required`；不是根据沉默猜测 |
| CancellationRequested | 本地已登记取消，但尚未确认进程树退出 |
| Cancelled | 取消由用户发起，且 Codex 进程及子进程已确认停止 |
| Interrupted | 进程退出/Host 恢复时没有收到终态，或 Codex 将旧 Turn 记录为 `interrupted` |

必须区分“工具命令失败”和“Codex Turn 失败”：某个 `command_execution` 的退出码非 0，Codex 仍可能理解错误、换一种方案并最终 `turn.completed`。只有最外层 Turn 终态才能决定 Agent 执行是否结束；ScreenGuide 再结合结构化结果决定业务任务是否成功。

App Server 路线可额外直接映射：

- `active` 且没有 flag：`Running`；
- `active + waitingOnApproval`：`WaitingForUser`；
- `active + waitingOnUserInput`：理论上为 `WaitingForUser`，但当前 Default mode 尚未验证可用；
- Turn `interrupted`：`Cancelled` 或 `Interrupted`，取决于是否存在本地取消请求；
- Thread `systemError`：`Failed` 或 `Interrupted`，需结合 Turn 和错误事件。

## 7. 推荐的结构化最终结果

正式 Connector 应为每个 Turn 固定输出 Schema，至少包含：

```json
{
  "outcome": "completed | action_required",
  "summary": "给用户看的简短结论",
  "changedFiles": ["relative/path"],
  "tests": [{ "name": "test name", "status": "passed | failed | not_run" }],
  "question": null,
  "decisionOptions": []
}
```

`turn.completed` 只表示 Codex 正常结束了这一 Turn；当 `outcome=action_required` 时，ScreenGuide Task 必须进入 `WaitingForUser`，不能误标为 `Succeeded`。

## 8. SQLite 必须持久化的信息

不建议只在 `Task` 上增加一个 `ExternalRunId`。一个 Task 可以有同一 Thread 下的多次续接、取消和恢复调用。建议第 2B 正式实现时增加独立的 `AgentRun`/`AgentAttempt` 持久化模型，并进行 Schema 迁移。

### 8.1 `agent_runs`：Task 与 Codex Thread 的稳定关系

必需字段：

- `id`：ScreenGuide 本地 AgentRun ID；
- `task_id`：外键，V0.1 可唯一约束一个活动 AgentRun；
- `connector_id`：固定 `codex`；
- `transport`：`exec-json-v1`，避免以后切 App Server 时无法识别；
- `external_thread_id`：Codex Thread ID，即 `ExternalRunId`；
- `external_session_id`：可空，App Server 使用；
- `codex_cli_version`：例如 `0.147.0`；
- `status`；
- `created_at_utc`、`updated_at_utc`；
- `last_event_sequence`、`last_event_type`、`last_event_at_utc`；
- `final_summary`、`final_result_json`；
- `failure_code`、`failure_message`。

### 8.2 `agent_attempts`：每次子进程/Turn 调用

必需字段：

- `id`：本地 Attempt ID；
- `agent_run_id`、`task_id`、`command_id`；
- `attempt_number`；
- `external_turn_id`：Exec 路线为空，App Server 可填；
- `operation`：`start`、`resume`、`cancel`、`recover`；
- `process_id`、`process_started_at_utc`、`executable_path_hash`：防止 PID 复用误杀；
- `started_at_utc`、`terminal_event_at_utc`、`process_exited_at_utc`；
- `terminal_event_type`、`exit_code`、`exit_signal`；
- `cancellation_requested_at_utc`、`cancellation_confirmed_at_utc`；
- `status`：`Starting`、`Running`、`Completed`、`Failed`、`Cancelled`、`Interrupted`；
- `last_event_sequence`、`last_event_at_utc`；
- `input_command_id` 或输入摘要哈希，用于恢复去重，避免保存重复敏感文本。

### 8.3 等待请求

稳定 Exec 路线的 `action_required` 需要保存：

- 本地 `decision_request_id`；
- `task_id`、`agent_run_id`；
- 问题、可选项及创建时间；
- `responded_command_id` 和响应时间；
- 状态：`Pending`、`Answered`、`Expired`、`Cancelled`。

如果以后启用 App Server，再增加 `external_request_id`、`external_turn_id` 和请求方法。不要把审批请求只留在内存里。

### 8.4 事件存储原则

继续使用现有 `TaskEvent` 作为产品级时间线，保存经过筛选的事件类型、Item ID、状态、退出码和摘要。不要把完整 stdout、推理内容、凭据、用户私密文件内容或整份 Codex rollout 复制进审计日志。原始实验/诊断日志仍应位于用户本地应用数据目录，并有大小和保留期限制。

## 9. 恢复策略

### 9.1 正常重启、前一 Turn 已结束

读取 `external_thread_id`，使用 `codex exec resume --json <thread-id>` 开始下一 Turn。实测可跨独立进程恢复上下文。

### 9.2 Host 在运行中意外退出

稳定 Exec JSONL 无法让新 Host 重新接管旧进程已经打开的 stdout 管道，因此不能声称“原地重连正在运行的 CLI Turn”。推荐策略：

1. 正式 Host 将每个 Codex 进程放入 Windows Job Object，并启用 `KILL_ON_JOB_CLOSE`；
2. Host 恢复时，将 SQLite 中 `Starting/Running/CancellationRequested` 且没有协议终态的 Attempt 标记为 `Interrupted`；
3. 确认旧 PID 的启动时间和可执行文件身份，确认进程树已停止；
4. 记录 `RecoveryDetected` 和审计事件；
5. 不自动重放原始指令，因为旧 Turn 可能已经产生部分文件修改或外部副作用；
6. 检查项目 Git diff/测试等可核验事实，再由用户决定是否用同一 Thread ID 发起恢复 Turn。

硬终止实验显示：旧 Turn 在 Codex 历史中为 `interrupted`，同一 Thread 可以开始新 Turn。但这不证明所有工具都没有外部副作用，所以恢复必须保守。

### 9.3 App Server 的未来恢复

如果未来使用独立常驻 App Server，客户端重启而服务器仍存活时，协议允许 `thread/resume` 重新加入运行线程；本轮尚未验证“客户端断线、服务器不退出、审批请求重放”的完整场景。App Server 进程本身退出后，已完成线程可恢复；运行中的请求是否能无损恢复不能作为 V0.1 承诺。

## 10. 取消策略

### Exec JSONL 主路径

1. 先在 SQLite 原子写入 `CancellationRequested`；
2. 停止继续接受该 Attempt 的业务结果；
3. 请求结束 Codex 进程，并终止其 Windows Job Object 进程树；
4. 等待进程退出，核验没有属于该 Job 的子进程；
5. 只有取消由用户发起且进程树已停止，才写 `Cancelled`；
6. 若停止超时或进程身份不匹配，写 `Interrupted`/`Failed`，不能假装取消成功。

SDK AbortSignal 本机实测能结束 CLI 和本次长命令，但事件流以异常结束，不会提供正常 Turn 终态。因此正式状态仍要结合“本地取消已登记 + 进程树已停止”。

### App Server 未来路径

发送 `turn/interrupt(threadId, turnId)`，等待 `turn/completed` 且状态为 `interrupted`，再核验命令子进程停止。本机已验证该流程。

## 11. 等待用户与继续策略

### V0.1 稳定方案

- Codex 用固定 Output Schema 返回 `action_required`；
- ScreenGuide 创建本地 DecisionRequest，并将 Task 设为 `WaitingForUser`；
- 用户答复经现有 `Command(UserResponse)` 去重并审计；
- `RespondToDecisionAsync` 使用原 `ExternalRunId` 执行 `codex exec resume`；
- 新 Attempt 属于同一 Task/AgentRun/Thread；
- 严禁调用 `start` 新建第二个 Codex Thread。

### 尚不能承诺的能力

当前 Default mode 中 `request_user_input` 不可用。App Server 的命令审批等待已经验证，但稳定 CLI 路线无法把交互式审批请求可靠交给 Host。因此 V0.1 的 Codex 运行配置必须使用明确的项目授权、受限 sandbox 和无交互审批策略；超出权限的动作应失败或返回 `action_required`，不能弹出不可见终端等待。

## 12. 不推荐方案

| 方案 | 不推荐原因 |
|---|---|
| 控制 Codex TUI、模拟键盘/点击、读屏 | 无结构化状态，脆弱，无法安全恢复，用户已明确禁止 |
| 依赖 Codex 桌面应用内部 exe 路径 | WindowsApps 权限和版本路径不稳定，本机已无法直接启动 |
| 用终端静默时间判断完成 | 无法区分思考、等待、卡死、审批和网络问题 |
| 直接读取 `~/.codex` rollout、内部 SQLite | 非公开存储结构，当前 `codex doctor` 甚至报告少量索引/文件清单差异，不适合作为产品契约 |
| 用 OS PID 当 ExternalRunId | PID 会复用、不能跨重启、与 Codex 对话生命周期无关 |
| V0.1 直接绑定 App Server | 本机 0.147.0 仍标 experimental，协议变化风险高 |
| 为 .NET Host 增加 SDK Node sidecar | SDK 已验证可用，但核心能力等价于启动 CLI；增加部署、更新和进程层级 |
| Codex MCP Server 作为执行生命周期接口 | MCP 面向工具暴露，不提供本任务所需的完整 Thread/Turn 恢复、状态和取消契约 |

## 13. “Codex 完成”与进程退出

- Exec/SDK：`turn.completed` 或 `turn.failed` 是协议终态；随后 CLI 子进程才退出。本机一次完成实验中相差约 1.2 秒。
- App Server：一个 Turn 完成后服务器进程仍继续运行，本机已验证。
- 进程退出码 0 但没有收到可解析的终态，不能标记成功；应记为 `Interrupted` 或协议错误。
- 收到 `turn.completed` 后进程退出异常，应保留业务终态，同时记录基础设施诊断；最终是否成功还要检查结构化结果。
- 单个工具命令退出不等于 Codex Turn 结束。

## 14. 正式 `CodexConnector` 设计草案

本阶段不实现以下代码，只固定设计边界。

```text
CodexConnector (implements IAgentConnector)
  ├─ CodexCapabilityProbe
  │    └─ executable discovery, version gate, auth/doctor summary
  ├─ ICodexTransport
  │    ├─ ExecJsonTransport       <- V0.1
  │    └─ AppServerTransport      <- future/feature flag
  ├─ CodexJsonEventParser
  ├─ CodexStateMapper
  ├─ CodexProcessSupervisor
  │    └─ redirected stdio + Windows Job Object + verified process identity
  ├─ CodexResultParser
  │    └─ fixed JSON Schema + action_required
  ├─ CodexRunRepository
  │    └─ AgentRun / AgentAttempt / DecisionRequest
  └─ CodexRecoveryPolicy
       └─ conservative interrupted-task reconciliation
```

现有 `IAgentConnector` 的映射：

- `StartTaskAsync`：校验授权路径，创建 Attempt，启动 `codex exec --json`；收到 `thread.started` 后保存 `ExternalRunId`；
- `GetTaskStatusAsync`：读取 SQLite 权威快照，而不是临时查看终端；
- `GetTaskEventsAsync`：返回已持久化、严格排序的产品级事件；
- `CancelTaskAsync`：原子登记取消，停止并核验 Job Object；
- `RespondToDecisionAsync`：校验 pending request 和命令幂等键，用原 Thread ID `resume`；
- `GetFinalResultAsync`：只返回已持久化的结构化最终结果。

需要对 2A 抽象做的最小后续调整：`AgentExecutionStatus` 应增加 `Interrupted`；事件应能区分 Turn 终态、工具进度和结构化决定请求；持久化不能只依赖当前内存中的 `AgentRunReference`。这些调整属于正式 2B，不在 2B-0 修改。

## 15. 已验证能力

- 本机 npm Codex CLI 版本发现和调用；
- 登录与网络可用性（使用官方 redacted doctor 输出）；
- 新建 Thread 和稳定 Thread ID；
- 跨独立进程按 ID 续接上下文；
- JSONL 事件流；
- JSON Schema 结构化最终结果；
- 明确成功和明确失败终态；
- SDK 取消及长命令停止；
- CLI 硬终止、旧 Turn 记为 interrupted、同 Thread 恢复；
- App Server Thread/Session/Turn ID；
- App Server 已完成线程在服务器重启后恢复；
- App Server 审批等待、拒绝和原 Turn 继续；
- App Server 协议取消和子进程停止；
- Codex Turn 完成与宿主进程生命周期不同。

## 16. 尚未验证能力

- 网络中断、限流、登录过期和订阅额度耗尽的全部错误码；
- 真实大型代码任务、长时间运行和大量 JSONL 的背压；
- Windows Job Object 的正式 .NET 实现；
- 所有 Shell/MCP/外部程序的子进程清理，而不只是本轮 PowerShell 长命令；
- 同一 Thread 并发启动多个 Turn 的冲突行为；
- Codex 会话被归档、删除、损坏或版本迁移后的恢复；
- App Server 客户端断线但服务器继续运行时，对运行 Turn/审批请求的无损重连；
- App Server `waitingOnUserInput` 在稳定默认模式下的可用性；
- 0.147.0 之后 CLI/JSONL/App Server 的兼容性；
- Codex 桌面会员登录在未来商业分发场景中的授权、计费和多用户部署方案。

## 17. 主要风险

1. **稳定性缺口**：完整审批和真实状态查询集中在 experimental App Server；稳定 Exec 路线只能用事件流和本地持久化补足。
2. **运行中不可重连**：Exec 子进程的 stdout 管道不能由重启后的 Host 接管，只能保守中断并恢复同一 Thread。
3. **版本漂移**：必须启动前做版本门禁、保存 CLI 版本、对未知 JSONL 事件前向兼容，并维护最小兼容性测试。
4. **重复副作用**：被中断 Turn 可能已经改文件或调用工具，绝不能自动重放整条任务指令。
5. **认证与商业化**：当前验证使用本机已有 ChatGPT 登录，不代表未来可静默替用户安装、共享账户或代付模型费用。
6. **隐私**：Codex 自身会在用户目录持久化会话；ScreenGuide 不应复制完整会话到自己的审计日志，产品需明确告知本地保存位置和清理策略。
7. **能力表述**：`request_user_input` 当前不可用，不能在产品文案中承诺“任何时候都能原地等待并继续”。

## 18. 2B 正式开发前的验收门槛

下一阶段只应实现 Exec JSONL 版最小 Connector，并满足：

1. 只允许授权 Project 内的工作目录；
2. 启动时检查 Codex 可执行文件、版本和登录状态，错误可读；
3. Thread ID 在收到后立即与 Task 原子持久化；
4. 所有状态来自协议事件、进程事实和 SQLite，不使用静默超时猜测；
5. 成功、失败、`action_required`、取消、Host 恢复各有自动化测试；
6. 取消后核验 Job Object 内没有剩余进程；
7. Host 重启不自动重复执行旧指令；
8. 运行数据、JSONL、Codex 会话、日志和凭据不进入仓库；
9. App Server 仍保持实验隔离，不提前接入正式 Tasking。

## 19. 本阶段产物

- `docs/V01_STAGE_2B0_CODEX_INTEGRATION.md`：本验证报告；
- `tools/CodexIntegrationHarness/`：与正式 Tasking/Host 隔离的可重复实验工具；
- 没有新增或修改正式 `CodexConnector`、Tasking、Persistence、手机、云端、推送、远程桌面或语音代码。
