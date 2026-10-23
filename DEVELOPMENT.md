# 开发文档

[返回项目介绍](README.md)

## 构建与检查

Windows x64、.NET 8 SDK、Python 3。WinUI / Windows SDK依赖固定在项目文件中，WiX 5.0.2固定在工具清单中。

```powershell
# 应用与全部工作进程
.\build.ps1
# 不依赖目标机DLL的逻辑检查，适用于CI
dotnet run --project .\Diagnostics\KClevoPanel.Diagnostics.csproj -- --self-test --portable
# 目标机检查：包含本机DLL、身份核对，不执行硬件写入
dotnet run --project .\Diagnostics\KClevoPanel.Diagnostics.csproj -- --self-test
# MSI、便携ZIP、SHA256
.\packaging\build-release.ps1
```

中间产物默认在 `work/build`，可通过 `X17BuildRoot` 修改。应用默认发布到仓库同级目录；完整安装产物在 `artifacts/release`。自包含发布必须保留运行库、Assets及全部工作进程。

## 代码结构

| 目录 | 职责 |
| --- | --- |
| App | WinUI窗口、图表、设置、托盘和确认交互 |
| Core | 数据模型、协议、事务和进程客户端 |
| TelemetryWorker | 隔离的温度与风扇只读采样 |
| PresetWorker | 已验证的性能档位事务 |
| FanWorker | 风扇控制、独立恢复；卸载时仅清理本安装目录的启动注册 |
| Diagnostics | 协议与逻辑检查 |
| packaging | MSI生成、发布打包和安装测试 |

## 适配与接口

当前控制配置只允许 `COLORFUL X17 AT 23H2`、BIOS `1.07.04TCOLO`、ACPI桥驱动 `1.0.0.7` / `1.0.40.0`。其他类似模具仅有适配可能，不能通过移除检查直接宣称兼容。

优先使用已安装的DriverStore接口库，SHA256：
`470D96CAE14FEF2823330C63EA683CF87CB0F6984DC4EB86D21420FF71BE3299`。
性能接口另有已核对的DCHU版本支持；风扇写入不使用该回退库。库加载使用绝对路径和受限搜索标志。

| 操作 | 已核对路径 |
| --- | --- |
| 温度 / 风扇 | GetDCHU_Data_Buffer 12 / 13，带保护页与边界检查 |
| 风扇曲线 | 普通SetDCHU_Data(14, frame, 256)，不使用Ex |
| 风扇模式 | SetDCHU_Data(121, {mode,0,0,1}, 4)，仅0 / 1 / 6 |
| 风扇选择镜像 | WriteAppSettings(4,5,1,[mode])，不写整页 |
| 性能档位 | 固定121/sub25及已核对的一字节镜像，不复刻完整Windows/GPU预设联动 |

曲线接口本机实际返回20/0x14，生效判据为新读的两路全部运行时节点匹配；模式应答和镜像记录不等于独立硬件效果测量。CPU终点100°C，GPU终点99°C，首尾节点锁定，中间占空比最低30%，温度与占空比要求单调。偏移、强冷、DTT及未知分支不开放。

## 会话与恢复

写入前保存基线并启动独立恢复进程。30秒试用可转为运行期间保持，心跳每秒更新，超过10秒无响应触发恢复。退出、温度/转速异常、外部状态覆盖及睡眠通知会请求恢复；恢复未确认会阻止继续控制。断电、内核挂起等场景不能保证恢复，隐藏斜率状态也不能证明逐字节复原。

自动/最大模式是明确选择的固件状态，退出时不主动撤销；自定义会话退出时恢复进入前状态。启动时不自动应用草稿或重放旧写入。记录与偏好位于 `%LOCALAPPDATA%\KClevoPanel`，普通运行不上传数据。

## 验证

- CI运行71项可移植逻辑检查、完整发布构建、MSI安装和卸载；不安装目标机驱动，不执行EC或固件控制。
- UI检查仅在本机运行，覆盖托盘点击、确认框取消、布局和导航；不能冒充托管CI硬件测试。
- [托盘/退出验证](docs/evidence/v0.14/tray-exit.json)、[可移植检查](docs/evidence/v0.14/portable-tests.txt)。
- [实机摘要](docs/evidence/v0.11/summary.json)记录曲线写后读取、到期恢复、最大风量与心跳故障测试。转速为原厂公式换算，未用外部仪器校准。尚未验证数小时高负载和真实睡眠/唤醒。
- 本机WMI查询曾出现超时；预览如实保留不可用状态，不填造传感器数据。

```powershell
# 在发布目录只读核对
.\KClevoPanel.FanWorker.exe --inspect
.\KClevoPanel.PresetWorker.exe --inspect
# 本机窗口验证，禁止控制写入
.\KClevoPanel.exe --smoke-test C:\absolute\output
```

## CI/CD与发布

main、PR及手动触发执行构建与安装验证。Action固定到commit SHA；构建任务仅有读取权限，标签发布任务具有contents写入权限。无需个人PAT，发布使用GitHub内置令牌。

发布时修改 `App/KClevoPanel.App.csproj` 三段版本号，提交并推送同名 `v版本号` 标签。标签必须与程序集版本一致；构建和安装验证通过后自动创建Release并上传MSI、ZIP与SHA256。Release正文只提供安装说明，不维护版本更新清单。

MSI按当前用户安装到 `%LOCALAPPDATA%\Programs\KClevoPanel`，不启动应用、不默认开启自启动、不安装驱动。升级/卸载前先从应用确认退出；卸载仅移除指向本安装目录的启动项，并保留草稿与事务记录。安装验证仅在可丢弃的GitHub runner运行，安装包目前未签名。
