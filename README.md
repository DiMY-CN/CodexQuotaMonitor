<div align="center">
  <img src="assets/app-icon.png" alt="Codex 用量监控图标" width="96" height="96">
  <h1>Codex 用量监控</h1>
  <p><strong>C# WPF 原生版</strong> · Windows 桌面版 Codex 的紧凑任务栏额度浮窗</p>
</div>

这是一个面向 Windows 桌面版 Codex 的轻量常驻工具。它固定覆盖在主任务栏左侧，以与任务栏相同的高度显示短周期额度、一周额度和本地刷新状态，不额外占用桌面工作区。

本项目不是 Codex CLI 的通用封装器或替代入口。程序会调用 Windows 桌面版 Codex 随附的本机 `codex.exe app-server`，用户侧用途仍是观察桌面版 Codex 的额度状态。

本项目由 OpenAI Codex 协助开发，但不是 OpenAI 官方项目。项目主要为个人自用，除严重或破坏性 bug 外，不承诺后续维护、兼容性支持或功能请求响应。仓库未提供开源许可证；公开可见不等于主动授予复用权利。

## 界面与功能

- 默认宽度约 260px，高度直接采用 Windows 主任务栏的实际高度。
- 固定在任务栏左侧并保持置顶；任务栏位置或尺寸变化后会自动重新贴靠。
- 半透明磨砂背景、矢量圆环和 DPI 自适应文字；窗口不出现在任务栏应用列表。
- 托盘图标可用于刷新和退出；单实例运行，不会创建重复窗口或托盘图标。
- 刷新失败时保留最后一次有效读数，并通过 `SYNC`、`WAIT`、`OLD`、`ERR` 或 `STALE` 标记状态。

![原生版紧凑任务栏浮窗](assets/gui-overview.png)

三个区域的含义：

- **5H**：约 5 小时短周期额度的剩余百分比及重置倒计时。如果服务端当前未提供该窗口，显示 `--` 和 `inactive`，不会拿其他窗口的数据代替。
- **WK**：一周额度窗口的剩余百分比及重置倒计时。
- **REF**：上次成功刷新时间 / 当前本地时间。两组时间使用不同颜色，状态点表示当前读取状态。

窗口和托盘共用同一组右键操作：

- **Refresh now**：立即读取一次额度。
- **Snap to taskbar left**：立即重新贴靠到主任务栏左侧。
- **Quota interval**：将自动刷新间隔设置为 1、3、5、10 或 15 分钟。
- **Exit**：退出窗口、托盘图标和后台进程。

![原生版右键菜单](assets/right-click-menu.png)

## 安装与运行

推荐从 GitHub Releases 下载 `CodexQuotaMonitor-Setup-1.1.1.msi`，双击即可安装。安装向导支持修改安装目录，并提供以下选项：

- **Create a desktop shortcut**：创建桌面快捷方式，默认选中。
- **Launch Codex Quota Monitor**：安装完成后立即启动，在完成页显示，默认不选中。

安装包包含 .NET 运行时，目标电脑不需要另行安装 .NET Desktop Runtime。`1.1.1` 及之后的安装包支持检测并升级同一产品的旧版本。

也可以下载自包含的 `CodexQuotaMonitor.Wpf.exe` 直接运行。该 EXE 同样不依赖目标电脑预装 .NET。

当前发布产物未使用商业代码签名证书。Windows SmartScreen 可能对首次下载的 MSI 或 EXE 显示来源未知提示；请从本仓库的 Releases 页面获取文件，并自行核对发布来源。

运行前需要：

- Windows 10/11 x64。
- Windows 桌面版 Codex 已安装并已登录。
- 程序能够从桌面版安装位置或 `PATH` 找到 `codex.exe`；诊断时也可用 `--codex-exe` 显式指定。

## 查询原理与风险

额度查询会启动本机 `codex.exe app-server --listen stdio://`，再通过 JSON-RPC 调用 `account/rateLimits/read`。程序依据服务端返回的 `windowDurationMins` 识别额度窗口：约 `300` 分钟归入 **5H**，约 `10080` 分钟归入 **WK**，而不是假定 `primary` 一定代表 5H。

程序不读取 `auth.json` 中的 token，也不自行把账号数据发送到第三方服务。`REF` 只显示本组件最近一次成功完成额度读取的本地时间与当前系统时间，不是 OpenAI 服务端时间。

需要注意：

- `codex.exe app-server` 不是面向本项目承诺稳定性的公开接口；桌面版 Codex 更新后，方法名、字段或窗口类型可能变化。
- 读数依赖本机 Codex 的登录状态和网络；账号切换、登录失效或网络异常都可能使刷新失败。
- 服务端可能临时停用某类额度窗口，此时对应区域会显示不可用。
- 剩余百分比和重置时间来自 Codex 返回值，只适合作为日常参考。
- 本地日志可能包含文件路径和错误信息，不应直接提交到公开仓库。

## 本地设置

安装版默认把状态文件写入：

~~~text
%LOCALAPPDATA%\CodexQuotaMonitor\settings.json
%LOCALAPPDATA%\CodexQuotaMonitor\logs\codex_quota_monitor.log
~~~

从源码目录运行时，`Start-CodexQuotaMonitorNative.cmd` 会把状态保存在项目根目录。环境变量 `CODEX_QUOTA_MONITOR_NATIVE_HOME` 可显式覆盖状态目录。

`settings.example.json` 中的默认值：

~~~json
{
  "quota_interval": 180,
  "no_tray": false,
  "window_width": 260,
  "red_threshold": 15.0,
  "amber_threshold": 30.0
}
~~~

## 从源码运行

源码构建需要 .NET 8 SDK。普通启动：

~~~cmd
Start-CodexQuotaMonitorNative.cmd
~~~

诊断和单次读取：

~~~cmd
Start-CodexQuotaMonitorNative.cmd --check --no-tray
Start-CodexQuotaMonitorNative.cmd --once --no-tray
~~~

支持的参数：

- **--check**：输出环境、状态目录和任务栏贴靠位置，不启动 GUI。
- **--once**：读取一次额度并输出 JSON，不启动 GUI。
- **--codex-home &lt;path&gt;**：指定 Codex home，默认使用 `%CODEX_HOME%` 或 `~/.codex`。
- **--codex-exe &lt;path&gt;**：指定 `codex.exe`。
- **--quota-interval &lt;seconds&gt;**：覆盖额度刷新间隔。
- **--tray / --no-tray**：覆盖托盘图标开关。

## 构建发布包

运行：

~~~powershell
pwsh -NoProfile -File .\Build-Release.ps1
~~~

脚本会生成：

~~~text
publish\win-x64-self-contained\CodexQuotaMonitor.Wpf.exe
publish\installer\CodexQuotaMonitor-Setup-1.1.1.msi
~~~

构建机需要 .NET 8 SDK，并会通过 NuGet 获取 WiX；WiX 只用于生成 MSI，终端用户不需要安装 WiX。

基础验证命令：

~~~cmd
dotnet build .\src\CodexQuotaMonitor.Wpf\CodexQuotaMonitor.Wpf.csproj -c Release
dotnet run --project .\tests\CodexQuotaMonitor.Tests\CodexQuotaMonitor.Tests.csproj -c Release
~~~

## 分支关系

本分支是 C# WPF 原生 Windows 版本，提供自包含 EXE、MSI、原生托盘和紧凑任务栏浮窗。同一仓库的 `python-tk` 分支保留 Python/Tk 实现：它更方便阅读和修改，但需要用户自行准备 Python，并通过 `python codex_quota_float.py` 运行。

## 公开仓库注意事项

`.gitignore` 已排除本地设置、日志和构建产物。公开提交前仍应确认没有包含：

- `settings.json`
- `logs/`
- `publish/`
- `src/**/bin/`、`src/**/obj/`
- `installer/**/bin/`、`installer/**/obj/`
- 个人路径、token、key、账户信息或运行日志
