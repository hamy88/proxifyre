# ProxiFyre

[![release](https://img.shields.io/github/v/release/wiresock/proxifyre?display_name=tag&sort=semver&label=release)](https://github.com/wiresock/proxifyre/releases/latest)
[![CI](https://img.shields.io/github/actions/workflow/status/wiresock/proxifyre/main.yml?event=pull_request&label=CI&logo=github)](https://github.com/wiresock/proxifyre/actions/workflows/main.yml)
[![license](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](LICENSE)

**面向 Windows 的透明 TCP 与 UDP SOCKS5 代理工具，提供 Windows 图形界面和服务支持。**

> **配套项目：[Alighieri](https://github.com/wiresock/alighieri)** —— 一个面向 ProxiFyre 的 SOCKS5-over-TLS 配套服务，可提供兼容的、启用 TLS 的 SOCKS5 监听器。

ProxiFyre 可以将选定的 Windows 应用程序（或所有未匹配的应用程序）透明地路由到一个或多个 SOCKS5 代理。它基于 Windows Packet Filter 的 `socksify` 示例构建，并增加了面向生产环境的配置、UDP 与 IPv6 路由、SOCKS5-over-TLS、服务运行模式以及提权管理界面。

## 主要特性

- 为没有原生代理支持的应用程序提供路由，包括 UDP 和 QUIC 流量。
- 使用多个有序 SOCKS5 代理，并支持按应用程序配置规则和排除项。
- 通过 TCP、UDP 或两者同时代理 IPv4 与 IPv6 目标。
- 可选普通 SOCKS5 或 TLS 传输，支持证书验证和可选的证书固定。
- 通过 LAN 绕过保持本地网络流量直连。
- 通过 Windows GUI 完成配置、验证、应用、监控和排错。
- 以 `ProxiFyreService` 服务方式无人值守运行，并提供通知区域控件。

[![ProxiFyre 路由规则与服务状态](docs/images/proxifyre-ui-routing.png)](docs/images/proxifyre-ui-routing.png)

## 安装

从 [GitHub Releases](https://github.com/wiresock/proxifyre/releases/latest) 下载与系统架构匹配的软件包。

| 软件包 | 适用场景 |
| --- | --- |
| `ProxiFyre-<version>-win-x86-setup.exe`、`...-x64-setup.exe` 或 `...-arm64-setup.exe` | **推荐。** 在线安装程序，必要时会自动获取已验证的 Visual C++ 和 Windows Packet Filter 前置依赖。 |
| `ProxiFyre-<version>-win-x86.msi`、`...-x64.msi` 或 `...-arm64.msi` | 适用于已预置全部前置依赖后的托管部署。它不会下载这些依赖。 |
| `ProxiFyre-v<version>-x86.zip`、`...-x64.zip` 或 `...-ARM64.zip` | 仅应用程序负载。它不是离线安装程序，需要按照[受保护的手动暂存流程](docs/zip-staging.md)操作。 |

运行安装程序前，请先安装：

- **x86/x64：** .NET Framework 4.7.2 或更高版本。
- **ARM64：** .NET Framework 4.8.1 或更高版本。

安装程序不会安装 .NET Framework。ProxiFyre 的官方软件包有意不签名，因此 Windows 可能显示**未知发布者**。请在提权前使用相应的 `.sha256` 副文件验证所选制品。

安装完成后：

1. 从“开始”菜单打开 **ProxiFyre**。
2. 添加路由规则，并输入 SOCKS5 端点及可选凭据。
3. 选择所需的协议、地址族和传输方式。
4. 选择**验证**，然后选择**应用并重启**。
5. 确认顶部区域显示**运行中**。当引擎日志级别设为 `Info`、`Debug` 或 `All` 时，“日志”选项卡还会显示已加载规则的摘要。

安装程序会注册服务，但在存在有效配置之前有意保持服务停止。

### Windows 7

Windows 7 仅在装有 Service Pack 1 时受支持。推荐基线是完全打补丁并具有当前服务堆栈及 SHA-2 支持更新的系统镜像，并在适用更新后重启。安装程序包含针对旧版 WinINet 行为的范围受限的兼容性处理，同时仍会验证下载的前置依赖摘要。有关具体行为和限制，请参阅[安装程序文档](docs/installer.md#windows-7-baseline)。

### 可选的非提权控制台模式

在 Windows Packet Filter 安装后，便携式启动器可以选择受限的交互模式：

```console
ProxiFyre.exe --allow-not-admin
```

该开关大小写敏感且必须精确匹配。此模式无法安装、启动、停止或移除 Windows 服务，也无法发送自定义服务控制命令。没有管理员权限时，进程归属只是尽力而为：无法解析所有者的流量（可能包括受保护、已提权、系统或其他用户的进程）即使配置了兜底规则，也会保持直连。请将此视为面向选定应用程序的兼容模式，而不是严格的当前用户安全边界。详见[配置参考](docs/configuration.md#optional-unelevated-console-mode)。

## 配置

GUI 编辑 `ProxiFyre.exe` 旁的 `app-config.json`。最小配置如下：

```json
{
  "logLevel": "Info",
  "bypassLan": true,
  "proxies": [
    {
      "appNames": ["chrome", "firefox"],
      "socks5ProxyEndpoint": "proxy.example.com:1080",
      "socks5Transport": "TCP",
      "supportedProtocols": ["TCP", "UDP"],
      "supportedAddressFamilies": ["IPv4", "IPv6"]
    }
  ],
  "excludes": []
}
```

规则自上而下评估，第一个匹配的规则生效。请将兜底规则（`"appNames": [""]`）放在最后；排除项优先于所有路由规则。不受支持的目标地址族会被阻止，而不会直接泄漏。

有关匹配规则、身份验证、LAN 绕过、日志、TLS 选项和完整示例，请参阅[配置参考](docs/configuration.md)。仓库还包含可直接编辑的[示例配置](app-config.sample.json)。

## Windows GUI

GUI 管理现有引擎，而不是自行处理数据包。它可以编辑并原子化保存配置，仅安装或移除服务注册，启动和停止服务，跟踪日志，检查已解析的路径和版本，并保持在通知区域中。每个 Windows 身份和终端会话只运行一个 GUI 实例。

[![ProxiFyre 实时日志查看器](docs/images/proxifyre-ui-logs.png)](docs/images/proxifyre-ui-logs.png)

**保存**只更新配置，不会重启正在运行的服务。**应用并重启**会保存并等待实际的 Windows 服务结果。引擎日志级别属于配置设置，服务重启后生效；“日志”选项卡的视图筛选只改变显示内容。

有关路径发现、回滚行为、服务控制、日志跟踪、通知区域行为、排错和发布验证，请参阅[GUI 文档](docs/gui.md)。

## SOCKS5 over TLS

将 `socks5Transport` 设为 `"TLS"`，可保护 SOCKS5 TCP 连接和 UDP `ASSOCIATE` 控制通道。公共证书使用正常的 Windows 证书和主机名验证。对于自签名端点，建议使用精确的 `tlsPinnedSha256` 叶证书固定，而不是禁用证书验证。

[Alighieri](https://github.com/wiresock/alighieri) 为 ProxiFyre 提供兼容的 SOCKS5-over-TLS 监听器。

## 重要限制

- SOCKS5 服务器端点必须可通过 IPv4 访问：请使用 IPv4 字面量或具有 A 记录的主机名。目标流量仍可以是 IPv4、IPv6 或两者。
- IPv6 代理要求客户端具有可用的 IPv6 连接，且 SOCKS5 服务器接受 IPv6 目标（`ATYP=4`）。ProxiFyre 只重定向应用程序发出的数据包，不会创建 IPv6 连接。
- 分片 IPv6 数据报会被直接放行，以避免破坏部分数据包。因此，发出超大分片 UDP 数据报的应用程序可能绕过代理。
- SOCKS5 用户名和密码以明文形式存储在 `app-config.json` 中，但 GUI 会在诊断信息中对其进行掩码和脱敏。
- MSI 会为 `ProxiFyre.exe` 创建程序专用的入站 TCP 和 UDP 防火墙规则，因为重定向监听端口是动态分配的。

## 文档

- [配置参考](docs/configuration.md)
- [GUI 架构、行为与排错](docs/gui.md)
- [安装程序、部署、发布工作流与软件包验证](docs/installer.md)
- [受保护的手动 ZIP 暂存](docs/zip-staging.md)
- [示例配置](app-config.sample.json)

## 从源码构建

开发环境需要 Visual Studio 2022，并安装 C++ 与 .NET 桌面工作负载、v143 工具集、.NET Framework 4.7.2 目标包、NuGet CLI、PowerShell 7、.NET SDK，以及仓库的 vcpkg 依赖。

确切的还原、Release 构建、测试和打包命令维护在 [docs/gui.md#build-and-test](docs/gui.md#build-and-test) 与 [docs/installer.md#building-installers](docs/installer.md#building-installers) 中。项目版本以 [Directory.Build.props](Directory.Build.props) 中的 `ProxiFyreReleaseVersion` 为准。

## 许可证

ProxiFyre 依据 [GNU Affero General Public License v3.0](LICENSE) 许可。Windows Packet Filter 和 Microsoft Visual C++ 运行时是独立的第三方依赖，按其各自的条款分发。
