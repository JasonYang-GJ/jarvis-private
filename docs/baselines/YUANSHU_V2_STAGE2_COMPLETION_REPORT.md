# 元枢 V2 阶段 2 完成报告

日期：2026-08-29
版本：V0.4.0
标签：`v0.4.0-stage2`
最终状态：**INTEGRATED_PASS / FINAL FREEZE**

## 结论

V2 阶段 2“可替换 AI 大脑与模型路由”已完成。普通聊天发布目标为 DeepSeek + Qwen，Qwen 是手动备用；两者都不能自动 fallback、retry 或跨 Provider resend。Codex 普通聊天继续失败关闭，原有 Codex 编程 Agent 与聊天 Provider 独立。

## 主要交付

- Provider 无关 Chat Model 契约、Registry、Turn 冻结 Router 和无静默降级边界。
- DeepSeek/Qwen 普通聊天 Provider，统一 SSE、取消、错误、健康和安全证据契约。
- Prompt Registry 与 SHA-256 追踪，SQLite schema v8 `ai_invocations` 审计。
- Windows DPAPI CurrentUser Provider 凭据、短生命期 lease、UI/IPC 不回显密钥。
- 普通聊天 Provider/Model 设置页，数据去向提示和手动切换。
- 只建议不授权的语义意图层；所有真实目标、确认和权限继续由本机确定性边界决定。

## 验收摘要

| Gate | 结果 |
|---|---|
| S2-R1 / R2 / R3 | `INTEGRATED_PASS` |
| DeepSeek 真实验收 | 已冻结 PASS，R4 未重测 |
| Qwen 真实 Health/Chat/Cancellation | PASS，3/3 HTTP、2/2 模型请求，0 retry/fallback/resend |
| R4 离线 Release 定向 QA | 173/173 PASS |
| 集成后定向 smoke | Qwen 56/56；R4 Runner 61/61；隔离 3/3 |
| 依赖锁冻结 | locked restore PASS；受影响项目 6/6 Release build，0 warnings / 0 errors |
| 普通聊天选 Qwen 时的真实 Codex Task | 1 Task / 1 attempt，`Succeeded/Verified`，普通聊天 Provider 请求 0 |
| Git 集成 | fast-forward only，ancestry / clean PASS |

详细冻结身份、标签目标、安装包哈希和回滚边界以 [V0.4.0 Stage 2 基线](V0.4.0_STAGE2.md) 为准。

## 保留风险

- 同 AppId 安装—卸载—重装尚未在干净 Windows 环境完成，不放行对外分发。
- 安装包如未签名，Windows 会显示未知发布者。
- 回滚到 V0.3.0 时必须保留 schema v8 数据库并使用 pre-v8 备份或隔离数据目录。
- 长期记忆、RAG、向量数据库、手机端和复杂多 Agent 产品不属于本阶段。

Stage 2 在此停止，不自动启动 Stage 3。
