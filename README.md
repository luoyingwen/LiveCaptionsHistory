# LiveCaptionsHistory · 字幕历史

Windows 本机语音转文字与可滚动字幕历史窗口。直接在独立子进程中调用 Windows 已安装的识别后端，无需运行 `LiveCaptions.exe`。

## 功能

- 转写电脑正在播放的声音。默认使用已安装的英语模型（优先美式英语），未安装英语时沿用 Windows 字幕设置。
- 保留历史字幕，支持滚动回看、文字选择、复制和导出。
- 可选将英语字幕替换为简体中文，支持单独开启提前翻译；[复用 CtrlLookup 的 LLM 配置](LiveCaptionsHistory/README.md#英语字幕翻译为中文)。
- 自动保存到 `%LOCALAPPDATA%\LiveCaptionsHistory\Sessions`。
- 精炼模式仅显示字幕和滚动条，可移动、缩放和置顶。
- 普通和精炼模式的主窗口默认不出现在支持 Windows 捕获排除的全屏分享中。
- 暂停会停止音频采集；恢复时创建新的识别会话。
- 关闭时保存与后台清理并行，窗口保存完成即可关闭。
- 已修复聚焦字幕框时逐字更新导致的顶部/底部回跳。

## 环境要求

- Windows x64，且已经安装所需的 Windows 字幕识别组件与语言模型。
- 构建需要能够编译 `net8.0-windows` 的 .NET SDK；运行需要 .NET 8 Windows Desktop Runtime。
- 当前适配的 `LiveCaptionsBackendDll.dll` 版本为 **2609.28000.400.0**，SHA-256：

  ```text
  0c8054bf89fb05742e82f1667a03afc8f1d6f52ec878296df0974f7747c6b4ca
  ```

这是对 Windows 私有接口的特定版本适配，不是通用的 Windows 语音 SDK。程序会在调用前校验后端文件；其他版本可能需要重新适配。没有将 Windows DLL、模型或模型许可证复制到本仓库。

## 构建和运行

在 Windows PowerShell 中执行：

```powershell
git clone https://github.com/luoyingwen/LiveCaptionsHistory.git
cd LiveCaptionsHistory
.\build.ps1
.\LiveCaptionsHistory\dist\LiveCaptionsHistory.exe
```

构建脚本先执行历史文本测试、识别语言选择测试及 3 个子进程生命周期场景，再发布程序到 `LiveCaptionsHistory\dist`，并生成本机快捷方式 `LiveCaptionsHistory\字幕历史.lnk`。分发或移动程序时需要保留完整 `dist` 目录及其 `engine` 子目录。

编译与上述自动测试不要求已安装识别模型；真实转写和 WPF 界面诊断需要符合运行条件的 Windows 环境。

## 精炼模式

| 操作 | 方法 |
| --- | --- |
| 切换精炼模式 | 工具栏按钮或 `Ctrl+Shift+M` |
| 返回完整界面 | `Esc` 或右键菜单 |
| 移动窗口 | 拖动顶部浅灰色区域，或按住 `Alt` 拖动字幕区 |
| 键盘移动 | `Alt+方向键` |
| 调整大小 | 拖动窗口边缘 |
| 回到最新字幕 | `Ctrl+End` 或右键菜单 |
| 暂停、置顶、翻译、复制、导出、关闭 | 右键菜单 |

普通鼠标拖动仍用于选择文字，滚轮用于回看。切换模式不会清空历史或重启识别。

## 项目结构

| 路径 | 内容 |
| --- | --- |
| `LiveCaptionsHistory/` | WPF 窗口、字幕累积、保存及子进程控制 |
| `SpeechBackendHost/` | Windows 原生识别后端适配、事件回调及进程生命周期 |
| `LiveCaptionsHistory.Tests/` | 历史文本处理测试 |
| `LiveCaptionsHistory.LifecycleTests/` | 真实管道和子进程退出测试 |
| `diagnostics/shutdown-20261109/ui/` | 关闭速度、精炼模式与滚动闪烁的 WPF 诊断宿主 |
| `docs/architecture.md` | 后端实现与兼容范围 |

详见[使用说明](LiveCaptionsHistory/README.md)、[验证记录](LiveCaptionsHistory/VERIFICATION.md)和[界面诊断说明](diagnostics/README.md)。

## 数据与限制

采集来源是系统播放音频，本版不采集麦克风。多个同时发声的程序会被一并转写。语音识别在本机运行。翻译默认关闭；开启后才会将英语字幕文字发送到配置的 LLM 端点，不发送音频。只有另外开启提前翻译才会发送可能被修订的文字。自动保存仍保留识别原文。Windows 后端内部遥测未做完整网络审计。

保存失败会保留窗口与文本，便于重试或导出。应用运行前或暂停期间的语音不能恢复。其他 Windows 构建、ARM64、长时间持续运行和音频设备热切换尚未完整验证。

## 许可证

仓库沿用 [MIT License](LICENSE)。Windows 系统组件与模型由本机 Windows 安装提供。
