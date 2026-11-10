# 灯控编排台 / ShowControl

本地运行的灯光节目编排和 UDP 场控程序。包含 Cue、播放组、定时任务、Action Pad、节点健康看板和桌面权限管理。默认以模拟模式运行。

## 本地运行

需要 .NET 10 SDK。从仓库根目录运行以下命令，然后打开 [本地工作台](http://127.0.0.1:8240/)。

    dotnet run --project ShowControl/ShowControl.csproj

Windows 桌面版的构建和使用说明见 [桌面版文档](ShowControl/Desktop/README.md)。Windows 打包使用 .NET 10 和对应版本的官方 Electron 发行包。

工程、权限设置、布局和日志保存在运行数据目录中。
