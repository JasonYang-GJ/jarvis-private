# 贾维斯的本地 IndexTTS2.5 语音

官方项目安装在 `D:\AI\IndexTTS2-official`，模型目录为其中的 `checkpoints`。
贾维斯通过 `tools\jarvis_indextts_server.py` 访问它；服务只监听本机
`127.0.0.1:17861`，不会把参考音频或生成语音上传到网络。

## 启用条件

把本人录音或已经得到明确授权的参考音频保存为：

`%LOCALAPPDATA%\Jarvis\IndexTTS2\reference.wav`

建议使用 10 至 20 秒、单人、无音乐、无混响的清晰语音。重启贾维斯后，
程序会优先尝试本机 IndexTTS2.5；本地服务未就绪时继续使用百炼语音。

不要使用电影、电视剧、名人或他人的录音克隆其声音，除非已经取得声音权利人的明确授权。
