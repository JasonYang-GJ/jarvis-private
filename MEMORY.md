# 元枢项目记忆

> 本文件记录稳定项目事实和历史决策，不是产品运行时的“用户长期记忆数据库”。更新时间：2026-08-24。

## 当前状态

- 当前阶段 1 源码与功能基线：V0.3.0，标签 `v0.3.0-stage1`，源码提交 `0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`，annotated tag object `9fc790ade57fa2d3c18bc5ee84e8dc9e7018aa89`。完整安装生命周期尚待干净机验收，因此安装包未获对外分发放行。
- V2 阶段 1“统一会话中枢”已经通过；363/363 自动化和实际 Release DesktopClient + DesktopHost 的真实桌面验收均通过。
- V0.2.1 标签 `v0.2.1-baseline` 保留为上一版回滚点；回滚必须同时使用 pre-v7 备份或隔离数据目录。
- 阶段 1 没有接入 DeepSeek、长期记忆、RAG、向量数据库、第二个普通聊天 Provider 或复杂多 Agent 产品功能。

## 长期架构决策

1. 渐进演进，不推倒重来。V0.2.1 中通过测试的窗口、权限、语音、存储和 Codex 连接能力必须保留。
2. DesktopClient 不直接访问 SQLite、Codex 或 Windows 动作；DesktopHost 是唯一编排和审计边界。
3. 电脑操作采用确定性意图 + 权限白名单；模型、网页、屏幕、文档和 SessionCoordinator 都不能自行扩大权限。
4. 只捕获用户确认的单个前台窗口，绝不回退为全桌面；授权只对显示的单个窗口和当前 Turn 生效。
5. 运行数据存放在用户本地应用数据目录，不进入仓库；卸载默认保留。
6. 模型供应商必须位于可替换接口后。当前 Codex 只是唯一实现，不代表长期绑定。
7. Conversation/Provider Thread、Session/Turn、编程 Task 和未来长期记忆是四种不同状态，不能混用。
8. 安装产物不提交 Git，以版本标签、哈希、测试记录和外部快照关联。
9. 正式版本只能在标签存在、干净源码构建和测试通过、安装验收通过、工作区干净后宣布冻结。

## 阶段 1 已确认决策

1. `SessionCoordinator` 是唯一当前话题和前台 Turn 协调入口，只协调状态、取消、补充和恢复，不拥有权限。
2. 一个 Session 表示一个明确话题，一个 Turn 表示用户的一次原始请求；Session 一对一关联 Conversation。
3. 首页后续输入默认继续当前 Session；只有用户明确“新话题”或选择会话才切换，不使用超时自动猜测。
4. 新前台输入先真正取消旧前台 Turn，再启动替代 Turn；编程任务可以在后台继续，用户可同时聊天。
5. 取消必须到达 Conversation Provider / Codex 进程树、窗口观察或真实编程 Task；Conversation 的取消与成功提交在 SQLite 中争夺唯一终态，每 Turn 串行门和并发版本阻止迟到结果或并发确认复活/重复执行。
6. 缺项目、文件、窗口或窗口同意时保留原始 Turn；补齐后重新规划同一 Turn。项目撤权会清除失效上下文并重新等待，目标变化时旧确认失效。
7. Host 重启恢复上次当前 Session；运行中的工作标记为 `Interrupted` 且不自动重放。等待项目等安全状态可保留。
8. DesktopClient 通过 protocol v7 的 Session 长轮询接收增量状态；快照用 Coordinator 实例 ID、启动时间和版本区分 Host 重启前后的新旧增量；UI 不再每两秒全量读取当前会话。
9. SQLite schema v7 保存 `sessions`、`session_turns` 及结构化 Expected/Plan Target；从 v5 或中间 v6 升级前自动生成 pre-v7 备份。
10. V0.2.1 不能直接打开 schema v7；程序回滚时必须同时使用升级前备份或隔离数据目录。
11. UI 已选应用绑定已发现的 Application ID，网站绑定规范化完整 HTTPS URI；Session Input、Plan 和 Turn 保存结构化期望/实际目标，Host 在规划与确认两处做 Ordinal 精确比较。
12. 同 AppId 的安装/卸载测试不能覆盖仍需保护的正式安装登记；本机保留 V0.2.0 时不运行 `test-desktop-installer.ps1`，完整安装生命周期改在干净机验收。
13. 正式冻结使用两提交：先提交源码/文档并创建标签，再从标签重建和计算安装包哈希，最后用单独证据提交回填提交号与哈希，避免自引用循环。

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

- 没有用户长期记忆、RAG、向量数据库或跨 Session 相关信息检索。
- Prompt 无集中管理、版本和自动评测。
- 通用对话只有 Codex Provider；DeepSeek 未接入运行时。
- Session 快照仍随完整会话历史增长；`SessionCoordinator.cs`、`MainWindow.xaml.cs` 和部分 SQLite Store 较大。
- 单窗口授权已比较句柄、进程名和标题，但尚未保存进程 ID / 启动时间；同程序同标题窗口的极端句柄复用风险留待后续加固。
- 安装包无数字签名；语音模型分发与许可证待定。
- 阶段 2 尚未启动，是否启动由项目负责人决定。

## 更新规则

- 只记录已经确认、未来仍有用的事实和“为什么这样决定”。
- 路线图设想写入 `ROADMAP.md`，不能写成已实现。
- 当前功能写入 `PRODUCT.md`，当前结构写入 `ARCHITECTURE.md`。
- 每个正式版本必须新增基线文档和 Git 标签；不得覆盖旧版本记录。
