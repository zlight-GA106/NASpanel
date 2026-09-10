# smartctl.exe

从 [smartmontools 官方发布](https://www.smartmontools.org/) 安装 Windows 版本（支持 JSON 的 7.x）。

推荐在部署目录的 `appsettings.Local.json` 指定安装后的完整路径，例如 `C:\\Program Files\\smartmontools\\bin\\smartctl.exe`。
也可将官方发行包需要的 smartctl.exe、drivedb.h 和许可证等文件一起放入发布目录 `tools/smartmontools/`。

此仓库不从不明来源分发硬件访问工具，也不在开发模式访问真实磁盘。
