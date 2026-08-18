# ScreenGuide Desktop

ScreenGuide Desktop 是一个运行在 Windows 上的本地 AI 工作助手。用户授权一个 Git 项目后，可以在桌面界面中把任务交给 Codex，并查看执行过程、取消任务、补充决定，以及以 Git 与测试证据为准的结果摘要。它也提供需要逐次确认的低风险电脑操作，可从受控清单打开应用或打开经过校验的 `https` 网站。

当前 V0.1 产品边界：

- `DesktopClient` 只通过当前用户可访问的 Named Pipe 连接 `DesktopHost`；
- `DesktopHost` 独立运行，负责项目授权、任务生命周期、Codex、证据和 SQLite；
- 关闭界面不会中断后台任务，重新打开可恢复查看；
- 桌面操作只允许打开受控应用和 `https` 网站，每次必须由用户在界面确认并写入审计；
- 不允许任意程序路径、命令行、鼠标控制、自动输入或破坏性操作；
- 不包含手机端、云端、远程桌面、桌面视频或完整语音控制。

## 开发构建

```powershell
dotnet build ScreenGuide.slnx
dotnet test ScreenGuide.slnx --no-build
```

运行时数据保存在当前用户的本地应用数据目录，不应写入仓库。正式发布与安装方式见桌面 V0.1 发布报告。
