# 元枢 V2 阶段 3 完成报告

日期：2026-08-30

版本：V0.5.0

标签：`v0.5.0-stage3`，目标 `d553e7e9d606037df87d98e99250de5498f5934a`

当前状态：**INTEGRATED_PASS / FINAL FREEZE**

## 结论

V2 阶段 3“可控长期记忆”的 R1/R2/R3 已完成集成、离线 Release 定向门禁、实际 Release DesktopClient + DesktopHost 本机产品验收和独立干净标签源码构建。安装包身份已与正式标签目标建立可复核关系；Stage 3 到此冻结。

## 主要交付

- 与 Conversation、Session/Turn、Codex Task、Provider Thread 和 `ai_invocations` 分离的本机加密记忆账本。
- 用户显式 CRUD、启停、确认删除、版本冲突、项目授权、到期与无内容墓碑。
- 用户主动触发的有界确定性本地预览，不保存查询、不调用模型。
- protocol/schema v10、`chat.general@2`、逐 Turn 完整出站快照和一次性确认。
- 记忆不进入语义意图、电脑动作、权限或 Codex；无自动提取、后台检索、retry、fallback 或 resend。

## 验收摘要

| Gate | 结果 |
|---|---|
| S3-R1 / R2 / R3 | `INTEGRATED_PASS` |
| exact-SHA QA / Security | PASS |
| Stage 3 离线 Release 定向门禁 | 90/90 PASS |
| 实际 Release DesktopProduct | 2/2 PASS；Stage 2 回归 + Stage 3 完整本机流程 |
| Provider / credential / network | 0 / 0 / 0 |
| DesktopHost / DesktopClient Release build | PASS；0 warning、0 error |
| Git / tag | `v0.5.0-stage3` → `d553e7e9...`；annotated tag object `a67d2b24...` |
| 标签源码产物 | locked restore / Release publish / installer compile PASS；539 个发布文件 |
| 安装包 | 64,173,791 bytes；SHA-256 `4683E10C...C16F95`；未签名 |

## 保留风险

- 同 AppId 安装—卸载—重装尚未在干净 Windows 环境完成，不放行对外分发。
- 安装包如未签名，Windows 会显示未知发布者。
- 回滚到 V0.4.0 时必须保留 schema v10 数据库并使用匹配来源的 pre-v10 备份或隔离目录。
- 自动记忆提取、语义/RAG 检索、向量数据库、画像、跨 Session 自动个性化不属于 Stage 3。

Stage 3 最终冻结后在此停止，不自动启动 Stage 4。
