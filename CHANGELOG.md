# 变更记录

本项目从 V0.2.1 起采用可追溯版本记录。更早内容以历史报告和 Git 历史为准。

## 0.3.0 - 2026-08-24（V2 阶段 1：通过）

> 功能、363/363 自动化、最终 Release 真实用户流程和源码/产物身份冻结均已通过；标签后的仅文档证据提交不改变标签所指源码。完整安装生命周期仍待干净机验收，不视为已获对外分发放行。

### 统一会话中枢

- 新增 DesktopHost `SessionCoordinator`，把普通聊天、受控电脑动作、单窗口观察和编程任务统一登记为 Session / Turn。
- 新增 `ISessionStore`、SQLite `sessions` / `session_turns` 和 schema v7 迁移；当前 Session、项目上下文、原始请求、等待状态及结构化期望/计划目标可跨 Host 重启保留。
- 首页输入默认继续当前 Session 和同一 Conversation / Provider Thread；新增明确“新话题”入口，不再每句话新建 Conversation。
- 编程任务与普通聊天共享 Session 但保持独立 Turn；编程任务可在后台运行，用户仍能继续聊天。

### 真取消与冲突保护

- 新输入和停止会把取消传到 ConversationService、Provider / Codex 进程树、窗口观察或真实编程 Task。
- Conversation 的取消与成功提交在 SQLite 中争夺唯一终态；取消生效后不再接受 Assistant 消息或迟到成功。Coordinator 等待旧前台工作结束后再继续替代请求。
- 加入当前 Session 串行门、每 Turn 串行门、幂等键、Turn 版本、Session/Turn 归属检查、并发确认保护和重启对账，防止重复执行、跨会话补充和旧结果覆盖。

### 上下文补齐

- 缺项目进入 `WaitingForProject`；选择已授权项目后在同一原始 Turn 继续编程请求。
- Session 中已撤权或失效的项目会被清除，并在同一原始 Turn 重新等待项目。
- 缺文件进入 `WaitingForFile`；选择存在的文件后在同一 Turn 继续，并保留可见确认。
- 缺窗口进入 `WaitingForWindow`；窗口可用后进入按单窗口绑定的 `WaitingForWindowConsent`。
- 窗口授权同意与拒绝均安全收束；窗口或搜索目标变化时旧确认失效。

### UI 与 IPC

- DesktopClient 新增统一 Session 状态卡、新话题、停止、项目/文件选择、窗口重试、窗口同意/拒绝和确认入口。
- 新增 `SessionUiPresenter`，把每个 Coordinator 阶段映射为唯一用户提示和控件组合；Coordinator 实例 ID、启动时间与版本共同拒绝旧 Host / 旧版本快照覆盖新 UI。
- IPC 协议升级到 v7，新增 Session 命令和 `sessions.wait` 长轮询；实时 Session 状态从全量频繁轮询改为变化唤醒。
- Named Pipe 服务增加监听错误重试、64 个连接上限、8 个忙碌响应槽、首请求超时和坏请求帧隔离。
- 电脑操作页改走 Session Turn；应用绑定已发现的 Application ID，网站绑定规范化完整 HTTPS URI。Host 在规划和确认两处精确比较 Expected Intent/Target 与 Canonical Plan Target，不一致时结束 Turn 且不执行。

### 安全

- SessionCoordinator 只协调状态，不授予权限；V0.2.1 的项目授权、文件确认、单窗口同意、确定性意图和动作白名单保持不变。
- 另一个 Session 不能为原 Turn 补项目、文件或窗口同意；用户拒绝后不捕获、不执行。
- 本阶段没有接入 DeepSeek、长期记忆、RAG、向量数据库、第二个普通聊天 Provider、复杂多 Agent 产品功能或更宽 Tool Calling。

### 验证

- 实际 Release DesktopClient + DesktopHost + 真实 Codex 连续对话 10/10，通过。
- 真实 Codex 取消 3/3，通过；每次取消后观察 10 秒，无迟到旧回答。
- 项目、文件和单窗口授权同意/拒绝共 5/5 场景，通过；项目/文件均续接同一原始 Turn。
- 独立桌面产品验收 2/2，通过。
- 实际 Release DesktopClient + DesktopHost + 真实 Codex Provider + 真实 Windows Notepad 验收 5/5，`Failed=0`、`FalseCompleted=0`；项目/文件续接和单窗口同意/拒绝均通过。
- 真实验收证据：`%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-184833`；真实 Notepad 严格校验窗口句柄与标题。
- 自动化全量测试：363/363 通过，失败 0，跳过 0。
- 官方 NuGet 源全 solution 已知漏洞检查通过，没有已知易受攻击的直接或传递依赖。
- 本机为保护同 AppId 的现有 V0.2.0 安装、卸载登记与用户数据，没有运行完整安装—卸载—重装脚本；这是一项安全保护，不计为测试失败，干净机发布生命周期验收仍需单独执行。
- 正式源码提交：`0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`。
- 正式标签：`v0.3.0-stage1`；annotated tag object：`9fc790ade57fa2d3c18bc5ee84e8dc9e7018aa89`。
- Client/Host ProductVersion：`0.3.0+0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`；FileVersion：`0.3.0.0`；发布目录：533 个文件。
- 安装包：`artifacts/release/元枢-V0.3.0-安装包.exe`，64,039,656 bytes，SHA-256 `42C609E130B29C6D96784C2B0266473B6D3417BE0DC5FE9C81C7517CB100FCC7`，`NotSigned`。
- 标签之后的提交只回填上述发布证据，不属于 `v0.3.0-stage1` 源码，也不改变发布二进制。

## 0.2.1 - 2026-08-23

### 基线冻结

- 把冻结前 39 修改、2 删除、44 未跟踪的真实 V0.2 成果纳入正式版本管理。
- 新增当前产品、架构、项目记忆、路线图、文件分类和版本基线文档。
- 为所有项目生成普通构建和 win-x64 发布依赖锁，发布时启用严格恢复。
- 发布/安装验收脚本更新到 V0.2.1，并允许验证指定旧安装包。
- 安装验收增加同 AppId 正式安装保护；检测到现有元枢时拒绝执行临时卸载。

### 稳定性修补

- Host 清除默认 EventLog Logger，只使用项目自己的滚动文件日志，避免普通用户权限下启动失败被 EventLog 异常掩盖。
- Host 启动失败审计改用独立令牌，保证关闭过程中仍尽量记录失败证据。
- Windows 搜索控件激活复用已有稳健前台窗口激活逻辑，减少只调用一次 `SetForegroundWindow` 的不稳定。

### 验证

- Release 构建 0 警告、0 错误。
- 真实 Windows 桌面全量测试 298/298。
- NuGet 官方漏洞源未发现已知易受攻击依赖。
- V0.2.0 与 V0.2.1 安装包均通过隔离安装、Host 初始化、卸载、重装和历史保留验收。

## 0.2.0 - 2026-08-19

- 建立可见常驻语音入口、确定性低风险动作、单窗口本机理解、普通对话和 Codex 编程任务能力。
- 旧安装包及历史说明保留在外部快照和 `DESKTOP_V02_CORRECTION_REPORT.md`。
- 当时成果没有对应的完整干净 Git 提交；V0.2.1 是首次正式冻结的可恢复基线。
