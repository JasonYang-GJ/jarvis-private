# ScreenGuide Desktop（开发代号）

ScreenGuide Desktop 是运行在 Windows 上的个人 AI 中枢。用户打开元枢后，首页会自动进入可见的本机语音监听状态，不需要按键或输入文字；明确的低风险语音指令本身就是本次一次性授权。读取画面、打开文件和编程任务仍保留单独确认。Codex 只是可选的编程技能，不是产品中心。

最近一次正式冻结版本是 V0.3.0 阶段 1，V0.2.1 仍是更早的可独立回滚基线。当前工作树正在验收 V2 阶段 2“可替换 AI 大脑与模型路由”，尚未形成新的正式版本、标签或安装包。产品事实以 [PRODUCT.md](PRODUCT.md) 为准，当前代码结构以 [ARCHITECTURE.md](ARCHITECTURE.md) 为准，阶段 2 设计与待验收边界见 [V2_STAGE2_AI_MODEL_ROUTING_DESIGN.md](docs/V2_STAGE2_AI_MODEL_ROUTING_DESIGN.md)，冻结证据仍以 [V0.3.0 阶段 1 基线](docs/baselines/V0.3.0_STAGE1.md) 和 [V0.2.1 基线](docs/baselines/V0.2.1_BASELINE.md) 为准。

V0.3.0 新增统一会话中枢：首页连续输入默认属于同一会话，用户插话会从界面到 Provider 调用链真正取消旧回答；缺项目、文件或单窗口查看同意时，原始请求会在同一任务中等待补充并自动续接。普通聊天、受控操作、窗口观察和编程任务共用一致的可见状态，但既有权限与安全门禁不变。

阶段 2 候选代码在不改变 SessionCoordinator 的前提下新增统一 Chat Model、Provider Registry、Model Router、Prompt Registry、DPAPI 凭据、Codex/DeepSeek 普通聊天 Provider 和设置页。普通聊天 Provider 可以独立选择，编程任务仍由 Codex 负责；模型语义输出只是不可信建议，现实操作仍必须经过本机确定性规划、项目/文件/窗口确认和 CapabilityPolicy。当前开发期自动化边界已建立，但真实 Codex、真实 DeepSeek Key/网络、实际 Release DesktopClient、全量回归和版本冻结仍待完成，不能把候选实现描述成已发布能力。

当前 V0.2 安全能力：

- 元枢窗口首次显示后自动开始本机离线中文识别，关闭到托盘后继续待命，真正退出软件立即停止；音频只在内存中处理、不落盘；
- 明确说出“打开应用”“打开网站”“在当前窗口搜索”“在文字栏/地址栏搜索”时，语音本身只授权这一次低风险动作，无需再点两次确认；
- 普通问答与电脑操作共用一个入口，但普通问答不会自动获得操作权限；
- “用 Google Chrome/Edge 打开已知网站”会保留用户指定的浏览器，并且只有在窗口恢复且显示到用户眼前后才算执行成功；
- 从 Windows 明确注册的位置发现已安装应用，不扫描整个硬盘；
- 打开唯一识别的已安装应用；
- 打开用户本次明确选择的文件；
- 打开经过校验的 `https` 网站；
- 记住用户切回元枢前最后使用的外部窗口；
- 只在 Windows UI Automation 唯一识别的可写搜索框或浏览器地址搜索栏中填入并提交内容；
- 经本次明确同意并再次确认后，只捕获确认卡中显示的单个窗口；
- 在本机结合 Windows OCR 与 UI Automation 识别应用、窗口类型和主要文字，图像不落盘、不上传并在使用后清零；
- 密码、支付、Windows 凭据等敏感窗口会被拒绝，读取过程中可随时停止；
- 编程任务只在已授权 Git 项目内交给 Codex，并保留已有 TaskEvidence；
- 每次动作使用一次性语音授权或可见确认，并保留命令去重、审计和结构化结果证据；
- `DesktopClient` 只通过当前用户 Named Pipe 调用独立 `DesktopHost`，不直接访问 SQLite 或执行 Codex。

当前明确不开放：

- 任意鼠标移动、任意按钮点击或任意输入框输入；
- 发送、发布、支付、删除、账号/安全设置、密码或凭据输入；
- 隐藏录音、唤醒词和未显示界面时的静默开机捕获；
- 全桌面截图、全盘自动扫描、未授权远程控制；
- 把窗口画面发送给云端视觉模型，以及对纯图片、图标、视频或复杂空间关系作无依据的强判断；
- 手机端、云端、远程桌面和实时视频。

## 开发构建

```powershell
dotnet restore ScreenGuide.slnx --locked-mode --disable-parallel
dotnet build ScreenGuide.slnx --configuration Release --no-restore --maxcpucount:1
dotnet test ScreenGuide.slnx --configuration Release --no-build --no-restore --maxcpucount:1
```

构建安装包：

```powershell
pwsh -NoProfile -File .\scripts\build-desktop-release.ps1
```

运行数据、日志、数据库、证据和语音模型保存在当前用户本地应用数据目录，不进入仓库。语音模型当前沿用本机 `%LOCALAPPDATA%\ScreenGuideTeacher\models`；正式分发模型前必须单独完成模型许可证与分发方案核验。
