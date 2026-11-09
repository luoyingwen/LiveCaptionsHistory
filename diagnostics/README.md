# WPF 界面诊断

此目录保留当前产品的关闭、精炼模式与滚动回归诊断宿主。历史目录名用于保持既有测试路径；日志和预览图片在本机生成，不提交到 Git。

请在 Windows 桌面会话中运行，先关闭正在使用的字幕历史窗口，以免触发单实例检查。诊断会创建独立测试窗口、使用合成文本，并可能启动本机识别后端；测试会话仍由产品正常保存到本机 Sessions 目录。

从仓库根目录执行：

```powershell
# 先构建程序和控制台测试
.\build.ps1

# 空记录、有待保存文本、保存失败、稳定运行后的关闭
dotnet run --project .\diagnostics\shutdown-20261109\ui -c Release -- empty
dotnet run --project .\diagnostics\shutdown-20261109\ui -c Release -- text
dotnet run --project .\diagnostics\shutdown-20261109\ui -c Release -- save-failure
dotnet run --project .\diagnostics\shutdown-20261109\ui -c Release -- settled

# 精炼模式及滚动位置检查，生成 WPF 渲染预览
dotnet run --project .\diagnostics\shutdown-20261109\ui -c Release -- compact

# 逐字追加、修订、选区、回看和换行期间的滚动/Rendering 采样
dotnet run --project .\diagnostics\shutdown-20261109\ui -c Release -- flicker
```

`flicker` 会对不应出现的回跳或选区改变返回非零退出码。`flicker-observe` 仅记录行为，便于对比。换行可以产生正常的单向行滚动，不视为顶部/底部往返。

这些检查调用实际 WPF 窗口与控件，不会向其他应用注入鼠标或键盘输入。Rendering 采样和预览不是对所有显示器、DPI、GPU 或 DWM 路径的完整视频审计。
