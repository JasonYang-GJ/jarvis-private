# 元枢 V2 阶段 2：可替换 AI 大脑与模型路由设计

状态：**V2 阶段 2 最终验收已通过，按 V0.4.0 冻结。** 普通聊天发布目标是 DeepSeek + 千问，千问只能由用户手动选择；Codex 普通聊天保持安全停用，独立 Codex 编程 Agent 已通过工作负载隔离回归。精确源码、标签和产物身份见 `docs/baselines/V0.4.0_STAGE2.md`。

## 1. 目标与边界

阶段 2 把普通聊天业务与具体模型供应商分开，使 Session、Conversation、安全策略、编程任务、UI 和未来记忆系统不依赖某一个模型。

本阶段只建设：

- 供应商无关的 Chat Model 契约；
- Provider Registry、Model Router、Prompt Registry；
- 安全停用的 Codex 普通聊天适配器，以及 DeepSeek、千问普通聊天 Provider；
- Provider/Model 设置、健康状态和安全凭据；
- 只建议、不授权的 AI 语义意图层；
- AI 调用追踪、故障映射、取消和必要 UI/IPC；
- 自动化与真实 Provider/桌面验收边界。

本阶段不建设长期记忆、RAG、向量数据库、用户画像、复杂多 Agent 产品系统、手机端、云端远程控制、大规模 Tool Calling 或新的现实操作能力。

## 2. 当前正式架构

```text
DesktopClient 设置页
  ├─ 普通聊天：Provider / Model / 配置状态 / 数据去向
  ├─ 编程任务：Codex（独立显示，不随聊天切换）
  └─ Key 写入、删除、健康检查
          │ 当前用户 Named Pipe，protocol v8
          ▼
DesktopHost
  ├─ SessionCoordinator（阶段 1 边界保持不变）
  │    └─ ConversationService
  │         └─ RoutedConversationProvider
  │              ├─ 从 ConversationStore 重建完整会话历史
  │              ├─ Prompt Registry 取 chat.general@1
  │              ├─ Model Router 为本 Turn 冻结路由
  │              └─ AI Invocation Store 记录调用证据
  ├─ AssistantCommandService
  │    ├─ DeterministicIntentPlanner
  │    ├─ ModelSemanticIntentSuggester（仅不可信建议）
  │    └─ 确定性重规划 + CapabilityPolicy / 原有权限门禁
  ├─ Provider Registry
  │    ├─ CodexChatModelProvider
  │    ├─ DeepSeekChatModelProvider
  │    └─ QwenChatModelProvider（手动备用）
  ├─ FileAiSettingsStore（只保存 Provider/Model 路由）
  └─ WindowsDpapiCredentialStore（只保存加密凭据）
```

新增 Provider 时，原则上只新增 `IChatModelProvider` 实现并完成注册、能力描述和测试，不应修改 SessionCoordinator 或电脑操作权限体系。

## 3. 统一 Chat Model 契约

`ScreenGuide.AI.Core` 中的 `IChatModelProvider` 统一了：

- System Prompt、会话消息和 Model ID；
- Temperature、最大输出 Token、Top P、Seed 等可选参数；
- 文字、JSON Object、JSON Schema 三种请求格式声明；
- 流式增量回调、Usage、Finish Reason 和 Provider Metadata；
- 健康检查、每 Turn 取消和统一错误分类；
- Provider ID、显示名、模型、数据去向、是否离开本机、凭据类型和适用工作负载。

能力通过 `ChatModelCapabilities` 明确声明，包括 Streaming、Tool Calling、Vision、JSON Object、JSON Schema、Reasoning 和 Context Window。调用方按能力选择请求格式，不假定所有 Provider 完全相同。

当前普通聊天 Provider：

| Provider | 注册模型 | 代码声明能力 | 凭据与数据去向 | 当前验收状态 |
|---|---|---|---|---|
| Codex | `codex-default` | `None`，不声明增量流式、结构化输出或 Tool Calling | 普通聊天生产策略安全停用；编程 Agent 继续使用独立的 Codex 连接器 | `PolicyDisabled` 失败关闭，不探测或启动 CLI；编程任务不受影响 |
| DeepSeek | `deepseek-v4-flash`、`deepseek-v4-pro` | Streaming、JSON Object、Reasoning；不声明 JSON Schema/Tool Calling/Vision | API Key；固定发送到 `https://api.deepseek.com` | 真实 Health/Chat/Streaming/Cancellation/Audit 证据已冻结 |
| 千问 | `qwen3.7-plus` | Streaming、JSON Object；不声明 Tool Calling/Vision/Reasoning | 独立 API Key；固定发送到 `https://dashscope.aliyuncs.com` | 手动备用；真实 Health/Chat/Cancellation 及审计通过 |

这里的“注册模型”只表示当前代码允许选择的 Model ID，不等于已经完成真实账户可用性验证。

### 3.1 R2 健康、错误与重试合同

- 核心健康状态为 `NotChecked`、`NotConfigured`、`Healthy`、`Degraded`、`Unavailable`、`PolicyDisabled`。配置状态与健康状态分离：凭据仓库是“是否已配置”的真值，连接失败不能把已保存凭据误报为缺失；无凭据型 Provider 的配置状态为 `NotRequired`。
- 为兼容现有 protocol v8，Host 只在 DTO 边界把 `PolicyDisabled` 投影为 `Unavailable`，同时保留安全策略说明和 `IsConfigured=true`；核心层不丢失 `PolicyDisabled` 语义。
- 稳定错误种类包括 `Configuration`、`Unauthorized`、`Authorization`、`PolicyDisabled`、`InsufficientBalance`、`RateLimited`、`Timeout`、`Network`、`Unavailable` 等。DeepSeek/Qwen 缺 Key 属于 `Configuration`，401 属于 `Unauthorized`，403 属于 `Authorization`，402 属于 `InsufficientBalance`，429 属于 `RateLimited`，5xx 属于 `Unavailable`。
- `ChatModelError.Code` 只用于安全诊断和审计。新代码使用不超过 80 个 ASCII 字符的 `owner.reason` 形式；旧下划线代码仍可读取，不要求迁移。用户可见 `FailureCode` 只由稳定错误种类产生，不依赖 Provider 诊断码。
- `IsRetryable` 由 AI Core 统一计算：只有 `RateLimited`、`Timeout`、`Network`、`Unavailable` 可重试。`RetryAfter` 仅接受限流或可信服务不可用响应，且必须大于 0、不超过 24 小时；这只是诊断事实，不会触发自动重试或 fallback。
- Provider 错误消息、请求 ID 和诊断码进入 Host 前必须经过固定边界；API Key、Bearer、用户路径、Prompt、Conversation、原始响应和内部堆栈不得进入用户可见错误。普通聊天失败后不会把同一正文改发到另一个 Provider。

## 4. Provider Registry

`ChatProviderRegistry` 负责：

- 注册多个普通聊天 Provider；
- 拒绝空 Provider、重复 Provider ID、重复 Model ID、未知工作负载位、未知能力位、无效描述元数据和没有普通聊天能力的 Provider；
- 按 Provider ID + Model ID 解析具体实现；
- 向 Host/UI 提供统一描述。

Registry 不保存当前会话状态、不保存 Key、不决定权限，也不承担自动降级。

## 5. Model Router

第一版 `ModelRouter` 只实现明确、可解释的路由：

1. 从 `IAiSettingsStore` 读取普通聊天默认 Provider/Model；
2. 在一个 Turn 开始时冻结 `FrozenChatModelRoute`；
3. 整个 Turn 固定使用该 Provider/Model，不被中途设置变化影响；
4. 记录 Turn 到真实 Provider 的活动映射，使取消能到达实际调用；
5. Provider 或 Model 不存在时返回明确错误。

设置切换只影响下一轮普通聊天。系统没有静默 fallback：Provider 故障时不会在未告知用户的情况下把同一内容发给另一个供应商。用户若要切换，必须在设置中明确选择。

同一 Session 可在相邻 Turn 使用不同 Provider。连续性来自元枢自己的 `ConversationStore`：每次调用都重建该 Conversation 的消息历史，而不是把供应商 Thread 当作唯一记忆。因此 A → B → A 切换不需要改 SessionCoordinator，也不要求两个 Provider 共享线程 ID。

## 6. Prompt Registry 与追踪

运行时 Prompt 位于 `prompts/runtime/`，当前注册：

- `chat.general@1`：供应商无关的普通聊天 System Prompt；
- `intent.semantic@1`：只建议、不授权的语义意图 Prompt。

`registry.json` 为每个 Prompt 记录 ID、版本、用途、内容文件、SHA-256、适用 Provider、创建时间和修改原因。Host 加载时会：

- 拒绝绝对路径和越出 Registry 根目录的内容文件；
- 重新计算内容 SHA-256，不一致则启动失败；
- 校验 Prompt ID + 版本唯一；
- 校验当前 Provider 是否在适用范围内。

SQLite `ai_invocations` 记录 Provider、Model、Prompt ID/版本/哈希、数据去向、状态、时间、Usage、Provider Request ID 和安全失败码。它不保存 API Key、Authorization Header、Prompt 正文或完整 Conversation 副本。

固定小型评测集位于 `tests/ScreenGuide.AI.Core.Tests/TestData/prompt-evaluation.v1.json`，覆盖普通聊天、连续指代、歧义、用户纠正、安全请求及缺项目/文件/窗口等边界。它是回归门，不是对真实模型质量的最终证明；真实 Provider Prompt 表现仍需总控验收。

## 7. 凭据与配置

普通 Provider/Model 路由写入用户本地应用数据目录中的 `settings/ai-settings.json`。该文件只保存非敏感 ID，不保存 Key。

DeepSeek 与 Qwen Key 通过 `WindowsDpapiCredentialStore` 分别保存到用户本地应用数据目录 `secrets/<provider>.bin`：

- 使用 Windows DPAPI `CurrentUser` 保护；
- 额外把 Provider ID 绑定为 entropy，避免密文被当作另一个 Provider 的凭据使用；
- 临时文件写入后原子替换；
- 解密后只通过短生命周期 lease 交给 Provider；
- byte/char 缓冲在使用后尽量清零；
- UI、IPC 响应和状态接口只返回 Missing/Configured/NotRequired，不读回完整 Key；
- 保存、异常、日志、Crash 和 IPC 文本经过敏感信息清理，授权头和常见 Key 形式会被遮盖。

DPAPI 解决“密钥明文落盘”问题，但不是对已取得同一 Windows 用户权限、管理员权限或能读取本进程内存的恶意程序的防护。真实安全验收仍需检查 Git、SQLite、日志、Crash、UI 和运行目录中是否出现完整 Key。

## 8. Codex 普通聊天与编程 Agent 分离

- 普通聊天 Codex 适配路径：`RoutedConversationProvider → ModelRouter → CodexChatModelProvider`；生产策略在探测/启动 CLI 或发送正文前以 `ProductionDisabled`/`PolicyDisabled` 失败关闭。
- 编程任务路径：`LocalTaskEntryService / AgentTaskExecutionService → CodexConnector / CodexSkillAdapter`。

两条路径的契约、调用生命周期和用户设置互相独立。Codex 普通聊天适配器安全停用不影响编程 Agent；在 DeepSeek 与千问之间手动切换普通聊天也不会修改编程 Agent、项目授权、Git 范围或 TaskEvidence。

## 9. 语义意图：模型只建议

阶段 2 没有让模型直接产生可执行权限。当前流程为：

1. 先由 `DeterministicIntentPlanner` 规划；
2. 只有确定性结果仍是普通聊天，且文本命中有限候选条件时，才调用当前 Chat Provider；
3. 语义 Provider 只收到当前用户文字，不收到本机路径、窗口身份、权限或确认状态；
4. 优先按 Provider 能力请求 JSON Schema，其次 JSON Object，再次 Text；
5. 本机严格解析器要求恰好五个字段，校验枚举、长度、置信度、歧义和缺失上下文组合；
6. 只有置信度不低于 0.80 且非歧义时，才可能提供 `CodingTask`、`OpenFile` 或 `DescribeForeground` 的“类型建议”；
7. 模型给出的 target 不被信任；Host 用真实本机上下文重新运行确定性 Planner；
8. 项目、文件、窗口、确认和 CapabilityPolicy 仍由原有确定性边界决定。

模型输出非法、Provider 失败、置信度不足或表达歧义时，返回空建议并保留普通聊天/请求补充路径；绝不因为“看起来像同意”而执行现实操作。

## 10. 取消、故障和晚到结果

- `ModelRouter` 按 Turn 保存实际 Provider，`CancelAsync` 直接转发给它。
- Codex 普通聊天生产策略在接触 CLI 前失败关闭，不启动需要取消的进程；独立 Codex 编程 Agent 继续使用其既有进程树取消边界。
- DeepSeek 使用与 Turn 绑定的取消令牌取消真实 HTTP/SSE 读取，并在取消后拒绝迟到成功。
- Qwen 同样把 Turn 取消传给 HTTP/SSE，并等待活动调用结束；它只发布 `delta.content`，对 `reasoning_content` 仅做有界计数消费，任何 Tool Call 都失败关闭。
- `RoutedConversationProvider` 继续依赖阶段 1 的 Conversation/Session 唯一终态，取消后的回答不能重新插入消息或覆盖新状态。
- DeepSeek 把未配置、401、403、402、400/422、404/模型错误、429、5xx、超时、网络错误、非法响应和不安全重定向映射为稳定种类与安全大白话；响应体和 SSE 有大小上限。
- 不自动付费重试，不静默改发其他 Provider；Qwen 只能由用户手动选中，失败不会触发 DeepSeek/Codex 请求。

## 11. UI 与 IPC

protocol v8 新增：

- `ai.settings.get`；
- `ai.chat-route.set`；
- `ai.credentials.set` / `ai.credentials.delete`；
- `ai.provider.health`。

设置页明确分成“普通聊天大脑”和“编程任务”：

- 显示 AI 服务（Provider）、聊天模型（Model）、配置状态和健康状态；
- 明示数据发送位置、内容会离开本机，以及切换后同一会话的既有历史会随下一条消息发送到新 Provider；
- Codex 显示“使用 Codex 登录账号”，隐藏 Key 输入；
- API Key 使用 PasswordBox，保存尝试后清空，不回显；
- 删除 Key 前需要可见确认；
- 当前运行中的回答不因设置变化中途换 Provider。

设置 IPC 只负责配置，不创建第二套 Session 或“当前任务”状态。

## 12. 数据库迁移与回滚

SQLite schema v8 新增 `ai_invocations`。从任一旧 schema 升级到当前 schema 前，`SqliteTaskStore` 会在数据库同目录建立 `tasking.pre-v8-from-v<旧版本>-<时间>.backup.db`，再执行迁移；迁移失败会报告备份位置。

回滚边界：

- 上一源码基线 `v0.3.0-stage1` 保持可达；
- V0.3.0 只支持 schema v7，不能直接打开阶段 2 的 schema v8 数据库；
- 回滚程序时必须先保留 schema v8 正式数据库，再使用自动生成的 pre-v8 备份或独立数据目录；
- 不得用 `git reset --hard`、`git clean` 或覆盖用户数据库完成回滚。

## 13. 验证状态

### 已实现且有开发期自动化覆盖

- Chat Model 契约、能力、错误和工作负载边界；
- Provider Registry 与 Model Router 的注册、冻结路由、A → B → A 和取消映射；
- Prompt 路径/哈希/Provider 约束及固定评测集；
- Codex 普通聊天安全停用边界，以及 DeepSeek/千问 Provider 的请求、故障、取消和晚到结果保护；
- DPAPI 保存、替换、删除、损坏密文、短生命周期 lease 和敏感信息清理；
- AI 设置 Service/IPC/UI 逻辑；
- 语义输出严格解析、置信度/歧义策略、Prompt 注入与权限绕过拒绝；
- schema v7 → v8 迁移、备份和阶段 1 Conversation 保留；
- 普通 Chat Provider 与 Codex 编程 Agent 工作负载分离。

以上边界已通过阶段 2 定向自动化与真实验收。R4 复用 R1/R2/R3 已冻结证据，不重复发送 DeepSeek 请求或机械跑 600+ 全矩阵。

### 已完成总控真实验收

- DeepSeek 既有真实证据与集成候选的模型/调用审计身份已对账并冻结；
- Qwen `qwen3.7-plus` 在授权的精确 SHA 上完成真实 Health、Ordinary Chat 和 Cancellation，无 retry/fallback/resend；
- 实际 Release DesktopClient/Host 的 Provider/Model、凭据状态、数据去向、路由和取消证据通过；
- 普通聊天选 Qwen 时，真实 Codex 编程 Task 仅 1 Task/1 attempt，项目文件范围、真实测试和 TaskEvidence 通过，普通聊天 Provider 请求为 0；
- R4 离线 Release 定向 QA 173/173 及集成后定向 smoke 通过；最终 Git/标签/产物身份收口到 V0.4.0 基线文档。

## 14. 当前风险与已解除历史项

- 真实 Provider 结论只绑定已验收的精确 SHA；日后修改 Provider/模型合同时必须重新申请最小真实请求预算。

### 已解除的冻结前历史项

- 千问的真实账号、区域网络和 `qwen3.7-plus` 可用性尚未在准确 SHA 授权下确认，模拟 HTTP 测试不能替代真实联网。
- 上述“千问真实账号尚未确认”是冻结前历史风险，已由 `f7506a6013d83318572c63865607d78861e669bc` 的授权真实验收解除，不再是当前阻塞。

### 当前保留风险
- Codex 普通聊天适配器只保留 `codex-default` 描述并在生产策略下失败关闭，不提供真实普通聊天模型或 Usage；它不属于阶段 2 发布目标。
- Provider 切换会把同一 Conversation 的既有历史发送到新数据目的地；UI 已提示，但用户仍需理解这一隐私影响。
- 每轮重建完整 Conversation 历史，当前以字符上限保护；长会话的 Token 估算、摘要和上下文裁剪属于后续工程，不是长期记忆。
- DPAPI 只保护本机静态密文，不抵御同用户高权限恶意进程或运行时内存读取。
- 语义意图当前只覆盖有限候选句式；保守回退是有意安全选择，不等于完整自然语言理解。
- schema v8 对 V0.3.0 是向前不兼容的；程序回滚必须同时使用 pre-v8 备份或隔离数据目录。

## 15. 阶段 3 边界

阶段 3 才考虑用户长期事实、项目状态、历史决策、相关性检索、来源/置信度/过期/冲突/删除和上下文压缩。阶段 2 的 Conversation 历史、AI 调用审计和 Provider Thread 都不能冒充长期记忆。
