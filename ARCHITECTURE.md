# 元枢当前架构（V2 阶段 2 候选 As-Built）

> 本文描述阶段 2 当前工作树已经实现的结构，并明确标出尚待真实验收的部分。更新时间：2026-08-29。V0.3.0 阶段 1 仍是最近一次正式冻结基线；阶段 2 尚未形成最终标签、版本或安装包身份。设计与验证边界见 `docs/V2_STAGE2_AI_MODEL_ROUTING_DESIGN.md`。

## 1. 运行结构

```text
用户
  ↓ 语音 / 文字 / 可见选择与确认
DesktopClient（WPF）
  ├─ 离线语音识别与朗读
  ├─ SessionUiPresenter：把唯一阶段映射为唯一用户状态
  ├─ 新话题、停止、项目/文件选择、窗口同意或拒绝
  ├─ 不直接访问 SQLite、Codex 或 Windows 动作
  ├─ 设置页：普通聊天 Provider/Model、数据去向、凭据和健康状态
  └─ 编程 Agent 独立显示为 Codex
  ↓ 当前用户 Named Pipe，protocol v8
DesktopHost
  ├─ SessionCoordinator（唯一会话与前台 Turn 协调入口）
  │    ├─ ConversationService → RoutedConversationProvider
  │    │    ├─ PromptRegistry → chat.general@1
  │    │    └─ ModelRouter → Provider Registry
  │    │         ├─ CodexChatModelProvider（生产策略安全停用）
  │    │         ├─ DeepSeekChatModelProvider
  │    │         └─ QwenChatModelProvider（仅手动备用）
  │    ├─ AssistantCommandService → 权限策略与 Windows Skills
  │    │    └─ ModelSemanticIntentSuggester（不可信建议）→ 确定性重规划
  │    ├─ Window Vision → 单窗口捕获、本机 OCR/UIA
  │    └─ LocalTaskEntryService → Codex Project Skill
  ├─ DeterministicIntentPlanner（确定性意图规划）
  ├─ CapabilityPolicyEngine（权限白名单）
  ├─ DPAPI Provider Credential Store（Key 不进 SQLite）
  ├─ SQLite：Session / Conversation / Task / Evidence / Audit / AI Invocation
  └─ Session 增量通知与 Host 恢复
```

DesktopHost 仍是唯一业务编排和审计边界。SessionCoordinator 协调“当前在聊什么、当前在做什么、缺什么信息以及如何取消”，但不拥有任何项目、文件、窗口或动作授权。Model Router 只选择普通聊天供应商，也不拥有权限。

## 2. 正式源码范围

V0.3.0 的正式源码与当前阶段 2 候选切片由下面这些内容共同组成；最终阶段 2 正式范围仍以验收后的版本基线为准：

- `src/`：生产项目；阶段 2 新增 `ScreenGuide.AI.DeepSeek` 与 `ScreenGuide.AI.Qwen`，并扩展 AI Core、Codex、Host、Protocol、Persistence 和 DesktopClient。
- `tests/`：自动化与真实桌面测试项目，包含 FakeCodexCli、FakeBrowser、Provider 网络边界和实际 WPF UI Automation 测试替身/Runner。
- `prompts/runtime/`：受版本控制的 Prompt Registry 与 Prompt 内容。
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
| `ScreenGuide.DesktopProtocol` | IPC protocol v8、Session/AI 设置 DTO、当前用户 Pipe 客户端、跨 IPC 敏感文本清理 | 业务规则、权限判断、Key 持久化和模型调用 |
| `ScreenGuide.DesktopHost` | SessionCoordinator、业务编排、权限、恢复、审计、增量状态通知 | 让 UI 绕过 Coordinator 直接组合新流程 |
| `ScreenGuide.AI.Core` | 供应商无关 Chat Model 契约、Provider Registry、Model Router、Prompt Registry、语义建议校验及确定性意图规划 | Provider HTTP/CLI 细节、自由执行工具或隐式授予权限 |
| `ScreenGuide.AI.DeepSeek` | 固定 DeepSeek 官方目的地的普通聊天 HTTP/SSE Provider、错误和健康映射 | 保存 Key、决定 Session、编程 Agent 或电脑权限 |
| `ScreenGuide.AI.Qwen` | 固定阿里云百炼兼容端点、仅 `qwen3.7-plus` 的普通聊天 HTTP/SSE Provider；消费但不公开 reasoning，拒绝 Tool Call | 自动 fallback、保存 Key、改变 Session/权限或替代 Codex 编程 Agent |
| `ScreenGuide.Core` | Session、任务、对话、AI 调用审计和权限领域契约 | Windows、SQLite 或模型供应商细节 |
| `ScreenGuide.Persistence` | SQLite schema v8 与 Session/任务/对话/AI 调用追踪存储 | UI、Key 和模型调用 |
| `ScreenGuide.Skills.*` | 可替换技能接口与 Windows 低风险动作 | 任意桌面控制 |
| `ScreenGuide.Vision.*` | 单窗口捕获、敏感窗口拒绝、本机 OCR/UIA | 全桌面捕获和云端上传 |
| `ScreenGuide.Voice.Windows` | 本机采音、离线识别、回声过滤、朗读 | 保存录音或后台隐蔽监听 |
| `ScreenGuide.Agent.*` | Agent 抽象、安全停用的 Codex 普通聊天适配、独立 Codex 编程连接和进程树取消 | 把普通聊天路由与编程 Agent 混为同一配置，或决定电脑动作权限 |
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
2. Session 一对一关联 Conversation；同一 Session 的普通聊天复用 Conversation。阶段 2 不依赖 Provider Thread：每个 Turn 从 `IConversationStore` 重建完整消息历史，再交给该 Turn 冻结的 Provider/Model。
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
  → ModelRouter 按 Turn 找到真实 Provider
  → Codex 进程树终止，或 DeepSeek/Qwen HTTP/SSE 取消
  → Conversation Turn = Cancelled
  → Session Turn = Cancelled
```

- 窗口观察同时调用对应 Operation 的取消入口。
- 编程 Turn 的取消会调用真实 Task 取消，而不只是改变 Session UI。
- Coordinator 等待旧前台工作进入终态，再继续替代请求。
- Conversation 的取消与成功提交在 SQLite 中争夺唯一终态：取消先把仍在 Running 的 Turn 原子结束，成功只能提交仍在 Running 的 Turn。取消赢得终态后，迟到 Provider 结果不能再插入 Assistant 消息。
- Session Turn 的确认、授权和取消由每 Turn 串行门协调；SessionStore 的终态和版本条件继续阻止迟到结果把取消改回成功。
- ModelRouter 在 Turn 开始时冻结路由；设置中途改变只影响下一轮。取消会到达这个 Turn 实际使用的 Provider，不会误取消新 Provider 的其他请求。
- Codex 普通聊天使用 Windows Job Object 终止进程树；DeepSeek 与 Qwen 取消真实 HTTP 请求和流式读取。三者在取消后都拒绝迟到成功。
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

## 8. IPC、AI 设置与状态更新

- DesktopClient 与 DesktopHost 使用当前 Windows 用户专属 Named Pipe，protocol v8，单条消息最大 4 MiB。
- v8 新增 `ai.settings.get`、`ai.chat-route.set`、`ai.credentials.set/delete` 和 `ai.provider.health`。AI 设置 DTO 只返回 Provider/Model、能力、数据目的地、健康和配置状态，绝不返回完整 Key。
- 普通聊天路由存入本地 `settings/ai-settings.json`；Key 单独存入 DPAPI 密文。设置页明确显示同一 Session 的既有历史会随下一条消息发送给新 Provider；当前运行回答不切换。
- `sessions.wait` 使用最长 30 秒的本机长轮询：只有 ChangeVersion 变化或等待超时才返回快照；DesktopClient 当前使用 20 秒等待。
- Session 变化通过进程内 ChangeVersion 唤醒等待者；Host 重启后版本从进程初始值重新开始。快照中的 Coordinator 实例 ID 与启动时间划分版本世代，使客户端可以接受新 Host 的较小版本并拒绝旧 Host 的迟到结果。
- IPC 服务将并发连接限制为 64，另保留 8 个忙碌响应槽；未发送首个请求的连接 1 秒释放，监听临时错误会退避重试。
- 无效长度、无效 JSON 等坏请求帧只记录并关闭该连接，不会让 Host 接受循环或关停流程失败。
- 当前 Session 快照仍包含该 Session 的全部 Turn、Conversation 消息和项目名称查询。长历史的快照体积与数据库读取仍是后续性能技术债。

## 9. AI、Prompt 与模型接入

### 统一普通聊天入口

`ConversationService` 仍只依赖既有 `IConversationProvider`，其当前实现改为 `RoutedConversationProvider`。这个兼容层从 ConversationStore 读取消息历史，获取 `chat.general@1`，冻结本 Turn 路由，调用统一 `IChatModelProvider`，并记录 AI 调用证据。SessionCoordinator 没有增加 Provider 分支。

`IChatModelProvider` 统一 System Prompt、Messages、Model、可选采样参数、CancellationToken、流式回调、Usage、Finish Reason、Provider Metadata、健康和错误；`ChatModelCapabilities` 显式描述 Streaming、Tool Calling、Vision、JSON Object、JSON Schema、Reasoning 和 Context Window，调用方按真实能力使用。

`ChatProviderRegistry` 当前注册：

- `codex / codex-default`：保留普通聊天适配器描述，但生产策略固定为 `ProductionDisabled`/`PolicyDisabled`，在探测或启动 CLI、读取项目或发送用户正文之前失败关闭；它不是阶段 2 发布目标。独立 Codex 编程 Agent 不受影响。
- `deepseek / deepseek-v4-flash`、`deepseek-v4-pro`：固定 HTTPS 目的地 `https://api.deepseek.com`，声明 Streaming、JSON Object 和 Reasoning；Key 只从凭据 lease 读取，不从环境变量、源码、SQLite 或普通配置读取。
- `qwen / qwen3.7-plus`：固定阿里云百炼 HTTPS 目的地，声明 Streaming 与 JSON Object；只允许用户手动选择，不自动 fallback、重试或重发。

Registry 中的注册不代表真实账号已经验收。阶段 2 普通聊天发布目标是 DeepSeek + 千问；千问真实账户/网络验收须在准确 SHA 获授权后执行。Codex 普通聊天保持安全停用，不属于该真实 Provider 发布门禁。

### 路由与无静默降级

`ModelRouter` 从 `IAiSettingsStore` 读取默认普通聊天路由，在 Turn 开始时冻结 Provider/Model，并保存 Turn → Provider 活动映射供真实取消使用。设置只影响下一轮；Provider 故障会返回明确错误，没有自动付费重试，也不会静默把内容改发另一个数据目的地。

### Prompt 与调用审计

`prompts/runtime/registry.json` 当前注册 `chat.general@1` 和 `intent.semantic@1`。每项包含 ID、版本、用途、文件、SHA-256、适用 Provider、创建时间和修改原因；加载时拒绝越界路径、重复项和哈希不一致。固定小型评测集覆盖连续指代、歧义、纠正、安全及缺项目/文件/窗口等场景。

SQLite `ai_invocations` 记录 Provider/Model、Prompt ID/版本/哈希、数据去向、状态、时间、Usage、Provider Request ID 和安全失败码；不记录 Key、Authorization Header、Prompt 正文或完整 Conversation 副本。

### 凭据

`WindowsDpapiCredentialStore` 使用 Windows DPAPI `CurrentUser` 和绑定 Provider ID 的 entropy 保护 Key，密文位于用户本地应用数据的 `secrets` 目录。写入采用临时文件后原子替换；解密值只在短生命周期 lease 中暴露并尽量清零。UI/IPC 只显示 Missing、Configured 或 NotRequired，不支持读回 Key。日志、Crash、IPC 和 UI 错误经过敏感文本清理。

### 普通聊天与编程 Agent

Codex 普通聊天适配器由 `CodexChatModelProvider` 承载，但生产策略有意失败关闭；编程任务仍走独立 `CodexConnector` / `CodexSkillAdapter`。在 DeepSeek 与千问之间手动切换普通聊天不改变项目授权、Codex 编程生命周期、Git 边界或 TaskEvidence。

### 语义意图与安全

确定性 Planner 仍是第一入口。只有它仍判断为普通聊天且文字命中有限候选条件时，`ModelSemanticIntentSuggester` 才把当前用户文字交给当前 Chat Provider。输出必须是严格五字段结构，并经过枚举、长度、置信度（至少 0.80）、歧义和缺失上下文组合校验。模型 target 不被采用；Host 只把通过门槛的意图类型重新交给确定性 Planner，并用真实本机上下文重算目标、上下文、确认和权限。失败、非法输出或低置信度都回到保守路径。

电脑动作继续采用确定性 Intent Planner + Capability Policy + 白名单 Skill，不是开放式模型 Tool Calling。阶段 2 没有长期记忆、RAG、向量数据库或复杂多 Agent 产品编排。

## 10. 数据、迁移与恢复

- 默认运行数据：`%LOCALAPPDATA%\ScreenGuide\V01`。
- SQLite：`state\tasking.db`；日志：`logs`；Codex 辅助数据：`codex`；任务证据：`evidence`；AI 路由：`settings\ai-settings.json`；DPAPI 密文：`secrets\<provider>.bin`。
- schema v8 继承 schema v7 的 Session/Expected/Plan Target，并新增 `ai_invocations`。从旧 schema 升级前，会在数据库同目录生成 `tasking.pre-v8-from-v<旧版本>-<时间>.backup.db`；迁移测试覆盖 v7 → v8 并保留阶段 1 Conversation 历史。
- V0.3.0 只支持 schema v7，不支持打开 schema v8。若回滚到 `v0.3.0-stage1`，必须保留当前数据库，并使用自动生成的 pre-v8 备份或独立数据目录。V0.2.1 的 pre-v7 回滚边界继续有效。
- 重启恢复：运行中的 Session Turn 先标记 `Interrupted`，再与已有 Conversation Turn / Task 终态对账；可安全等待的项目补充状态保留，不自动执行原请求。
- 单窗口像素和录音不写入数据库或仓库；单窗口像素在本机分析后清零。

## 11. 构建、测试与发布

- .NET SDK 由 `global.json` 固定到 10.0.400，允许同补丁线更新。
- 普通依赖与 win-x64 发布依赖使用锁文件，发布脚本在 locked mode 下恢复。
- `scripts/build-desktop-release.ps1` 是现有发布入口；阶段 2 最终版本号和安装产物尚未冻结。
- V0.3.0 冻结证据保持不变：全量自动化 363/363；实际 Release DesktopClient + DesktopHost + 真实 Codex Provider + 真实 Windows Notepad 已完成阶段 1 的 10 轮连续对话、3 次真取消和项目/文件/单窗口场景，证据目录为 `%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-184833`。
- 阶段 2 当前已有开发期自动化覆盖：Provider 契约/Registry/Router、A → B → A、Prompt 哈希与固定评测、DPAPI、敏感信息清理、Codex 安全停用边界、DeepSeek/千问故障与取消、AI 设置 Service/IPC/UI、语义注入拒绝、schema v8 迁移和聊天/编程分离。
- 阶段 2 普通聊天发布目标是 DeepSeek + 千问；千问真实账户/网络验收仍须在准确 SHA 获授权后完成。Codex 普通聊天不属于发布目标，独立 Codex 编程 Agent 仍需相应回归。最终全量测试数字、实际 Release DesktopClient、版本提交/标签和安装包身份尚待总控验收，当前不得宣布阶段 2 通过或发布。
- 阶段 1 标签 `v0.3.0-stage1` 继续指向源码提交 `0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`，annotated tag object 为 `9fc790ade57fa2d3c18bc5ee84e8dc9e7018aa89`。
- 标签源码的 locked restore 通过。发布目录共 533 个文件；Client/Host ProductVersion 为 `0.3.0+0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`，FileVersion 为 `0.3.0.0`。
- 安装包 `artifacts/release/元枢-V0.3.0-安装包.exe` 为 64,039,656 bytes，SHA-256 为 `42C609E130B29C6D96784C2B0266473B6D3417BE0DC5FE9C81C7517CB100FCC7`，未签名。标签后的仅文档证据提交不改变标签源码或二进制来源。

## 12. 当前主要技术债

- `SessionCoordinator.cs`、`MainWindow.xaml.cs`、`SqliteTaskStore.cs` 等文件较大；阶段 1 为稳定边界保留了集中实现，后续只能在测试保护下逐步拆分。
- Session 快照随完整会话历史增长；ChangeVersion 是单 Host 进程内信号，实例 ID/启动时间只解决重启后的快照世代判断，不是跨进程持久事件日志。
- 编程任务监视器仍在 Host 内部定时查询 Task 状态；这不等于 DesktopClient 的全量轮询，但仍可在后续改为更直接的任务事件。
- 单窗口授权当前绑定窗口句柄、进程名和标题；这比只比较句柄更安全，但同一程序重新创建同标题窗口时仍可能碰到 Windows 句柄复用。后续应加入进程 ID 与进程启动时间等更稳定身份。
- DeepSeek 既有真实证据仍须与当前发布候选身份对账；千问的真实账号、网络和 `qwen3.7-plus` 可用性尚待准确 SHA 授权后的联网验收，模拟 HTTP 边界不能代替真实联网。
- Codex 普通聊天只保留 `codex-default` 描述并在生产策略下失败关闭，不提供真实普通聊天模型或 Usage；这不影响独立 Codex 编程 Agent。
- Provider 切换会把同一 Conversation 的既有历史交给新的数据目的地；UI 已明确提示，但仍需真实用户体验验收。
- 每轮重建完整 Conversation 历史并以字符上限保护；Token 预算、摘要和上下文裁剪尚未实现。
- DPAPI 保护静态密文，但不抵御已取得同一 Windows 用户权限、管理员权限或运行时内存读取能力的恶意程序。
- 语义意图只覆盖有限候选句式，保守回退是有意安全选择；不能把它宣传为完整自然语言操作理解。
- 没有长期记忆、RAG、向量库或跨 Session 检索；Conversation 历史和 `ai_invocations` 都不是长期记忆。
- schema v8 对 V0.3.0 的 schema v7 向前不兼容；回滚必须同时管理 pre-v8 数据备份。
- 安装包未签名，语音模型未纳入可分发方案。
