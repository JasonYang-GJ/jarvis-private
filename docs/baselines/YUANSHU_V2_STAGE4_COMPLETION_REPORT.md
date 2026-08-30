# 元枢 V2 Stage 4 完成报告

## 结论

截至 2026-08-30，S4-R1、S4-R2、S4-R3 与 S4-R4 均已 `INTEGRATED_PASS`，V0.6.0 / Stage 4 已完成 Final Freeze。

正式标签 `v0.6.0-stage4` 指向 C0 `3a591a7b6af7da7d97e07093d4c33a3f44553b82`，annotated tag object 为 `20045c7960c182a052a5e0b2552ce0ed14a3863f`。独立干净标签源码构建与产物身份已核验；本 C1 只记录证据，不改变标签源码或二进制。安装包仍未获对外分发放行。

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

## 冻结版本与产物证据

- Version / InformationalVersion：`0.6.0`
- AssemblyVersion / FileVersion：`0.6.0.0`
- 安装包名称：`元枢-V0.6.0-安装包.exe`
- 正式标签：`v0.6.0-stage4`
- C0 exact SHA / TagTarget：`3a591a7b6af7da7d97e07093d4c33a3f44553b82`
- annotated tag object：`20045c7960c182a052a5e0b2552ce0ed14a3863f`
- PublishFileCount：539
- Client ProductVersion / FileVersion：`0.6.0+3a591a7b6af7da7d97e07093d4c33a3f44553b82` / `0.6.0.0`
- Host ProductVersion / FileVersion：`0.6.0+3a591a7b6af7da7d97e07093d4c33a3f44553b82` / `0.6.0.0`
- InstallerBytes：64,203,075
- InstallerSha256：`5F912F94960E1E90A1EF918139C46751FCA8377A4055B5E68BA060A9BF4E56D7`
- SignatureStatus：`NotSigned`

S4-R1～R4 与 C0 QA 的 exact-SHA targeted 测试证据在冻结流程中复用，没有在标签源码阶段重跑。独立标签源码已完成离线 locked restore、`build-desktop-release.ps1 -SkipTests` 的 Client/Host publish、安装包编译和身份检查；网络、Provider、凭据读取、GUI 和麦克风活动均为 0。

## 数据兼容与回滚

- 当前 Desktop IPC 为 v11，SQLite schema 为 v11。
- 回滚到 V0.5.0 时，Host 与 Client 必须成对回滚。
- 必须保留 v11 主库，只能在隔离数据目录使用匹配的 `pre-v11-from-v10`。
- 没有匹配备份时失败关闭，不覆盖正式主库、不原地降级、不让 V0.5.0 打开 v11 数据库。

## 安全与发布边界

- C0 与 C1 不改变 Provider、Prompt、凭据、权限、AppId、协议、schema 或安装行为；C1 只记录证据，不改变二进制身份。
- 冻结流程不执行真实 Provider/Codex 请求，不读取真实凭据，不启动 GUI 或麦克风，也不重跑已冻结的真实语音/视觉/DeepSeek/Qwen 证据。
- 同 AppId 安装—卸载—重装生命周期尚未放行。
- 安装包未签名；语音模型许可和分发方案未放行。
- 因此 Final Freeze 不能视为已获对外分发批准，也不开始 S4-R5、Stage 5 或其他功能开发。

## 最终冻结门禁结果

1. C0 exact SHA 与 annotated tag object/target：PASS。
2. 复用 S4-R1～R4 与 C0 QA exact-SHA targeted 测试证据：PASS。
3. 独立干净标签源码离线 locked restore、`build-desktop-release.ps1 -SkipTests` Client/Host publish 与安装包编译：PASS。
4. ProductVersion、FileVersion、发布文件数、安装包大小、SHA-256 和签名状态：已记录。
5. C1 仅文档证据提交：不改变标签源码或发布二进制。
6. 同 AppId 干净机生命周期、数字签名和语音模型许可/对外分发：尚未放行。

本报告状态为 **FINAL FREEZE / INTEGRATED_PASS**。该状态证明源码与产物身份已经冻结，但不等于安装包已经签名、完成干净机生命周期或获准对外分发。
