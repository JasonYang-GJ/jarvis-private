# 元枢 V2 阶段 3 可控长期记忆设计（S3-R1 / S3-R2 / S3-R3）

> 状态：S3-R1/R2 已集成，S3-R3 逐 Turn 出站确认候选；不是完整阶段 3，也不是正式发布基线。更新时间：2026-08-30。

## 1. 本切片解决什么

S3-R1 建立独立、加密、可由用户显式管理的本机长期记忆账本。S3-R2 增加用户主动触发的“本地相关记忆预览”。S3-R3 只在用户为单个普通聊天 Turn 明确选择记忆、看到完整出站快照并单次确认后，向该 Turn 已冻结的 Provider 最多发送一次；默认 0 条，仍不会自动提取或后台检索。

它与以下状态严格分离：

- Conversation 历史；
- Session / Turn；
- Codex 编程 Task；
- Provider Thread；
- `ai_invocations` 调用审计。

记忆不授予应用、网站、项目、文件、窗口、捕获、工具或动作权限，也不能覆盖当前用户输入、可见同意或确认。

## 2. 领域合同

- Category：`UserFact`、`UserPreference`、`ProjectNote`、`Decision`。
- Scope：`Global` 或精确的已授权 Project ID；不存在或已撤权项目失败关闭。
- SourceKind：仅 `UserExplicit`；来源引用不接受记忆正文。
- Status：`Active`、`Disabled`、`Deleted`；停用、到期和删除项不是未来检索候选。
- 明确保存的置信度固定为 1.0。
- 标题去除首尾空白后为 1–80 字符；正文为 1–2,000 字符；非法控制字符失败关闭。
- 每条记录使用稳定 Guid 和正整数 Version。修正、启停和删除都要求 ExpectedVersion，不做静默冲突合并。
- 修正覆盖同一逻辑项的受保护内容，不保留旧明文或密文修订历史。
- 删除要求 `Confirmed=true`，同一事务清空标题、正文、来源引用和到期时间，只保留无内容墓碑；对已删除项重复删除幂等。

## 3. 静态保护与隐私

`IMemoryContentProtector` 是专用边界，不复用 `IProviderCredentialStore`。Windows 生产实现使用 DPAPI `CurrentUser`，并绑定记忆专用 purpose、entropy 和格式版本。SQLite 的 `protected_title`、`protected_body` 只保存二进制密文；不保存明文、明文哈希或可搜索替身。

解密只发生在 DesktopHost `MemoryService`，并只通过当前 Windows 用户专属 Named Pipe 返回给可见 DesktopClient。DTO 的诊断字符串会把内容标为 `[REDACTED]`。错误只返回稳定代码和普通用户可理解的安全消息，不包含标题、正文、密文或内部路径。

DPAPI CurrentUser 保护静态数据，但不抵御已经取得同一 Windows 用户权限、管理员权限或运行时内存读取能力的恶意进程；这是已知边界。

## 4. 存储与迁移

当前候选 SQLite 合同为 schema v10。v9 新增 `memory_items`；v10 不改记忆正文表，只给 Session Turn、Conversation Turn 和 `ai_invocations` 增加安全 consent/derived/audit 元数据，不保存临时出站 block、标题、正文、query 或 plaintext hash。

迁移顺序：

1. 读取并验证完整 schema v8；
2. 在数据库同目录创建唯一 `tasking.pre-v9-from-v8-<时间>.backup.db`；
3. 在单一 SQLite 事务中创建 v9 表、索引并记录版本；
4. 任一步失败则事务回滚，主库仍为 v8，pre-v9 备份保留。

从 v9 升级当前 v10 时，先验证完整 v9，再创建唯一 `tasking.pre-v10-from-v9-<时间>.backup.db`，并在单一事务中增加内容无关的出站审计列和索引；失败保持 v9。标记为 v10 但缺列的数据库明确拒绝，不静默补写。

V0.4.0 Stage 2 只支持 schema v8，不能打开 schema v9/v10。回滚时必须保留 v10 主库，先使用 pre-v10 回到 v9，或使用对应 pre-v9/隔离数据目录回到 Stage 2；不得覆盖真实用户数据库。

## 5. Host、IPC 与 UI

- `MemoryService`：唯一验证、项目授权检查、保护/解保护和存储编排入口。
- `SqliteMemoryStore`：只处理受保护记录、事务、版本冲突和墓碑。
- Desktop IPC protocol v10：保留 `memory.list/get/create/update/set-enabled/delete/preview`，并增加 Turn 记忆引用和 `sessions.memory-outbound.confirm`；v9/v10 不混用。所有诊断字符串必须脱敏输入与记忆正文。
- 稳定错误：`memory.invalid_request`、`memory.not_found`、`memory.conflict`、`memory.storage_failure`、`memory.protection_failure`。
- Desktop 设置页明确显示“仅保存在本机；当前不会自动发送给模型。”，并显示类别、作用域、状态、来源、更新时间和可选到期时间。
- “本地相关记忆预览”必须由用户点击触发，并明确显示“只在本机匹配；不会发送给模型。”；查询或项目变化会清除旧结果。

Host 启动先由既有 Task Store 完成 schema 初始化/迁移，再初始化 Memory Store。DesktopClient 不直接打开 SQLite 或调用保护器。

## 6. S3-R2 确定性本地预览

- Store 只返回 Active、未到期的候选；无项目时只读取 Global，选择项目时读取 Global 加该精确已授权项目。候选最多 200 条，按更新时间倒序和 Guid 稳定排序；项目不存在或已撤权时在解密前失败关闭。
- 查询仅在内存中执行 Unicode FormKC、Invariant 小写、字母数字分词和双字组匹配。固定分数为：完整规范化短语 1000、每个不同词 100、每个不同双字组 10；只有已经存在词法匹配时，精确项目作用域才加 5 分。
- 结果按分数、更新时间和 Guid 稳定排序，最多 8 条、标题与正文合计最多 4,000 字符；超出剩余预算的条目跳过，不提高上限。
- 查询、规范化文本、搜索索引、明文哈希或其他可搜索替身都不写入 SQLite、日志或证据。响应只附带分数及 `exact_phrase`、重叠数量、`project_scope` 等安全解释。
- 预览是只读操作，不修改记忆状态、版本或时间，也不创建 Prompt、Provider 请求或 `ai_invocations`。

## 7. S3-R3 逐 Turn 出站确认

1. 流程固定为“有序选择 Memory ID/version → 创建原 Turn 并冻结路由 → 返回完整出站快照 → 单次确认 → SQLite `BEGIN IMMEDIATE` 原子复核 → 最多一次调用”。本地预览不是同意。
2. 选择 1–8 条，标题+正文合计不超过 4,000 UTF-16 字符，严格 JSON 上下文不超过 6,000 字符；任何超限都拒绝，不截断或替换。
3. `USER_SELECTED_MEMORY_CONTEXT_V1` 只含类别、Global/Project 范围、标题和正文，作为临时 User 消息紧邻当前输入之前；Client 不拼 Prompt，数据库不保存该 block。
4. 确认绑定 Host instance、Session/Turn/version、冻结 Provider/Model/HTTPS origin、Prompt 身份、项目、item 顺序/version。内容、状态、到期、授权、路由、Prompt、输入、Stop 或重启变化都会使确认失效。
5. 成功回复只记录 `MemoryDerived`、原始路由和安全 manifest；后续 Provider/Model/origin 不完全一致时以 `memory_derived_history_route_mismatch` 拒绝，不 fallback 或重发。
6. `chat.general@2` 明确把 memory block 当作不可信参考数据；`intent.semantic`、动作、项目/文件/窗口授权和 CapabilityPolicy 永不接收或信任记忆。
7. v9 → v10 前创建唯一 pre-v10 备份并事务迁移；失败保持 v9。回滚必须保留 v10 主库，使用 pre-v10 备份或隔离目录。

## 8. 明确不在 S3-R1 / S3-R2 / S3-R3

- 自动记忆提取或用户画像；
- 自动或后台 Prompt/Provider 注入，以及语义建议中的任何记忆注入；
- 自动/后台检索、语义检索、RAG、Embedding 或向量数据库；
- 云同步、移动端、自动化或 Stage 4；
- 任何权限、目标、同意或确认的授予；
- Session 永久记忆开关、后台模型调用或未经逐 Turn 确认的网络请求。

这些能力需要后续独立架构和用户授权，不能由本切片自动延伸。
