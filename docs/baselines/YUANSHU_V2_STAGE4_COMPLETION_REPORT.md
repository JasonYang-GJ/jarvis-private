# 元枢 V2 Stage 4 完成报告（C0 预冻结候选）

## 结论

截至 2026-08-30，S4-R1、S4-R2、S4-R3 与 S4-R4 均已 `INTEGRATED_PASS`，源码开始进入 V0.6.0 / Stage 4 Final Freeze C0 候选流程。

这不是最终完成报告：`v0.6.0-stage4` 尚未创建，独立标签源码构建及安装包大小、SHA-256、发布文件数、签名状态尚未产生，后续 C1 仅文档证据提交也尚未完成。V0.5.0 / Stage 3 仍是当前正式发布基线。

## 已完成范围

### S4-R1：Window Identity v2

- 单窗口授权使用 Host 可信 `{HWND, PID, ProcessStartTimeUtc, ProcessName, Title}`。
- schema v11 持久化 PID 与启动时间；窗口确认、UI Automation、捕获和分析前均重验完整身份。
- 公开桌面操作 IPC 不接受客户端 HWND/标题作为授权；历史 v10 Turn 不复用旧授权。

### S4-R2：本机真实使用评测

- 独立前台 Runner 只做人工启动的本机评测，不进入产品运行时。
- 真实语音固定合同结果为 18/20，另有独立 STOP 取消通过。
- 真实视觉固定合同结果为 20/20，identity-change 按稳定取消错误失败关闭。
- 不保存声音、转写正文、窗口像素或自由文本诊断；网络和 Provider 请求为 0。

### S4-R3：有界 Session 投影与 Task 状态事件

- Desktop IPC 升至 protocol v11；SQLite 保持 schema v11。
- Session bootstrap/delta/reset、消息 keyset 分页、精确 Turn 查询和 Client 有界缓存替代长会话全量重复快照。
- 编程 Task 使用持久化提交后的状态事件唤醒，移除 250ms 轮询；SQLite 仍是权威状态真源。

### S4-R4：Session runtime gate 有界化

- Session/Turn 临时门闩抽取为 Host 内部单例注册表。
- holder 与 waiter 共同引用计数，最后释放时按 key 与同实例身份安全移除，空闲计数回到 0。
- 该注册表只做同 key 串行，不持久化状态、不发布事件、不拥有记忆同意、任务状态或权限，也不替代 `_currentSessionGate`。

## C0 版本与构建合同

- Version / InformationalVersion：`0.6.0`
- AssemblyVersion / FileVersion：`0.6.0.0`
- 安装包名称：`元枢-V0.6.0-安装包.exe`
- 计划标签：`v0.6.0-stage4`
- C0 exact SHA：待提交后由 Git 事实核对，本文不自引用预填
- 标签源码产物身份：`PENDING_AFTER_TAG_BUILD`

C0 的离线 Host/Client Release build、安装包编译和静态检查属于预标签候选门禁；即使通过，也不能替代标签源码的独立重建和 C1 最终证据。

## 数据兼容与回滚

- 当前 Desktop IPC 为 v11，SQLite schema 为 v11。
- 回滚到 V0.5.0 时，Host 与 Client 必须成对回滚。
- 必须保留 v11 主库，只能在隔离数据目录使用匹配的 `pre-v11-from-v10`。
- 没有匹配备份时失败关闭，不覆盖正式主库、不原地降级、不让 V0.5.0 打开 v11 数据库。

## 安全与发布边界

- C0 不改变 Provider、Prompt、凭据、权限、AppId、协议、schema 或安装行为。
- C0 不执行真实 Provider/Codex 请求，不读取真实凭据，不启动 GUI 或麦克风，也不重跑已冻结的真实语音/视觉/DeepSeek/Qwen 证据。
- 同 AppId 安装—卸载—重装生命周期尚未放行。
- 安装包未签名；语音模型许可和分发方案未放行。
- 因此本候选不能对外分发，也不开始 S4-R5、Stage 5 或其他功能开发。

## 最终冻结尚缺

1. 创建并核验 C0 exact SHA。
2. 创建 annotated tag `v0.6.0-stage4`。
3. 从独立干净标签源码完成 locked restore、Release build、测试和安装包编译。
4. 记录与标签绑定的 ProductVersion、发布文件数、安装包大小、SHA-256 和签名状态。
5. 由仅文档 C1 提交回填最终证据，不改变标签源码或发布二进制。
6. 对同 AppId 生命周期、数字签名和语音模型许可/分发作出明确发布决定。

在这些门禁完成前，本报告状态为 **C0 / PRE-FREEZE CANDIDATE**，不是 Stage 4 最终冻结完成声明。
