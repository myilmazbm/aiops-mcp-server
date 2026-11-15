<#
.SYNOPSIS
    Registers the AIOpsReader JEA endpoint on a fleet server.
    Runs as SYSTEM from any delivery mechanism (SCCM baseline/package, GPO startup
    script, DSC); idempotent, safe to re-run. Windows PowerShell 5.1 compatible.

.PARAMETER Principal
    The identity allowed to connect, e.g. 'CONTOSO\gMSA-AIOps$' or an AD group.

.PARAMETER EnsureWinRm
    Also make sure the WinRM service is Automatic/Running with an HTTP listener (what a
    WinRM GPO would otherwise provide). Firewall is NOT touched: in production it is
    managed at the network layer (firewall appliances), never via Windows Firewall.

.EXAMPLE
    .\Register-AIOpsReader.ps1 -Principal 'CONTOSO\gMSA-AIOps$' -EnsureWinRm
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Principal,

    [switch]$EnsureWinRm,

    # Domain controllers are out of scope by policy: a JEA virtual account on a DC runs
    # with domain-controller privileges. Registration is refused unless explicitly allowed.
    [switch]$AllowDomainController
)

$ErrorActionPreference = 'Stop'

# ProductType 2 = domain controller
if (-not $AllowDomainController -and (Get-CimInstance Win32_OperatingSystem).ProductType -eq 2) {
    Write-Warning "This host is a domain controller; AIOpsReader is not registered on DCs (pass -AllowDomainController to override)."
    return
}

$endpointName  = 'AIOpsReader'
$moduleName    = 'AIOpsJea'
$modulePath    = Join-Path $env:ProgramFiles "WindowsPowerShell\Modules\$moduleName"
$transcriptDir = 'C:\ProgramData\AIOps\Transcripts'
$scriptRoot    = Split-Path -Parent $MyInvocation.MyCommand.Path

# 0. WinRM service + HTTP listener (what a WinRM GPO would otherwise deliver).
#    Never Enable-PSRemoting / Set-WSManQuickConfig / winrm quickconfig here: those create
#    Windows Firewall rules (and -SkipNetworkProfileCheck adds the Public-profile rule).
#    Firewall is managed at the network layer (appliances) and is not this script's business.
#    Creating the listener directly on the WSMan: drive touches no firewall rule. The default
#    session configurations are deliberately not registered either; only AIOpsReader is needed.
if ($EnsureWinRm) {
    Set-Service -Name WinRM -StartupType Automatic
    if ((Get-Service WinRM).Status -ne 'Running') { Start-Service WinRM }
    if (-not (Get-ChildItem WSMan:\localhost\Listener -ErrorAction SilentlyContinue | Where-Object { $_.Keys -contains 'Transport=HTTP' })) {
        New-Item -Path WSMan:\localhost\Listener -Transport HTTP -Address * -Force | Out-Null
    }
}

# 1. Transcript directory (mandatory for the session configuration).
#    The ACL is set explicitly instead of inheriting from C:\ProgramData, whose default DACL
#    grants BUILTIN\Users read access. JEA transcripts contain the full output of every
#    command (event records, certificate data, inventory), so inheriting that would hand any
#    local user on the server the data the MCP layer takes care to truncate.
#    Result: D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA) - SYSTEM and Administrators only,
#    inheritance disabled, nothing for Users. The session itself runs under a virtual account,
#    which is a member of the local Administrators group, so it can still write transcripts.
#    Idempotent: the DACL is rewritten from scratch on every run (SCCM re-evaluates).
if (-not (Test-Path $transcriptDir)) {
    New-Item -ItemType Directory -Path $transcriptDir -Force | Out-Null
}

$transcriptAcl = New-Object System.Security.AccessControl.DirectorySecurity
$transcriptAcl.SetAccessRuleProtection($true, $false)   # disable inheritance, drop inherited ACEs
$aclInherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
# Well-known SIDs, not names: the fleet has localized (Turkish) Windows installations.
foreach ($wellKnownSid in @('S-1-5-18', 'S-1-5-32-544')) {      # Local System, BUILTIN\Administrators
    $transcriptAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        (New-Object System.Security.Principal.SecurityIdentifier($wellKnownSid)),
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        $aclInherit,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)))
}
(Get-Item -LiteralPath $transcriptDir -Force).SetAccessControl($transcriptAcl)

# 2. Role capability must live inside a module's RoleCapabilities folder
$roleCapDir = Join-Path $modulePath 'RoleCapabilities'
if (-not (Test-Path $roleCapDir)) {
    New-Item -ItemType Directory -Path $roleCapDir -Force | Out-Null
}
Copy-Item -Path (Join-Path $scriptRoot 'AIOpsReader.psrc') -Destination (Join-Path $roleCapDir 'AIOpsReader.psrc') -Force
New-ModuleManifest -Path (Join-Path $modulePath "$moduleName.psd1") -ModuleVersion '1.0.0' -Description 'AIOps JEA role capabilities'

# 3. Materialize the session configuration with the real principal.
#    The generated file is written into the module directory under Program Files, not %TEMP%:
#    as SYSTEM, %TEMP% is C:\Windows\Temp, where any local user may create files. A user who
#    pre-creates AIOpsReader.pssc there stays its owner after it is overwritten and could swap
#    the content between validation and registration - and a session configuration decides who
#    may connect and whether the session runs as a local administrator. Program Files is
#    writable by SYSTEM/Administrators only, and the role capability already lives there.
$psscTemplate = Get-Content -Path (Join-Path $scriptRoot 'AIOpsReader.pssc') -Raw
$psscContent  = $psscTemplate -replace '__AIOPS_PRINCIPAL__', $Principal
$psscPath     = Join-Path $modulePath 'AIOpsReader.generated.pssc'
if (Test-Path -LiteralPath $psscPath) { Remove-Item -LiteralPath $psscPath -Force }

try {
    Set-Content -Path $psscPath -Value $psscContent -Encoding UTF8

    if (-not (Test-PSSessionConfigurationFile -Path $psscPath)) {
        throw "Generated session configuration file is invalid: $psscPath"
    }

    # 4. Register (replace an existing registration to pick up config changes).
    #    Both calls restart WinRM: every remote session on this host drops at that moment,
    #    including the one this script may have been started from (see Install-JeaEndpoint.ps1).
    if (Get-PSSessionConfiguration -Name $endpointName -ErrorAction SilentlyContinue) {
        Unregister-PSSessionConfiguration -Name $endpointName -Force
    }
    Register-PSSessionConfiguration -Name $endpointName -Path $psscPath -Force | Out-Null
} finally {
    # Registration keeps its own copy; the generated file is not needed afterwards.
    Remove-Item -LiteralPath $psscPath -Force -ErrorAction SilentlyContinue
}

Write-Output "JEA endpoint '$endpointName' registered for principal '$Principal'."
