# 元枢 V2 阶段 1：统一会话中枢设计

状态：阶段 1 已通过，设计已经实现并完成 363/363 自动化与真实 Release 桌面流程验收；本文保留为设计边界，最终 As-Built 以 `ARCHITECTURE.md`、`PRODUCT.md` 和 `docs/baselines/YUANSHU_V2_STAGE1_COMPLETION_REPORT.md` 为准。V0.3.0 的最终提交、标签和安装包哈希在版本冻结时补入基线。

## 1. 本阶段解决什么

V0.2.1 已经能分别完成普通聊天、受控电脑操作、单窗口观察和编程任务，但这些流程各自保存“当前状态”。主页语音普通问答还会每句话新建 Conversation；插话只取消 DesktopClient 的等待，Host 中的 Provider 仍可能继续；缺项目、文件或窗口同意时，原始请求不会被保存和续接。

阶段 1 新增唯一 `SessionCoordinator`，由 DesktopHost 持有。DesktopClient、ConversationService、电脑动作、窗口观察和编程任务不再各自决定当前用户流程。

## 2. 统一边界

```text
DesktopClient（语音 / 文字 / 可见选择与确认）
                    │
                    ▼
             SessionCoordinator
       ┌────────────┼──────────────┐
       ▼            ▼              ▼
Conversation   AssistantCommand   LocalTask
Provider       + 安全策略          + Codex Task
       └────────────┼──────────────┘
                    ▼
          Session / Turn 持久状态
```

- `Session`：一个明确话题，关联一个现有 Conversation，保存当前项目上下文和是否为当前话题。
- `Turn`：用户的一次请求，记录输入、意图、当前阶段、缺少的上下文、关联的对话 Turn / 编程 Task / 窗口操作和最终结果。
- `Conversation`：继续负责聊天消息和 Provider Thread，不改造成长期记忆。
- `SessionCoordinator`：只协调状态、取消和续接，不授予项目、文件、窗口或动作权限。
- 原有 Planner、CapabilityPolicyEngine、项目授权、文件确认和单窗口同意仍是权限事实来源。

## 3. 状态

用户可见阶段包括：理解中、回答中、执行中、查看窗口、编程任务运行中、等待项目、等待文件、等待窗口、等待窗口同意、等待确认、等待用户补充、已完成、已取消、失败和被重启中断。

编程任务允许在后台继续，用户可在同一 Session 继续普通聊天；其 Task ID 和状态仍归当前 Session 管理。普通回答、窗口观察和待确认请求属于前台 Turn，新输入会先取消旧 Turn，再开始新 Turn。

## 4. 连续会话规则

- 首次输入自动创建当前 Session；之后默认继续同一 Session 和同一个 Provider Thread。
- 用户点击“新话题”才明确创建新 Session；阶段 1 不使用容易误判的自动超时切话题。
- 应用重启后恢复上次当前 Session；正在运行的调用标记为中断，不自动重放。
- 普通聊天和编程任务共享 Session，但 Provider 只接收当前会话所需的简短状态摘要，不建设长期记忆或检索。
- 切换项目只更新当前 Session 的项目上下文，不自动合并其他 Session 的历史。

## 5. 打断保证

1. 每个前台 Turn 拥有独立 CancellationTokenSource。
2. 插话先取消 Session Turn，再把取消传给 ConversationService 和 Provider / Codex 进程树。
3. Coordinator 等待旧 Turn 进入终态后才启动替代 Turn。
4. 持久层只允许仍为运行态且版本匹配的 Turn 写入结果；取消后的旧结果不能覆盖新状态。
5. UI 只渲染当前 Session 的最新版本，旧版本增量会被忽略。

## 6. 上下文补齐

- 缺项目：保存原始请求并进入 `WaitingForProject`；选择已授权项目后重新规划同一 Turn。
- 缺文件：进入 `WaitingForFile`；文件选择只补齐上下文，执行前仍保留可见确认。
- 缺窗口：进入 `WaitingForWindow`；用户切回目标窗口后继续。
- 缺窗口同意：进入 `WaitingForWindowConsent`；拒绝立即安全取消，同意只授权本 Turn 显示的单个窗口。
- 项目、文件和窗口变化后都会重新规划；若目标变化，不沿用旧确认。

## 7. 状态更新

DesktopClient 使用本机 Named Pipe 长轮询等待 Session 版本变化；快照携带 Coordinator 实例 ID、启动时间和版本，区分 Host 重启前后的新旧增量。项目清单、历史任务等非实时数据改为低频兜底刷新，并在用户操作后立即刷新。不引入网络服务、消息队列或新的第三方基础设施。

## 8. 测试公共边界

- `ISessionStore`：迁移、当前 Session、Turn 状态和重启恢复。
- Session IPC：连续十轮、插话、快速双输入、幂等和失败恢复。
- Provider 边界：取消令牌与进程树终止，取消后无 Assistant 消息。
- 上下文补齐 IPC：项目、文件、窗口同意/拒绝后续接同一 Turn。
- DesktopClient：当前状态、选择/拒绝/取消控件和增量更新。
- 真实 Release DesktopClient：真实 Codex 部分连续对话与真实进程取消；安全窗口和项目流程使用低风险隔离数据。

## 9. 明确非目标

不接 DeepSeek，不更换普通聊天模型，不做长期记忆、RAG、向量库、复杂多 Agent 产品功能、手机端或任意 Tool Calling，也不扩大 V0.2.1 的操作白名单。
