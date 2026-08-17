# V0.1 核心数据模型

契约版本：1

该模型是后续电脑端、云端和手机端的共同语义基础。增加可选字段可以向后兼容；删除字段、改变字段含义或改变状态转换必须升级契约版本并提供数据库迁移。

## 关系

```text
Device  1 ── N Command
Device  1 ── N Task（创建者）
Device  1 ── N Project（授权者）
Project 1 ── N Task
Project 1 ── N Command
Task    1 ── N TaskEvent
Task    1 ── N Command
Device  1 ── N AuditLogEntry（可空，系统恢复事件没有设备）
```

## Task（代码类型 AgentTask）

| 字段 | 类型 | 说明 |
|---|---|---|
| id | Guid | 主键 |
| projectId | Guid | 已授权项目 |
| createdByDeviceId | Guid | 创建任务的设备 |
| title | string | 用户可读标题 |
| instruction | string | 原始任务指令 |
| workingDirectoryRelativePath | string | 相对项目根目录，默认 `.` |
| executor | string | 执行器标识，V0.1 固定为 `codex`，本阶段不启动它 |
| status | TaskStatus | 权威任务状态 |
| cancellationRequestedAtUtc | DateTimeOffset? | 取消请求时间 |
| createdAtUtc / updatedAtUtc | DateTimeOffset | 创建和最后更新时间 |
| startedAtUtc / completedAtUtc | DateTimeOffset? | 实际开始和结束时间 |
| lastHeartbeatAtUtc | DateTimeOffset? | 后续执行器心跳 |
| failureCode / failureMessage | string? | 机器可读错误码和用户可读错误 |
| version | long | 乐观并发版本 |

状态：`Pending`、`Running`、`WaitingForUser`、`CancellationRequested`、`Succeeded`、`Failed`、`Cancelled`、`Interrupted`。

`Succeeded`、`Failed`、`Cancelled` 是终态。`Interrupted` 表示程序发现任务没有正常结束，但不等同失败，也不会自动重跑。

## TaskEvent

| 字段 | 类型 | 说明 |
|---|---|---|
| id | Guid | 主键 |
| taskId | Guid | 所属任务 |
| sequenceNumber | long | 任务内严格递增序号 |
| eventType | enum | Created、StateChanged、CancellationRequested、RecoveryDetected、Note |
| fromStatus / toStatus | TaskStatus? | 状态变化前后值 |
| source | enum | System、User、Agent、Recovery |
| sourceDeviceId | Guid? | 发起设备 |
| commandId | Guid? | 关联命令 |
| occurredAtUtc | DateTimeOffset | 发生时间 |
| message | string | 人类可读说明 |
| dataJson | JSON? | 版本化扩展数据 |

## Project

| 字段 | 类型 | 说明 |
|---|---|---|
| id | Guid | 主键 |
| name | string | 显示名称 |
| rootPath | string | 规范化绝对路径，忽略大小写唯一 |
| authorizationState | enum | Authorized 或 Revoked |
| authorizedByDeviceId | Guid | 执行本地授权的设备 |
| authorizedAtUtc / revokedAtUtc | DateTimeOffset? | 授权和撤销时间 |
| createdAtUtc / updatedAtUtc | DateTimeOffset | 记录时间 |

项目授权只证明该根目录可供任务选择，不代表允许任意外部副作用。
授权根目录和任务工作目录必须已经存在；为防止路径越界，V0.1 拒绝路径链中包含 Windows 符号链接或目录联接的目录。

## Device

| 字段 | 类型 | 说明 |
|---|---|---|
| id | Guid | 主键 |
| displayName | string | 用户可识别名称 |
| deviceType | enum | WindowsHost 或 Mobile |
| trustState | enum | Local、Paired、Revoked |
| publicKeyThumbprint | string? | 后续配对使用，只保存指纹，不保存私钥 |
| createdAtUtc / lastSeenAtUtc / revokedAtUtc | DateTimeOffset? | 生命周期时间 |

第 1 步只登记 `Local` WindowsHost，不实现手机配对。

## Command

| 字段 | 类型 | 说明 |
|---|---|---|
| id | Guid | 主键 |
| sourceDeviceId | Guid | 来源设备 |
| projectId | Guid? | 目标项目 |
| taskId | Guid? | 处理后关联的任务 |
| idempotencyKey | string | 来源设备范围内唯一的幂等键 |
| commandType | enum | CreateTask、CancelTask、ResumeTask、UserResponse |
| payloadJson | JSON | 原始结构化载荷 |
| receivedAtUtc / expiresAtUtc | DateTimeOffset? | 接收和过期时间 |
| status | enum | Received、Processed、Rejected、Failed |
| processedAtUtc | DateTimeOffset? | 完成处理时间 |
| rejectionReason | string? | 拒绝原因 |

命令去重键是 `(sourceDeviceId, idempotencyKey)`，而不是命令 ID。网络重试必须复用同一个幂等键。

## AuditLogEntry

审计记录包含 `id`、`occurredAtUtc`、`actorDeviceId`、`action`、`entityType`、`entityId`、`outcome` 和 `detailsJson`。审计日志描述安全相关事实，不代替完整 TaskEvent 时间线。

## 状态转换

```text
Pending -> Running | CancellationRequested | Failed
Running -> WaitingForUser | CancellationRequested | Succeeded | Failed | Interrupted
WaitingForUser -> Running | CancellationRequested | Failed | Interrupted
CancellationRequested -> Cancelled | Failed | Interrupted
Interrupted -> Pending | Running | CancellationRequested | Failed | Cancelled
Succeeded / Failed / Cancelled -> 不允许再转换
```

恢复时只把 `Running`、`WaitingForUser`、`CancellationRequested` 标记为 `Interrupted`；`Pending` 保持等待状态。

## SQLite 原子性

- 创建任务、处理 CreateTask 命令、写入首个 TaskEvent 和 AuditLog 在同一事务内完成。
- 状态转换、TaskEvent 和 AuditLog 在同一事务内完成。
- 取消请求、状态变化、事件和审计在同一事务内完成。
- 状态更新使用 `version` 防止旧数据覆盖新状态。
