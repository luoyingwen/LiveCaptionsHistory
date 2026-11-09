# LiveCaptionsHistory · 字幕历史

Windows 本机语音转文字与可滚动字幕历史窗口。直接在独立子进程中调用 Windows 已安装的识别后端，无需运行 `LiveCaptions.exe`。

## 功能

- 转写电脑正在播放的声音，使用本机已安装的中文或英语模型。
- 保留历史字幕，支持滚动回看、文字选择、复制和导出。
- 自动保存到 `%LOCALAPPDATA%\LiveCaptionsHistory\Sessions`。
- 精炼模式仅显示字幕和滚动条，可移动、缩放和置顶。
- 暂停会停止音频采集；恢复时创建新的识别会话。
- 关闭时保存与后台清理并行，窗口保存完成即可关闭。
- 已修复聚焦字幕框时逐字更新导致的顶部/底部回跳。

## 环境要求

- Windows x64，且已经安装所需的 Windows 字幕识别组件与语言模型。
- 构建需要能够编译 `net8.0-windows` 的 .NET SDK；运行需要 .NET 8 Windows Desktop Runtime。
- 当前适配的 `LiveCaptionsBackendDll.dll` 版本为 **2608.26000.0.0**，SHA-256：

  ```text
  eccd4adc7e4e8f127344a0ccbb7e09c92e185d8589cf5197a6f0dd1de0ce9fef
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

构建脚本先执行 30 项历史文本测试及 3 个子进程生命周期场景，再发布程序到 `LiveCaptionsHistory\dist`，并生成本机快捷方式 `LiveCaptionsHistory\字幕历史.lnk`。分发或移动程序时需要保留完整 `dist` 目录及其 `engine` 子目录。

编译与上述自动测试不要求已安装识别模型；真实转写和 WPF 界面诊断需要符合运行条件的 Windows 环境。

## 精炼模式

| 操作 | 方法 |
| --- | --- |
| 切换精炼模式 | 工具栏按钮或 `Ctrl+Shift+M` |
| 返回完整界面 | `Esc` 或右键菜单 |
| 移动窗口 | 拖动顶部留白，或按住 `Alt` 拖动字幕区 |
| 键盘移动 | `Alt+方向键` |
| 调整大小 | 拖动窗口边缘 |
| 回到最新字幕 | `Ctrl+End` 或右键菜单 |
| 暂停、置顶、复制、导出、关闭 | 右键菜单 |

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

采集来源是系统播放音频，本版不采集麦克风。多个同时发声的程序会被一并转写。应用本身没有配置云识别服务或上传字幕接口；Windows 后端内部遥测未做完整网络审计。

保存失败会保留窗口与文本，便于重试或导出。应用运行前或暂停期间的语音不能恢复。其他 Windows 构建、ARM64、长时间持续运行和音频设备热切换尚未完整验证。

## 许可证

仓库沿用 [MIT License](LICENSE)。Windows 系统组件与模型由本机 Windows 安装提供。
