# 元枢项目记忆

> 本文件记录稳定项目事实和历史决策，不是产品运行时的用户记忆账本。更新时间：2026-08-30。

## 当前状态

- V0.6.0 / Stage 4 已完成 Final Freeze，S4-R1/R2/R3/R4 均为 `INTEGRATED_PASS`。正式标签 `v0.6.0-stage4` 指向 C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`，annotated tag object 为 `20045c7960c182a052a5e0b2552ce0ed14a3863f`；C1 只记录冻结证据，不改变标签源码或二进制身份。Stage 4 到此结束，不新增 S4-R5。
- Stage 5 的唯一推荐方向是 Safe Distribution & Upgrade Readiness；当前只有 Charter/Preflight，实施状态为 `NOT_STARTED / NOT_AUTHORIZED`。S5-R1～R4 只是许可清单、隔离安装生命周期、签名/release identity 和最终分发验收的串行规划，不授权下载、安装、签名、采购、联网、上传或发布。
- S5-R1 Offline Inventory、Official Evidence Verification 与四轴归属/NOTICE 合同均已 PASS；状态拆分为 `S5-R1_CONTRACT_PASS`、`NOTICE_IMPLEMENTATION_PENDING`、`EXTERNAL_DISTRIBUTION_BLOCKED`。Owner 的概念启发/无源码复制/无其他已知个人或公司贡献、AI 辅助制作、“元枢”品牌来源及图标生成来源事实已记录为 `OWNER_DECLARATION_RECORDED`；这不是仓库验证或法律结论。未来编译产品/服务商业意向为 `COMPILED_COMMERCIAL_INTENT_DECLARED`，源码公开分发保持 `SOURCE_DISTRIBUTION_NOT_AUTHORIZED`；品牌/图标暂由 Owner 保留，但图标仍为 `ICON_TERMS_EVIDENCE_REQUIRED`。C0 当前产品 payload 只收纳 win-x64 publish 树与删除脚本，Inno engine/translation 是 installer 基础设施；中文语音模型为 `VERIFIED-EXCLUDED`，WinSDK Ref 与非 win-x64 Sherpa 为 `EXCLUDED-CONDITIONAL`。原 C0 逐文件 manifest、NOTICE 实际布置、SQLite native、Inno exact compiler/translation provenance、.NET/Sherpa/ORT 文件映射及条件排除项仍未闭合；签名和 clean-machine lifecycle 是独立后续 Gate。Owner 声明不替代第三方许可证，合同 PASS 不是外部分发放行，Stage 5 implementation 仍未完成。唯一详细记录为 `docs/V2_STAGE5_DISTRIBUTION_LICENSE_INVENTORY.md`。
- V2 阶段 1“统一会话中枢”已经通过；363/363 自动化和实际 Release DesktopClient + DesktopHost 的真实桌面验收均通过。
- V0.2.1 标签 `v0.2.1-baseline` 保留为上一版回滚点；回滚必须同时使用 pre-v7 备份或隔离数据目录。
- V2 阶段 2“可替换 AI 大脑与模型路由”已通过：统一 Chat Model、Provider Registry、Model Router、Prompt Registry、DPAPI、安全停用的 Codex 普通聊天适配器、DeepSeek/千问普通聊天 Provider、设置 UI/IPC、语义建议和 schema v8 AI 调用审计。
- 阶段 2 普通聊天发布目标是 DeepSeek + 千问；千问是手动备用，无自动 fallback/retry/resend。DeepSeek 真实证据已冻结，Qwen 真实 Health/聊天/取消和普通聊天选 Qwen 时的真实 Codex 编程隔离均已通过。Codex 普通聊天保持 `ProductionDisabled`/`PolicyDisabled`，不是发布目标。
- S3-R1 本机加密记忆账本、S3-R2 确定性本地预览与 S3-R3 schema/protocol v10、chat.general@2 和逐 Turn 完整出站确认均已集成。默认 0 条，只有用户单次确认的普通聊天 Turn 才最多发送一次。

## 长期架构决策

1. 渐进演进，不推倒重来。V0.2.1 中通过测试的窗口、权限、语音、存储和 Codex 连接能力必须保留。
2. DesktopClient 不直接访问 SQLite、Codex 或 Windows 动作；DesktopHost 是唯一编排和审计边界。
3. 电脑操作采用确定性意图 + 权限白名单；模型、网页、屏幕、文档和 SessionCoordinator 都不能自行扩大权限。
4. 只捕获用户确认的单个前台窗口，绝不回退为全桌面；授权只对显示的单个窗口和当前 Turn 生效。
5. 运行数据存放在用户本地应用数据目录，不进入仓库；卸载默认保留。
6. 模型供应商必须位于可替换接口后。阶段 1 的 Codex 真实证据是历史冻结事实；阶段 2 普通聊天发布目标改为 DeepSeek + 千问，不与 Codex 编程 Agent 绑定。
7. Conversation/Provider Thread、Session/Turn、编程 Task 和未来长期记忆是四种不同状态，不能混用。
8. 安装产物不提交 Git，以版本标签、哈希、测试记录和外部快照关联。
9. 正式版本只能在标签存在、干净源码构建和测试通过、安装验收通过、工作区干净后宣布冻结。
10. 产品运行时长期记忆是独立状态真源，不得复用 Conversation、Session/Turn、编程 Task、Provider Thread 或 `ai_invocations`。本地预览不等于同意；S3-R3 只允许用户为单个普通聊天 Turn 查看完整出站快照后单次确认，不能授予任何权限，也不能自动发送。
11. 单窗口授权的可信身份是 `{HWND, PID, ProcessStartTimeUtc, ProcessName, Title}`。schema v11 在 Session Turn 内持久化 PID 与启动时间，protocol 维持 v10；公开桌面操作 IPC 不接受客户端 HWND/标题作为窗口授权，Host 完整身份必须一直传到 UI Automation 并在控件读取、写入和提交前重验。确认、UIA、捕获后端/回退和分析前任一身份缺失或变化都必须清帧并重新确认，历史 v10 Turn 不得复用旧授权。
12. S4-R2 的真实使用指标只能由独立、前台、人工启动的本机 Runner 产生：每项 1 次预热 + 至少 20 次正式尝试；失败率分母只含 success+failure，取消/阻塞单列，延迟使用 Stopwatch 与 nearest-rank。不得加入产品遥测、后台采样或任意正文证据，声音、转写、窗口图像和身份一律不持久化。

## 阶段 1 已确认决策

1. `SessionCoordinator` 是唯一当前话题和前台 Turn 协调入口，只协调状态、取消、补充和恢复，不拥有权限。
2. 一个 Session 表示一个明确话题，一个 Turn 表示用户的一次原始请求；Session 一对一关联 Conversation。
3. 首页后续输入默认继续当前 Session；只有用户明确“新话题”或选择会话才切换，不使用超时自动猜测。
4. 新前台输入先真正取消旧前台 Turn，再启动替代 Turn；编程任务可以在后台继续，用户可同时聊天。
5. 取消必须到达 Conversation Provider / Codex 进程树、窗口观察或真实编程 Task；Conversation 的取消与成功提交在 SQLite 中争夺唯一终态，每 Turn 串行门和并发版本阻止迟到结果或并发确认复活/重复执行。
6. 缺项目、文件、窗口或窗口同意时保留原始 Turn；补齐后重新规划同一 Turn。项目撤权会清除失效上下文并重新等待，目标变化时旧确认失效。
7. Host 重启恢复上次当前 Session；运行中的工作标记为 `Interrupted` 且不自动重放。等待项目等安全状态可保留。
8. S4-R3 使用 protocol v11：Session bootstrap 最多 32 Turn/50 消息，wait 返回 NoChange/有界 Delta/ResetRequired，历史消息按 50 条 keyset 游标分页；Coordinator 世代、Session 和版本共同拒绝旧响应，客户端缓存只是显示状态。
9. SQLite schema v7 保存 `sessions`、`session_turns` 及结构化 Expected/Plan Target；从 v5 或中间 v6 升级前自动生成 pre-v7 备份。
10. V0.2.1 不能直接打开 schema v7；程序回滚时必须同时使用升级前备份或隔离数据目录。
11. UI 已选应用绑定已发现的 Application ID，网站绑定规范化完整 HTTPS URI；Session Input、Plan 和 Turn 保存结构化期望/实际目标，Host 在规划与确认两处做 Ordinal 精确比较。
12. 同 AppId 的安装/卸载测试不能覆盖仍需保护的正式安装登记；本机保留 V0.2.0 时不运行 `test-desktop-installer.ps1`，完整安装生命周期改在干净机验收。
13. 正式冻结使用两提交：先提交源码/文档并创建标签，再从标签重建和计算安装包哈希，最后用单独证据提交回填提交号与哈希，避免自引用循环。
14. Stage 5 不得伪装成 S4-R5，也不以内部重构冒充用户价值；唯一 Charter 为 `docs/V2_STAGE5_CHARTER.md`。版本/tag、许可判定、干净机生命周期、证书/私钥/费用、真实签名与发布都必须在对应切片获得 Owner 可见授权。

## 阶段 2 已冻结决策

1. 普通聊天业务只依赖供应商无关的 `IChatModelProvider`。Provider 差异必须留在实现层；SessionCoordinator、权限和 Conversation 领域不增加 DeepSeek/千问/Codex 分支。
2. `ChatProviderRegistry` 只接收声明 `OrdinaryChat` 工作负载的 Provider，并校验 Provider/Model 唯一；新增 Provider 不应改变 SessionCoordinator。
3. `ModelRouter` 第一版只支持用户明确选择的默认路由。每个 Turn 开始时冻结 Provider/Model；设置变化只影响下一轮，没有静默 fallback、自动付费重试或隐式跨供应商发送。
4. 对话连续性由元枢 `ConversationStore` 保存的消息历史负责，而不是依赖 Provider Thread。A → B → A 时每轮把同一 Conversation 历史交给当时选中的 Provider。
5. 普通聊天与编程 Agent 独立：`CodexChatModelProvider` 作为安全停用适配器保留，生产普通聊天以 `ProductionDisabled`/`PolicyDisabled` 失败关闭；编程任务继续走 `CodexConnector` / `CodexSkillAdapter`。DeepSeek/千问聊天切换不能改变项目授权或 TaskEvidence。
6. Prompt Registry 使用仓库内受版本控制的文件和清单：`chat.general@1` 仍是无记忆普通聊天的默认 Prompt，`chat.general@2` 只用于用户逐 Turn 明确选择并完整确认的记忆出站，`intent.semantic@1` 始终不接收记忆。每次加载校验相对路径、适用 Provider 和 SHA-256；每次调用记录 Prompt ID/版本/哈希。
7. Provider/Model 设置与凭据分开。路由 ID 写普通设置文件；DeepSeek/Qwen Key 分别使用 Windows DPAPI `CurrentUser` 加密密文，只通过各自短生命周期 lease 读取，Key 不进 Git、SQLite、普通日志或 IPC 响应。
8. 不做静默跨 Provider 降级。Provider 故障必须给用户明确、安全提示；是否切换数据目的地由用户决定。
9. AI 语义层只提供不可信的意图类型建议。严格本机解析、置信度和歧义门槛通过后，仍由确定性 Planner 用真实上下文重算；模型 target、缺失上下文和任何“用户已同意”主张都不能授权。
10. protocol v8 只新增 AI 设置/凭据/健康 IPC，不创建第二套 Session 状态。设置页必须明确普通聊天与 Codex 编程 Agent 的责任和数据目的地。
11. SQLite schema v8 新增 `ai_invocations`，只记录 Provider/Model/Prompt/目的地/状态/Usage 等调用证据，不保存 Key、Prompt 正文或完整 Conversation。升级前生成 pre-v8 备份。
12. Qwen 是 DeepSeek 的手动备用普通聊天 Provider，仅注册 `qwen3.7-plus`。它固定发送到阿里云百炼官方兼容端点，不允许自定义 URL，不自动 fallback/重试/重发，不接管 Codex 编程 Agent；reasoning 只做有界消费，绝不进入 UI、数据库或日志。
12. V0.3.0 只支持 schema v7。回滚到 `v0.3.0-stage1` 时必须保留 schema v8 数据库并使用 pre-v8 备份或隔离数据目录；不使用破坏性 Git/文件清理。

## 阶段 2 已确认验收

- R1/R2/R3 全部 `INTEGRATED_PASS`；DeepSeek Flash/Pro 真实验收证据已冻结，R4 不重测。
- Qwen `qwen3.7-plus` 在 `f7506a6013d83318572c63865607d78861e669bc` 通过真实 Health、Ordinary Chat 和 Cancellation：3/3 HTTP、2/2 模型请求，DeepSeek/Codex 普通聊天请求 0，无 retry/fallback/resend。
- R4 离线 Release 定向 QA 173/173 通过；集成后 Qwen 56/56、R4 Runner 61/61、设置/无 fallback/工作负载隔离 3/3 通过。
- 普通聊天选为 Qwen 时，真实 Codex 编程回归仅 1 Task/1 attempt，指定文件为唯一 Git 变化，指定 `dotnet test` 通过，任务时窗内 `ai_invocations=0`。
- 最终标签源码 locked restore、Client/Host win-x64 publish 和安装包编译通过；发布目录 538 个文件，Client/Host ProductVersion 均绑定 `33b5859d...`，安装包 64,128,304 bytes 且未签名。

## 阶段 3 R1 / R2 / R3 冻结决策

1. 记忆类别只允许 UserFact、UserPreference、ProjectNote、Decision；作用域只允许 Global 或精确已授权 Project ID；来源仅 `UserExplicit`，置信度固定 1.0。
2. 标题和正文使用专用 DPAPI CurrentUser 保护器后存入 SQLite，不能复用 Provider 凭据存储；数据库、日志和错误证据不保存明文。
3. 修正使用 ExpectedVersion 覆盖同一逻辑项，不保存旧明文/密文历史；删除必须显式确认并原子清空密文，只保留无内容墓碑。
4. schema v9 只新增记忆表/索引；protocol v9 只新增显式记忆 CRUD。v8 → v9 前建立 pre-v9 备份并原子迁移，V0.4.0 回滚使用该备份或隔离目录。
5. S3-R1 不创建 Prompt、Provider、AI Invocation、向量或 RAG 入口；记忆不能改变当前输入、目标、项目/文件/窗口权限、同意或确认。
6. S3-R2 只增加用户点击触发的本地词法预览：Active/未到期候选最多 200，结果最多 8 条/4,000 字符；查询和搜索替身不持久化，预览零状态写入、零模型调用。
7. S3-R3 使用 protocol/schema v10 与 `chat.general@2`：有序选择 1–8 条、完整显示 Provider/Model/HTTPS origin/项目/正文后单次确认；原子复核失败时 Provider 与 Invocation 都为 0，确认消费后禁止 retry/fallback/resend。
8. 出站 block 是临时 User JSON，不持久化；审计只保存 ID/version、路由、Prompt 身份、计数和 manifest，不保存 query、标题、正文、原始 block 或 plaintext hash。语义建议永不接收记忆。

## 阶段 1 已确认验收

- 实际 Release DesktopClient + DesktopHost + 真实 Codex 连续对话 10/10，通过；同一 Session 正确理解“刚才那个”“第二个”“继续”“不是这个，我说的是……”和前文引用。
- 真实 Codex 打断 3/3，通过；每次取消后继续观察 10 秒，旧回答没有重新出现。
- 项目补充、文件补充和单窗口同意/拒绝共 5/5 场景，通过；项目、文件均在同一原始 Turn 续接。
- 独立桌面产品验收 2/2，通过；包含实际 Release WPF Client/Host、UI 停止、状态同步、窗口拒绝和进程恢复场景。
- 实际 Release DesktopClient + DesktopHost + 真实 Codex Provider + 真实 Windows Notepad 验收 5/5，`Failed=0`、`FalseCompleted=0`；Notepad 严格校验窗口句柄与标题；运行证据：`%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-184833`。
- 自动化全量测试 363/363，通过；失败 0，跳过 0。
- 官方 NuGet 源全 solution 已知漏洞检查通过，没有已知易受攻击的直接或传递依赖。
- 标签源码 locked restore 成功；发布目录 533 个文件。Client/Host ProductVersion 为 `0.3.0+0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`，FileVersion 为 `0.3.0.0`。
- 安装包 `artifacts/release/元枢-V0.3.0-安装包.exe` 为 64,039,656 bytes，SHA-256 为 `42C609E130B29C6D96784C2B0266473B6D3417BE0DC5FE9C81C7517CB100FCC7`，未签名。

## 阶段 0 保留事实

- 阶段 0 正式基线：V0.2.1；独立干净源码验收通过，以 `v0.2.1-baseline` 为冻结点。
- 冻结前 39 修改、2 删除、44 未跟踪，共 85 个变化；71 正式能力、11 保留后重构、3 历史遗留、0 构建产物、0 无法判断。
- V0.2.1 最终真实桌面测试 298/298；V0.2.0 已安装目录中的 533 个发布文件与受保护旧发布目录逐一哈希一致。

## 尚未解决

- V0.6.0 标签源码冻结证据已完成：发布目录 539 个文件，Client/Host ProductVersion 均绑定 C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`，安装包为 64,203,075 bytes，SHA-256 `5F912F94960E1E90A1EF918139C46751FCA8377A4055B5E68BA060A9BF4E56D7`，且未签名。尚未解决的是同 AppId 干净机生命周期、数字签名和语音模型许可/外部分发放行。
- 回滚到 V0.5.0 必须让 Host 与 Client 成对回滚，保留 schema v11 主库，并只在隔离目录使用匹配的 `pre-v11-from-v10`；没有匹配备份时失败关闭。
- 阶段 3 只有显式本机预览与逐 Turn 完整确认后的单次发送；没有自动提取、自动模型注入、后台/语义检索、RAG、向量数据库或跨 Session 自动个性化。
- 固定 Prompt 评测集已建立，但真实模型质量、Token/成本对比和长期回归趋势尚未形成发布证据。
- Codex 普通聊天只保留 `codex-default` 描述并在生产策略下失败关闭，不提供真实普通聊天模型或 Usage；独立 Codex 编程 Agent 继续保留。
- Provider 切换会把同一 Conversation 既有历史发送到新的数据目的地；UI 已提示，真实用户是否理解仍需验收。
- 每轮发送完整 Conversation 历史并设字符上限；没有 Token 精确预算、摘要和上下文裁剪。
- DPAPI 不抵御同一 Windows 用户高权限恶意进程或运行时内存读取。
- S4-R3 已移除长会话全量重复快照和编程任务 250ms 状态轮询；SQLite 仍是事实真源，Host journal/Task 事件与 Client cache 只负责有界投影和唤醒。S4-R4 已把 Session/Turn 临时门闩抽到内部单例注册表并在空闲时清零；`SessionCoordinator.cs`、`MainWindow.xaml.cs` 和部分 SQLite Store 仍较大。
- S4-R2 已完成真实麦克风 1+20、独立 STOP 场景、可见测试窗口 1+20 和身份变化人工验收；这些结果只证明当前本机与固定评测合同，不等于所有麦克风、口音、窗口内容或 Windows 设备上的普遍质量保证。
- 安装包无数字签名；语音模型分发与许可证待定。
- 阶段 3 的 S3-R1/R2/R3 已集成；自动/语义检索、画像或其他模型使用仍需新的独立批准。

## 更新规则

- 只记录已经确认、未来仍有用的事实和“为什么这样决定”。
- 路线图设想写入 `ROADMAP.md`，不能写成已实现。
- 当前功能写入 `PRODUCT.md`，当前结构写入 `ARCHITECTURE.md`。
- 每个正式版本必须新增基线文档和 Git 标签；不得覆盖旧版本记录。
