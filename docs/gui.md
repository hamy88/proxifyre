# ProxiFyre 图形界面

`ProxiFyreUI.exe` 是 ProxiFyre 托管 Windows Forms 应用程序的本地宿主。两者共同编辑引擎配置、控制 `ProxiFyreService`，并跟随引擎日志文件。界面有意不充当数据包处理宿主：`ProxiFyre.exe` 和现有的 `socksify.dll` C++/CLI 组件仍是网络引擎。

## 架构与信任边界

托管依赖关系图有意保持单向：

```text
ProxiFyre.exe --------------> ProxiFyre.Configuration.dll
      |
      +---------------------> socksify.dll

ProxiFyreUI.exe (native integrity/CLR host)
      |
      +---------------------> ProxiFyreUI.Managed.dll
                                      |
                                      +--> ProxiFyre.Configuration.dll
                                      +--> Newtonsoft.Json.dll
```

`ProxiFyre.Configuration` 包含 JSON 模型、向前兼容的序列化、规范化、结构化验证、指纹和原子文件操作。它没有本地依赖。本地 `ProxiFyreUI.exe` 宿主会验证并租用整条未签名的托管界面链，并在启动 .NET Framework 之前核对其精确的 CLR 配置哈希；这可以防止可变的 CLR 配置在托管完整性检查之前运行代码。`ProxiFyreUI.Managed.dll` 从不引用或加载 `socksify.dll`；它通过 Windows 服务控制管理器控制已安装的服务，并且只使用固定的 `install` 或 `uninstall` 参数调用已解析的 `ProxiFyre.exe`。

在完成负载验证之后、构造第一个控件之前，托管界面会确认 .NET Framework WinForms 能够解析其正常的默认字体。如果旧式 GDI+ 通用字体路径失败，它会尝试已安装的具名 Windows 界面字体，然后请求 GDI 将当前 Windows 界面字体实现为 TrueType，把它选入本地设备上下文，并直接根据该上下文创建托管字体。这样就避开了 GDI+ 不可用的通用族和 `LOGFONT` 重建路径，而这些路径在创建 `DataGridView` 等控件之前会被用到。设计器排版也采用受保护的族构造方式：健康的系统保留 Segoe UI/Consolas 样式，而损坏的 GDI+ 族查找会回退到恢复出的进程字体。

界面在启动时请求管理员权限。管理 Windows 服务和更新位于受保护目录中已安装引擎旁边的配置都需要该权限。ProxiFyre 的第一方模块和安装程序有意不签名，因此可能显示**未知发布者**。在加载 CLR 之前，静态链接的本地宿主会移除 CLR 控制环境覆盖项，并从本地 DLL 查找中移除工作目录。在 Windows 8 及更高版本上，它还会把默认的本地 DLL 搜索路径限制为 Windows 系统目录。Windows 7 有意保留标准的应用程序/System32 顺序，因为该操作系统的 GDI+ 字体初始化在 `SetDefaultDllDirectories` 之后会损坏；对安全敏感的系统模块仍会通过绝对 System32 路径加载。宿主还会拒绝意外的应用本地可执行模块，核对精确的 CLR 配置哈希，并以不共享写入/删除权限的方式保持打开所需的托管文件以及每个正常目录组件。在 Release 构建中，本地宿主还要求其自身文件、每个托管负载文件以及完整的目录链位于直接挂载的固定卷上，由 `SYSTEM`、`Administrators` 或 `TrustedInstaller` 所有，并且其 ACL 不授予标准用户修改权限。负载目录上的可继承 `CREATOR OWNER` 条目（例如每个新建的 Program Files 子目录都会继承的那种）会被接受：它只授予新创建项的所有者所有权已隐含的权利，并且每个负载文件仍必须有一个受信任的所有者。当目录还带有可继承的 `OWNER RIGHTS` 条目时会被拒绝，因为该条目刻意移除了新项所有者的隐式权利；只作用于目录本身的 `OWNER RIGHTS` 条目不会改变这一点。生产服务操作要求引擎文件名和版本资源身份，核对固定的引擎/日志配置哈希，并在进程或 SCM 启动期间保持完整的应用本地负载和路径。Release 服务的安装、启动、重启和卸载应用相同的固定卷、所有者、ACL、重解析点、加载器重定向和可执行模块限制。WiX MSI 会创建该受保护的每机器目录；界面有意拒绝就地改写用户控制的目录，因为攻击者可能在 ACL 变更后保留预先存在的写入或权限控制句柄。Debug 构建保留显式的开发者目录策略。第三方依赖可以保留其上游发布者的签名，但 ProxiFyre 不要求也不替换它们。SOCKS5 凭据仍保留在现有的明文 JSON 架构中，因为引擎必须能够读取它们。界面会遮蔽密码，并将它们从日志、工具提示和诊断信息中省略。

主要服务控件包含显式的安装和卸载操作。MSI 通常以按需/手动启动方式安装 `ProxiFyreService`；界面可以从受保护的已安装负载恢复缺失的注册。**卸载服务…**需要确认，会先停止正在运行的服务，并且只删除 Windows 服务注册；它不会卸载 MSI、快捷方式、防火墙规则或 Windows Packet Filter。`app-config.json`、它的备份和引擎日志会被保留。如果另一个服务管理句柄延迟了删除，界面会报告 Windows 的“待删除”状态，而不是把成功的卸载请求当作失败。同样的仅服务操作仍可在**设置 / 诊断**下使用。要进行完整的产品卸载，请使用 Windows 应用/已安装应用。

## 引擎与配置发现

界面按以下顺序解析引擎：

1. 已安装 `ProxiFyreService` 的注册映像路径。
2. 位于 `ProxiFyreUI.exe` 旁边的 `ProxiFyre.exe`。
3. 保存在 `%LocalAppData%\ProxiFyreUI\ui-settings.json` 中的最近一次显式选择的引擎。
4. 用户选择的文件。

带引号的服务映像路径和固定的尾随服务参数会在不执行已注册命令行的情况下被解析。相对注册和包含空白且不带引号的可执行文件路径会被拒绝，因为界面和 Windows 服务管理器可能把它们解析为不同的可执行文件。所选文件必须命名为 `ProxiFyre.exe`。引擎、配置和日志路径显示在诊断选项卡上。

活动配置始终是已解析引擎旁边的 `app-config.json`。仅界面的首选项永远不会写入该文件。如果活动文件缺失，界面可以从引擎或界面旁边的 `app-config.sample.json` 初始化它；否则它从一个空白模型开始，该模型必须获得一个有效的代理规则，服务才能启动。

## 编辑规则

路由规则自上而下求值，第一个匹配项生效。使用**上移**和**下移**来更改序列化顺序。专用的兜底选项显示为**所有未匹配的应用程序**，并被序列化为 `appNames` 中的一个显式空字符串。请把它放在最后：靠前的兜底规则会遮蔽其后的每条规则，验证器会立即报告这种情况。

不含斜杠的应用程序条目匹配可执行文件名。含有斜杠或反斜杠的条目匹配完整路径。排除项与路由条目分开，优先于兜底路由，并有意识地使用宽松的进程名或路径子串匹配。必要时排除另一个 VPN 或隧道的承载进程，以避免递归路由。

每条新编辑的规则都要求：

- 一个主机名或 IPv4 上游，加上 1 到 65535 之间的端口；
- 用户名和密码两者同时提供或同时不提供；
- TCP 或 UDP 至少其一；
- IPv4 或 IPv6 至少其一；
- 规范传输为普通 TCP 或 TLS。

当前引擎通过 IPv4 连接其上游 SOCKS5 服务器，因此会拒绝 IPv6 字面量上游。目标流量仍可使用 IPv4、IPv6 或两者。

SOCKS5-over-TLS 保护 TCP 控制连接（包括 UDP `ASSOCIATE` 协商）；这并不意味着 SOCKS5 UDP 中继负载被包裹在该 TLS 流中。`tlsServerName` 默认为端点主机。证书指纹通过移除空白、冒号和连字符进行规范化，并且必须恰好包含 64 个十六进制字符。固定的叶证书可以认证自签名实验端点。**允许无效证书**会禁用正常的证书验证，要求显式确认，在没有固定证书时尤其危险。

## 保存、应用、冲突与恢复

**保存**会验证完整模型，并在存在错误时拒绝写入。它在配置目录中写入一个临时文件，刷新它，将先前的活动文件保留为 `app-config.json.bak`，然后原子地替换活动文件。如果服务正在运行，界面会报告仍需要重启。

**应用并重启**执行相同的已验证原子保存，然后停止并启动已安装的服务，同时等待实际的 SCM 状态。只有当 SCM 报告 `Running` 时才报告成功。**引擎日志级别**设置是 `app-config.json` 的一部分，因此更改服务的日志详细程度需要**应用并重启**（或之后手动重启服务）。在更改服务状态之前，界面会以不请求读/写访问的方式打开 `\\.\NDISRD`，并查询 `IOCTL_NDISRD_GET_VERSION`。它接受 API 主版本 `3` 和 API 次版本 `0x0601` 或更高；已安装但停止的服务可由 SCM 通过声明的依赖启动。如果 Windows Packet Filter 缺失或活动驱动程序不兼容，已保存的配置保持待处理状态，界面会立即报告该依赖，而不会停止服务或提供配置回滚。其他启动失败仍可能提供恢复备份，并使用恢复后的配置执行至多一次受控重启。

工作区在加载文件时对其进行指纹记录。如果在保存前有其他进程更改了它，界面会提供重新加载、显式覆盖或取消选项。它从不静默覆盖外部修改过的配置。

## Logs and notification area

引擎日志存储在 `ProxiFyre.exe` 旁边的 `logs` 目录中。活动文件是 `logfile.txt`。当达到 50 MiB（52,428,800 字节）时，NLog 将其轮转到 `logs/archive`，保留三个存档，名称从 `logfile_0.txt`（最新）到 `logfile_2.txt`（最旧）。活动文件加存档通常最多占用约 200 MiB；大小阈值不是严格的磁盘配额，单个异常大的日志事件可能超过它。`Info` 记录服务生命周期和配置摘要；`Debug` 还记录每个连接的路由、代理协商和中继细节。

旧版本写入 `logfile_yyyy-MM-dd.txt` 文件。这些文件在升级期间被保留，不属于新的保留策略。在停止引擎后，请把想保留的历史日志移出 `logs` 目录，并在需要回收空间时删除剩余的旧日期文件。之后重新启动引擎。应用程序不会自动删除这些历史诊断文件。

日志选项卡以读/写/删除共享的方式读取有界尾部，增量跟随追加的内容，并在轮转替换活动文件、创建、截断或临时锁定后恢复。轮转会将显示视图重置为新的活动文件。旧文件被存档时尚未读取的行不会重放；需要历史时请检查保留的存档。子目录中的存档不会被选为活动日志。**打开日志文件**会启动当前正在跟随的文件，或在跟随暂停时启动最新的顶层 `.log`/`.txt` 文件。日志选项卡的**视图筛选（仅显示）**只改变界面显示哪些已写入的行；它不会改变引擎的日志详细程度或配置，也不需要重启。跟随/暂停、文本筛选、复制、重新加载、清除视图和打开文件夹操作只影响查看器；清除视图不会删除日志文件。

Release 界面会根据固定的完整性哈希核验随附的 `NLog.config` 字节。不要编辑该文件来调整保留策略；被修改的文件会使引擎验证失败。请用**引擎日志级别**和**应用并重启**来更改日志详细程度。界面不暴露保留控制项。

关闭窗口会将其隐藏到通知区域。托盘菜单反映真实的服务状态，并提供打开、启动、停止、重启、日志和退出操作。在每个 Windows 身份和终端会话内，ProxiFyre 允许一个界面实例；再次启动它会恢复并激活现有窗口，包括托盘隐藏的窗口。退出界面不会停止引擎服务。未保存的编辑会在重新加载、引擎更改或应用程序退出之前进行检查。

## Build and test

先决条件是 Visual Studio 2022（带 .NET Framework 和 C++ 桌面工作负载）、NuGet、vcpkg、仓库的 `ms-gsl` 和 `boost-pool` 三元组、PowerShell 7，以及用于固定 WiX Toolset 6.0.2 包的 .NET SDK。

在 Visual Studio 开发者命令行中：

```powershell
nuget restore ProxiFyre.Configuration\packages.config -PackagesDirectory packages -NonInteractive
nuget restore ProxiFyre\packages.config -PackagesDirectory packages -NonInteractive
nuget restore ProxiFyre.Tests\packages.config -PackagesDirectory packages -NonInteractive

msbuild ProxiFyreSetupBootstrapper\ProxiFyreSetupBootstrapper.vcxproj -t:Restore -v:minimal -p:RestoreLockedMode=true -p:RestoreConfigFile="$PWD\NuGet.Installer.Config"
msbuild ProxiFyreSetupEngineExtension\ProxiFyreSetupEngineExtension.vcxproj -t:Restore -v:minimal -p:RestoreLockedMode=true -p:RestoreConfigFile="$PWD\NuGet.Installer.Config"
dotnet restore ProxiFyre.Installer\ProxiFyre.Installer.wixproj --configfile NuGet.Installer.Config --locked-mode
dotnet restore ProxiFyre.Bundle\ProxiFyre.Bundle.wixproj --configfile NuGet.Installer.Config --locked-mode

msbuild socksify.sln -t:Rebuild -v:minimal -p:Configuration=Release -p:Platform=x64 -p:Version=2.6.4
vstest.console.exe bin\tests\x64\Release\ProxiFyre.Tests.dll /TestAdapterPath:packages\NUnit3TestAdapter.4.6.0\build\net462 /Platform:x64
pwsh -File scripts\Build-Installer.ps1 -Platform x64 -Version 2.6.4 -NoRestore
```

旧式 `packages.config` 项目、两个本地设置辅助程序和两个 WiX 项目需要分别还原，以便它们的包管理模型和锁定的安装程序依赖图互不干扰。`Directory.Build.props` 是仓库版本来源；上面显式的 `2.6.4` 参数让发布验证无歧义，并且必须与之匹配。在匹配主机上测试时，把 `x64` 替换为 `x86`。安装 Visual Studio 工具链后可以交叉构建 ARM64 负载，但其测试必须在 Windows ARM64 上运行，而不是在 x64 CI 主机上运行。各架构运行时文件（包括 `ProxiFyre.exe`、本地 `ProxiFyreUI.exe` 宿主、`ProxiFyreUI.Managed.dll`、`socksify.dll`、`ProxiFyre.Configuration.dll`、托管依赖项和示例配置）放在：

```text
bin\exe\<architecture>\Release\
```

该 Release 目录为安装程序构建和发布 ZIP 创建提供输入；它不是开发者启动目录。ZIP 只包含应用程序负载。它不是离线安装程序，不会安装或检测 Visual C++ 运行时、Windows Packet Filter、服务注册、快捷方式或防火墙规则。常规分发使用架构匹配的在线 `*-setup.exe`，它会先下载并验证缺失的前置依赖，再安装内嵌的 MSI。

由于第一方负载未签名，提权的 Release 界面会拒绝从用户可写的解压目录（例如 Downloads、`%TEMP%` 或仓库构建树）加载托管代码。请遵循[受保护的 ZIP 暂存流程](zip-staging.md)了解确切的所有权和 ACL 要求以及可复制的 Windows PowerShell 脚本。先创建并保护一个新的目标目录，再向其中解压；不要先解压到用户可写的目录再收紧其 ACL，因为现有的修改句柄无法被撤销。普通开发者树测试请使用 Debug 构建。

测试写入 `bin\tests\<architecture>\<configuration>\`，不需要数据包筛选驱动程序、已安装的服务、管理员权限或可用的 SOCKS5 端点。

安装程序构建把未签名的独立 MSI、未签名的在线 Burn 安装程序和 SHA-256 副文件写入 `bin\installer\<architecture>\Release\`。CI 会把 `bin\exe\<architecture>\Release\` 单独打包为应用程序 ZIP，并发布全部三种制品类型及校验和。常规安装请使用 setup；仅当前置依赖已就绪时，才将 MSI 用于托管或离线部署；ZIP 仅用于受保护的手动暂存或负载检查。确切的负载、固定官方 Visual C++ 和 Windows Packet Filter 获取流程、独立 MSI 要求、防火墙规则、升级/卸载语义、构建选项和生命周期验证矩阵见 [installer.md](installer.md)。

## Troubleshooting

- **服务未安装：**对于 MSI 安装，请运行产品修复以调和该服务及所有其他组件。界面的**安装服务**操作只能从已解析的受保护引擎负载恢复服务注册。
- **访问被拒绝：**重启界面并批准管理员提示。如果引擎安装在正常应用程序目录之外，请检查目录 ACL。
- **ZIP 因所有者 SID 被拒绝：**单个管理员账户并不是内置的 Administrators 组。`icacls /reset` 不会更改所有权。请遵循 [ZIP 暂存流程](zip-staging.md)；它会在不削弱启动器检查的前提下设置目录和已解压文件的所有权。
- **界面负载或路径被拒绝：**当 ACL 检查导致拒绝时，请查看启动器的**验证详情**，了解被拒绝的确切路径、受托者 SID、ACE 掩码、被禁止的权利以及该 ACE 是否被继承。启动器无法评估的 DACL 条目（报告为不受支持、格式错误或不可读的 ACE）会被有意拒绝，因为它仍可能授予访问权；请将其移除或用官方 setup 重建目录。标准用户对任何安装路径祖先（包括 `Program Files`）拥有通用全部/写入、删除/删除子项，或权限/所有者控制访问权，都会允许在提权前替换路径，并且不得绕过。请让系统管理员从正确的操作系统或组织 ACL 基线恢复该 Windows 目录；不要跨 `Program Files` 递归重置或复制权限。
- **服务位置不受保护：**引擎和界面会指出被拒绝的路径、受托者，以及违规条目是继承的还是仅适用于新项。服务策略无法评估的条目（例如 DACL 中的审核、警报、复合或强制标签条目）会出于同样原因被有意拒绝；请将其移除或用官方 setup 重建目录。如果预 MSI 服务安装在受保护父目录之外，请停止并删除其注册，备份 `app-config.json`，然后退出界面。通过 ProxiFyre WiX setup 安装匹配的架构，让 Windows Installer 创建受保护的 Program Files 目录，再通过提权进程或界面恢复配置。已安装的注册优先于浏览，因此仅仅浏览到新副本并不会迁移现有服务。
- **未知发布者：**这对 ProxiFyre 有意不签名的第一方安装程序和二进制文件来说是预期的。请在提权前通过可信来源验证发布的 SHA-256。不要把从同一不可信下载获取的校验和与发布者身份验证混为一谈。
- **Windows 7 界面启动失败：**请使用当前的 setup 构建，并确认 Windows 7 SP1 已安装当前的服务堆栈、SHA-2 和 .NET Framework 4.7.2 更新。构建在 Windows 7 上不得在 WinForms 启动前调用 `SetDefaultDllDirectories`，因为操作系统 GDI+ 缺陷随后会导致即使已安装的字体也无法构造。本地宿主使用上述兼容的 DLL 搜索策略，托管界面保留其具名/GDI 字体恢复，以应对字体安装被独立损坏的情况。
- **配置无效：**打开验证详情。界面不会保存或启动新的无效规则集。
- **已保存，需要重启：**完成相关编辑后，选择**应用并重启**或重启服务。
- **Windows Packet Filter 不可用或不兼容：**在线重新运行架构匹配的 setup，让 Burn 获取固定的官方 3.6.2.1 包；或从[官方发布](https://github.com/wiresock/ndisapi/releases)手动安装匹配的包并重试。如系统要求则重启 Windows。界面会在安装/启动/重启前检查活动 `NDISRD` API，不会把驱动程序失败当作配置失败。
- **防火墙规则缺失或重复：**修复 MSI，并核对准确的 `ProxiFyre (TCP-In)` 和 `ProxiFyre (UDP-In)` 程序规则。不要禁用 Windows 防火墙或添加全局端口规则。
- **引擎启动失败：**服务可能拒绝了配置，或遇到其他本地初始化错误。启动期间回到 `Stopped` 状态的服务会立即报告；请查看日志选项卡上最近的行。
- **尚无日志文件：**在引擎创建其 `logs` 目录后启动服务或使用**重新加载**。跟随器会继续检查创建和轮转；只要出现 `.log` 或 `.txt` 文件，**打开日志文件**就有用了。
- **检测到外部更改：**重新加载以接受磁盘上的文件；仅当知道另一处编辑已过时才能覆盖；或先取消并比较两个版本。
- **与其他 VPN 递归超时：**把另一个 VPN 的承载/隧道进程加入排除项。

## GUI 手动发布验证

此清单涵盖界面。[安装程序生命周期矩阵](installer.md#installer-lifecycle-validation-matrix)另行涵盖干净的前置依赖获取、离线/失败路径、MSI 修复、防火墙幂等性、升级、配置保留和卸载。

在装有合适驱动程序和具备管理员权限的 Windows 测试机上：

1. 构建并测试 Release x64，然后在安装工具链后构建 x86 和 ARM64。
2. 用 setup 安装 Release 负载，或将完整 Release ZIP 暂存到上文所述受保护的固定卷位置。启动 `ProxiFyreUI.exe`，解析一个位于别处的受保护引擎，并加载示例配置。
3. 添加、编辑、复制、删除和重排规则。确认兜底规则保存为 `"appNames": [""]`。
4. 确认 TLS 字段和有意添加的未知 JSON 字段在加载/保存后仍然保留。
5. 在外部修改活动 JSON，并确认保存会呈现冲突选项。
6. 验证服务未安装状态，然后在安全的情况下安装/启动/停止/重启服务。
7. 确认无效配置会阻止应用，并且引擎启动失败绝不会显示为正在运行。
8. 跟随日志经历服务重启、追加、截断和按大小轮转，然后从日志工具栏打开活动文件。确认轮转会将视图重置为新的活动文件，并且存档历史仍可在存档目录中访问。确认更改日志选项卡的**视图筛选（仅显示）**只改变显示的行，而更改**引擎日志级别**在服务重启后生效。
9. 复制诊断信息，并确认不会出现任何密码或包含完整凭据的配置。
10. 将界面隐藏到通知区域，再次启动 `ProxiFyreUI.exe`，并确认恢复的是现有窗口，而没有创建第二个托盘图标。
11. 退出界面并确认服务继续运行。
12. 在 Windows 7 SP1 x64 上启动已安装的界面，确认主窗口在没有托管启动失败的情况下构造完成，然后再重复基本服务状态检查。
