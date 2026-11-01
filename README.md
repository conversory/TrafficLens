# 流量侦探 TrafficLens 1.6 — 程序、路由与每日流量核对

Windows 10/11 x64 桌面软件，需 .NET 10 Desktop Runtime。默认只读连接本机 Clash Verge。

## 自动保存和按天查看

1. 软件启动后自动记账，每约 5 秒保存一次；正常退出再保存剩余增量。
2. 打开 **每日历史（自动保存）**，选择日期，或使用 **前一天 / 后一天 / 今天**。
3. 每个同名程序一行，显示当天上传、下载，以及代理、直连、其他 / 未知的双向流量。
4. 历史页支持程序搜索、线路筛选和排序，点击流量列标题也能排序。
5. **导出当天 CSV** 保存所选日期及筛选结果；**保存 / 刷新** 立即保存；**历史目录** 打开数据文件夹。
6. 重启软件或 Windows 后，旧日期仍可查看，当天新流量继续累加。实时页 **重新计数** 不删除历史。

上方卡片始终为本次实时监控；历史页日期工具栏下的合计对应所选日期与表格。

## 历史保存目录

默认使用程序目录的上一级公共文件夹 `../TrafficLens-Data/History/`。也可以用 `--data-dir` 指定可写目录

每天一份 yyyy-MM-dd.json，包含程序名、线路字节量、内核合计和观察时间；1.6 起保存域名/目标、出口节点的每日聚合；不保存请求正文或接口密钥。同一父目录下升级版本会继续使用这些记录。备份时复制整个 TrafficLens-Data 目录。

写入使用临时文件、刷新磁盘和同目录替换，保留上一份 .bak。主文件损坏时尝试备份并保留损坏副本；读写失败在底部提示，不能读取的原记录不会被静默清零。异常断电可能损失尚未保存的几秒增量，备份也可能稍旧。

历史目录同一时间只允许一个写入者，避免重复计数。实时明细约 2 万条后会折叠旧连接，但每日程序累计不会减少。内存只缓存少量日期，过去的文件按需读取。

## 计数范围

- 每日历史针对经过 Clash 且能采样归因的程序连接增量。同名程序忽略大小写和 .exe 后缀，跨进程、路径和域名合并。
- 重开时先建立当前连接基线，不重复计入连接累计。软件未运行期间、旧版未保存的流量无法补录。
- 按 Windows 本地日期记账。跨午夜的采样间隔按时长比例拆分字节，余数计入最后一天，总量守恒；实际跨日时间只能近似，长时间休眠或断线时误差可能更明显。
- 代理 / VPN 按 Clash 最终出口和 /proxies 的类型判断；TUN 接管但出口为 Direct 的仍算直连，不能确认的归其他 / 未知。
- 短连接可能漏采，程序合计可能与 Clash 内核有差额；未归因差额不强行分配程序或线路。
- 绕过 Clash 的流量使用 **系统流量** 查看；系统 ETW 明细仍手动导出 CSV。
- KB/MB/GB 使用 1024 进制。代理转发可能出现多层计数，各口径不能直接相加作为运营商计费总量。

## 实时监控、托盘与自启

**程序汇总（同名合并）** 比较本次上传、下载与速度，下方查看所选程序的域名及具体连接。**域名 / 连接排行** 保留按程序、域名和组合分组及搜索。进程名缺失时尝试通过 Windows 本机源地址和端口唯一匹配补全，无法确认则显示未知。

点击 × 或 **后台监控** 隐藏到托盘，继续采样和保存。双击托盘恢复，右键 **退出** 才停止。主界面和托盘的 **开机自启** 开关控制当前用户登录后自动在托盘监控。升级时自启路径需要关联新版；正式交付会保持用户原有启用偏好。重复打开新版会恢复现有窗口。

自启使用 HKCU Run / TrafficLens，命令为带引号的完整 exe 路径和 --background --startup。Windows 禁用启动项时会提示在系统 设置 → 应用 → 启动 中开启。

## 系统流量

点击 **系统流量 → 开启程序监控**，通过 Windows 管理员提示后采集 ETW 的 TCP/UDP、IPv4/IPv6 进程事件。选择实际联网网卡比较网卡速度；本机通信视图可追查谁把数据交给本地代理。加密正文与具体上传文件内容不可见。

## 构建与验证

- dotnet publish -c Release -r win-x64 --self-contained false。
- --history-test result.json：跨天、重开、备份、写入失败、折叠和缓存验证，默认使用报告旁的独立测试目录，不使用正式数据。
- --history-live-test result.json：真实 Clash 采样、自动保存和重新读取对账，也默认隔离测试目录。
- --history-read result.json --day yyyy-MM-dd：读取已保存日期；--data-dir 完整路径 可指定目录。
- --history-ui-check preview.png --data-dir 测试目录 --day yyyy-MM-dd：历史页检查。
- --clash-test result.json：原统计、程序归属和 CSV；--tray-test result.json：托盘生命周期。
- --startup-status result.json、--startup-enable result.json、--startup-disable result.json：自启状态及设置。
- --self-test result.json、管理员 --validate result.json：系统流量统计与回环收发验证。

关键源码：DailyHistory.cs 每日账本与落盘，HistoryPanel.cs 日期查询，HistoryVerification.cs 验证，ClashMonitor.cs 采样，ClashForm.cs 界面，StartupRegistration.cs 自启，TrafficMonitor.cs 系统 ETW。

参考：[Mihomo API](https://wiki.metacubex.one/api/)、[出口链](https://github.com/MetaCubeX/mihomo/blob/Meta/constant/adapters.go)、[Microsoft 登录自启](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)、[TraceEvent](https://github.com/microsoft/perfview)。


## 1.6 新增

- 每日历史显示全线路内核计数与程序明细的有符号差额。不把差额分配给 VPN，也不把差额视为精确漏采量：计数更新时序、跨日与内核重启都可能影响它。
- “域名 / 节点明细”沿用日期、程序搜索和线路筛选；按程序、目标、出口、线路保存每日字节量，旧数据无法补录。
- 每日最多 10,000 个明细组合，超出后单独累计溢出字节；程序总量不受此上限影响。日期缓存有界，但历史磁盘总量仍随天数增长。
- 采样维持约 500 ms，界面自动刷新最多每秒一次；磁盘每 5 秒批量保存。
- 记录采样数、首末观察时间、有效间隔累计及 >5 秒长间隔。此信息从升级后开始记录，不代表全天在线证明；退出期间无法测量，长间隔不计有效时长。
- 修复连接 ID 被不同启动时间的连接复用时混合累计的问题。

## 数据与开源

历史文件可能包含程序名、域名、出口名称等私人信息，请不要上传到公开仓库或 Issue。
发布源码只包含代码、构建锁文件、文档和合成测试，不包含真实流量、配置、账户凭据或本机诊断报告。
MIT 许可证见 LICENSE；设计参考和第三方依赖见 THIRD_PARTY_NOTICES.md。

## 可重复构建

安装 Windows .NET 10 SDK 后执行：
```powershell
pwsh -File ./tools/Restore-Dependencies.ps1
dotnet publish -c Release --no-restore -o dist
.\dist\TrafficLens.exe --history-test .\test-results\history.json
```
历史测试要求新的空测试目录。测试会自动隔离数据；真实采样测试用 `--history-live-test`，需要本机 Clash。

还原脚本需要 PowerShell 7。应用依赖及其传递依赖的版本和内容哈希必须与锁文件一致；只有 SDK 自动选择的 `Microsoft.NET.ILLink.Tasks` 允许随 .NET 10 SDK 更新。脚本验证后恢复仓库锁文件，后续发布使用 `--no-restore` 复用本次还原结果。若确实升级应用依赖，应先审查并更新锁文件。CI 在 Windows Server 2022 与最新 Windows runner 上分别验证。
