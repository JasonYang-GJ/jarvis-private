# 元枢产品事实（V0.3.0 阶段 1）

> 当前产品事实的唯一入口。更新时间：2026-08-24。V0.3.0 已通过功能、自动化、真实桌面验收和源码/产物身份冻结；完整安装生命周期仍待干净机验收。V0.2.1 保留为上一版可回滚基线。

## 产品定位

元枢是 Windows 本机学习与操作助手。它在用户可见、明确授权的前提下理解当前话题和当前单个窗口，给出中文帮助，并只执行少量经过白名单限制的低风险动作。

## V0.3.0 已实现能力

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
- DesktopClient 通过 protocol v7 的本机 Named Pipe 长轮询等待 Session 版本变化；快照带 Host 实例身份和启动时间，可区分重启后的新旧版本；项目、任务等非实时数据保留低频兜底刷新。
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

- 普通问答继续通过可替换的 `IConversationProvider` 接口；V0.3.0 仍只注册 `CodexConversationProvider`。
- 该 Provider 启动本机 Codex CLI；实际底层模型由 Codex 自身配置决定，元枢没有固定模型 ID。
- 电脑动作仍由本机确定性规划器和权限策略决定，SessionCoordinator 只协调状态，不能授予或绕过权限。
- 本阶段没有接入 DeepSeek，没有更换普通聊天模型，也没有新增 API Key、Endpoint 或模型路由。

## 已确认验收

- 实际 Release DesktopClient + DesktopHost + 真实 Codex 连续对话 10/10，通过；包含“刚才那个”“第二个”“继续”“不是这个，我说的是……”以及对前文的引用，始终保持同一 Session。
- 真实 Codex 打断 3/3，通过；每次取消后等待 10 秒，无迟到旧回答。
- 项目补充、文件补充、单窗口同意与拒绝共 5/5 场景，通过；项目和文件均在同一原始 Turn 续接。
- 独立桌面产品验收 2/2，通过；覆盖实际 Release WPF Client/Host、UI 停止、状态同步、窗口授权拒绝和 Host/Client 生命周期。
- 实际 Release DesktopClient + DesktopHost + 真实 Codex Provider + 真实 Windows Notepad 验收为 5/5，`Failed=0`、`FalseCompleted=0`；项目选择后执行了真实隔离编程任务，文件选择后经过可见确认，Notepad 单窗口严格校验句柄与标题，拒绝为 `Cancelled`、同意为 `Completed`。
- 真实验收运行证据位于 `%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-184833`；该目录含隔离测试数据和日志，不进入 Git。
- 自动化全量测试：363/363 通过，失败 0，跳过 0。

## 尚未完成

- 没有用户长期记忆、RAG、向量数据库、相关性检索、记忆纠错或跨 Session 个性化。SQLite 保存 Session/聊天记录不等于长期记忆。
- Prompt 没有集中注册、版本号、变更记录和自动评测；普通对话 System Prompt 仍内嵌在 Codex Provider 代码中。
- 没有 DeepSeek Provider，也没有第二个可实际切换的通用模型 Provider。
- 本机单窗口理解主要依赖 OCR 和可访问控件，不能可靠理解纯图片、视频、图标语义和复杂空间关系。
- 语音模型不在安装包内；商业分发前仍需完成许可证、下载和更新方案。
- 安装包未做数字签名；Windows 可能显示未知发布者警告。
- 没有手机端、云同步、远程控制、复杂多 Agent 产品功能或开放式 Tool Calling。

## 明确禁止或不开放

- 任意移动鼠标、任意点击或向任意输入框输入。
- 发送、发布、付款、删除、修改账号或安全设置、输入密码或凭据。
- 全桌面截图、隐藏捕获、全盘扫描、未授权远程控制。
- 把窗口画面上传到云端模型。
- 让模型输出、屏幕内容、网页内容或 SessionCoordinator 自行产生权限。

## 版本与安装状态

- 当前阶段 1 源码与功能基线：V0.3.0，标签 `v0.3.0-stage1`，源码提交 `0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`。完整安装生命周期仍需在干净机验收后，才可把安装包视为对外分发版本。
- annotated tag object：`9fc790ade57fa2d3c18bc5ee84e8dc9e7018aa89`。标签之后的仅文档证据提交不改变标签所指源码。
- DesktopClient / DesktopHost ProductVersion 均为 `0.3.0+0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`，FileVersion 均为 `0.3.0.0`；发布目录共 533 个文件。
- 正式安装包为 `artifacts/release/元枢-V0.3.0-安装包.exe`，64,039,656 bytes，SHA-256 为 `42C609E130B29C6D96784C2B0266473B6D3417BE0DC5FE9C81C7517CB100FCC7`，未签名。
- V0.2.1 标签 `v0.2.1-baseline` 保留为上一版回滚点；回滚数据必须使用 pre-v7 备份或隔离数据目录。
- 为保护本机同 AppId 的现有 V0.2.0 安装、卸载登记和用户数据，本阶段没有在该机器重复完整安装—卸载—重装；该发布生命周期仍应在干净机执行。
