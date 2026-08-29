# 元枢 V2 分阶段路线图

> 路线图是计划，不代表已经实现。每个阶段必须独立验收；上一阶段未完成版本冻结时，不得自动进入下一阶段。更新时间：2026-08-29。

## 阶段 0：冻结真实基线

状态：已通过（2026-08-23）。

已完成：

- 外部快照和 Git bundle 可恢复；
- 85 个原始变化全部分类；
- 锁定依赖可恢复，Release 构建成功；
- 真实桌面全量测试 298/298；
- V0.2.0 旧安装/发布关系与 V0.2.1 新安装包关系得到验证；
- 从最终 Git 标签的干净源码副本可重建、测试和生成安装包；
- 唯一事实来源完整，标签 `v0.2.1-baseline` 可回滚。

## 阶段 1：统一会话中枢

状态：已通过（2026-08-24）。

已完成：

- 唯一 SessionCoordinator 和持久化 Session / Turn 状态；
- 首页默认续接当前 Session、显式“新话题”和 Host 重启恢复；
- 普通聊天、受控电脑动作、单窗口观察、编程任务统一登记；
- Provider / Codex、窗口观察和编程 Task 的真实取消；
- Conversation 数据库唯一终态、每 Turn 串行门、取消后迟到结果保护、幂等、防重复执行和并发冲突保护；
- 缺项目、文件、窗口及窗口同意时保存并续接同一原始 Turn；
- 统一 UI 状态、Session 状态卡、Host 快照世代识别和 protocol v7 增量长轮询；
- SQLite schema v7 迁移、结构化 Expected/Plan Target 与升级前数据库备份；
- 保留 V0.2.1 全部权限和隐私门禁。

已确认验收：

- Release DesktopClient + DesktopHost + 真实 Codex 连续对话 10/10；
- 真实 Codex 打断 3/3，每次取消后观察 10 秒均无迟到旧回答；
- 项目、文件和单窗口同意/拒绝 5/5；
- 独立桌面产品验收 2/2；
- 实际 Release DesktopClient + DesktopHost + 真实 Codex Provider + 真实 Windows Notepad 综合场景 5/5，`Failed=0`、`FalseCompleted=0`；
- 官方 NuGet 源全 solution 已知漏洞检查通过；本机完整安装生命周期因保护同 AppId 的现有 V0.2.0 安装与数据而未执行，不计为测试失败；
- 证据目录：`%LOCALAPPDATA%\ScreenGuide\Experiments\DesktopV01\20260823-184833`；真实 Notepad 严格校验窗口句柄与标题。

版本冻结：

- `v0.3.0-stage1` 已创建，annotated tag object 为 `9fc790ade57fa2d3c18bc5ee84e8dc9e7018aa89`，指向源码提交 `0a8cd9e164c35b86f67ffd94b9e0f17c312a2576`；
- 标签源码 locked restore 成功，发布目录共 533 个文件，Client/Host ProductVersion 与标签提交一致；
- 正式安装包大小 64,039,656 bytes，SHA-256 为 `42C609E130B29C6D96784C2B0266473B6D3417BE0DC5FE9C81C7517CB100FCC7`，未签名；
- 标签后的仅文档证据提交负责记录上述结果，不改变标签源码或发布二进制。

明确非目标：

- 不接 DeepSeek；
- 不更换普通聊天模型；
- 不做长期记忆、RAG 或向量数据库；
- 不做复杂多 Agent 产品功能、手机端或开放式 Tool Calling；
- 不扩大 V0.2.1 的动作范围。

## 阶段 2：可替换 AI 大脑与模型路由

状态：**已通过（2026-08-29）；V0.4.0 Stage 2 冻结。**

已完成：

- 供应商无关 Chat Model 契约与能力/健康/错误/取消描述；
- Provider Registry、按 Turn 冻结的 Model Router 和无静默 fallback 规则；
- Prompt Registry、版本/用途/适用 Provider/修改原因/SHA-256 追踪及固定小型评测集；
- Codex 普通聊天 Provider 与既有 Codex 编程 Agent 分离；
- DeepSeek 与手动备用千问普通聊天 Provider 的 HTTP/SSE、健康、故障、取消和边界限制；
- Windows DPAPI 凭据、敏感信息清理、AI 设置 Service/IPC/UI；
- `ai_invocations` 与 SQLite schema v8 迁移/升级前备份；
- 只建议、不授权的结构化语义意图，本机严格校验后仍交给确定性 Planner 和 CapabilityPolicy；
- 同一 Session 的 A → B → A、对话历史重建和编程 Agent 工作负载隔离测试边界。

最终验收证据：

- R1/R2/R3 集成证据冻结，DeepSeek 不在 R4 重测；
- Qwen 官方网络中的健康、普通聊天、真取消与调用审计通过，且无 retry/fallback/resend；
- 实际 Release DesktopClient/Host 的设置、凭据状态、路由与工作负载隔离通过；
- 普通聊天选为 Qwen 时，真实 Codex 编程 Task 和 TaskEvidence 通过，普通聊天 Provider 请求为 0；
- Release 定向测试、Git 集成、标签源码构建和产物身份收口到 `docs/baselines/V0.4.0_STAGE2.md`。

阶段 2 不改变阶段 1 的 Session/Turn 安全边界，也不把 Provider Thread、Conversation 历史或 AI 调用审计当作长期记忆。详细设计见 [V2_STAGE2_AI_MODEL_ROUTING_DESIGN.md](docs/V2_STAGE2_AI_MODEL_ROUTING_DESIGN.md)。

## 阶段 3：可控长期记忆

状态：**R1 候选开发中；尚未完成阶段 3。**

S3-R1 当前范围：

- 独立于 Conversation、Session/Turn、Task、Provider Thread 和 AI Invocation 的本机记忆账本；
- 用户显式新增、查看、修正、启停和确认删除；
- 专用 DPAPI CurrentUser 静态保护、项目授权校验、版本冲突、到期和无内容墓碑；
- schema/protocol v9 与 pre-v9 回滚保护；
- 当前绝不自动提取、检索或发送给模型。

后续未批准范围：相关性检索、RAG、向量数据库、用户画像、自动记忆提取和模型上下文注入。

## 阶段 4：体验与分发加固

状态：未开始。

- 真实使用评测、语音/视觉失败率和延迟数据；
- 长会话快照、Host 内部任务状态同步和 SessionCoordinator 大文件的渐进优化；
- 单窗口授权加入进程 ID、进程启动时间等更稳定身份；
- 语音模型许可证与下载更新方案；
- 安装包数字签名、升级/回滚和发布自动化；
- 在证据充分后再评估新的低风险动作范围。
