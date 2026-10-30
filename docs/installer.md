# ProxiFyre 安装程序与部署

ProxiFyre 分别发布 x86、x64 和 ARM64 Windows 包。对于常规交互式安装，推荐制品是架构匹配的 `*-setup.exe` 在线引导程序。它使用 WiX Burn 在必要时获取并安装 Microsoft Visual C++ 运行时和 Windows Packet Filter，然后安装内嵌的 ProxiFyre MSI。Setup **不会**获取 .NET Framework：x86 和 x64 需要 .NET Framework 4.7.2 或更高版本，而 ARM64 需要 .NET Framework 4.8.1 或更高版本。请在运行 setup 前预置该框架；MSI 会核验它，并在缺失时以可操作的提示停止。

独立 MSI 用于在已预置 .NET Framework、架构匹配的 Visual C++ 运行时和 Windows Packet Filter 之后的托管部署。发布 ZIP 只是应用程序负载归档。它不会安装或调和前置依赖、服务、受保护的安装目录权限、快捷方式、添加/删除程序注册或防火墙规则，并且**不是**离线安装程序。Release 界面还拒绝从普通用户可写的解压目录（例如 Downloads 或 `%TEMP%`）加载其未签名的托管负载；请使用 setup，或遵循[受保护的 ZIP 暂存流程](zip-staging.md)。

ProxiFyre 的第一方二进制文件、MSI 和引导程序有意不签名。因此 Windows 可能在 SmartScreen 或提权提示中显示**未知发布者**。请在提权前验证已发布的 SHA-256 校验和。第三方前置依赖包保留其上游签名；打包会结合固定哈希一起验证这些签名，而不会把它们当作第一方发布者身份。

Release 构建会直接生成最终制品。它们不需要代码签名证书、证书存储设置、签名密钥、时间戳服务，也不需要构建后的归档替换步骤。

## 工具集与项目结构

打包通过固定的 SDK 风格项目使用 WiX Toolset **6.0.2**：

- `ProxiFyre.Installer/ProxiFyre.Installer.wixproj` 构建各架构 MSI。
- `ProxiFyre.Bundle/ProxiFyre.Bundle.wixproj` 构建各架构 Burn 引导程序。
- `ProxiFyreSetupBootstrapper/ProxiFyreSetupBootstrapper.vcxproj` 构建架构匹配、静态运行时的 WixStdBA functions DLL，用于 Windows 7 的 TLS 1.2 获取兼容性，以及下文所述的窄范围最终 Visual C++ HTTP 重试。
- `ProxiFyreSetupEngineExtension/ProxiFyreSetupEngineExtension.vcxproj` 构建架构匹配、静态运行时的扩展，它运行在 Burn 的引擎进程内，在不改动 Internet 选项的情况下修复一处格式错误的 WinINet 配置。
- `scripts/Build-Installer.ps1` 验证并暂存封闭的运行时负载，验证两个前置依赖来源，构建两个包，并写入 SHA-256 副文件。
- `scripts/Test-InstallerPackage.ps1` 静态检查 MSI 数据库。
- `scripts/Test-BundlePackage.ps1` 提取并验证 Burn 清单、注册/显示版本、前置依赖元数据、链顺序、自定义窗口图标、本地引导程序 functions 负载，以及精确的引擎扩展负载、导出项、架构和静态运行时依赖集。
- `scripts/Test-SetupBootstrapperFunctionsSource.ps1` 守护 Windows 7 获取钩子：它必须在获取开始时保留确切的 URL，只在 WiX 解析出远程下载操作后才武装该候选，先允许正常 HTTPS 重试，并且只对固定 Visual C++ 包和负载的 WinINet 错误 `12029` 提供最后一次 HTTP 尝试。它还会防止摘要检查失败后再次获取。
- `scripts/Test-SetupEngineExtensionSource.ps1` 守护扩展的作用域为进程的非 NULL WinINet 调用，并拒绝全局 WinINet 刷新或注册表写入。
- `scripts/Test-UnsignedArtifacts.ps1` 拒绝 ProxiFyre 第一方发布模块和包上的 Authenticode 签名。

选择 WiX 6 是因为其 SDK 风格项目能在 Visual Studio 2022/.NET SDK 环境中干净地构建，而且其维护中的 Burn、Firewall、UI 和 Util 扩展覆盖了所需的引导程序、声明式防火墙、功能选择和前置依赖搜索。包锁文件和 `NuGet.Installer.Config` 把还原固定到已审查的 WiX 6.0.2 输入。

MSI/引导程序模型保持 Windows Installer 的确定性：MSI 从不通过自定义操作下载或启动另一个安装程序。Burn 拥有 Visual C++ 和 Windows Packet Filter 的获取与链式执行，而 MSI 在管理员已预置这些依赖项和所需 .NET Framework 时仍可独立部署。

WiX 6 在独立进程中运行 WixStdBA，其窗口不继承 `Bundle/@IconSourceFile`。因此 ProxiFyre 提供自定义的 WiX 超链接许可证主题副本，其 `Window/@IconFile` 引用规范的多尺寸 `ProxiFyre.ico` 负载。包检查会同时核对主题引用和提取出的图标字节，使 setup 标题栏和任务栏保留应用程序品牌，包括在 Windows 7 上。

## Architecture-specific artifacts

对于版本 `2.6.4`，打包脚本在 `bin\installer\<x86|x64|ARM64>\Release\` 下生成这四个当前版本文件：

```text
ProxiFyre-2.6.4-win-x86.msi
ProxiFyre-2.6.4-win-x86.msi.sha256
ProxiFyre-2.6.4-win-x86-setup.exe
ProxiFyre-2.6.4-win-x86-setup.exe.sha256
```

x64 和 ARM64 目录在相应文件名中使用 `x64` 和 `arm64`。请使用与机器原生 Windows 架构匹配的包。引导程序会在获取前检查架构，因为 Windows Packet Filter 是内核驱动程序；例如，x86 setup 有意不使用 Windows 在 x64 机器上的 x86 仿真。全部五个绑定架构的第一方运行时 PE 文件（`ProxiFyre.exe`、本地界面宿主、两个托管程序集和 `socksify.dll`）以及两个本地 setup 辅助 DLL 都会在打包前按请求的架构核对。

WiX 在链接 bundle 时物化已验证、未压缩的 Visual C++ 和 Windows Packet Filter 来源负载。打包脚本把这些布局副产品限制在其受保护的临时暂存目录中，并在验证后删除它们。脚本只创建上述四个当前版本 `ProxiFyre-*` 文件；它不会清理所选输出目录中无关或更旧的文件，`-Force` 也只替换这恰好四个路径。当任一外部前置依赖文件名已存在于该目录时，它会拒绝构建，以免陈旧的布局悄悄变成离线包。Setup 必须从固定的官方 URL 获取缺失的前置依赖，或在 Burn 的包缓存中找到它们。

对于 `v2.6.4` 标签，发布工作流恰好发布这 18 个构建资产；GitHub 还会另外添加其两个标准源代码归档：

```text
ProxiFyre-v2.6.4-x86.zip
ProxiFyre-v2.6.4-x86.zip.sha256
ProxiFyre-2.6.4-win-x86.msi
ProxiFyre-2.6.4-win-x86.msi.sha256
ProxiFyre-2.6.4-win-x86-setup.exe
ProxiFyre-2.6.4-win-x86-setup.exe.sha256

ProxiFyre-v2.6.4-x64.zip
ProxiFyre-v2.6.4-x64.zip.sha256
ProxiFyre-2.6.4-win-x64.msi
ProxiFyre-2.6.4-win-x64.msi.sha256
ProxiFyre-2.6.4-win-x64-setup.exe
ProxiFyre-2.6.4-win-x64-setup.exe.sha256

ProxiFyre-v2.6.4-ARM64.zip
ProxiFyre-v2.6.4-ARM64.zip.sha256
ProxiFyre-2.6.4-win-arm64.msi
ProxiFyre-2.6.4-win-arm64.msi.sha256
ProxiFyre-2.6.4-win-arm64-setup.exe
ProxiFyre-2.6.4-win-arm64-setup.exe.sha256
```

安装程序文件名使用规范化的小写架构标记 `x86`、`x64` 和 `arm64`。ZIP 文件名保留工作流矩阵标签 `x86`、`x64` 和 `ARM64`，并包含来自发布标签的前导 `v`。拉取请求和手动非标签运行改用 `ProxiFyre-ci-<run-id>-<matrix-platform>.zip`；它们从不发布 GitHub release。任何发布资产都不包含 Visual C++ 或 Windows Packet Filter 安装程序的离线副本。

## Windows 7 baseline

Windows 7 仅在装有 Service Pack 1 时受支持。setup 和独立 MSI 都会在获取或安装前置依赖之前拒绝 Windows 7 RTM。在 Windows 7 SP1 上测试前，请安装该镜像可用的当前服务堆栈和 SHA-2 支持更新并重启。特别是 [Microsoft 的 SHA-2 指南](https://support.microsoft.com/en-us/topic/2019-sha-2-code-signing-support-requirement-for-windows-and-wsus-64d1c82d-31ee-c273-3930-69a4cde8e64f)将 KB4490628 和 KB4474419（或替代更新）记录为 2019 年后 SHA-2 更新的服务基线；这是推荐的完整补丁基线，而不是此处修复的 Visual C++ 检测失败的原因。

固定的 Visual C++ 可再发行组件携带其运行时在 Windows 7 上所需的 [Universal CRT 更新](https://support.microsoft.com/en-us/servicing/os/windows/2020/04/update-for-universal-c-runtime-in-windows)。如果该前置依赖报告需要重启，Burn 会安排重启，并且安装必须在 Windows 重启后重试或完成。因此 VM 验证必须包含干净的 Windows 7 SP1 x64 镜像、前置依赖安装、任何请求的重启以及第二次 setup 运行。

Burn 对远程负载使用当前用户的 WinINet 协议设置。仅在 Windows 7 上，并且紧接 HTTPS 前置依赖获取之前，ProxiFyre 的本地 setup functions DLL 会临时把 TLS 1.2 位添加到 `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\SecureProtocols`。它会记录该值原本是否存在及其确切内容，在缓存完成或 setup 退出时恢复该状态，并避免覆盖并发的 Internet 选项更改。该辅助程序使用静态 C++ 运行时，因此可以在 Visual C++ 前置依赖存在之前运行。较新的 Windows 版本以及仅缓存/本地安装路径保持不变。

一些已完全打补丁的 Windows 7 系统仍无法通过 HTTPS 与 Microsoft 当前的 Visual C++ CDN 协商：WinINet 能到达主机，但返回错误 `12029`，而同一台机器却能完成其他 HTTPS 获取。setup functions DLL 在获取开始时验证并保留确切的 `VisualCppRuntime` URL，然后仅在 Burn 把负载解析为远程下载后才武装该候选；缓存和本地来源不会武装它。这种两阶段处理也绕过了 [WiX issue #9275](https://github.com/wixtoolset/issues/issues/9275)——在该问题中 WiX 6.0.2 在后续来源解析回调中省略了 URL。缺失的运行时下载总是先得到 WixStdBA 的正常 HTTPS 尝试。仅在 Windows 7 上，当对该确切包和负载的重试以 `12029` 耗尽后，辅助程序请求 Burn 在仅把相同 `download.visualstudio.microsoft.com/download/pr/` 内容寻址 URL 的 `https` 方案替换为 `http` 后，做最后一次获取尝试。它对其他错误、其他包或负载、意外的主机/路径或 Windows 8 及更高版本不使用 HTTP。如果最后一次 HTTP 获取或其摘要验证失败，辅助程序会抑制另一次获取尝试。嵌入在 bundle 中的 URL 仍是 Microsoft 的 HTTPS URL。

这种旧式传输有意保持窄范围，不会让负载信任依赖 HTTP。bundle 绑定 Microsoft 签名文件的精确字节长度和 SHA-512，构建还会单独验证其 SHA-256、SHA-512、版本和 Microsoft Authenticode 签名。Burn 在执行下载文件前验证嵌入的 SHA-512，因此被修改或替换的字节会以失败关闭。未加密的中间人可以观察或阻断 Windows 7 下载，但无法提供 Burn 会接受的其他可执行文件。

WiX 6 在 Burn 引擎进程之外承载 WixStdBA 及其 functions DLL，因此在那里设置的代理信息无法影响 Burn 的下载器。ProxiFyre 因此嵌入第二个极简的 BootstrapperExtension，由 WiX 直接加载到引擎进程中。它只处理这种无效配置：即使机器处于在线 LAN 上，`INTERNET_PER_CONN_FLAGS_UI` 和 `INTERNET_PER_CONN_FLAGS` 仍同时为零。在应用任何内容之前，它还要求 `ProxyEnable` 是显式零 DWORD；原始 `ProxyServer` 和 `AutoConfigURL` 值缺失或恰好是单 NUL 的 `REG_SZ` 值；`ProxySettingsPerUser` 策略缺失或恰好是 DWORD `1`；并且 WinINet API 或 `AutoDetect` 都不存在代理、PAC 或自动发现意图。`DefaultConnectionSettings` 和 `SavedLegacySettings` 都必须以两种可识别的 IE 连接 blob 格式之一（`0x3C` 或 `0x46` 标记）存在，只包含零或 DIRECT 路由标志，并带有有界的空内嵌代理和自动配置字符串。未知标记或标志位、截断的长度前缀字符串，或任何缺失、格式错误、受策略控制、代理、PAC、WPAD、调制解调器、离线或模糊状态都会让 Burn 保持正常行为不变。

## MSI 内容与行为

MSI 将以下封闭文件集安装到与架构对应的每机器 Program Files `ProxiFyre` 目录：

```text
ProxiFyre.exe
ProxiFyre.exe.config
ProxiFyreUI.exe
ProxiFyreUI.exe.config
ProxiFyreUI.Managed.dll
ProxiFyre.Configuration.dll
socksify.dll
Newtonsoft.Json.dll
NLog.dll
Topshelf.dll
NLog.config
app-config.sample.json
```

活动文件 `app-config.json`、`app-config.json.bak` 和 `logs` 目录有意不在 MSI File 表中。它们是运行时/用户数据，会在修复、主版本升级和卸载期间保留。`app-config.sample.json` 和可执行配置文件是产品文件，因此 Windows Installer 会正常更新或删除这些文件。位于 `%LocalAppData%\ProxiFyreUI` 下的界面首选项也在 MSI 之外。

安装程序应用受保护的应用目录 ACL：`SYSTEM` 和内置 Administrators 获得完全控制，标准用户获得读取和执行访问权。这支持界面和服务负载完整性检查，而不试图把安全性强加给用户控制的解压目录。

添加/删除程序将产品标识为 **ProxiFyre**，制造商为 **NT KERNEL**，并包含项目、支持、更新和应用程序图标元数据。必需的全用户开始菜单快捷方式和可选的共享桌面快捷方式都启动 `ProxiFyreUI.exe`；服务可执行文件不作为普通用户入口呈现。

### 服务

MSI 以声明方式安装：

- 服务名 `ProxiFyreService`；
- 显示名 `ProxiFyre Service`；
- 来自安装目录的可执行文件 `ProxiFyre.exe`；
- 账户 `LocalSystem`；
- 启动类型 **按需/手动**；
- 错误控制 `normal`；
- 服务依赖 `NDISRD`。

安装程序有意不启动服务。新安装还没有活动代理配置，因此用户先打开 ProxiFyreUI，创建或检查 `app-config.json`，然后从界面启动服务。卸载期间 Windows Installer 会停止服务、等待它，并只删除 `ProxiFyreService` 注册。

### 防火墙规则

引擎创建绑定到未指定本地地址的动态分配 IPv4 和 IPv6 TCP 与 UDP 监听器。Windows Packet Filter 把选定的本地流重定向到这些监听器，因此固定端口规则不完整，而打开任意全局端口则过于宽泛。因此 WiX Firewall 扩展恰好拥有两条入站、按程序作用域的允许规则：

| 规则 | 程序 | 方向 | 协议 | 端口 | 配置文件 | 远程作用域 | 边缘遍历 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `ProxiFyre (TCP-In)` | 已安装的 `ProxiFyre.exe` | 入站 | TCP | 任意/动态 | 域、专用、公用 | 任意 | 拒绝 |
| `ProxiFyre (UDP-In)` | 已安装的 `ProxiFyre.exe` | 入站 | UDP | 任意/动态 | 域、专用、公用 | 任意 | 拒绝 |

没有任何规则针对 `ProxiFyreUI.exe`，不添加出站规则，也不打开独立端口。这些规则是 MSI 的组件：修复是幂等的，主版本升级会随引擎组件一起更新它们，卸载只删除这些 ProxiFyre 拥有的规则。安装程序不调用 `netsh`、PowerShell 或防火墙自定义操作。

## Microsoft Visual C++ 运行时前置依赖

`socksify.dll` 是使用 Visual Studio 2022 v143 工具集构建的 C++/CLI 模块。因此 Burn 会在 Windows Packet Filter 和 ProxiFyre 之前链入架构匹配的 [Microsoft Visual C++ 2015-2022 Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170)。版本固定为 **14.44.35211.0**，而不是跟随 Microsoft 变动的 `aka.ms/vc14` 链接，这样已经发布的引导程序就保留其测试所依据的确切负载身份和兼容的操作系统下限。

| 架构 | 官方资产 | 字节数 | 固定的 SHA-256 |
| --- | --- | ---: | --- |
| x86 | `VC_redist.x86.exe` | 13953392 | `0c09f2611660441084ce0df425c51c11e147e6447963c3690f97e0b25c55ed64` |
| x64 | `VC_redist.x64.exe` | 25635768 | `cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b` |
| ARM64 | `VC_redist.arm64.exe` | 11722336 | `5139e1440c3a20b92153a4db561c069a0175aaf76c276c3e5b6f56099edcf4b0` |

在构建时，打包脚本通过其内容寻址的 `download.visualstudio.microsoft.com` URL 下载固定资产，或通过 `-VisualCppRedistributablePath` 接受它。它要求精确的长度、SHA-256、SHA-512、文件/产品版本和当前有效的 Microsoft Corporation Authenticode 签名。它还要求官方 URL 路径包含该 SHA-256 和文件名。第三方可执行文件仍是外部 Burn 负载；它不会提交到此仓库或嵌入 setup。在安装时，只有上文所述的窄范围 Windows 7 兼容路径会更改传输方案；较新的 Windows 系统使用嵌入的 HTTPS URL。

在安装时，Burn 读取 Microsoft 的 `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\<architecture>` 值，并在与固定 VC 可再发行组件相同的下限上检查本地 `ucrtbase.dll`、`msvcp140.dll`、`msvcp140_atomic_wait.dll`、`vcruntime140.dll`，以及在 x64 上检查 `vcruntime140_1.dll`。完整兼容的 14.44.35211 或更新 DLL 集即使注册缺失也会被跳过。缺失或过旧的文件会强制运行可再发行组件：当前注册选择 `/repair`，而缺失或过旧注册选择 `/install`。Burn 验证绑定进 setup 的 SHA-512，并把该包作为关键的、永久的、共享的机器前置依赖安静运行。正常成功会继续链；需要重启的结果会在 ProxiFyre MSI 之前停止，以免待处理的 DLL 替换触发其启动条件，Windows 重启后重新运行 setup 即可成功。当所需文件仍不完整时，冲突的较新包不会被接受为成功。任何其他结果也会停止链。ProxiFyre 从不卸载这个共享运行时。

独立 MSI 无法获取运行时。它在 Windows 系统目录中对本地 `ucrtbase.dll` 以及架构特定的 VC DLL 执行声明式版本搜索，并在完整依赖不可用时以可操作提示退出。x86 搜索本地 x86 `SystemFolder`；x64 和 ARM64 搜索 `System64Folder`，避免 32 位文件系统重定向。当文件版本恰好等于 `Signature.MinVersion` 时，Windows Installer 会比较文件语言，因此 MSI 把语言无关的 VC 最小值编码为紧随其前的四部分值 `14.44.35210.65535`，把 UCRT 最小值编码为 `10.0.10239.65535`。面向用户的有效要求仍分别是 **14.44.35211.0** 和 **10.0.10240.0**。即使存在过时的注册表检测，这也能捕获缺失或不完整的运行时。

当 ProxiFyre bundle 链入 MSI 时，Burn 设置 `BURNMSIINSTALL=1`，bundle 添加 `PROXIFYRE_SETUP_CHAIN=1`；MSI 要求两个标记都存在，才会跳过这个冗余的 Visual C++ 启动条件。Burn 在规划 MSI 之前已经检查了相同的本地文件集和版本下限。如果初始检查为假，Burn 相信固定的关键可再发行组件退出码 0 即为成功完成；它不会在链式包之间重新运行文件搜索。重启和失败结果会在 MSI 之前停止或挂起链。普通的独立部署和无关的 Burn 包装器不会自动获得这两个标记，因此 MSI 自己的 AppSearch 前置依赖门仍保持活动。

这两个值都是公开的 MSI 路由标记，不是身份验证或安全证明；显式的 `msiexec` 命令行或转换可以提供它们。这里可以接受，因为启动条件是早期依赖诊断，而不是授权边界，并且 ProxiFyre 执行时本地加载器仍会强制运行时依赖。

Microsoft 根据适用的 [Visual Studio 再分发列表](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution)和[许可条款](https://visualstudio.microsoft.com/license-terms/)允许再分发所列的运行时包。发布者必须独立确认自己是获许可的 Visual Studio 用户，并且其分发符合这些条款；仅凭公开下载 URL 不构成再分发授权。该包保持未修改，以便 Microsoft 独立提供服务。

## Windows Packet Filter 前置依赖

ProxiFyre 需要 `NDISRD` 服务和兼容的 Windows Packet Filter 3.x 驱动程序。兼容性在两个层面检查：

1. 安装程序执行声明式注册表和驱动程序文件版本搜索。它要求 `NDISRD` 服务注册以及实际的 `ndisrd.sys` 文件版本至少为 `3.6.1.0` 且早于 `4.0.0.0`；仅有过时的产品/服务注册不会被接受。与 VC 文件一样，MSI Signature 行使用语言无关的前驱编码 `3.6.0.65535`，而有效的兼容性下限仍是 `3.6.1.0`。
2. 在安装/启动/重启操作前，ProxiFyreUI 不请求读或写访问地打开 `\\.\NDISRD`，并发出 `IOCTL_NDISRD_GET_VERSION`。运行时使用要求 API 主版本 `3` 和 API 次版本 `0x0601` 或更高。这个活动设备检查能捕获那些满足安装程序文件版本范围、但不暴露 ProxiFyre 所使用 API 的自定义或异常版本驱动程序。

已安装但停止的 `NDISRD` 服务是有效的。MSI 将其记录为服务依赖，使服务控制管理器能在启动 `ProxiFyreService` 时启动它。

### Burn 获取与验证

引导程序根据上游 [`v3.6.2` 发布](https://github.com/wiresock/ndisapi/releases/tag/v3.6.2)中发布的官方 Windows Packet Filter **3.6.2.1** MSI 资产构建：

| 架构 | 官方资产 | 字节数 | 固定的 SHA-256 |
| --- | --- | ---: | --- |
| x86 | `Windows.Packet.Filter.3.6.2.1.x86.msi` | 786432 | `ff882c295f9bb0c4465b179e912ad2d2c41eadc053142bd2f125f49a85ee4e1f` |
| x64 | `Windows.Packet.Filter.3.6.2.1.x64.msi` | 819200 | `9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c` |
| ARM64 | `Windows.Packet.Filter.3.6.2.1.ARM64.msi` | 745472 | `b13c6832c9e5c0c14948bbf5c17ccbe65dff55c0f6069df01494d97ebd1f3d69` |

构建执行以下流程：

1. 从 `https://github.com/wiresock/ndisapi/releases/download/v3.6.2/` 下的版本化资产 URL 下载架构匹配的 MSI，或通过 `-WindowsPacketFilterMsiPath` 接受相同资产。
2. 要求精确的字节长度和上面所示的固定 SHA-256。
3. 要求该第三方 MSI 上有有效的上游 Authenticode 签名。
4. 在构建时把已验证的 MSI 交给 WiX，让 Burn 记录其包身份和远程负载元数据/摘要。
5. 将该前置依赖标记为未压缩，并指向相同的官方版本化 URL。Windows Packet Filter 二进制文件不会提交到此仓库，也不会嵌入 ProxiFyre 引导程序。

在安装时，Burn 检查 `NDISRD` 注册和本地 `System32\drivers\ndisrd.sys` 版本。如果兼容范围已存在，Burn 跳过该前置依赖。否则它下载官方架构匹配的 MSI，根据绑定进引导程序的负载元数据验证获取的字节，将其作为关键包安装，并继续执行 ProxiFyre MSI。ProxiFyre MSI 重复其声明式前置依赖条件，在产品安装继续前提供第二次存在/版本检查。

如果无法获取、远程负载被修改、上游安装程序失败或用户取消，关键前置依赖会在 ProxiFyre 安装之前停止链。在离线机器上，请在本地策略下预置所需的 .NET Framework、Visual C++ 运行时和 Windows Packet Filter 包，然后重新运行 setup 或部署独立 ProxiFyre MSI。Burn 也可以使用其包缓存中已有的前置依赖负载。ProxiFyre 不发布离线前置依赖 bundle。

Windows Packet Filter 是机器范围的，可被其他软件共享。它的 Burn 包是永久的，因此卸载 ProxiFyre **不会**删除或降级 Windows Packet Filter，即使该组件最初由 ProxiFyre 引导程序获取。

Windows Packet Filter 是独立的第三方产品。任何再分发 ProxiFyre 的人都必须审查其许可条款以及所选择的获取/分发流程。此仓库的远程下载实现并不主张或授予再分发权利。

## 独立 MSI 部署

仅当所需 .NET Framework、架构匹配的 Visual C++ 运行时和 Windows Packet Filter 已预置时，才直接使用 MSI：

```powershell
msiexec.exe /i .\ProxiFyre-2.6.4-win-x64.msi
```

在没有兼容运行时文件或已注册驱动程序/文件版本的干净机器上，MSI 会以可操作提示退出，引导用户使用 setup 可执行文件和官方前置依赖来源。它不下载前置依赖。它也从不下载 .NET Framework：x86 和 x64 包需要 .NET Framework 4.7.2 或更高版本，ARM64 包需要 .NET Framework 4.8.1 或更高版本。

企业部署可以分别缓存并验证 .NET Framework 以及官方 Visual C++ 和 Windows Packet Filter 包，在本地策略下安装它们，然后部署 ProxiFyre MSI。这是预置后确定性的、网络无关的部署路径；MSI 本身不是离线前置依赖安装程序。

## 版本来源与发布流程

`Directory.Build.props` 是仓库的权威发布版本来源。对于本版本，它把 `ProxiFyreReleaseVersion` 定义为 `2.6.4`，将该值作为默认 MSBuild `Version`，派生四部分本地资源元组，并提供规范的仓库身份。显式 `/p:Version` 仍可用于受控的构建和测试场景，但受支持的打包脚本会把全部五个绑定架构的第一方运行时 PE 文件和两个本地 setup 辅助 DLL 的 `FileVersion` 与 `ProductVersion`，与通过 `-Version` 传入的相同三部分值进行核对。它还会验证 MSI 产品版本和 Burn 注册/显示版本。这可以防止以某版本命名的包悄悄包含来自另一个版本的二进制文件或 setup 辅助程序。

发布工作流直接读取 `ProxiFyreReleaseVersion`，而不是从任意标签派生版本。发布标签必须逐字节匹配它并带一个前导 `v`：仓库版本 `2.6.4` 只接受标签 `v2.6.4`。不匹配的标签、额外前导零和预发布后缀都会在编译前失败。Windows Installer 收到不带 `v` 的规范三部分值；预发布版本有意不受支持，因为 MSI 无法安全保留 SemVer 预发布顺序。

拉取请求和手动 `workflow_dispatch` 运行构建相同的仓库版本，但把 ZIP 标记为 `ci-<run-id>`，并且不创建 GitHub release。标签构建运行 x86、x64 和 ARM64 Release 矩阵，在 x64 分支上运行托管测试套件，在每个分支上验证未签名负载和两个安装程序包，并上传 ZIP、MSI、在线 setup 和 SHA-256 副文件。发布作业等待全部三个架构作业，因此失败的分支无法创建部分发布，然后生成发布说明并使用草稿-上传-发布顺序，使发布不会以不完整的资产集暴露。仓库级 GitHub release 不可变性（如启用）是仓库设置，而不是此工作流配置的内容。确切的标签资产名见[架构特定制品](#architecture-specific-artifacts)。

## Upgrade, repair, and uninstall

MSI 使用主版本升级关系并拒绝降级。发布标签使用规范的 `vMAJOR.MINOR.PATCH`；二进制文件和 MSI ProductVersion 使用来自 `Directory.Build.props` 的相应 `MAJOR.MINOR.PATCH` 值。就地升级请使用相同的处理器架构。修复或升级会调和产品文件、服务注册、快捷方式、应用目录 ACL 和两条防火墙规则，而不会创建重复规则。

因为 `app-config.json`、它的备份和日志不是 Windows Installer 组件，它们会在修复、升级和卸载后保留。卸载会删除产品文件、快捷方式、添加/删除程序注册、`ProxiFyreService` 和两条 ProxiFyre 防火墙规则。它会保留用户配置、备份、日志、界面首选项、Visual C++ 运行时和 Windows Packet Filter。

这是第一个基于 MSI 的部署模型。较早的 ZIP/手动安装没有 Windows Installer 产品注册，无法参与自动主版本升级。在首次 MSI 安装前，请停止并删除手动注册的 `ProxiFyreService`，备份 `app-config.json`，并移除或迁移旧的解压程序文件。安装匹配的 MSI 后，通过提权进程或界面把配置恢复或复制到已安装的 ProxiFyre 目录。后续匹配架构的 MSI 版本可以使用正常的主版本升级路径。

## Building installers

构建先决条件是 Visual Studio 2022（带 C++ 桌面和 .NET Framework 工作负载）、NuGet、解决方案使用的 vcpkg 依赖、PowerShell 7，以及能够还原固定 WiX 包的 .NET SDK。首次 NuGet 还原需要互联网访问；除非提供已验证的本地来源，否则两个官方前置依赖安装程序也需要互联网访问。

在 Visual Studio 开发者命令行中：

```powershell
nuget restore .\ProxiFyre.Configuration\packages.config -PackagesDirectory .\packages -NonInteractive
nuget restore .\ProxiFyre\packages.config -PackagesDirectory .\packages -NonInteractive
nuget restore .\ProxiFyre.Tests\packages.config -PackagesDirectory .\packages -NonInteractive
msbuild .\ProxiFyreSetupBootstrapper\ProxiFyreSetupBootstrapper.vcxproj -t:Restore -v:minimal -p:RestoreLockedMode=true -p:RestoreConfigFile="$PWD\NuGet.Installer.Config"
msbuild .\ProxiFyreSetupEngineExtension\ProxiFyreSetupEngineExtension.vcxproj -t:Restore -v:minimal -p:RestoreLockedMode=true -p:RestoreConfigFile="$PWD\NuGet.Installer.Config"
dotnet restore .\ProxiFyre.Installer\ProxiFyre.Installer.wixproj --configfile .\NuGet.Installer.Config --locked-mode
dotnet restore .\ProxiFyre.Bundle\ProxiFyre.Bundle.wixproj --configfile .\NuGet.Installer.Config --locked-mode
msbuild .\socksify.sln -t:Rebuild -v:minimal -p:Configuration=Release -p:Platform=x64 -p:Version=2.6.4
vstest.console.exe .\bin\tests\x64\Release\ProxiFyre.Tests.dll /TestAdapterPath:.\packages\NUnit3TestAdapter.4.6.0\build\net462 /Platform:x64
pwsh -File .\scripts\Build-Installer.ps1 -Platform x64 -Version 2.6.4 -NoRestore
```

请把三个旧式 `packages.config` 还原与锁定的本地辅助程序还原和 WiX 还原分开。解决方案级的 NuGet CLI 还原不能为 SDK 风格安装程序项目保留已审查的还原边界。`NuGet.Installer.Config` 和每个包锁文件把两个本地 setup 辅助程序和两个 WiX 项目约束到已审查的包图；只有在上面的四个锁定还原都成功后，`-NoRestore` 才有效。

对 `x86` 和 `ARM64` 重复此过程。只在匹配的 Windows 主机上运行各架构测试；ARM64 负载可以交叉构建，但 ARM64 测试需要 Windows ARM64。要在打包期间复用之前下载的官方前置依赖：

```powershell
pwsh -File .\scripts\Build-Installer.ps1 `
  -Platform x64 `
  -Version 2.6.4 `
  -WindowsPacketFilterMsiPath C:\staging\Windows.Packet.Filter.3.6.2.1.x64.msi `
  -VisualCppRedistributablePath C:\staging\VC_redist.x64.exe `
  -NoRestore
```

脚本对这些本地文件应用相同的字节长度、SHA-256、SHA-512、版本（如适用）和上游签名检查。只用 `-Force` 替换所请求架构和版本的恰好四个输出。

### 验证未签名的发布制品

每个发布的 ZIP、MSI 和 setup 可执行文件都有 `.sha256` 副文件。`Build-Installer.ps1` 写入 MSI 和 setup 副文件，发布工作流写入 ZIP 副文件：

```powershell
$artifact = '.\ProxiFyre-2.6.4-win-x64-setup.exe'
$expected = ((Get-Content "$artifact.sha256" -Raw) -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -cne $expected) { throw "SHA-256 mismatch for $artifact" }
```

从同一位置下载的校验和能检测意外损坏，但不等同于发布者签名。要获得更强的来源证明，请通过独立可信渠道获取预期摘要，或对照可信的发布记录进行验证，再接受**未知发布者**提权提示。

## Installer lifecycle validation matrix

下文使用的覆盖标签：

- **Automated/static（自动化/静态）**：仓库测试或 MSI 数据库/包检查可以在不改变测试机的情况下验证编写内容。
- **Manual（手动）**：在带管理员权限的一次性 Windows VM 上执行。静态检查不能证明 Windows 已完成安装、修复、升级或删除。

| 场景 | 预期结果 | 覆盖方式 |
| --- | --- | --- |
| 在 Windows 7 RTM 上运行 setup | Setup 立即以可操作的 Windows 7 SP1/更新提示退出，并且不获取任何内容。 | 在一次性 VM 上手动；已编译的 WixStdBA 条件为自动化/静态。 |
| 在已完全更新、Internet 选项中 TLS 1.2 初始禁用的 Windows 7 SP1 上干净在线 setup | Setup 启用 TLS 1.2 并保留正常 HTTPS 重试预算。只有耗尽的 VC CDN `12029` 重试会对相同内容寻址的 Microsoft URL 做最后一次 HTTP 尝试，随后进行精确 SHA-512 验证；WPF 保持 HTTPS，并且 setup 在缓存或取消后逐字节恢复 `SecureProtocols`（包括缺失状态）。 | 在一次性 Windows 7 SP1 VM 上手动；辅助程序的来源覆盖作用域、错误/重试门、嵌入摘要、CPU 架构、导出项和静态运行时依赖为自动化/静态。 |
| WinINet LAN 标志格式错误为零、无代理/PAC/WPAD 意图且前置依赖缺失的在线 setup | 引擎扩展仅在 Burn 进程内应用 DIRECT；两个固定负载通过各自的平台传输下载并验证，而独立进程和 HKCU/HKLM Internet 设置保持逐字节不变。 | 手动故障注入 VM 测试；扩展嵌入、精确验证的 DLL 哈希、CPU 架构、导出项、仅系统依赖和独立来源安全守护为自动化/静态。 |
| 带显式代理、PAC URL、自动发现、机器范围代理策略、调制解调器/离线路由或任何非零 WinINet 连接标志的 setup | 引擎扩展记录 no-op，Burn 保留已配置的路由；它从不强制 DIRECT。 | 手动矩阵测试；所有保守门输入以及无注册表/全局 WinINet 写入均由来源自动守护。 |
| Windows 7 上的 setup 窗口品牌 | Setup 标题栏和任务栏使用规范 ProxiFyre 图标，而不是通用 WixStdBA 宿主图标。 | 在 Windows 7 上手动；主题引用和提取的图标身份为自动化/静态。 |
| 已安装所需 .NET Framework 和两个链式前置依赖的干净 setup | Burn 跳过 Visual C++ 和 WPF 包；ProxiFyre MSI 成功安装。 | 手动；前置依赖版本/搜索编写为自动化/静态。 |
| ProxiFyre Setup 在验证 Visual C++ 后链入 MSI | Burn 提供 `BURNMSIINSTALL=1`，bundle 提供其前置依赖标记；MSI 不重复不一致的 AppSearch 启动门，而所有其他启动条件保持活动。 | 手动；Burn 检测和双标记 MSI 旁路为自动化/静态。 |
| 缺少所需 .NET Framework 的 setup 或独立 MSI | Setup 可以完成任何所需的 Visual C++ 和 WPF 链工作，但 ProxiFyre MSI 随后以各架构特定的 .NET Framework 4.7.2/4.8.1 提示停止，并且不进行产品安装。Setup 从不下载 .NET Framework；请预置并重新运行。 | 在 x86/x64 和 ARM64 上手动；两个 MSI 启动条件为自动化/静态。 |
| 已安装全部依赖的干净独立 MSI | MSI 接受 .NET Framework、Visual C++ 和 WPF，无需网络获取。 | 手动；启动条件存在性为自动化/静态。 |
| 恰好 14.44.35211.0 运行时的独立 MSI | MSI 找到架构本地的 DLL，并接受精确的最低版本，尽管它们的语言元数据不同。 | 当 x64 构建主机装有固定运行时，真实 x86/x64 AppSearch 为自动化；ARM64 和 Windows 7 上手动。 |
| 没有 Visual C++ 运行时的干净 setup | Burn 下载并验证 14.44.35211.0，先安装它，然后经过 WPF 继续到 ProxiFyre。 | 手动；URL、来源大小/哈希/版本/签名、退出码映射和链顺序在构建时自动化/静态。 |
| 兼容的 Visual C++ 注册但缺少/过旧所需 DLL | Burn 不只信任注册；它在继续前以修复模式运行固定的可再发行组件。 | 手动故障注入测试；本地文件搜索和条件 `/repair` 选择为自动化/静态。 |
| Visual C++ 安装或修复需要重启 | Burn 在 WPF 和 ProxiFyre MSI 之前停止，报告 Windows 必须重启，重启后重新运行 setup 检测到已完成的运行时并继续。 | 在一次性 VM 中手动；停止链的 `3010` 映射为自动化/静态。 |
| 离线且未缓存时缺少 Visual C++ 运行时 | Burn 报告获取失败，不安装 ProxiFyre；用户可以手动安装固定且架构匹配的运行时并重试。 | 在隔离的离线 VM 中手动。 |
| Visual C++ 下载被修改 | Burn 拒绝负载摘要并在 ProxiFyre 安装前停止。 | 手动故障注入测试；构建时来源验证为自动化/静态。 |
| Visual C++ 安装失败或被取消 | 关键前置依赖中止链；不安装 ProxiFyre。 | 在一次性 VM 中手动。 |
| 没有兼容 Visual C++ 运行时的独立 MSI | MSI 以可操作的运行时提示退出，不进行产品安装。 | 手动；文件/版本搜索编写为自动化/静态。 |
| 没有 WPF 的干净 setup | Burn 下载并验证官方 3.6.2.1 包，安装它，然后安装 ProxiFyre。 | 手动；URL、来源大小/哈希/签名和链编写在构建时自动化/静态。 |
| 离线且未缓存时缺少 WPF | Burn 报告获取失败，不安装 ProxiFyre MSI；用户可以手动安装 WPF 并重试。 | 在隔离的离线 VM 中手动。 |
| WPF 下载被修改 | Burn 拒绝负载摘要并在 ProxiFyre 安装前停止。 | 手动故障注入测试；构建时来源摘要验证为自动化/静态。 |
| WPF 安装失败或被取消 | 关键前置依赖中止链；不安装 ProxiFyre。 | 在一次性 VM 中手动。 |
| 没有兼容 WPF 的独立 MSI | MSI 以可操作的前置依赖提示退出，不进行产品安装。 | 手动；启动条件编写为自动化/静态。 |
| Setup 架构与原生 Windows 不匹配 | Setup 在获取 WPF 或更改机器之前以可操作的架构提示退出。 | 在每个不匹配主机上手动；已编译的 WixStdBA 条件为自动化/静态。 |
| ProxiFyre 服务安装 | `ProxiFyreService` 以 LocalSystem、按需启动、依赖 `NDISRD` 存在，并初始保持停止。 | 服务表为自动化/静态；实际 SCM 状态为手动。 |
| ProxiFyre 服务启动 | 保存有效配置后，界面启动服务，SCM 按需启动 `NDISRD` 依赖。 | 运行时探针/状态逻辑有自动化测试；实际驱动程序/服务启动为手动。 |
| 界面启动 | 开始菜单和可选桌面快捷方式启动一个提权的 `ProxiFyreUI.exe` 实例。 | 快捷方式目标为自动化/静态；提权、CLR 启动、图标和单实例行为为手动。 |
| 防火墙规则创建 | 恰好一条 TCP-In 和一条 UDP-In 程序规则针对已安装的 `ProxiFyre.exe`，覆盖全部配置文件且无固定端口。 | 防火墙表/计数/目标为自动化/静态；Windows 防火墙状态为手动。 |
| 重复 setup 或 MSI 修复 | 产品修复成功，防火墙规则计数保持一条 TCP 加一条 UDP。 | 声明式组件所有权为静态；修复/幂等性为手动。 |
| 从较早的匹配架构 MSI 升级 | 新产品文件替换旧文件；服务和防火墙组件调和；拒绝降级。 | 主版本升级编写为静态；双版本升级为手动。 |
| 从 ZIP/手动发布过渡 | 先删除现有手动服务；MSI 安装后备份并恢复配置。不声称自动迁移。 | 仅手动。 |
| 修复/升级时的配置保留 | `app-config.json`、`.bak`、日志和界面首选项逐字节完整保留。 | 它们不在 MSI File 表中为自动化/静态；内容保留为手动。 |
| 产品卸载 | 删除产品文件、快捷方式、添加/删除程序条目、服务和 ProxiFyre 防火墙规则。 | 服务/防火墙删除编写为自动化/静态；实际删除为手动。 |
| 服务删除 | `ProxiFyreService` 被停止，Windows Installer 等待完成，服务注册不再存在。 | ServiceControl 停止/删除/等待编写为自动化/静态；实际 SCM 删除为手动。 |
| 防火墙规则删除 | 两条 MSI 拥有的 ProxiFyre 规则不存在，无关防火墙规则保持不动。 | 组件所有权为自动化/静态；实际 Windows 防火墙状态为手动。 |
| 卸载时的配置保留 | 活动配置、备份、日志和界面首选项保留。 | File 表排除为自动化/静态；文件系统结果为手动。 |
| 卸载后的共享前置依赖 | Visual C++ 运行时、`NDISRD` 和 WPF 包保持已安装并可被其他产品使用。 | 两个 Burn 包的 `Permanent=yes` 元数据为静态；实际保留的依赖为手动。 |

对于当前 Windows 版本上的手动 VM 检查，请记录安装程序日志并用以下命令验证状态：

```powershell
Get-CimInstance Win32_Service -Filter "Name='ProxiFyreService'" |
  Select-Object Name, StartMode, State, StartName, PathName

$rules = Get-NetFirewallRule `
  -DisplayName 'ProxiFyre (TCP-In)', 'ProxiFyre (UDP-In)'
$rules | Select-Object DisplayName, Direction, Action, Profile, Enabled
$rules | Get-NetFirewallApplicationFilter | Select-Object Program
```

默认的 Windows 7 PowerShell 环境不提供 NetSecurity cmdlet。请在那里使用操作系统工具（记录哈希时替换为实际安装目录）：

```text
sc.exe qc ProxiFyreService
sc.exe query ProxiFyreService
sc.exe query NDISRD
netsh.exe advfirewall firewall show rule name="ProxiFyre (TCP-In)" verbose
netsh.exe advfirewall firewall show rule name="ProxiFyre (UDP-In)" verbose
certutil.exe -hashfile "C:\Program Files\ProxiFyre\app-config.json" SHA256
certutil.exe -hashfile "C:\Program Files\ProxiFyre\app-config.json.bak" SHA256
```

在修复、升级和卸载前，请记录 `app-config.json` 和 `app-config.json.bak` 的 SHA-256 哈希并清点日志。每次操作后，比较这些值，统计两个确切的防火墙显示名，查询 `Get-Service NDISRD`，并检查架构匹配的 Visual C++ 运行时注册表键，确认两个共享前置依赖仍然保留。

## 当前限制

- ProxiFyre 第一方制品没有 Authenticode 发布者身份，可能触发 SmartScreen 或**未知发布者**提示。
- SHA-256 副文件提供有用的完整性检查，但需要可信来源提供预期摘要，才能提供发布者级的来源证明。
- Setup 从不安装 .NET Framework。请在 x86/x64 上预置 .NET Framework 4.7.2 或更高版本，在 ARM64 上预置 4.8.1 或更高版本，再运行 setup 或独立 MSI。
- 当兼容的 Visual C++ 或 Windows Packet Filter 包既未安装也未缓存时，在线引导程序需要网络访问。独立 MSI 和 ZIP 不获取这些依赖，ProxiFyre 也不发布离线前置依赖安装程序。
- 安装程序检测使用已注册的服务加驱动程序文件版本；确切的活动驱动程序 API 由 ProxiFyreUI 在服务生命周期操作前检查。
- 服务不会自动启动，因为用户必须先创建或验证 `app-config.json`。
- 预 MSI 的 ZIP/手动安装需要手动迁移；主版本升级适用于 MSI 安装的版本。
- 不支持跨架构安装和原地切换架构；请卸载旧架构并安装与原生 Windows 匹配的 setup。
- Windows 7 必须是 SP1，并应安装当前服务堆栈和 SHA-2 支持更新。Setup 会为 HTTPS 获取临时启用当前用户的 TLS 1.2 WinINet 位。只有确切的 Visual C++ 负载以 WinINet `12029` 耗尽正常 HTTPS 重试后，setup 才对相同的 Microsoft 内容寻址路径做一次最终 HTTP 尝试；WPF 和其他所有情况保持 HTTPS。Burn 仍要求精确的嵌入 SHA-512，但该回退没有传输机密性，可被观察或阻断。Windows 7 已停止支持，因此每次发布都需要对该旧式路径做显式的一次性 VM 检查。
- 完整的安装、修复、升级、防火墙、驱动程序和卸载行为每次发布和每种架构都需要一次性机器验证。
- Windows Packet Filter 的许可和分发条款仍是分发者的责任；本项目不主张再分发权利。
- Microsoft Visual C++ 运行时的再分发受发布者适用的 Visual Studio 许可证约束；本项目不独立授予该权利。
