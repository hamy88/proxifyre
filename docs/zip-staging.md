# 手动暂存 Release ZIP

ZIP 包含应用程序负载。请先安装架构匹配的 .NET Framework、Visual C++ 运行时和 Windows Packet Filter；参见[安装程序前置依赖](installer.md)。Setup 仍是推荐的部署方式。ZIP 暂存不会创建快捷方式、防火墙规则或服务注册。暂存完成后，打开 `ProxiFyreUI.exe`，配置引擎，并在需要时使用**安装服务**。

## 所有权与权限

Release 界面可以从直接挂载的固定本地卷上的非默认目录运行。启动器检查整个目录链（包括卷根目录）和所需的负载文件。它会拒绝重解析点、用户控制的路径、意外的可执行模块和被修改的固定配置文件。位于 `Program Files` 下的目录并不会自动受信任。

可接受的所有者是 `SYSTEM`（`S-1-5-18`）、内置的 **Administrators 组**（`S-1-5-32-544`）和 `TrustedInstaller`（`S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464`）。单个管理员账户的 SID 不是可接受的所有者。提升启动器不会更改文件所有权。`icacls /reset` 重置继承的权限；它不会设置所有者。

请使用与 MSI 相同的应用目录策略：Administrators 拥有目录；SYSTEM 和 Administrators 拥有完全控制；Users 拥有读取/执行访问权。该配方还添加一个可继承的 **OWNER RIGHTS** 读取控制 ACE。这会抑制所有者更改 DACL 的隐式权限，在新文件和子目录的初始所有者是解压账户时仍然保护它们。最后的所有权处理再把 Administrators 设置为所有内容的所有者。如果没有该 ACE，另一个以解压用户身份运行的非提权进程可以在所有权变更前给自己授予写入权限。文件和子目录必须同时满足权限和受信任所有者检查。父链不得授予其他身份删除、删除子项、通用写入/全部，或权限/所有权控制访问权。

请务必在解压**之前**创建一个新的受保护目标目录。不要把现有的用户所有解压目录移动到位，也不要在事后收紧其权限：另一个进程可能保留先前打开的修改句柄。不要递归重置权限或获取 `Program Files` 或驱动器根目录的所有权。如果父目录未通过验证，请让其管理员恢复正确的 Windows 或组织基线。

## 可复制的暂存流程

从同一[官方发布](https://github.com/wiresock/proxifyre/releases)下载架构匹配的应用程序 ZIP 及其 `.sha256` 副文件。从该副文件获取预期的 64 字符哈希，并在提权前验证它。保持归档完整；脚本会在以无写入/删除共享方式保持归档打开时再次检查哈希。

将以下内容保存为 `Stage-ProxiFyreZip.ps1`。以**管理员身份在 Windows PowerShell 5.1** 中运行它，使用 Windows 的原生架构。它使用 Windows 目录创建来原子地应用所有权和受保护的 ACL，并拒绝已存在的目标目录（包括并发创建的目标目录）。此配方不支持 PowerShell 7。

目标目录是系统 Program Files 文件夹下名为 `ProxiFyre-Portable` 的新直接子目录。要使用其他名称，请用 `-DestinationName ProxiFyre-Manual`。其他固定卷位置可以满足应用策略，但需要同样受保护的父链；此配方有意把暂存限制在 Program Files。

<!-- zip-staging-script -->
```powershell
#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$ExpectedSha256,
    [ValidatePattern('^ProxiFyre(?:-[A-Za-z0-9]+)*$')]
    [string]$DestinationName = 'ProxiFyre-Portable'
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Use Windows PowerShell 5.1, not PowerShell 7.'
}

function Assert-ProxiFyreStagingParent([IO.DirectoryInfo]$Directory) {
    if ([IO.DriveInfo]::new($Directory.Root.FullName).DriveType -ne 'Fixed') {
        throw 'The staging parent must be on a fixed local volume.'
    }
    $trusted = @('S-1-5-18', 'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    for ($ancestor = $Directory; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse point in parent chain: $($ancestor.FullName)"
        }
        $acl = Get-Acl -LiteralPath $ancestor.FullName
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) {
            throw "Untrusted parent owner: $($ancestor.FullName)"
        }
        $dangerousRights = 0x500D0040 # Generic all/write, delete/child, change ACL/owner.
        if ($ancestor.FullName -eq $Directory.FullName) {
            # Only trusted identities may create siblings of the destination.
            $dangerousRights = $dangerousRights -bor 0x4 # CreateDirectories
        }
        foreach ($rule in $acl.GetAccessRules($true, $true,
                [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and
                -not ($rule.PropagationFlags -band
                    [Security.AccessControl.PropagationFlags]::InheritOnly) -and
                $rule.IdentityReference.Value -notin $trusted -and
                ([long]$rule.FileSystemRights -band $dangerousRights)) {
                throw "Untrusted parent mutation permission: $($ancestor.FullName)"
            }
        }
    }
}

$parent = [IO.DirectoryInfo]::new([Environment]::GetFolderPath('ProgramFiles'))
$destination = Join-Path $parent.FullName $DestinationName
if (Test-Path -LiteralPath $destination) {
    throw "Destination already exists: $destination. Use a fresh directory."
}
Assert-ProxiFyreStagingParent $parent

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
public static class ProxiFyreStagingDirectory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);

    public static void CreateNew(string path, byte[] descriptor)
    {
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes {
                Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                Descriptor = pinned.AddrOfPinnedObject()
            };
            if (!CreateDirectoryW(path, ref attributes))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 183) // ERROR_ALREADY_EXISTS: do not adopt or delete it.
                    throw new IOException("Destination already exists: " + path);
                throw new Win32Exception(error);
            }
        }
        finally { pinned.Free(); }
    }
}
'@
$archiveStream = [IO.File]::Open((Resolve-Path -LiteralPath $ZipPath).Path,
    [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$archive = $null
$createdDestination = $false
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $actual = [BitConverter]::ToString($sha.ComputeHash($archiveStream)).Replace('-', '')
    if ($actual -ine $ExpectedSha256) { throw 'ZIP SHA-256 mismatch.' }
    $archiveStream.Position = 0
    $archive = [IO.Compression.ZipArchive]::new($archiveStream,
        [IO.Compression.ZipArchiveMode]::Read, $true)
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetSecurityDescriptorSddlForm(
        'O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)(A;OICI;RC;;;OW)')
    [ProxiFyreStagingDirectory]::CreateNew($destination,
        $security.GetSecurityDescriptorBinaryForm())
    $createdDestination = $true
    [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $destination)

    # Do not set the already trusted root owner again: Windows removes its
    # explicit OWNER RIGHTS ACE on ownership assignment. Keep it inheritable
    # throughout this pass so children remain protected until individually owned.
    & (Join-Path ([Environment]::GetFolderPath('System')) 'icacls.exe') `
        (Join-Path $destination '*') /setowner '*S-1-5-32-544' /T /Q
    if ($LASTEXITCODE -ne 0) { throw 'Setting payload ownership failed.' }
    Write-Output "Staged to $destination. Launch ProxiFyreUI.exe from this directory."
}
catch {
    $failure = $_
    if ($createdDestination) {
        try {
            $resolved = (Resolve-Path -LiteralPath $destination).Path
            if ((Split-Path -Parent $resolved) -ne $parent.FullName -or
                (Split-Path -Leaf $resolved) -ne $DestinationName -or
                ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Unexpected cleanup path: $resolved"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
        catch { Write-Warning "Could not remove partial staging directory: $destination. $($_.Exception.Message)" }
    }
    throw $failure
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    $sha.Dispose()
    $archiveStream.Dispose()
}
```

例如，替换发布版本、归档路径和已发布哈希后：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Stage-ProxiFyreZip.ps1 `
    -ZipPath 'C:\Downloads\ProxiFyre-v<version>-x64.zip' `
    -ExpectedSha256 '<64-character hash from the official .sha256 sidecar>'
```

该示例只为该进程设置执行策略，这样本地审查过的脚本就能在默认策略为 Restricted 的客户端上运行。组织组策略仍可能阻止执行；这种情况下请遵循管理员的策略。

如果解压或所有权分配失败，脚本会尝试只删除它创建的新目标目录。如果清理也失败，它会报告剩余路径；不要启动那个不完整的负载。重试前请检查失败原因。脚本不会更改任何父 ACL、替换现有安装或修复现有解压。

## 检查结果

用 `Get-Acl` 检查目录和文件所有者，用 `icacls` 检查权限。打开暂存后的 `ProxiFyreUI.exe`；Release 启动器和服务控件仍会执行完整的完整性和安全检查。保留随附的 `ProxiFyreUI.exe.config`、`ProxiFyre.exe.config` 和 `NLog.config` 字节。通过 `app-config.json` 或界面编辑路由和日志详细程度。

如果启动器报告 **owner SID ... is not a trusted machine identity**，请用被拒绝的确切路径区分负载所有权问题和父链问题。如果它报告不受信任的允许 ACE，请检查其受托者和被禁止的权利。仅更改所有者并不能修复不安全的 ACL。其他负载失败请参阅 [GUI 排错指南](gui.md#troubleshooting)。

要升级手动安装，请先停止其引擎/服务，备份活动配置，再单独暂存新版本。不要覆盖正在运行的负载。注册到旧目录的服务必须删除并从新目录重新注册；复制文件不会更新其映像路径。[迁移到 MSI](installer.md#upgrade-repair-and-uninstall)有单独的说明。

## 流程验证

维护者可以在构建 Release x64 后，从提权的单个管理员账户运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-ZipStaging.ps1`；该测试不支持 SYSTEM。它在全新的 Windows PowerShell 5.1 进程中，对根据已构建负载组装的 ZIP 执行上面这个精确受防护的脚本。它检查受信任所有权和现有的托管服务/界面完整性策略，拒绝用户所有的文件和用户可写的目录，并验证错误的校验和或已存在的目标目录不会覆盖文件。重复条目归档测试验证解压失败后的清理。确定性的并发创建用例验证另一个运行的目录即使其 ACL 与预期描述符匹配也会被保留。该测试在创建其夹具之前验证祖先，并在写入任何可执行文件之前拒绝不安全的夹具父目录。

惰性文件探针会创建带暂存目录继承 DACL 的用户所有文件和目录，然后在模拟同一用户、禁用 Administrators 并移除特权的情况下，尝试实际内容写入、子项创建和 DACL 更改。这在可信所有权被分配之前确定性地检查保护；它不尝试并发解压竞争。该测试使用 Program Files 下的临时专用目录，只删除这些测试目录，并且不注册/启动服务或安装驱动程序。原生界面启动和前置依赖安装仍是手动集成检查。
