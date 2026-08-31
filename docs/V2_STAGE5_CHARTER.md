# 元枢 V2 Stage 5 Charter / Preflight

## 1. 状态与授权边界

- 推荐阶段名称：**Stage 5 = Safe Distribution & Upgrade Readiness（安全分发与升级准备）**。
- 当前状态：**CHARTER / PREFLIGHT ONLY**。
- 实施状态：**NOT_STARTED / NOT_AUTHORIZED**。
- 本文只定义用户问题、串行切片、风险和验收合同，不授权任何产品、安装器、签名、网络或发布行为。
- V0.6.0 / Stage 4 已是 `INTEGRATED_PASS / FINAL FREEZE`。C0、`v0.6.0-stage4`、独立标签源码构建和 C1 证据不可变；后续分发准备不新增、也不命名为 S4-R5，不修改 V0.6.0 tag 或安装包 identity。

## 2. 要解决的用户问题

元枢功能已经冻结，但用户目前仍不能确定能否安全地把产品交给另一台 Windows 机器安装、升级和回滚，原因包括：

- 安装包未签名；
- 尚未在隔离、干净 Windows 环境完成 same-AppId install → launch → upgrade → rollback → uninstall 生命周期；
- 语音模型许可、下载、更新与外部分发边界尚未放行；
- 签名证书来源、费用、私钥保护和最终 release identity 尚无经 Owner 批准的合同。

Stage 5 的用户价值是给出可核验、失败关闭的答案：**是否可安全交付另一台机器、为什么，以及还缺什么。**

## 3. 推荐的最小串行切片

以下切片只是规划。每一项开始前都需要独立 Delegation Packet、Owner 可见授权和 exact-SHA 门禁。

### S5-R1 Distribution Contract & License Inventory

- Offline Inventory 与 Official Evidence Verification：**PASS**；唯一详细证据见 [S5-R1 分发许可与归属清单](V2_STAGE5_DISTRIBUTION_LICENSE_INVENTORY.md)。
- Attribution / NOTICE Contract：**S5-R1_CONTRACT_PASS**；Packaging / NOTICE Gate Infrastructure：**FAIL_CLOSED_INFRASTRUCTURE_IMPLEMENTED**；Exact LICENSE/NOTICE Bundle：**NOTICE_BUNDLE_IMPLEMENTATION_PASS**。冻结 payload 的 539 条 installed-payload 与 2 条 installer-container entry 已绑定精确材料、双 SHA-256 和组件内 NOTICE 路径；WinSDK reference-only terms、Inno 6.7.3 与 Kira exact-commit translation 已闭合，失败关闭边界不变。External Distribution 仍为 **EXTERNAL_DISTRIBUTION_BLOCKED**。
- 只读盘点安装包、语音模型、第三方资产和依赖的许可与分发边界。
- 为每项给出可分发、不可分发或证据不足的明确判定；证据不足时失败关闭。
- 不下载模型，不接受许可条款，不采购服务，不修改 V0.6.0 tag 或产物。
- C0 installer 的产品 payload 收纳边界是 win-x64 publish 树与删除脚本；Inno engine/translation 是 installer 基础设施。中文语音模型为 `VERIFIED-EXCLUDED`，未来捆绑/下载时重新进入许可 Gate。WinSDK Ref 已按 exact DLL/package/REDIST/Owner acceptance/reference-only terms 闭合；七个非 win-x64 Sherpa 已由 539 payload 不存在性关闭当前 Windows x64 边界，未来纳入其他 RID 时重开 Gate。
- 四轴合同固定区分 Source、Bundling、Attribution 与 Notice；contract PASS 不等于 NOTICE 已实现或可分发。
- Owner 权属/分发形态确认、原始 C0 只读 manifest、clean rebuild、NOTICE installer 修改、未来在线补证和 S5-R2 lifecycle 都是独立授权门禁。
- release/installer 已接入离线 fail-closed bundle gate，exact bundle 通过后才生成确定性 mandatory Inno include；缺失/篡改/错绑/越界/default-branch substitute 均在 restore/publish/installer compile 前阻断。安装后 layout 验证与新 release identity 尚未执行；不得把它们塞入 S5-R2 或拖到 S5-R4。
- Stage 5 implementation 整体仍未完成；Charter 不预定 V0.7.0，不授权下载、真实安装、签名或发布。S5-R2 仅完成离线 Harness 候选，真实 Sandbox 生命周期仍需独立 QA 授权与执行。

### S5-R2 Isolated Installer Lifecycle

- 仅在未来单独授权的干净、隔离 Windows 环境中验证 install → launch → upgrade → rollback → uninstall。
- 保护现有安装登记、现有用户数据和当前开发机；不得在需保留的同 AppId 安装上试验。
- 验证 Host+Client 成对升级/回滚、数据库保留、匹配备份与缺失备份失败关闭。
- 不把浅层脚本成功或安装器退出码单独当作用户可见生命周期通过。
- 离线 Harness 状态为 `READY_FOR_QA`：Host 只做 exact-SHA/clean/hash/Sandbox/既有安装预检并生成临时 `.wsb`；输入只读映射，证据只写入专属临时目录，网络、剪贴板、音频、麦克风、视频与打印均关闭，Host 不执行安装器。
- Sandbox bootstrap 固定验证 V0.5 schema v10 → V0.6 schema v11 → 匹配 `pre-v11-from-v10` 备份回滚 → 卸载与数据保留，并输出不含正文、路径、凭据或日志的紧凑 JSON。当前真实 Sandbox 启动和安装器执行均为 0，不能记为生命周期 PASS。

### S5-R3 Signing & Release Identity

- 先定义签名策略、证书来源与费用、私钥保存/使用边界、时间戳和 signed artifact identity。
- 证书采购、费用支出、私钥接触、真实签名及任何外部服务访问必须分别获得 Owner 可见授权。
- 未签名、签名无效、身份/hash/version 不一致或私钥边界不明确时失败关闭。

### S5-R4 Final Distribution Acceptance

- 仅在许可、生命周期、签名与 release identity 门禁全部通过后执行小型最终 release gate。
- 复用 Stage 1～4 已通过的 exact-SHA 证据，不机械重跑 600+ 全矩阵。
- Push、上传、发布、对外发送安装包或创建公开下载入口仍需独立 Owner 授权。

## 4. 明确非目标

Stage 5 不用于：

- 增加新产品功能、新动作范围、RAG/记忆能力、Provider、手机端或云端远控；
- 拆分 `SessionCoordinator`，或把内部重构冒充用户分发价值；
- 修改 schema、protocol、Prompt、Provider、凭据或权限合同；
- 自动下载语音模型、自动接受许可、自动购买证书、自动签名、自动运行安装器或自动发布；
- 启动 Stage 6 或其他未批准路线。

## 5. 风险分层

- 本 Charter 文档：**Low**，本地、可逆、无产品行为变化。
- S5-R1 许可判定：**High**，错误结论可能导致未经授权分发。
- S5-R2 安装生命周期：**High**，可能影响安装登记、应用文件和用户数据。
- S5-R3 签名：**High**，涉及费用、证书、私钥和外部信任链。
- S5-R4 对外分发：**High**，涉及上传、发布和第三方获取。

风险不会因 Charter 通过而自动降级；每个切片都必须重新取得授权。

## 6. 最小 Acceptance Criteria

Stage 5 只有在以下证据链分别成立时，才可回答“可安全交付另一台机器”：

1. Licensing：语音模型、第三方资产和依赖的许可/分发结论可追溯；未知即失败关闭。
2. Lifecycle：隔离干净机的安装、启动、升级、回滚、卸载和数据保护通过；缺证据即失败关闭。
3. Signature：签名有效，证书/时间戳/私钥边界符合批准合同；不一致即失败关闭。
4. Release identity：tag、ProductVersion、FileVersion、文件数、installer hash/signature 与目标产物完全一致；漂移即失败关闭。
5. V0.6.0 tag 与安装包 identity 保持不变；Stage 5 不回写或重建 Stage 4 冻结事实。
6. 证书、私钥、费用、联网、安装、上传和发布均有独立、可见的 Owner 授权。

## 7. 预计验证（规划，不在 Charter 执行）

### Charter 本轮

- doc-only exact-SHA QA；
- 只允许 `ROADMAP.md`、`PRODUCT.md`、`ARCHITECTURE.md`、`MEMORY.md` 与本文变化；
- `git diff --check`、Git clean、四份事实文档与本文一致；
- `v0.6.0-stage4` tag object/target 不移动；
- 不运行测试、构建、安装器、GUI、麦克风、网络、Provider 或凭据操作。

### 未来切片

- S5-R1：license inventory static gate 与来源/判定一致性检查；
- S5-R2：isolated clean-machine lifecycle targeted smoke、rollback 和数据保护证据；
- S5-R3：signature verification、hash/version/release identity 检查；
- S5-R4：小型 release regression，复用 Stage 1～4 已通过证据，不运行 600+ 全矩阵。

## 8. 版本与 Tag 决策

本 Charter 不决定 V0.7.0、任何新版本号或新 tag。版本和 tag 只能在 S5-R1 的架构/发布合同形成并获得 Owner 批准后确定。`v0.6.0-stage4` 必须继续指向既有 C0。

## 9. 外部活动默认值

- Charter：NetworkRequests=0、ProviderRequests=0、CredentialReads=0、InstallerRuns=0、SigningOperations=0。
- 未来 Stage 5 默认不需要真实 Provider 请求；若出现需要，必须先证明与分发验收直接相关并单独申请授权。

## 10. 角色与治理

- 未来继续复用现有 Architect/Security、Developer 与 QA，不因 Stage 5 创建新角色。
- Developer 是每个批准切片的唯一写入者；Coordinator 继续担任 Integration Owner。
- Module Registry 保持 Shadow；本 Charter 不写 Registry 或 Lease。
- 本 Charter 提交后需要 Architect/Security exact-SHA 只读一致性 Gate；Gate 通过也不等于 Stage 5 实施授权。
