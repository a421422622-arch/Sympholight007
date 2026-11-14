# 灯控编排台

跨平台本地场控程序。启动服务后，在浏览器打开 `http://127.0.0.1:8240/`。Windows、macOS 和 Linux 使用同一份发布包，运行主机需要 **ASP.NET Core Runtime 10**。

| 系统 | 启动发布包 |
| --- | --- |
| Windows | 双击 `start.cmd`，或在目录中运行 `dotnet ShowControl.dll` |
| macOS / Linux | 在目录中运行 `sh start.sh` |

## 配置工程

1. 首次启动为空工程。导入已有工程，或先创建演出、分组与节点。
2. 选择 Cue 列表，在右侧配置目标 IP 分组、模板或指令、端口、Ping、预期回执和失败策略。使用上移和下移调整顺序。确认 Cue 更改后即可运行。
3. 用 **手动 Go**、定时任务或 **Action Pad** 触发同一执行引擎。定时按服务器本机时间匹配到分钟。与运行中的任务冲突时，本次定时触发跳过。手动或 Action Pad 会取消正在执行的定时任务，等待其退出后启动。手动和 Action Pad 同级，同级请求忙时返回冲突。
   手动 Go 旁的 **停止执行** 可取消当前列表的回执等待、重试及后续 Cue。底部执行日志也提供停止入口，适用于当前运行的任意列表。
4. 在 **节点看板** 刷新 Ping，按 IP、分组和状态筛选，导出当前筛选结果为 CSV。页面执行的健康检查独立于控制指令。

## 运行与协议

- 每条 Cue 按最终配置向节点发送 String 或 HEX 报文。同一 Cue 的重复发送会合并。支持模板继承、固定发送端口、独立接收端口及自定义回执。Ping 独立记录网络状态。
- 重试仅用于明确允许重复执行的未确认命令。旧配置默认禁用自动重试，可在模板或 Cue 目标组启用。跳过继续下一条，停止终止当前调度。现场停止可在列表设置中绑定实际 LAS 停止流程。
- 实时日志保留本次运行最近 300 条事件。项目保存到发布目录的 `data/project.json`。可用 `SHOWCONTROL_DATA_DIR` 指定独立数据目录，用 `SHOWCONTROL_PORT` 改本地端口。
- 服务绑定本机回环。定时器每秒检查一次，服务启动时从下一分钟开始匹配，停机期间的任务不补跑。

## 构建

```sh
dotnet publish ShowControl/ShowControl.csproj -c Release --self-contained false -p:UseAppHost=false -o dist/ShowControl
```
