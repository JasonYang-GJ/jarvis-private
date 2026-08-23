# 元枢当前架构（V0.3.0 阶段 1 As-Built）

> 本文只描述当前代码已经实现的系统，不把后续路线图写成现有能力。更新时间：2026-08-24。阶段 1 已通过；V0.3.0 的最终提交、标签和安装包哈希见 `docs/baselines/V0.3.0_STAGE1.md`。

## 1. 运行结构

```text
用户
  ↓ 语音 / 文字 / 可见选择与确认
DesktopClient（WPF）
  ├─ 离线语音识别与朗读
  ├─ SessionUiPresenter：把唯一阶段映射为唯一用户状态
  ├─ 新话题、停止、项目/文件选择、窗口同意或拒绝
  └─ 不直接访问 SQLite、Codex 或 Windows 动作
  ↓ 当前用户 Named Pipe，protocol v7
DesktopHost
  ├─ SessionCoordinator（唯一会话与前台 Turn 协调入口）
  │    ├─ ConversationService → IConversationProvider
  │    ├─ AssistantCommandService → 权限策略与 Windows Skills
  │    ├─ Window Vision → 单窗口捕获、本机 OCR/UIA
  │    └─ LocalTaskEntryService → Codex Project Skill
  ├─ DeterministicIntentPlanner（确定性意图规划）
  ├─ CapabilityPolicyEngine（权限白名单）
  ├─ SQLite：Session / Conversation / Task / Evidence / Audit
  └─ Session 增量通知与 Host 恢复
```

DesktopHost 仍是唯一业务编排和审计边界。SessionCoordinator 协调“当前在聊什么、当前在做什么、缺什么信息以及如何取消”，但不拥有任何项目、文件、窗口或动作授权。

## 2. 正式源码范围

V0.3.0 的正式源码由下面这些内容共同组成：

- `src/`：14 个生产项目。
- `tests/`：12 个自动化与真实桌面测试项目，包含 FakeCodexCli 和 FakeBrowser 测试替身。
- `tools/`：3 个集成与真实验收 Runner。
- `installer/`：Inno Setup 安装定义、简体中文语言文件和用户数据删除工具。
- `scripts/`：发布与隔离安装验收脚本。
- `assets/branding/`：正式品牌资源。
- `Directory.Build.props`、`global.json`、`ScreenGuide.slnx` 和依赖锁文件：版本与可重复构建元数据。
- `PRODUCT.md`、`ARCHITECTURE.md`、`MEMORY.md`、`CHANGELOG.md`、`ROADMAP.md`、`AGENTS.md` 与 `docs/baselines/`：项目唯一事实来源。

`bin/`、`obj/`、`artifacts/`、运行日志、SQLite 运行数据库、语音模型和用户配置不属于源码。

## 3. 模块边界

| 模块 | 当前职责 | 不应承担 |
|---|---|---|
| `ScreenGuide.DesktopClient` | WPF 界面、托盘、可见确认、Host 生命周期、语音交互、Session 状态呈现 | 直接执行动作、直接读写 SQLite、直接调用 Codex |
| `ScreenGuide.DesktopProtocol` | IPC protocol v7、Session DTO、当前用户 Pipe 客户端 | 业务规则、权限判断和存储 |
| `ScreenGuide.DesktopHost` | SessionCoordinator、业务编排、权限、恢复、审计、增量状态通知 | 让 UI 绕过 Coordinator 直接组合新流程 |
| `ScreenGuide.AI.Core` | V0.2 安全子集的确定性意图规划 | 自由执行工具或隐式授予权限 |
| `ScreenGuide.Core` | Session、任务、对话和权限领域契约 | Windows、SQLite 或模型供应商细节 |
| `ScreenGuide.Persistence` | SQLite schema v7 与 Session/任务/对话存储 | UI 和模型调用 |
| `ScreenGuide.Skills.*` | 可替换技能接口与 Windows 低风险动作 | 任意桌面控制 |
| `ScreenGuide.Vision.*` | 单窗口捕获、敏感窗口拒绝、本机 OCR/UIA | 全桌面捕获和云端上传 |
| `ScreenGuide.Voice.Windows` | 本机采音、离线识别、回声过滤、朗读 | 保存录音或后台隐蔽监听 |
| `ScreenGuide.Agent.*` | Agent 抽象、Codex CLI 对话/编程连接、进程取消 | 决定电脑动作权限 |
| `ScreenGuide.Evidence` | Git、测试和任务结果证据 | 代替真实用户验收 |

## 4. Session Coordinator 数据模型

### Session

`SessionRecord` 表示一个明确话题，包含：

- 唯一 Session ID 与关联的 Conversation ID；
- 标题、创建设备、当前/归档状态；
- 当前选中的已授权项目；
- 创建、最近活动、更新时间和并发版本。

同一时刻只有一个当前 Session。首次输入自动创建，后续输入默认续接；“新话题”显式创建另一个 Session。

### Turn

`SessionTurnRecord` 表示用户的一次原始请求，包含：

- 原始文字、输入方式、会话内顺序号和幂等键；
- 工作类型：普通聊天、桌面动作、窗口观察、编程任务或未知；
- 当前阶段和缺失上下文；
- 关联的 Plan、Conversation Turn、Task、Operation、项目、文件和单窗口身份；
- UI 已确认的 Expected Intent/Target 与 Host 规划得到的 Canonical Plan Target；
- 是否需要确认、是否已经确认、是否请求取消；
- 结果、错误、完成时间和并发版本。

同一 Session 内 `sequence_number` 和 `idempotency_key` 均唯一，重复提交不会重复启动 Provider 或动作。更新使用乐观并发版本；每个 Turn 的补充、确认与取消还经过同一把串行门，避免确认和取消并发启动两次。终态 Turn 不接受普通迟到结果覆盖。

### 状态

前台工作状态：

`Understanding`、`Responding`、`WaitingForProject`、`WaitingForFile`、`WaitingForWindow`、`WaitingForWindowConsent`、`WaitingForConfirmation`、`Executing`、`ObservingWindow`。

编程状态：

`ProgrammingTask`、`WaitingForUser`。编程任务可以在后台继续，不会被同一 Session 的普通聊天自动取代。

终态：

`Completed`、`Failed`、`Cancelled`、`Interrupted`。

## 5. 连续对话与并发规则

1. 首页输入统一调用 `SessionCoordinator.SubmitAsync`，不再每句话创建新 Conversation。
2. Session 一对一关联 Conversation；同一 Session 的普通聊天复用 Conversation 及 Provider Thread。
3. 新前台输入会先取消同一 Session 的旧前台 Turn，再启动新 Turn。
4. 编程任务拥有独立 Task ID 和监视器，可在后台运行；用户可以同时继续普通聊天。
5. 创建、切换或通过指定 Session 提交输入都经过同一个“当前 Session”串行入口；切换会停止上一话题的前台工作，不会自动取消已经转为后台编程任务的真实状态。
6. UI 快照携带 Coordinator 实例 ID、实例启动时间与 ChangeVersion：同一 Host 只接受不旧的版本；Host 重启时只接受启动时间更新的新实例，拒绝旧 Host 的迟到快照。

这套 Session 只管理当前话题和任务状态，不提取用户长期事实，也不跨 Session 自动检索历史。

## 6. 真取消与迟到结果保护

```text
用户停止 / 新输入
  → Session Turn CancellationTokenSource
  → ConversationService.CancelAsync
  → IConversationProvider.CancelAsync
  → Codex 进程树终止
  → Conversation Turn = Cancelled
  → Session Turn = Cancelled
```

- 窗口观察同时调用对应 Operation 的取消入口。
- 编程 Turn 的取消会调用真实 Task 取消，而不只是改变 Session UI。
- Coordinator 等待旧前台工作进入终态，再继续替代请求。
- Conversation 的取消与成功提交在 SQLite 中争夺唯一终态：取消先把仍在 Running 的 Turn 原子结束，成功只能提交仍在 Running 的 Turn。取消赢得终态后，迟到 Provider 结果不能再插入 Assistant 消息。
- Session Turn 的确认、授权和取消由每 Turn 串行门协调；SessionStore 的终态和版本条件继续阻止迟到结果把取消改回成功。
- Host 关闭时会取消仍在内存中的前台工作和监视器；重启恢复不会自动重放。

## 7. 上下文补齐与安全边界

### 项目

缺项目时保存原始请求并进入 `WaitingForProject`。用户只能选择仍处于授权状态的项目；选择后更新 Session 项目上下文并重新规划同一 Turn。若 Session 原来选择的项目已经撤权或失效，Coordinator 会清除该选择并回到等待项目，而不是丢失原请求。

### 文件

缺文件时进入 `WaitingForFile`。选择路径必须存在；补齐后重新规划同一 Turn。打开文件仍需要本次可见确认。

### 窗口

没有可用目标窗口时进入 `WaitingForWindow`；用户切换后重试。识别出窗口后进入 `WaitingForWindowConsent`，同意只绑定当时显示的窗口句柄、标题和进程；拒绝直接取消。窗口或搜索目标变化会撤销旧确认并要求重新确认。

模型输出、屏幕内容、网站内容、文档内容和 SessionCoordinator 都不能授予权限。原有 CapabilityPolicyEngine、项目授权、文件确认、单窗口同意和敏感动作禁令仍是最终安全门禁。

电脑操作页的“已发现应用/网站”入口也先创建 Session Turn，再进入等待确认和执行。Session Input 同时携带结构化 `ExpectedIntentKind` 与 `ExpectedTarget`：应用目标是已发现的 Application ID，网站目标是规范化后的完整 HTTPS URI。Assistant Plan 返回 `CanonicalTarget`，Session Turn 持久化 Expected/Plan Target；Host 在初次规划和确认执行两处都用 Ordinal 精确比较。任何意图或目标不一致都会结束 Turn，绝不执行；UI 只做同样的第二层显示前复核。

## 8. IPC 与状态更新

- DesktopClient 与 DesktopHost 使用当前 Windows 用户专属 Named Pipe，protocol v7，单条消息最大 4 MiB。
- `sessions.wait` 使用最长 30 秒的本机长轮询：只有 ChangeVersion 变化或等待超时才返回快照；DesktopClient 当前使用 20 秒等待。
- Session 变化通过进程内 ChangeVersion 唤醒等待者；Host 重启后版本从进程初始值重新开始。快照中的 Coordinator 实例 ID 与启动时间划分版本世代，使客户端可以接受新 Host 的较小版本并拒绝旧 Host 的迟到结果。
- IPC 服务将并发连接限制为 64，另保留 8 个忙碌响应槽；未发送首个请求的连接 1 秒释放，监听临时错误会退避重试。
- 无效长度、无效 JSON 等坏请求帧只记录并关闭该连接，不会让 Host 接受循环或关停流程失败。
- 当前 Session 快照仍包含该 Session 的全部 Turn、Conversation 消息和项目名称查询。长历史的快照体积与数据库读取仍是后续性能技术债。

## 9. AI、Prompt 与模型接入

- `ConversationService` 依赖 `IConversationProvider`。V0.3.0 仍只注册 `CodexConversationProvider`。
- Provider 启动本机 Codex CLI；底层模型由 Codex 配置决定，元枢没有固定模型 ID。
- 普通问答 Prompt 仍由 `CodexConversationProvider.BuildPrompt` 内嵌生成，当前没有集中 Prompt 注册、版本、A/B 测试或评测流水线。
- 电脑动作继续采用确定性 Intent Planner + Capability Policy + 白名单 Skill，不是开放式模型 Tool Calling。
- 运行时代码没有 DeepSeek API、Endpoint、Key、模型 ID 或 Provider。
- 本阶段没有长期记忆、RAG、向量数据库或复杂多 Agent 产品编排。

## 10. 数据、迁移与恢复

- 默认运行数据：`%LOCALAPPDATA%\ScreenGuide\V01`。
- SQLite：`state\tasking.db`；日志：`logs`；Codex 辅助数据：`codex`；任务证据：`evidence`。
- schema v7 包含 Session 表、索引和结构化 Expected/Plan Target 字段；从旧 schema 升级前，会在数据库同目录生成 `tasking.pre-v7-from-v5-<时间>.backup.db` 或 `tasking.pre-v7-from-v6-<时间>.backup.db` 一类备份。
- V0.2.1 不支持打开 schema v7。若回滚程序，必须同时使用升级前备份或独立数据目录，不能让旧程序直接打开已升级数据库。
- 重启恢复：运行中的 Session Turn 先标记 `Interrupted`，再与已有 Conversation Turn / Task 终态对账；可安全等待的项目补充状态保留，不自动执行原请求。
- 单窗口像素和录音不写入数据库或仓库；单窗口像素在本机分析后清零。

## 11. 构建、测试与发布

- .NET SDK 由 `global.json` 固定到 10.0.400，允许同补丁线更新。
- 普通依赖与 win-x64 发布依赖使用锁文件，发布脚本在 locked mode 下恢复。
- `scripts/build-desktop-release.ps1` 测试、发布 DesktopClient/DesktopHost、合并自包含目录并生成 V0.3.0 Inno Setup 安装包。
- 全量自动化测试 363/363 通过，失败 0、跳过 0。
- 实际 Release DesktopClient + DesktopHost + 真实 Codex Provider + 真实 Windows Notepad 完成 10 轮连续对话、3 次真取消和 5 个项目/文件/单窗口场景；`Failed=0`、`FalseCompleted=0`，证据目录为 `%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-182601`。
- 自动化总数为 363/363；最终 Git 提交、安装包 SHA-256 与正式标签在版本冻结时写入 `docs/baselines/V0.3.0_STAGE1.md`。

## 12. 当前主要技术债

- `SessionCoordinator.cs`、`MainWindow.xaml.cs`、`SqliteTaskStore.cs` 等文件较大；阶段 1 为稳定边界保留了集中实现，后续只能在测试保护下逐步拆分。
- Session 快照随完整会话历史增长；ChangeVersion 是单 Host 进程内信号，实例 ID/启动时间只解决重启后的快照世代判断，不是跨进程持久事件日志。
- 编程任务监视器仍在 Host 内部定时查询 Task 状态；这不等于 DesktopClient 的全量轮询，但仍可在后续改为更直接的任务事件。
- 单窗口授权当前绑定窗口句柄、进程名和标题；这比只比较句柄更安全，但同一程序重新创建同标题窗口时仍可能碰到 Windows 句柄复用。后续应加入进程 ID 与进程启动时间等更稳定身份。
- Prompt 仍内嵌，通用会话只有一个真实 Provider。
- 没有长期记忆、RAG、向量库或跨 Session 检索。
- 安装包未签名，语音模型未纳入可分发方案。
