# 《元枢 V2 阶段 1 完成报告》

报告日期：2026-08-24
阶段：V2 阶段 1“统一会话中枢”
版本：V0.3.0
版本标签：`v0.3.0-stage1`（待最终冻结创建）

## 总结结论

阶段 1 的核心功能和真实用户流程已经达到目标：元枢现在能维持一个明确的当前话题，连续对话不再每句话重新开始；用户插话会真正停止旧 Provider / Codex 工作；缺项目、文件或窗口同意时会保留原请求，并在同一 Turn 补齐后继续；聊天、窗口观察、受控动作和编程任务共享一套状态事实。

已经确认的真实结果：

- 真实 Codex 连续对话 10/10；
- 真实 Codex 打断 3/3，每次取消后观察 10 秒均无迟到旧回答；
- 项目、文件与单窗口同意/拒绝 5/5；
- 独立桌面产品验收 2/2。

全量自动化与最终 Release 真实桌面复验均已完成，第四轮独立终审未发现 P0、P1 或 P2 问题。

> **阶段 1：通过。**

最终源码提交、`v0.3.0-stage1` 标签和提交后重建安装包的 SHA-256 将在版本冻结动作完成后补入。这三项属于版本身份记录，不改变本报告已经确认的功能、测试和真实用户流程结论。

## 1. 原来存在什么问题

V0.2.1 的聊天、电脑动作、窗口观察和编程任务都能单独工作，但缺少共同的“当前话题与当前任务”：

1. 首页每次输入都会创建新 Conversation，导致“刚才那个”“第二个”“继续”等连续表达失去上下文。
2. DesktopClient 停止等待不等于 Host 和 Provider 停止；旧 Codex 进程可能继续完成，并在几秒后把旧答案写回。
3. 缺项目、文件或窗口同意时，原始请求没有统一状态保存，用户补充后往往需要重说。
4. ConversationService、动作计划、窗口操作、编程 Task 和不同页面各自维护当前状态，容易出现串线、重复执行、卡死或旧结果覆盖。
5. 编程任务和聊天没有明确并发规则；应用重启也没有统一决定哪些状态恢复、哪些工作中断。
6. DesktopClient 依赖频繁全量刷新，状态更新不够及时，也会产生无意义 IPC 和数据库读取。

## 2. 实际修改了什么

### 会话领域与存储

- 新增 Session / Turn 领域模型和 `ISessionStore`。
- SQLite 升级到 schema v7，新增 `sessions`、`session_turns`、唯一约束、查询索引和结构化 Expected/Plan Target 字段。
- 保存当前 Session、项目上下文、原始请求、输入方式、工作类型、阶段、缺失上下文、Expected Intent/Target、Canonical Plan Target、关联 Conversation Turn / Task / Operation / 文件 / 单窗口及结果。
- 从 schema v5 或中间 v6 迁移前自动创建 pre-v7 数据库备份。

### Host 统一编排

- 新增 `SessionCoordinator`，成为首页会话、动作、窗口观察和编程任务的唯一协调入口。
- 建立当前 Session 串行切换、每 Turn 的补充/确认/取消串行门、前台 Turn 互斥、后台编程任务并发、幂等、乐观并发版本、终态保护和跨 Session 归属校验。
- 新输入会先取消旧前台工作；编程任务可在后台继续。
- Host 重启时恢复当前 Session，运行中工作中断且不自动重放；已有 Conversation / Task 终态会重新对账。

### 真取消

- Session Turn 的取消令牌连接到 ConversationService 与 Provider。
- Codex Provider 收到取消后终止真实进程树。
- 窗口观察使用 Operation ID 取消；编程 Turn 取消连接到真实 Task 取消。
- Conversation 的取消与成功提交在 SQLite 中争夺唯一终态；取消生效后 ConversationService 不再写 Assistant 消息。Session Turn 终态与版本检查继续拒绝迟到成功。

### 上下文补齐

- 项目：`WaitingForProject` → 选择已授权项目 → 同一 Turn 重新规划并继续。
- 已撤权或失效的 Session 项目会被清除，并在同一 Turn 回到 `WaitingForProject`。
- 文件：`WaitingForFile` → 选择存在文件 → 同一 Turn 重新规划，继续保留可见确认。
- 窗口：`WaitingForWindow` → 用户切换后重试 → `WaitingForWindowConsent`。
- 窗口同意只授权显示的单个窗口；拒绝安全取消。目标变化后旧确认失效。

### DesktopClient 与 IPC

- 首页改为提交 Session 输入，不再每句话直接新建 Conversation。
- 电脑操作页的应用/网站入口也改为创建 Session Turn；应用绑定已发现的 Application ID，网站绑定规范化完整 HTTPS URI。Session Input、Assistant Plan 与 Turn 传递结构化期望/实际目标，Host 在初次规划和确认执行两处做 Ordinal 精确比较，不一致就结束 Turn 且不执行。
- 新增统一 Session 状态卡、“新话题”、“停止”、项目/文件选择、窗口重试、窗口同意/拒绝和确认控件。
- 新增 `SessionUiPresenter`，把每个 Host 阶段映射为唯一 UI 文案与控件组合；Coordinator 实例 ID、启动时间与版本共同拒绝旧 Host / 旧版本快照。
- IPC 协议升级到 v7，新增 Session DTO 与命令、`sessions.wait` 长轮询。
- Named Pipe 增加监听失败重试、并发上限、忙碌响应、首请求超时和坏请求帧隔离；实时 Session 状态使用变化唤醒，非实时页面保留低频刷新。

## 3. Session Coordinator 最终架构

```text
DesktopClient
  │ 文字 / 语音 / 新话题 / 停止 / 选择与同意
  ▼
DesktopProtocol v7
  ▼
SessionCoordinator
  ├─ Session：当前明确话题 + Conversation + 当前项目
  ├─ Turn：一次原始请求 + 阶段 + 缺失上下文 + 关联工作
  ├─ 前台规则：当前 Session 串行切换；新输入先取消旧 Turn
  ├─ 后台规则：编程 Task 可继续，用户可聊天
  ├─ 恢复规则：恢复当前 Session，运行中工作中断且不重放
  └─ 版本规则：每 Turn 串行门、幂等、乐观并发、终态拒绝迟到覆盖
       ├─ ConversationService → IConversationProvider → Codex
       ├─ AssistantCommandService → CapabilityPolicyEngine → Windows Skills
       ├─ Window Vision → 单窗口捕获 / 本机 OCR / UIA
       └─ LocalTaskEntryService → Codex Project Task
```

权限边界没有迁入 Coordinator。项目授权、文件确认、单窗口同意、确定性意图和动作白名单仍由原有安全组件决定。

## 4. 重要变化的文件与模块

| 范围 | 重要文件或模块 | 作用 |
|---|---|---|
| 领域模型 | `src/ScreenGuide.Core/Sessions/` | Session、Turn、阶段、缺失上下文与存储契约 |
| 会话存储 | `SqliteSessionStore.cs`、`SqliteSchema.cs` | schema v7、Session/Turn、Expected/Plan Target 持久化、恢复与并发更新 |
| 总协调 | `SessionCoordinator.cs` | 唯一会话入口、取消、续接、并发、恢复与快照 |
| 普通聊天 | `ConversationService.cs`、`CodexConversationProvider.cs` | 取消传播、Provider 终态与迟到输出保护 |
| 编程任务 | `TaskingModels.cs`、任务服务 | Session Turn 与真实 Task 取消/状态对账 |
| 意图 | `IntentPlanning.cs` | 区分缺项目、文件、窗口等可补齐上下文 |
| IPC | `DesktopIpcProtocol.cs`、`DesktopApiDtos.cs`、`DesktopApiClient.cs`、`DesktopApiDispatcher.cs` | protocol v7、Session 命令与增量等待 |
| IPC Host | `DesktopIpcHostedService.cs` | 连接上限、忙碌响应、首请求超时与监听恢复 |
| UI | `MainWindow.xaml(.cs)`、`SessionUiPresenter.cs` | 当前话题、统一状态、停止和上下文补充控件 |
| 发布 | `Directory.Build.props`、installer/scripts | V0.3.0 版本与安装包名称 |

## 5. 新增和强化了哪些测试

新增或强化的测试覆盖：

- `SqliteSessionStoreTests`：当前 Session、项目上下文、等待 Turn、结构化目标、v6 → v7 迁移备份和重启恢复；
- `SessionCoordinatorIpcTests`：十次输入保持一个 Session/Provider Thread、迟到回答拒绝；
- `SessionContextContinuationTests`：项目、文件、窗口同意/拒绝在同一 Turn 续接；
- `SessionCoordinatorAdversarialTests`：幂等、跨 Session 越权、并发确认、Provider 失败、长轮询、重启、目标变化；
- `SessionCoordinatorConflictTests`：编程与聊天并发、快速双输入、等待授权时取消、确认/取消竞态、真实 Task 取消、迟到成功、失效项目和重新确认；
- `SessionCoordinatorP1RegressionTests`：提交与新话题/切换并发、文件确认与替代输入并发、窗口同意与第二条输入并发，证明最多只有一个前台工作且被取消动作不执行；
- `DesktopIpcPressureTests`：多个长轮询、普通刷新、静默/坏帧客户端、并发上限与恢复；
- `SessionUiPresenterTests`：阶段到 UI 的唯一映射、等待控件、旧版本及旧 Host 快照拒绝；
- `IntentPlannerTests` 与 UI 目标匹配测试：`启动“GitHub Desktop”` 必须保持应用意图，网站替代计划不能复用应用确认；
- `DesktopSessionUiAutomationTests`：实际 Release WPF Client/Host、新话题、UI 停止真取消和窗口拒绝；
- Conversation、Codex Provider、Intent Planner、IPC 与原安全门禁的回归测试。

## 6. 自动化测试结果

| 项目 | 结果 |
|---|---|
| 自动化全量总数 | **363/363 通过** |
| 通过 / 失败 / 跳过 | 363 / 0 / 0 |
| locked restore | 通过 |
| Release build | 通过 |
| Release publish | 通过 |
| NuGet 已知漏洞检查 | 通过；官方源检查全 solution，无已知易受攻击的直接或传递依赖 |
| 本机安装—卸载—重装 | 为保护同 AppId 的现有 V0.2.0 安装、卸载登记与用户数据，安全保护下未执行；不计为自动化失败 |

项目分布：Agent.Codex 19、AI.Core 20、Core 92、DesktopClient 36、DesktopHost 112、Tasking 50、Voice 14、Vision 7、Skills 11、DesktopProduct 2，共 363。失败 0，跳过 0。

## 7. 真实用户流程验收结果

真实验收使用实际 Release DesktopClient、实际 DesktopHost、真实 Codex Provider、真实 Windows Notepad 和隔离用户数据，不以 Fake Provider 代替全部流程。

结果：

- Client/Host 启动与 IPC：通过；
- 同一 Session 连续聊天：通过；
- 用户插话和停止：通过；
- 选择项目后自动继续：通过；
- 选择文件后自动继续：通过；
- 单窗口同意：通过；
- 单窗口拒绝：通过；
- UI 状态与 Host 状态同步：通过；
- 失败/重启恢复：自动化与独立桌面产品验收通过。

最终 Release 综合结果：5/5，`Failed=0`、`FalseCompleted=0`。运行证据：`%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-182601`。

本机仍保留 V0.2.0（Client/Host `0.2.0.0`，提交 `23b0760`）。同 AppId 的临时安装和卸载会覆盖正式卸载登记，因此本阶段没有在该机器运行 `test-desktop-installer.ps1`。发布目录和实际 Release DesktopClient/Host 流程已经验收；完整安装—卸载—重装应在干净机执行。这是保护旧安装和用户数据的安全边界，不是阶段 1 功能失败。

## 8. 打断测试结果

- 真实 Codex 重复测试 3 次，3/3 通过。
- 每次在旧回答生成期间发出停止/替代请求。
- Provider / Codex 实际收到取消，不只是 UI 隐藏。
- 每次取消后观察 10 秒，旧回答没有重新出现。
- 旧回答没有覆盖新回答或把 Turn 从 `Cancelled` 改回成功。
- UI Automation 另用 FakeCodex 子进程标记验证：点击 UI 停止后，子进程树被终止，等待 5 秒未产生迟到标记。

## 9. 十轮连续对话完整验收

结果：10/10 通过。

验收内容包含：

1. 建立“开发 AI 助手”的主题；
2. 追问第一步；
3. 使用“第二个”引用候选；
4. 使用“继续”要求展开；
5. 使用“刚才那个”回指前文；
6. 使用“不是这个，我说的是……”纠正对象；
7. 引用前面某句话继续讨论；
8. 继续细化同一主题；
9. 再次回指较早内容；
10. 总结当前话题。

十轮始终属于同一个 Session，并复用同一 Conversation / Provider Thread；没有出现每句话新开 Conversation、回答串线或旧回答覆盖。

## 10. 项目、文件、窗口补充流程

项目、文件和窗口相关场景合计 5/5 通过：

- 缺项目：进入 `WaitingForProject`，选择已授权项目后自动继续同一原始 Turn，并执行真实隔离编程任务；
- 缺文件：进入 `WaitingForFile`，选择存在文件后自动继续同一原始 Turn，并经过本次可见确认；
- 缺窗口：进入 `WaitingForWindow`，切换目标后继续；
- 真实 Notepad 窗口同意：只捕获本 Turn 显示并获准的单个窗口，Turn 为 `Completed`；
- 真实 Notepad 窗口拒绝：Turn 为 `Cancelled`，不捕获、不执行。

另有自动化测试确认：另一个 Session 不能补项目或授予窗口同意；窗口/搜索目标变化后必须重新同意或确认；并发双确认最多执行一次。

## 11. 新发现的架构风险

这些风险不阻断阶段 1，但必须继续记录：

1. Session 快照仍读取完整 Turn 和 Conversation 消息；极长会话会增加 IPC 体积和 SQLite 查询成本。
2. ChangeVersion 是单个 Host 进程内的增量信号；Coordinator 实例身份与启动时间可划分 Host 重启前后的快照世代，但它仍不是跨进程持久事件序号。
3. `SessionCoordinator.cs` 与 `MainWindow.xaml.cs` 已较大；后续应在现有测试保护下逐步拆分，不能为了整洁重写。
4. 编程任务状态仍由 Host 内部短间隔监视器同步；后续可以考虑直接任务事件，但阶段 1 不引入新消息基础设施。
5. schema v7 不是 V0.2.1 向后兼容格式；程序回滚必须同时使用 pre-v7 数据备份或隔离数据目录。
6. 单窗口授权当前比较窗口句柄、进程名和标题，但尚未保存进程 ID / 启动时间；同一程序重新创建同标题窗口时仍有极端句柄复用风险，后续需加固稳定身份。
7. 真实验收证据目前位于本机隔离运行目录；最终冻结必须把摘要、哈希和产物关联写入基线，而不能只依赖本机目录长期存在。
8. 当前机器因保护已安装 V0.2.0 和同 AppId 卸载登记，没有重复完整安装—卸载—重装；对外分发前仍需在干净机补做该发布生命周期验收。

## 12. 应留到阶段 2 或以后解决的问题

阶段 2：

- Prompt 集中注册、版本和自动评测；
- Provider 能力、超时、错误恢复、Token/成本记录；
- 用第二个真实普通聊天 Provider 验证可替换性。

阶段 3：

- 用户长期记忆、来源、置信度、过期、冲突、纠错和删除；
- RAG/检索是否必要以及如何与 Session 分层；
- 严禁把完整历史无脑塞给模型。

阶段 4：

- 长 Session 快照性能、内部任务事件化和大文件渐进拆分；
- 语音模型分发/许可证、安装包签名和升级自动化。

DeepSeek、长期记忆、RAG、向量数据库、复杂多 Agent 产品功能、手机端和扩大 Tool Calling 均未在阶段 1 实现。

## 13. Git 提交与版本状态

- 当前开发分支：`codex/v2-stage1-session-coordinator`。
- V0.2.1 回滚标签：`v0.2.1-baseline`。
- V0.3.0 最终源码提交：**待最终冻结填入**。
- V0.3.0 标签：`v0.3.0-stage1`，**待最终冻结创建**。
- 自动化全量结果：363/363 通过，失败 0，跳过 0。
- 正式安装包 SHA-256：**待最终冻结填入**。

版本冻结采用两提交方式：第一笔提交包含完整源码与文档，并在该提交创建 `v0.3.0-stage1`；从标签目标重建安装包并计算 SHA-256 后，再用第二笔仅含证据的文档提交回填提交号和哈希，避免让标签提交引用它自身尚不存在的哈希。

回滚不能使用破坏当前成果的清理命令。源码应在新目录/worktree 检出 `v0.2.1-baseline`；数据应使用升级前 pre-v7 备份或独立目录，不能让 V0.2.1 打开 schema v7 数据库。

## 14. 是否建议进入阶段 2

**建议进入阶段 2。**

阶段 1 的产品、架构、安全、自动化和真实用户流程均已通过。开始阶段 2 代码前，应先完成最终源码提交、创建 `v0.3.0-stage1` 标签，并把提交后重建安装包的 SHA-256 填入版本基线，确保 V0.3.0 的身份与产物可以精确追溯。
