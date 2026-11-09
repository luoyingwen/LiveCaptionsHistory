# 实现与兼容范围

主窗口为 .NET 8 WPF 应用。`CaptionReader` 启动本程序的 `engine\SpeechBackendHost.exe`，使用标准输入/输出上的 JSON 行传递就绪、字幕、错误和停止消息。

识别子进程在 STA 线程创建 DispatcherQueue，从本机 Windows SystemApps 加载 `LiveCaptionsBackendDll.dll`，通过 `DllGetActivationFactory` 创建 `LiveCaptionsBackend.CaptionBackend`。模型选择、系统播放音频采集和语音识别继续使用 Windows 原有实现。

## 后端初始化

1. 查找包含原生后端的 Windows 系统应用目录，校验已验证版本的 SHA-256。
2. 添加系统组件的 DLL 搜索目录，并初始化 WinRT/DispatcherQueue。
3. 创建后端对象，从 Windows 设置读取当前模型语言并枚举已安装模型。
4. 根据本次会话选择切换语言；不调用写入 Windows 全局语言设置的方法。
5. 订阅增量/最终识别事件，启动后端并运行 STA 消息循环。

验证版本使用 `Microsoft.CognitiveServices.Speech.core.dll`、embedded.sr、embedded.sr.runtime 和 ONNX Runtime。应用不需要额外的托管 Speech SDK 包，不复制 Windows DLL 或模型，不提取模型许可证。

早期通过公开 Embedded Speech 包装层直接初始化本机模型的实验返回 `Model parsing error`。最终实现复用 Windows 自身的初始化流程，已在没有 `LiveCaptions.exe` 进程时完成真实中英文转写。仓库保留当前工作的实现，不依赖早期实验中临时下载的 SDK 路径。

## 历史和界面

`CaptionAccumulator` 分开保存已提交段落与当前识别假设。增量事件只替换当前句子，最终事件提交一个段落；不同句子即使文本相同也会保留。暂停或断开时保留当前可见文本。

界面更新纯追加文本时使用 `AppendText`。修订文本时保持选区，并在 WPF 的光标 BringIntoView 操作之后、绘制之前恢复视口，避免精炼模式下出现顶部/底部往返。跟随状态仅在实际需要时滚动；回看状态保持阅读位置。

## 退出与异常清理

每个录入区间使用独立识别进程。暂停、重连和语言切换会结束旧进程，旧会话迟到事件通过代次检查丢弃。

关闭窗口同时启动保存和停止识别；保存成功后关闭可见窗口。WPF 显式退出生命周期继续等待后台清理，最多等待 2 秒。识别进程正常退出预算为 1 秒，随后有强制结束及有界确认。识别进程另有独立线程等待父进程句柄，父进程退出时可终止宿主，包括原生 Stop/Release 正在执行的情况。

## 私有接口边界

原生调用使用通过符号、虚表和实际执行确认的方法槽位与事件 ABI。当前只支持 README 指定的后端文件哈希。不能仅因另一个 Windows 版本具有相同文件名，就假设该 ABI 仍兼容；未知版本会停止连接。

本项目不修改或注入 `LiveCaptions.exe`，也不从其界面读取字幕。REA 和调试/反编译工具只用于开发调查，不是产品运行依赖。
