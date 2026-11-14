# SymphoCue 多智能体测试

在 Codex 对话中请求「使用多个智能体测试 SymphoCue」时，由当前会话中的子智能体分工检查调度与停止、UDP 与网卡、配置与日志，主智能体检查桌面和锁屏后汇总结果。无需另行启动 Codex CLI。子智能体应使用独立临时工程，桌面窗口测试串行执行。

配套自动测试程序依次验证引擎与 UDP 回环、网卡界面、锁屏和桌面生命周期。测试结果写入系统临时目录中的 JSON 报告。

## 准备

- Node.js 18 或更新版本，以及 .NET 10 SDK。
- 构建好的 Windows 版 SymphoCue 完整发布目录。
- Playwright Node 模块。可在测试目录运行 `npm install --no-save playwright`，也可以指定现有模块路径。

## 运行

在项目根目录打开 PowerShell，按本机路径替换参数。

```powershell
node tests/MultiAgent/run.cjs --exe "dist/SymphoCue-v2-1-5-win-x64/SymphoCue.exe" --playwright "C:/path/to/node_modules/playwright" --dotnet "C:/path/to/dotnet.exe"
```

省略 `--playwright` 会从测试目录和项目根目录查找 Playwright。省略 `--dotnet` 会使用 PATH 中的 dotnet。快速检查可加 `--fast`，该模式跳过真实分钟定时触发，报告会标明。

桌面任务串行运行，并各自使用新的临时工程、用户配置目录和随机端口。循环发送使用 127.0.0.1。网卡状态变化通过模拟接口列表验证。定时完整测试需要等待实际分钟切换。锁屏测试会在临时工程中设置测试密码并触发界面进程恢复测试。

每项的状态、输出和测试限制记录在报告中。Codex 子智能体的发现由主智能体核对并汇总到当前对话。物理网卡拔插、真实控制设备和多日连续运行需另行验证。自动测试通过仅表明列出的场景通过，不能作为生产合格结论。

引擎回归从当前源码编译运行。桌面、网卡和锁屏测试运行 `--exe` 指定的已发布程序。若要验证同一版本，请先从当前源码打包该程序。此前发现的停止请求竞争、无效节目任务优先级、未分组节点健康检测、停止按钮配置及回执匹配问题不属于这组自动测试的完整覆盖范围。
