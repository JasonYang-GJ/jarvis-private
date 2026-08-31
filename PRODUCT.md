# 元枢产品事实（V0.6.0 / Stage 4 Final Freeze）

> 当前产品事实的唯一入口。更新时间：2026-08-30。V0.6.0 / Stage 4 已完成 Final Freeze，S4-R1 Window Identity v2、S4-R2 本机真实使用评测、S4-R3 有界 Session 投影与 S4-R4 runtime gate 有界化均为 `INTEGRATED_PASS`。正式标签 `v0.6.0-stage4` 指向 C0 源码提交 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`；本 C1 只记录标签源码构建与产物证据，不改变标签或二进制身份。安装包仍未获对外分发放行，不新增 S4-R5；Stage 5 只有 Charter/Preflight，实施仍未授权。

## 产品定位

元枢是 Windows 本机学习与操作助手。它在用户可见、明确授权的前提下理解当前话题和当前单个窗口，给出中文帮助，并只执行少量经过白名单限制的低风险动作。

## 状态说明

- **阶段 1 保留事实**：V0.3.0 的 Session、真取消、上下文补齐、安全门禁和真实桌面证据保持有效。
- **阶段 2 正式能力**：统一 Chat Model、Provider Registry、Model Router、Prompt Registry、DPAPI 凭据、安全停用的 Codex 普通聊天适配器、DeepSeek/千问 Provider、设置 UI/IPC、AI 调用审计和只建议不授权的语义意图边界。
- **发布合同**：普通聊天发布目标是 DeepSeek + 千问；千问仅作手动备用，无自动 fallback、retry 或跨 Provider resend。Codex 普通聊天保持 `ProductionDisabled`/`PolicyDisabled`，独立 Codex 编程 Agent 不随聊天 Provider 改变。
- **阶段 3 R1 / R2 / R3**：已有本机加密记忆账本和确定性预览；用户可为单个 Turn 选择记忆，并在查看完整 Provider、HTTPS 去向、项目绑定和正文后单次确认发送。默认仍为 0 条，不自动提取或后台发送。
- **阶段 4 R1 / R2**：单窗口授权已升级为 Host 可信完整身份；独立人工 Runner 已完成真实麦克风、单窗口 OCR、停止和窗口身份变化验收，不加入产品遥测、网络或 Provider 路径。
- **阶段 5 Charter / Preflight**：推荐方向为“安全分发与升级准备”，仅规划许可清单、隔离安装生命周期、签名/release identity 和最终分发验收。实施仍为 `NOT_STARTED / NOT_AUTHORIZED`，不改变当前产品行为或 Stage 4 冻结身份。

详细代码边界见 [阶段 2 AI 模型路由设计](docs/V2_STAGE2_AI_MODEL_ROUTING_DESIGN.md)。
Stage 5 的唯一推荐章程见 [Stage 5 Charter](docs/V2_STAGE5_CHARTER.md)。

## V0.3.0 已冻结能力（阶段 2 保留）

### 统一会话与连续对话

- DesktopHost 新增唯一 `SessionCoordinator`（会话中枢）。首页普通聊天、受控电脑动作、单窗口观察和编程任务都登记到同一个 Session / Turn 状态模型，不再由各页面各自维护互相冲突的“当前状态”。
- `Session` 表示一个明确话题；`Turn` 表示用户在该话题中的一次请求。每个 Session 关联一个 Conversation，因此普通聊天可以继续同一个 Provider Thread。
- 首页首次输入会创建当前 Session，后续输入默认继续该 Session。只有用户明确点击“新话题”或选择另一个会话才切换，不使用自动超时猜测新话题。
- 应用或 Host 重启后恢复上次当前 Session；正在运行的工作会标记为 `Interrupted`，不会偷偷重放。等待项目等可安全补充的状态会保留原请求。
- 普通聊天和编程任务可以属于同一个 Session；编程任务在后台运行时，用户仍可继续聊天，二者有独立 Turn 和结果状态。

### 真正打断

- 新输入或“停止”会取消旧前台 Turn，并把取消信号传到 `ConversationService`、实际 Provider / Codex 进程树、窗口观察或编程任务。
- Coordinator 会等待旧工作结束，再让替代请求继续。对话取消和成功提交争夺数据库中的唯一终态；取消已生效后，迟到结果不能写入 Assistant 消息、覆盖新回答或恢复为成功。
- 已确认的真实 Codex 验收连续执行 3 次打断，3/3 均真正停止；每次停止后观察 10 秒，旧回答均未重新出现。

### 同一请求补齐上下文

- 缺项目时进入 `WaitingForProject`；用户选择已授权项目后，系统在同一个 Session、同一个 Turn 中继续原编程请求，不要求重说。
- 如果 Session 原来选择的项目已经撤权或失效，元枢会清除失效选择并回到 `WaitingForProject`，继续保留原请求。
- 缺文件时进入 `WaitingForFile`；选择存在的文件后继续原请求，并保留原有可见确认门禁。
- 缺目标窗口时进入 `WaitingForWindow`；用户切换窗口后可继续。需要查看时进入 `WaitingForWindowConsent`；同意只授权该 Turn 显示的单个窗口，拒绝则安全取消。
- 如果窗口或搜索目标在确认前发生变化，旧同意不会沿用，必须重新确认。

### 统一状态与增量更新

- UI 统一显示：理解中、回答中、执行中、查看窗口、编程任务运行中、等待项目、等待文件、等待窗口、等待窗口同意、等待确认、等待用户补充、已完成、已取消、失败和被重启中断。
- S4-R3 已将 Desktop IPC 升为 protocol v11：`sessions.current` 只返回最多 32 个 Turn 与 50 条最新消息的 bootstrap；`sessions.wait` 返回 `NoChange`、最多 32 个 Turn/50 条消息的 delta，或 `ResetRequired`。消息历史通过每页最多 50 条的 keyset 游标读取，精确确认继续由 Host 权威 Turn 查询负责；客户端显示缓存不授予权限。
- S4-R4 已把 Session/Turn 临时操作门闩集中到 Host 内部单例注册表；holder 与 waiter 共同计数，空闲 key 会被移除。它只负责同 key 串行，不读取或决定 Session/Turn 状态、权限和事件。
- IPC 服务具备监听失败重试、连接并发上限、忙碌响应和无请求连接超时，避免长轮询阻塞普通操作。
- SQLite schema v7 保存 `sessions` 与 `session_turns`，并新增结构化 `ExpectedIntentKind`、`ExpectedTarget`、`PlanTarget`：应用绑定已发现的 Application ID，网站绑定规范化完整 HTTPS URI，Host 在规划和确认两处都做区分大小写的精确比较。

## 继承自 V0.2.1 的正式能力

- Windows WPF DesktopClient 与独立 DesktopHost；客户端不直接访问数据库、Codex 或 Windows 动作。
- 可见的本机离线中文语音监听、自动分句、语音朗读、朗读时插话和回声过滤。
- 确定性意图识别：普通问答、打开已登记应用、打开受校验的 HTTPS 网站、在可靠搜索框搜索、读取当前单窗口、打开本次选择的文件、执行已授权 Git 项目中的编程任务。
- 明确语音只对“打开应用、打开网站、可靠搜索”产生一次性授权；窗口读取、打开文件和编程任务仍需要单独的可见确认。
- 仅操作 Windows UI Automation 唯一识别的可写搜索框；拒绝密码框和无法唯一识别的控件。
- 仅捕获用户确认的单个窗口；本机 OCR 与 UI Automation 联合分析；图像只在短期内存中存在，使用后清零。
- 编程任务通过 Codex Connector 执行，并记录 Git、测试和 TaskEvidence 证据。
- 自包含 win-x64 发布目录、简体中文 Inno Setup 安装包和隔离安装验收脚本。

## 当前 AI 大脑的真实状态

- 普通问答现在通过 `RoutedConversationProvider → ModelRouter → IChatModelProvider`，SessionCoordinator 和 ConversationService 不需要知道具体供应商。
- `ChatProviderRegistry` 当前注册三个普通聊天 Provider：安全停用的 Codex `codex-default`、DeepSeek 的 `deepseek-v4-flash`/`deepseek-v4-pro`，以及手动备用千问的 `qwen3.7-plus`。阶段 2 普通聊天发布目标是 DeepSeek + 千问；Codex 普通聊天不是发布目标。Qwen 真实健康、普通聊天和真取消已在授权预算内通过；它不会自动接管 DeepSeek 失败，也不会自动重试或跨 Provider 重发。
- 同一 Conversation 的消息历史由元枢 SQLite 保存，每个 Turn 会重新交给当时明确选择的 Provider。Provider A → B → A 不依赖供应商 Thread，也不创建新 Session。
- 切换只影响下一轮普通聊天；正在运行的回答保持原路由。系统没有静默 fallback，故障时不会在未告知用户的情况下把内容改发另一个供应商。
- Prompt 已迁移到 `prompts/runtime/`：`chat.general@1` 仍是无记忆普通聊天的默认 Prompt；`chat.general@2` 只用于用户逐 Turn 明确选择并完整确认的记忆出站；`intent.semantic@1` 始终不接收记忆。Registry 校验版本、适用 Provider、相对路径和内容 SHA-256；每次 AI 调用把 Prompt ID/版本/哈希、Provider、Model、目的地、状态和 Usage 写入 `ai_invocations`，不保存 Key 或完整 Prompt/Conversation 副本。
- DeepSeek 与千问 Key 分别使用 Windows DPAPI `CurrentUser` 加密保存在各自 Provider 凭据槽；路由设置与 Key 分开。UI/IPC 只显示配置状态，不读回或长时间展示完整 Key。
- 设置页明确区分“普通聊天大脑”和“编程任务”。普通聊天可切换 Provider/Model；编程任务仍由独立的 Codex Connector/Skill 承担，不随普通聊天改变。
- AI 语义层当前只在确定性规划仍判为普通聊天且文本命中有限候选条件时提供结构化“意图类型建议”。本机严格校验字段、枚举、置信度、歧义和上下文组合；模型 target 不被采用，真实目标、权限和确认都由本机确定性 Planner 与 CapabilityPolicy 重新计算。
- 电脑动作、安全权限和 Session 终态继续由 V0.3.0 的确定性边界负责；模型、Prompt、网页、屏幕内容和 Provider 都不能授予权限。

## V0.3.0 已确认验收（上一冻结基线）

- 实际 Release DesktopClient + DesktopHost + 真实 Codex 连续对话 10/10，通过；包含“刚才那个”“第二个”“继续”“不是这个，我说的是……”以及对前文的引用，始终保持同一 Session。
- 真实 Codex 打断 3/3，通过；每次取消后等待 10 秒，无迟到旧回答。
- 项目补充、文件补充、单窗口同意与拒绝共 5/5 场景，通过；项目和文件均在同一原始 Turn 续接。
- 独立桌面产品验收 2/2，通过；覆盖实际 Release WPF Client/Host、UI 停止、状态同步、窗口授权拒绝和 Host/Client 生命周期。
- 实际 Release DesktopClient + DesktopHost + 真实 Codex Provider + 真实 Windows Notepad 验收为 5/5，`Failed=0`、`FalseCompleted=0`；项目选择后执行了真实隔离编程任务，文件选择后经过可见确认，Notepad 单窗口严格校验句柄与标题，拒绝为 `Cancelled`、同意为 `Completed`。
- 真实验收运行证据位于 `%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-184833`；该目录含隔离测试数据和日志，不进入 Git。
- 自动化全量测试：363/363 通过，失败 0，跳过 0。

## 阶段 2 最终验收状态

已通过的固定证据：

- R1/R2/R3 全部 `INTEGRATED_PASS`；DeepSeek Flash 和 Pro 的真实健康、普通聊天、流式、取消和审计证据已冻结，未在 R4 重测。
- Qwen `qwen3.7-plus` 在精确 SHA `f7506a6013d83318572c63865607d78861e669bc` 通过真实 Health、Ordinary Chat 和 Cancellation：3/3 HTTP 成功、2/2 模型请求，取消后 Session/Conversation/AI Invocation 全为 `Cancelled`，DeepSeek/Codex 普通聊天请求为 0，无 retry/fallback/resend。
- R4 离线 Release 定向 QA 173/173 通过；集成后 Qwen 56/56、R4 Runner 61/61、设置/无 fallback/工作负载隔离 3/3 通过。
- 当普通聊天保存为 `qwen/qwen3.7-plus` 时，真实 Codex 编程任务仅1个 Task、1次 attempt，指定文件为唯一 Git 变化，指定 `dotnet test` 真实通过，TaskEvidence 完整，任务时窗内普通聊天 AI Invocation 为 0。

## 阶段 3 R1 / R2 / R3 当前能力

- 设置页提供“长期记忆（阶段 3）”，用户可以显式新增、查看、修正、启用/停用和确认删除四类记忆：用户事实、用户偏好、项目备注和决定。
- 记忆独立于 Conversation、Session/Turn、编程 Task、Provider Thread 和 `ai_invocations`；默认不参与 Prompt 或普通聊天，只有 S3-R3 的本 Turn 完整确认路径可以临时发送，且永不进入语义建议或动作授权。
- 标题和正文通过专用 Windows DPAPI `CurrentUser` 保护后写入 SQLite；数据库只保存密文。项目作用域只接受当前已授权的精确项目 ID。
- SQLite/Desktop IPC 当前合同为 v10。直接 v8→v10 创建唯一 `pre-v10-from-v8`，v9→v10 创建唯一 `pre-v10-from-v9`；各版本步骤单独事务提交，失败保留该步骤开始前的版本。回滚必须保留新主库并使用匹配来源的备份或隔离目录。
- 删除需要可见确认，并在同一事务清除标题/正文密文，只保留无内容墓碑；并发修正、启停或删除使用版本号冲突保护。
- 设置页的“本地相关记忆预览”只在用户点击后执行固定词法匹配，最多读取 200 个有效候选、展示 8 条且正文合计不超过 4,000 字符；停用、到期、删除或非所选项目记忆不会进入候选。
- 预览不保存查询或搜索索引，不修改记忆，也不调用 Prompt、Provider、Session、Conversation 或 AI Invocation；界面明确显示“只在本机匹配；不会发送给模型”。
- 本地预览不代表同意。只有本 Turn 明确选择、查看完整出站快照并确认后，临时 `USER_SELECTED_MEMORY_CONTEXT_V1` 才会作为 User 参考数据最多发送一次；取消、变化、过期或重启都会使确认失效，且无 retry、fallback 或 resend。

## 阶段 4 R1 当前能力

- 单窗口授权在 Host 内绑定 `{HWND, PID, ProcessStartTimeUtc, ProcessName, Title}`，身份只来自可信 Windows/Host 读取，不接受 Client、屏幕内容或模型提供的身份。
- 身份在确认前、UI Automation 控件读取/写入/提交前、每个单窗口捕获后端入口、回退入口、捕获完成后和本机分析前重新核验；公开桌面操作 IPC 不接受客户端提供的 HWND/标题作为窗口授权。任何缺失、读取失败或变化都会清理已捕获画面并要求重新选择/确认，不会弱化为只比较 HWND、进程名或标题。
- SQLite 候选合同为 schema v11，在 `session_turns` 保存 PID 和进程启动时间；v10→v11 先创建唯一 `pre-v11-from-v10` 备份并以单事务迁移。历史 v10 Turn 缺少新身份时失败关闭，不能复用旧窗口授权。
- Desktop IPC 继续保持 protocol v10；PID 与进程启动时间不通过 IPC 暴露，也不新增权限、网络、Provider 或凭据路径。

## 阶段 4 R2 已集成评测能力

- 新增独立的 `ScreenGuide.Stage4.RealUsageRunner`，不接入产品后台采样、遥测、Session、Provider 或凭据路径。它只在用户从可见终端手工启动并输入 `YES` 后工作。
- 语音模式复用真实 `OfflineContinuousVoiceListener`，每次由用户按 Enter 主动准备并使用独立 Start/Stop 监听周期；只有麦克风启动完成且 Runner 明确显示“监听已就绪”后用户才开始说话。按已经同一本地模型内存探针精确通过的常用中文非个人短句完成 1 次预热和 20 次正式尝试；15 秒内必须只有一个非空最终结果，并按固定规范化做精确匹配。活动尝试期间输入 STOP 会触发 CancellationToken，listener fault 或取消会立即结束批次。
- 正式语音批次发生无法解释的全文不匹配时，Runner 可在重新取得可见同意后执行 1 个不计入正式指标的诊断样本；只汇总长度关系与编辑距离分桶，不输出或保存识别正文。
- 视觉模式先显示 Runner 自己的可见非个人测试窗口，再把批次同意绑定到该精确窗口；通过真实单 HWND Graphics Capture 和 Windows 本机 OCR 完成 1 次预热和 20 次正式尝试。评测标记比较只忽略 OCR 插入的排版空白，缺字、错字或顺序变化仍失败关闭。普通批次或专用场景一旦窗口身份变化都立即取消。
- 正式视觉批次持续出现无法解释的标记缺失时，可在重新取得可见同意后执行 1 个不计入正式指标的诊断样本；同一自建窗口显示 4 组固定非个人候选标记，结果只输出候选 ID、长度和编辑距离等脱敏形态，不输出或保存图像、OCR 正文或标记正文。
- 计时只使用单调 `Stopwatch`，汇总 count/min/p50/p95/max；失败率只以 success+failure 为分母，cancelled/blocked 单列，预热不计入正式指标且不删除离群值。
- 最终标准输出只包含 exact SHA、粗粒度环境、次数、终态、聚合耗时、稳定错误码、清理状态以及固定的 `NetworkRequests=0`/`ProviderRequests=0`。不输出或保存录音、波形、识别正文、固定短句、窗口标题/身份、图片、路径、异常正文或堆栈。
- 离线门禁在最终代码候选 `996da9acb9cf00537794668bfd81f65d1444cca8` 通过 focused 3/3、Vision 35/35、Voice 43/43，以及 Runner/DesktopHost Release 构建；独立 QA 与安全/架构审查均为 PASS。
- 真实语音批次为 18/20（成功率 90%，达到首批观察线），另一次 STOP 场景正确取消；真实视觉批次在最终代码候选上为 20/20（成功率 100%），窗口身份变化场景正确以 `Cancelled / vision.identity_changed` 失败关闭。所有批次均确认清理完成，`NetworkRequests=0`、`ProviderRequests=0`。

## 尚未完成

- 阶段 3 不包含自动记忆提取、后台/语义检索、RAG、向量数据库、用户画像或跨 Session 自动个性化；`intent.semantic` 永不接收记忆。
- 同 AppId 的安装—卸载—重装生命周期尚未在干净 Windows 环境执行；V0.6.0 标签是可追溯的 Stage 4 冻结源码基线，但不等于已放行对外分发。
- Prompt 已有版本、哈希和固定小型评测集，但真实模型质量评测、成本/Token 对比和长期回归趋势仍未形成发布证据。
- Provider 路由当前只支持用户明确默认选择，不做自动成本/速度路由或自动降级；这是阶段 2 的有意范围，不是缺陷。
- 每轮会把当前 Conversation 历史交给所选 Provider，并有字符上限；尚未做 Token 精确预算、摘要或上下文裁剪。
- Codex 普通聊天适配器仍注册 `codex-default`，但生产策略有意在接触 CLI 或用户项目之前以 `ProductionDisabled`/`PolicyDisabled` 失败关闭；它不是阶段 2 发布目标。Codex 编程 Agent 继续走独立连接器。
- 本机单窗口理解主要依赖 OCR 和可访问控件，不能可靠理解纯图片、视频、图标语义和复杂空间关系。
- 语音模型不在安装包内；商业分发前仍需完成许可证、下载和更新方案。
- 安装包未做数字签名；Windows 可能显示未知发布者警告。
- Stage 5 的 S5-R1 Offline Inventory、归属合同与 exact LICENSE/NOTICE bundle 已 PASS：539 条 frozen payload 与 2 条 installer-container entry 已完成确定性组件/版本/材料映射，release/installer 继续 fail-closed。External Distribution 仍为 `BLOCKED`，因为数字签名和 clean-machine same-AppId 安装/升级/回滚/卸载生命周期尚未通过；本状态不代表允许发布。详细依据见 [分发许可清单](docs/V2_STAGE5_DISTRIBUTION_LICENSE_INVENTORY.md)。
- 没有手机端、云同步、远程控制、复杂多 Agent 产品功能或开放式 Tool Calling。

## 明确禁止或不开放

- 任意移动鼠标、任意点击或向任意输入框输入。
- 发送、发布、付款、删除、修改账号或安全设置、输入密码或凭据。
- 全桌面截图、隐藏捕获、全盘扫描、未授权远程控制。
- 把窗口画面上传到云端模型。
- 让模型输出、屏幕内容、网页内容或 SessionCoordinator 自行产生权限。

## 版本与安装状态

- 阶段 4 版本为 V0.6.0，已完成 Final Freeze / `INTEGRATED_PASS`。正式标签 `v0.6.0-stage4` 指向 C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`，annotated tag object 为 `20045c7960c182a052a5e0b2552ce0ed14a3863f`。独立干净标签源码的离线 locked restore、`build-desktop-release.ps1 -SkipTests` Client/Host publish 与安装包编译通过；发布目录 539 个文件，Client/Host ProductVersion 均为 `0.6.0+3a591a7b6af7da7d97e07093d4c33a3f44553b82`，FileVersion 均为 `0.6.0.0`。安装包 `元枢-V0.6.0-安装包.exe` 为 64,203,075 bytes，SHA-256 `5F912F94960E1E90A1EF918139C46751FCA8377A4055B5E68BA060A9BF4E56D7`，未签名。
- 阶段 3 版本为 V0.5.0；正式标签 `v0.5.0-stage3` 指向 `d553e7e9d606037df87d98e99250de5498f5934a`，annotated tag object 为 `a67d2b24308edb2ce72db97676b837f5018617b8`。90/90 离线 Release 定向测试、实际 Release Client+Host 本机 Fake Provider 桌面流程和独立干净标签源码构建均已通过；安装包为 64,173,791 bytes，SHA-256 `4683E10CD6C5317EB537681978DB8A77E2DC15041838EF3DCE2DEE47C7C16F95`，未签名。
- 阶段 2 版本为 V0.4.0，正式标签 `v0.4.0-stage2` 指向 `33b5859dcaa697bacd5edc5036a58d162b723a0e`，annotated tag object 为 `47a2b3b70fc22904954e2291470e809e58303eca`。干净标签源码产出的安装包为 64,128,304 bytes，SHA-256 `222DC720E677202BBCAEC6D507F48ACFA8B2FCA31535FF7B03010DF80EE7DEE9`，未签名。
- 阶段 2 最终集成功能 SHA 为 `f7506a6013d83318572c63865607d78861e669bc`；最终标签还包含 V0.4.0 版本和冻结文档。
- 上一标签 `v0.3.0-stage1` 继续可达；完整安装生命周期仍需在干净机验收后，才可把安装包视为对外分发版本。
- V0.2.1 标签 `v0.2.1-baseline` 保留为上一版回滚点；回滚数据必须使用 pre-v7 备份或隔离数据目录。
- 阶段 2 候选代码把 SQLite 升到 schema v8 并在升级前建立 `pre-v8` 备份。V0.3.0 不能直接打开 schema v8；回滚到阶段 1 时必须使用 pre-v8 备份或隔离数据目录，不能覆盖正式数据库。
- V0.5.0 使用 schema v10。直接从 V0.4.0 schema v8 升级只建立 `pre-v10-from-v8`，不会自动建立中间 pre-v9；从 v9 升级建立 `pre-v10-from-v9`。V0.4.0 不能直接打开 v9/v10；回滚时必须保留新主库，并使用匹配来源的 pre-v10、既有 v8 备份或隔离数据目录。
- 阶段 4 当前使用 schema v11、protocol v11；S4-R3/S4-R4 没有新增 schema 迁移。回滚到 V0.5.0 时 Host 与 Client 必须成对回滚，保留 v11 主库，只能在隔离数据目录使用与来源匹配的 `pre-v11-from-v10`；没有匹配备份时必须失败关闭，不能覆盖或原地降级正式主库。
- 为保护本机同 AppId 的现有 V0.2.0 安装、卸载登记和用户数据，本阶段没有在该机器重复完整安装—卸载—重装；该发布生命周期仍应在干净机执行。
- V0.6.0 冻结安装包仍未获得数字签名、语音模型许可/分发和同 AppId 干净环境生命周期放行，因此 Final Freeze 只证明源码与产物身份，不得声称已获对外分发批准。
