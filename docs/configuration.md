# ProxiFyre 配置参考

ProxiFyre 从已解析的 `ProxiFyre.exe` 所在目录读取 `app-config.json`。推荐使用 GUI 编辑：它会验证完整模型，保留未知 JSON 属性，原子化写入，并将上一文件保留为 `app-config.json.bak`。

配置更改不会影响正在运行的服务。请在 GUI 中使用**应用并重启**，或在手动编辑文件后重启 `ProxiFyreService`。

## Optional unelevated console mode

普通 GUI 和 Windows 服务工作流仍保持提权运行。便携式管理器可以改为在明确受限的交互式控制台模式下启动引擎：

```console
ProxiFyre.exe --allow-not-admin
```

`--allow-not-admin` 是一个精确且区分大小写的显式选项，仅适用于普通交互式运行。它不能与 Topshelf 的 `install`、`uninstall`、`start` 或 `stop` 生命周期命令，或 `command` 服务控制动词组合使用，并且对服务执行没有影响。Windows Packet Filter 必须已安装且可访问；如果引擎无法打开驱动程序，启动会失败，而不是在未进行重定向的情况下静默运行。每当受限模式处于活动状态时，引擎都会在控制台和日志中写入醒目的警告。

对于标准用户，进程归属查询只是尽力而为。ProxiFyre 可以路由其能正常解析的应用程序，但 Windows 可能拒绝访问受保护、已提权、系统或其他用户的进程。所有者无法解析的流量会保持直连，包括配置中包含 `"appNames": [""]` 兜底规则的情况。这种“失败即直连”的行为可防止无法解析的特权进程被误认为普通兜底匹配项，但也意味着该模式**不是**严格的当前用户隔离或防泄漏边界。当需要完整的全机器归属和强制代理时，请使用提权服务。

## 完整示例

```json
{
  "logLevel": "Info",
  "bypassLan": true,
  "proxies": [
    {
      "appNames": ["chrome", "firefox"],
      "socks5ProxyEndpoint": "proxy.example.com:1080",
      "username": "example-user",
      "password": "example-password",
      "socks5Transport": "TCP",
      "supportedProtocols": ["TCP", "UDP"],
      "supportedAddressFamilies": ["IPv4", "IPv6"]
    },
    {
      "appNames": [""],
      "socks5ProxyEndpoint": "tls-proxy.example.com:443",
      "socks5Transport": "TLS",
      "tlsServerName": "tls-proxy.example.com",
      "tlsPinnedSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "tlsAllowInvalidCertificate": false,
      "supportedProtocols": ["TCP"],
      "supportedAddressFamilies": ["IPv4"]
    }
  ],
  "excludes": [
    "vpn-client.exe",
    "C:\\Program Files\\Local App\\NotProxied.exe"
  ]
}
```

## 顶层属性

| 属性 | 说明 |
| --- | --- |
| `logLevel` | `Error`、`Warning`、`Info`、`Debug` 或 `All`。缺失或不支持的值会回退为 `Info`。 |
| `bypassLan` | 为 `true` 时，受支持的本地 IPv4 地址段保持直连。默认值：`false`。 |
| `proxies` | 一条或多条有序路由规则。第一个匹配的规则生效。 |
| `excludes` | 始终绕过代理路由的进程名称或路径。 |

## 路由规则属性

| 属性 | 说明 |
| --- | --- |
| `appNames` | 此规则匹配的应用程序名称或路径。显式空字符串为兜底匹配。 |
| `socks5ProxyEndpoint` | SOCKS5 主机名或 IPv4 地址加端口，例如 `proxy.example.com:1080`。 |
| `username`、`password` | 可选 SOCKS5 身份验证。两者都配置或都不配置。 |
| `socks5Transport` | `TCP` 表示普通 SOCKS5，`TLS` 表示 SOCKS5-over-TLS。默认值：`TCP`。 |
| `tlsServerName` | TLS SNI 与证书验证名称。默认为端点主机名。 |
| `tlsPinnedSha256` | 可选的期望叶证书 SHA-256 指纹。 |
| `tlsAllowInvalidCertificate` | 禁用正常证书验证。默认值：`false`；没有固定值时请避免开启。 |
| `supportedProtocols` | `TCP`、`UDP` 或两者。省略表示两者。 |
| `supportedAddressFamilies` | `IPv4`、`IPv6` 或两者。省略表示两者。 |

## 应用程序匹配与规则顺序

路由规则自上而下评估，第一个匹配的规则生效。

- 不含 `/` 或 `\` 的值按可执行文件完整名称进行不区分大小写的匹配。`firefox` 和 `firefox.exe` 可以匹配 `firefox.exe`；`firefox` 不会匹配 `NewFirefox.exe`。
- 含 `/` 或 `\` 的值作为进程完整路径的子串进行匹配。这对于安装在可变包目录下的应用程序很有用。
- 显式空字符串（`""`）匹配所有尚未匹配或排除的应用程序。请将此兜底规则放在最后，因为它会屏蔽其后的所有规则。
- 空值和仅含空白的条目会被忽略；空格不是兜底匹配。

验证器会在不改变规则顺序的情况下，对重复、重叠或已遮蔽的匹配项发出警告。

至少需要一条代理规则。没有有效 `appNames` 条目的规则会为了兼容而保留，但不会匹配任何进程，验证时会报告警告。

## 排除项

排除项优先于所有路由规则，包括兜底规则。它们有意采用宽松的子串匹配：名称与进程文件名比较，路径形式条目与完整路径比较。例如，`chrome` 也会排除 `chrome_proxy.exe`。

当 ProxiFyre 与另一个 VPN 或隧道一起使用时，如有必要请排除该产品的承载进程。否则兜底规则可能捕获外层隧道流量，使代理连接递归依赖自身。

## 协议与地址族

`supportedProtocols` 接受 `TCP`、`UDP` 或两者。省略表示两者，以保持向后兼容。显式空列表也保留旧版双协议行为，但 GUI 要求新编辑的规则至少选择一种协议。

`supportedAddressFamilies` 接受 `IPv4`、`IPv6` 或两者。省略表示两者；显式空列表或未知值无效。

如果匹配的规则不支持目标地址族，ProxiFyre 会阻止该流量，而不是让它直连或发送到不兼容的 SOCKS 路径。例如，只选择 `IPv4` 可以防止 IPv6 泄漏，同时允许浏览器等应用程序回退到 IPv4。

IPv6 目标路由还要求：

- 客户端具有可用的 IPv6 连接，因为 ProxiFyre 重定向的是应用程序发出的数据包，而不是创建 IPv6 可达性；
- SOCKS5 服务器接受 IPv6 目标地址（`ATYP=4`）；并且
- 上游代理可通过 IPv4 访问。端点本身必须是 IPv4 字面量或具有 A 记录的主机名；不支持 IPv6 字面量和仅有 AAAA 记录的上游。

分片 IPv6 数据报会被直接放行，以避免重写不完整的数据包。因此，发出超大分片 UDP 数据报的应用程序可能绕过代理；TCP 和正常 QUIC 这类路径 MTU 感知的 UDP 流量不受影响。

## LAN 绕过

将 `bypassLan` 设为 `true`，可在匹配的互联网流量走代理的同时，使以下本地 IPv4 目标保持直连：

- `10.0.0.0/8`
- `172.16.0.0/12`
- `192.168.0.0/16`
- `169.254.0.0/16`（链路本地/APIPA）
- `224.0.0.0/4`（多播）

更改此设置需要重启服务。

## 身份验证

当 SOCKS5 服务器不需要身份验证时，请省略 `username` 和 `password`。启用身份验证时，两个属性均为必填，且每个值编码后不得超过 255 字节，以满足本机 SOCKS5 协议。

凭据会以明文形式保留在 `app-config.json` 中，因为服务必须读取它们。GUI 会掩码密码，并在诊断信息和最近日志摘要中脱敏已配置或检测到的敏感形式。

## SOCKS5 over TLS

将 `socks5Transport` 设为 `"TLS"` 可使用 TLS 保护的 SOCKS5 连接。这会保护 TCP `CONNECT` 流量和 UDP `ASSOCIATE` 控制通道；SOCKS5 UDP 中继数据报不会在该 TLS 流内部传输。

对于公开可信证书，通常只设置 `tlsServerName`，或让它默认为端点主机名。正常的 Windows 证书链和主机名验证保持启用。

对于自签名实验端点，建议使用 `tlsPinnedSha256`。固定值是期望叶证书的 32 字节 SHA-256 指纹，写为 64 个十六进制字符；规范化时会忽略空白、冒号和连字符。固定值可以在不要求公共证书链的情况下认证该特定证书。

`tlsAllowInvalidCertificate: true` 会禁用正常证书验证并产生醒目的警告。没有固定值时，它会完全禁用上游身份验证。本机传输当前协商 TLS 1.2。

[Alighieri](https://github.com/wiresock/alighieri) 提供兼容的 SOCKS5-over-TLS 监听器。

## 日志

| 级别 | 适用场景 |
| --- | --- |
| `Error` | 仅失败。 |
| `Warning` | 失败以及可能不安全或降级的情况。 |
| `Info` | 服务生命周期和配置摘要。 |
| `Debug` | 增加每条连接的路由、代理协商和中继详情。 |
| `All` | 最大本机与管理细节。 |

引擎日志写入 `ProxiFyre.exe` 旁的 `logs` 目录。更改 `logLevel` 需要重启服务。“日志”选项卡的**视图筛选**仅影响显示：它过滤已经写入的行，不会改变引擎详细程度，也不需要重启。

有关日志跟随、轮转、文件访问和排错，请参见 [gui.md](gui.md#logs-and-notification-area)。
