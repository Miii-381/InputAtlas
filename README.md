# InputAtlas（输入图谱）

InputAtlas 是完全离线的 Windows 键盘与鼠标聚合统计工具。它只保存 5 分钟/1 小时桶内的次数和覆盖秒数，不保存字符、输入顺序、逐事件时间、组合键、窗口、进程、设备身份、鼠标坐标或轨迹。

## 环境

- Windows 11 x64，或仍受支持的 Windows 10 LTSC/Enterprise x64
- PowerShell 7
- .NET SDK 10.0.400（仅源码构建需要）
- Inno Setup 7.1.0 x64（打包时需要）

仓库会优先使用 `%LocalAppData%\Microsoft\dotnet10\dotnet.exe`，也支持系统级 .NET 10.0.400。
正式安装包采用 `win-x64` 自包含发布，已内置 .NET 10 Desktop Runtime；安装和运行均不要求目标机器预装 .NET，也不会联网下载运行时。

从 v1.1.2 起，InputAtlas 默认以管理员权限运行。双击程序或快捷方式后，请在 Windows 用户账户控制（UAC）提示中确认。启用登录自启动后，确认授权即可在托盘中开始统计。取消授权会结束本次启动。

## 构建

```powershell
pwsh ./build.ps1 check
pwsh ./build.ps1 test
pwsh ./build.ps1 benchmark
pwsh ./build.ps1 ci
pwsh ./build.ps1 package -Version 1.1.2
pwsh ./build.ps1 release -Version 1.1.2
```

编译、测试、性能基准和发布均保留用户当前的电源设置。性能基准已显式禁用 BenchmarkDotNet 的自动电源计划切换。

运行数据位于 `InputAtlas.exe` 同级的 `Data` 目录。开发文档是冻结需求基线，具体隐私与统计语义见 `开发文档/开发文档.md`。
