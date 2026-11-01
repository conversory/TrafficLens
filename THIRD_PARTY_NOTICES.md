# 来源与依赖

本次功能借鉴 Mihomo Traffic Ledger 的按程序、目标、出口记账与多维查询思路；本项目为独立 C# 实现，本次没有复制其 Go/JavaScript 源码。
- 参考项目：https://github.com/severin-ye/mihomo-traffic-ledger （MIT）
- 其上游：https://github.com/zhf883680/clash-traffic-monitor
- Mihomo API：https://wiki.metacubex.one/api/ （运行时只读访问，本项目不分发 Mihomo）
- Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0：https://github.com/microsoft/perfview （MIT）
- .NET 与 Microsoft.Extensions 组件：https://github.com/dotnet/runtime （MIT）

完整依赖版本见 packages.lock.json。软件发行包保留 NuGet 包附带的许可证与声明文件；这些组件保留各自版权。
