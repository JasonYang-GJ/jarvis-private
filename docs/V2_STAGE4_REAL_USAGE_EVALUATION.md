# V2 Stage 4 S4-R2 本机真实使用评测合同

## 目标与边界

S4-R2 只回答两个问题：本机离线中文语音在真实麦克风下的失败率和延迟是多少；单一可见窗口在真实 Windows Graphics Capture + 本机 OCR 路径下的失败率和延迟是多少。

它不改变产品功能，不加入后台遥测，不读取凭据，不联网，不调用普通聊天 Provider，也不保存用户内容。

## 固定样本与终态

- Voice：1 次预热 + 20 次正式尝试；每次由用户按 Enter 主动开始，15 秒内只允许一个非空 final，固定版本规范化后必须精确匹配可见的非个人短句。
- Voice diagnostic：仅在正式批次出现无法解释的 `voice.text_mismatch` 后使用；重新取得可见批次同意并采集 1 个样本，只输出预期/实际长度关系和编辑距离分桶，不输出或保存转写正文。诊断样本不计入正式成功率，也不能替代正式批次。
- Vision：1 次预热 + 20 次正式尝试；每次由用户按 Enter 主动开始，只读取 Runner 创建的可见测试 HWND，并在内存中检查固定非个人标记。
- Vision identity change：单独一次受控场景；捕获前改变目标标题，必须以 `Cancelled / vision.identity_changed` 结束且不得保留帧。
- 终态只有 `Success`、`Failure`、`Cancelled`、`Blocked`。失败率为 `failure / (success + failure)`；cancelled/blocked 单独报告。预热不进入正式指标，不删除离群值。

## 计时与汇总

只使用 `Stopwatch.GetTimestamp/GetElapsedTime`。Voice 统计检测到语音到首个 final 的端到端时间；Vision 分别统计 capture、analysis 和 end-to-end。每组输出 count、min、p50、p95、max，p50/p95 使用 nearest-rank。

首批建议观察线为 Voice 成功率 90%、Vision 成功率 95%。在至少两批干净环境数据前不设硬性延迟门槛。

## 同意、停止与清理

用户必须在可见终端输入 `YES` 才能开始整个批次，每次尝试仍需按 Enter。视觉测试窗口必须先显示，再把该次同意绑定到其精确身份。活动尝试期间输入 `STOP` 或按 Ctrl+C 会通过 CancellationToken 立即终止；语音每次尝试使用独立 Start/Stop 监听周期，帧在每次分析后清零。fault、配置变化或窗口身份变化均结束当前授权，后续重跑必须重新同意。

## 允许与禁止的证据

允许：合同版本、exact SHA、模式、粗粒度 Windows build/架构、能力布尔值、麦克风数量桶、次数、四类终态、聚合耗时、稳定错误码、清理状态、网络/Provider 请求数 0，以及仅由计数构成的语音匹配摘要（样本数、编辑距离 0/1/2/3+ 分桶、同长度不匹配、较短、较长）。

禁止：录音、波形、partial/final 转写、固定短句、图像、OCR/UIA 正文、窗口标题、HWND/PID/启动时间、用户名/机器名、路径、异常消息/堆栈、Prompt、Provider、凭据或内容哈希。

默认只在标准输出生成一条 `YUANSHU_S4_R2_RESULT` JSON，不写文件。Runner 在任何设备操作前还会把 `--expected-sha` 与自身 ProductVersion 中的源码 SHA 精确比较，不匹配时失败关闭。

## 门禁顺序

1. 精确 SHA、Parent、Branch、Worktree、clean Git 静态核验；
2. 离线定向单元测试与 Release Runner build；
3. 独立 QA 对 exact SHA 做小型 Commit Gate；
4. 用户从可见 Windows 终端手工执行 Voice、Vision、Vision identity-change；
5. Coordinator 检查成功率、聚合证据、Stop/identity-change 和零网络/Provider 事实后，才可进入 S4-R2 Integration Gate。
