# 元枢当前架构（V0.2.1 As-Built）

> 本文只描述已经存在的系统，不把 V2 设想写成已实现能力。更新时间：2026-08-23。

## 1. 运行结构

```text
用户
  ↓ 语音 / 可见界面
DesktopClient（WPF）
  ├─ 离线语音识别与朗读
  ├─ 可见确认、托盘和结果展示
  └─ 不直接访问数据库、Codex 或 Windows 动作
  ↓ 当前用户 Named Pipe，protocol v6
DesktopHost
  ├─ DeterministicIntentPlanner（确定性意图规划）
  ├─ AssistantCommandService（一次性计划与执行编排）
  ├─ CapabilityPolicyEngine（权限白名单）
  ├─ ConversationService → IConversationProvider
  ├─ Windows Skills（应用、网站、文件、可靠搜索）
  ├─ Window Vision（单窗口捕获、本机 OCR、UIA）
  ├─ Codex Project Skill（已授权 Git 项目）
  └─ SQLite / Evidence / Audit
```

## 2. 正式源码范围

V0.2.1 的正式源码不是某个单独目录，而是下面这组可共同重建产品的文件：

- `src/`：14 个生产项目。
- `tests/`：12 个自动化/真实桌面测试项目，包含 FakeCodexCli 和 FakeBrowser 测试替身。
- `tools/`：3 个集成与真实验收 Runner。
- `installer/`：Inno Setup 安装定义、简体中文语言文件和用户数据删除工具。
- `scripts/`：发布与隔离安装验收脚本。
- `assets/branding/`：正式品牌资源。
- `Directory.Build.props`、`global.json`、`ScreenGuide.slnx` 和各项目依赖锁文件：版本与可重复构建元数据。
- `PRODUCT.md`、`ARCHITECTURE.md`、`MEMORY.md`、`CHANGELOG.md`、`ROADMAP.md`、`AGENTS.md` 和 `docs/baselines/`：项目唯一事实来源。

`bin/`、`obj/`、`artifacts/`、运行日志、SQLite 数据库、语音模型和用户配置不属于源码。

## 3. 模块边界

| 模块 | 当前职责 | 不应承担 |
|---|---|---|
| `ScreenGuide.DesktopClient` | WPF 界面、托盘、可见确认、Host 生命周期、语音交互 | 直接执行动作、直接读写 SQLite、直接调用 Codex |
| `ScreenGuide.DesktopProtocol` | IPC 协议、DTO、当前用户 Pipe 客户端 | 业务规则和存储 |
| `ScreenGuide.DesktopHost` | 唯一业务编排入口、权限、恢复、审计 | 把 Provider 或 Windows 实现暴露给 UI |
| `ScreenGuide.AI.Core` | V0.2 安全子集的确定性意图规划 | 自由执行工具或隐式授予权限 |
| `ScreenGuide.Core` | 任务、对话和权限领域契约 | Windows、SQLite 或模型供应商细节 |
| `ScreenGuide.Persistence` | SQLite schema 与任务/对话存储 | UI 和模型调用 |
| `ScreenGuide.Skills.*` | 可替换技能接口与 Windows 低风险动作 | 任意桌面控制 |
| `ScreenGuide.Vision.*` | 单窗口捕获、敏感窗口拒绝、本机 OCR/UIA | 全桌面捕获和云端上传 |
| `ScreenGuide.Voice.Windows` | 本机采音、离线识别、回声过滤、朗读 | 保存录音或后台隐蔽监听 |
| `ScreenGuide.Agent.*` | Agent 抽象、Codex CLI 对话/编程连接 | 决定电脑动作权限 |
| `ScreenGuide.Evidence` | Git、测试和任务结果证据 | 代替真实测试 |

## 4. AI、Prompt 与模型接入

### 普通问答

`ConversationService` 依赖 `IConversationProvider`，因此接口层允许未来替换模型供应商。V0.2.1 的依赖注入只注册 `CodexConversationProvider`。它启动本机 Codex CLI，使用只读沙箱、禁止批准、独立工作目录和 JSON 输出 Schema。

普通问答 Prompt 当前由 `CodexConversationProvider.BuildPrompt` 内嵌生成。它要求简体中文、禁止现实操作、严格返回 `{ "reply": "..." }`。当前没有集中 Prompt 注册表、版本号、A/B 测试或评测流水线。

Codex 的 `Model` 选项存在于接口中，但 DesktopHost 默认没有指定；实际底层模型取决于本机 Codex 配置。因此，V0.2.1 不能声称绑定或稳定使用某个具体模型。

### DeepSeek

运行时代码中没有 DeepSeek API、Endpoint、Key、模型 ID 或 Provider。`prompts/DEEPSEEK_REVIEW.md` 是早期给外部 AI 做代码审查的文字模板，没有被产品加载。它属于历史辅助材料，不是元枢的 AI 大脑。

### 电脑动作

电脑动作不依赖大模型自由生成 Tool Call。`DeterministicIntentPlanner` 先把明确中文指令映射到有限意图，`CapabilityPolicyEngine` 校验一次性授权，`AssistantCommandService` 再调用白名单 Skill。模型输出、网页文字和窗口文字都不能生成操作授权。

## 5. 上下文、记忆和任务状态

### 已有的上下文

- 当前 UI 指令和两分钟过期的动作计划。
- 用户切回元枢前最后一个外部前台窗口。
- SQLite 中的 `conversations`、`conversation_messages`、`conversation_turns`。
- Codex 返回的 External Thread ID，用于同一普通对话续接。
- 项目、任务、命令、Agent Run、Decision Request、Evidence 和 Audit 状态。

### 当前没有的长期记忆

没有用户画像、长期事实提取、相关性检索、记忆冲突处理、过期时间、删除/纠错入口，也没有把任务决策与普通聊天统一检索的机制。SQLite 保存聊天记录不等于长期记忆；Codex Thread 续接也不等于元枢自己拥有可控记忆。

## 6. Tool Calling 与 Agent 状态

- Windows 动作采用程序内确定性 Skill 路由，不是开放式模型 Tool Calling。
- V0.2.1 开放的动作白名单：已登记应用、校验后的 HTTPS 网站、本次明确选择的文件、唯一可靠搜索框、经同意的单窗口说明。
- 编程任务采用 `IAgentConnector` / `ISkillAdapter` 边界，目前只有 Codex 项目技能。
- 已保存 Agent Run、Attempt、事件、等待用户决策和 TaskEvidence，可在 Host 重启后恢复“任务状态”，但没有通用多 Agent 编排器。

## 7. 数据与隐私

- 默认运行数据：`%LOCALAPPDATA%\ScreenGuide\V01`。
- SQLite：`state\tasking.db`；日志：`logs`；Codex 辅助数据：`codex`；任务证据：`evidence`。
- 语音模型沿用 `%LOCALAPPDATA%\ScreenGuideTeacher\models`，不在仓库和当前安装包中。
- 截图和录音不写入仓库；单窗口像素只在内存分析后清零。
- 卸载程序默认不删除用户数据，删除数据需要独立、可见操作。

## 8. 构建与发布

- .NET SDK：由 `global.json` 固定到 10.0.400，允许同补丁线更新。
- 普通依赖和 win-x64 发布依赖使用不同锁文件，并在发布脚本中以 locked mode 验证。
- `scripts/build-desktop-release.ps1` 顺序测试、发布 DesktopClient/DesktopHost、合并自包含目录并生成 Inno Setup 安装包。
- 安装包和 `artifacts/` 不进入 Git；Git 标签、SHA-256 和外部快照共同建立对应关系。

## 9. 当前主要技术债

- `MainWindow.xaml.cs`、`SqliteTaskStore.cs` 等少数文件过大。
- 普通会话、语音动作、窗口上下文和编程任务尚未统一到一个 Turn/Session 中枢。
- Prompt 内嵌且无版本/评测体系。
- 只有一个实际可用的通用对话 Provider。
- 安装包未签名，语音模型未纳入可分发方案。
