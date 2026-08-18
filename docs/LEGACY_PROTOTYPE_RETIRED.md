# 旧桌面原型退役说明

旧的角色化 WPF 原型已从正式解决方案和仓库中移除。依赖审计确认，当前
`ScreenGuide.DesktopClient`、`ScreenGuide.DesktopHost`、任务、Codex Connector 与证据层
均不依赖旧项目。

旧原型中与角色形象、唤醒口令、本地音色和悬浮动画绑定的代码、资源、说明文档与工具
不再进入产品构建。仍有通用价值的语音文本规范化逻辑已迁移为中性的
`SpeechTextNormalizer`，不包含角色名称或第三方素材。

ScreenGuide 仅作为 V0.1 开发代号，商业品牌名称留待后续验证后确定。
