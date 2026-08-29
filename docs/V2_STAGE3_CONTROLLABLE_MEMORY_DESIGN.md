# 元枢 V2 阶段 3 可控长期记忆设计（S3-R1）

> 状态：S3-R1 候选；不是完整阶段 3，也不是正式发布基线。更新时间：2026-08-29。

## 1. 本切片解决什么

S3-R1 只建立一个独立、加密、可由用户显式管理的本机长期记忆账本。用户可在 Desktop 设置页新增、查看、修正、启用/停用和确认删除记忆。当前不会自动从聊天中提取记忆，不会检索记忆，不会把记忆发送给任何模型。

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

当前候选 SQLite 合同为 schema v9。v9 只新增 `memory_items` 表及状态/到期、项目/状态索引，不修改 Conversation、Session、Task、`ai_invocations` 或凭据结构。

迁移顺序：

1. 读取并验证完整 schema v8；
2. 在数据库同目录创建唯一 `tasking.pre-v9-from-v8-<时间>.backup.db`；
3. 在单一 SQLite 事务中创建 v9 表、索引并记录版本；
4. 任一步失败则事务回滚，主库仍为 v8，pre-v9 备份保留。

V0.4.0 Stage 2 只支持 schema v8，不能打开 schema v9。回滚时必须保留 v9 主库，改用 pre-v9 备份或隔离数据目录；不得把 v9 主库复制回 v8，也不得覆盖真实用户数据库。继续回滚到 Stage 1 时，仍需使用对应的 pre-v8 备份。

## 5. Host、IPC 与 UI

- `MemoryService`：唯一验证、项目授权检查、保护/解保护和存储编排入口。
- `SqliteMemoryStore`：只处理受保护记录、事务、版本冲突和墓碑。
- Desktop IPC protocol v9：`memory.list/get/create/update/set-enabled/delete`；删除 DTO 必须带 `Confirmed=true`。
- 稳定错误：`memory.invalid_request`、`memory.not_found`、`memory.conflict`、`memory.storage_failure`、`memory.protection_failure`。
- Desktop 设置页明确显示“仅保存在本机；当前不会自动发送给模型。”，并显示类别、作用域、状态、来源、更新时间和可选到期时间。

Host 启动先由既有 Task Store 完成 schema 初始化/迁移，再初始化 Memory Store。DesktopClient 不直接打开 SQLite 或调用保护器。

## 6. 明确不在 S3-R1

- 自动记忆提取或用户画像；
- Prompt/Provider/语义建议中的记忆注入；
- 相关性检索、RAG、Embedding 或向量数据库；
- 云同步、移动端、自动化或 Stage 4；
- 任何权限、目标、同意或确认的授予；
- 记忆使用开关、后台模型调用、AI Invocation 或网络请求。

这些能力需要后续独立架构和用户授权，不能由本切片自动延伸。
