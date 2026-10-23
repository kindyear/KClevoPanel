<div align="center">

<img src="docs/images/banner.svg" alt="KClevoPanel" width="100%" />

**为 Clevo 蓝天模具的七彩虹 X17 笔记本打造的原生 Windows 控制面板。**

[下载安装](https://github.com/kindyear/KClevoPanel/releases/latest) · [开发文档](DEVELOPMENT.md) · [MIT License](LICENSE)

</div>

## 主要功能

- **实时监测**：CPU、内存、GPU、电池，以及温度、功耗和双风扇转速。
- **散热控制**：自动、最大风量与自定义曲线，支持 CPU / GPU 独立调整、试用和恢复。
- **性能档位**：安静、省电、性能、娱乐四档固件模式。
- **托盘速览**：左键打开主窗口，右键打开小面板；两个退出入口均有确认提示。
- **原生体验**：WinUI 3、深浅主题、设备信息、可选开机自启动和单实例运行。

## 界面预览

<table><tr>
<td width="70%"><img src="docs/images/fan-curves-dark.png" alt="主窗口与风扇曲线" /></td>
<td width="30%" valign="top"><img src="docs/images/tray-panel.png" alt="托盘实时小面板" /></td>
</tr></table>

## 兼容性

目前仅在 **七彩虹 COLORFUL X17 AT 23H2（i7-13650HX / RTX 4060 Laptop，Windows 11 x64）** 上验证。

其他使用相似 Clevo 蓝天模具的型号或许也能通用，但**不保证兼容性**；控制功能仍受设备校验限制。需要保留本机已有的 ACPI 桥接驱动，本项目不包含或安装原厂驱动、DLL。

## 开始使用

1. 从 [Releases](https://github.com/kindyear/KClevoPanel/releases/latest) 下载 **MSI 安装包**，或解压 **便携 ZIP**。
2. 启动应用，查看实时状态；自启动可在设置中开启。
3. 退出请使用侧栏或托盘小面板中的红色「退出程序」，并确认。升级、卸载前请先退出。

自定义风扇曲线在程序运行期间保持，退出时请求恢复原设置。请避免与原厂软件同时控制；系统故障下不能保证恢复。安装包暂未签名。

## 开源许可

采用 [MIT License](LICENSE)。感谢 [Clevo-X370-Control-Center](https://github.com/jiyeguang25/Clevo-X370-Control-Center) 提供的公开研究参考。
