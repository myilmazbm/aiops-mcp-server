<#
.SYNOPSIS
    Registers the AIOpsScomReader JEA endpoint on the SCOM management server (one host
    only — not part of the fleet baseline). Idempotent. Windows PowerShell 5.1.

.PARAMETER Principal
    The MCP service identity, e.g. 'CONTOSO\gMSA-AIOps$'. It must also be a member of the
    SCOM "Read-Only Operators" user role (Operations Console → Administration → User Roles),
    because the session runs as the caller, not as a virtual account.

.EXAMPLE
    .\Register-AIOpsScomReader.ps1 -Principal 'CONTOSO\gMSA-AIOps$'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Principal
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable OperationsManager)) {
    throw 'The OperationsManager module is not present; run this on the SCOM management server.'
}

$endpointName  = 'AIOpsScomReader'
$moduleName    = 'AIOpsJea'
$modulePath    = Join-Path $env:ProgramFiles "WindowsPowerShell\Modules\$moduleName"
$transcriptDir = 'C:\ProgramData\AIOps\Transcripts'
$scriptRoot    = Split-Path -Parent $MyInvocation.MyCommand.Path

# Transcript directory (mandatory for the session configuration).
# The ACL is set explicitly instead of inheriting from C:\ProgramData, whose default DACL
# grants BUILTIN\Users read access. JEA transcripts hold the full output of every command
# (alerts, discovered inventory, performance data), so inheriting that would let any local
# user on the management server read it. Inheritance is disabled; only SYSTEM and the local
# Administrators group keep full control.
# $Principal is granted Modify as well because this endpoint deliberately does NOT use a
# virtual account (RunAsVirtualAccount = $false, see the .pssc): the session runs as the
# connecting gMSA, which is not a local administrator here and would otherwise be unable to
# write its own transcript - JEA fails the session when transcription cannot be started.
# That grant goes to the MCP service identity itself, never to Users.
# Idempotent: the DACL is rewritten from scratch on every run.
if (-not (Test-Path $transcriptDir)) { New-Item -ItemType Directory -Path $transcriptDir -Force | Out-Null }

$transcriptAcl = New-Object System.Security.AccessControl.DirectorySecurity
$transcriptAcl.SetAccessRuleProtection($true, $false)   # disable inheritance, drop inherited ACEs
$aclInherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
# Well-known SIDs, not names: localized (Turkish) Windows installations rename both groups.
foreach ($wellKnownSid in @('S-1-5-18', 'S-1-5-32-544')) {      # Local System, BUILTIN\Administrators
    $transcriptAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        (New-Object System.Security.Principal.SecurityIdentifier($wellKnownSid)),
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        $aclInherit,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)))
}
$transcriptAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    (New-Object System.Security.Principal.NTAccount($Principal)),
    [System.Security.AccessControl.FileSystemRights]::Modify,
    $aclInherit,
    [System.Security.AccessControl.PropagationFlags]::None,
    [System.Security.AccessControl.AccessControlType]::Allow)))
(Get-Item -LiteralPath $transcriptDir -Force).SetAccessControl($transcriptAcl)

# Same module as the fleet role; a second role capability file alongside it.
$roleCapDir = Join-Path $modulePath 'RoleCapabilities'
if (-not (Test-Path $roleCapDir)) { New-Item -ItemType Directory -Path $roleCapDir -Force | Out-Null }
Copy-Item -Path (Join-Path $scriptRoot 'AIOpsScomReader.psrc') -Destination (Join-Path $roleCapDir 'AIOpsScomReader.psrc') -Force
if (-not (Test-Path (Join-Path $modulePath "$moduleName.psd1"))) {
    New-ModuleManifest -Path (Join-Path $modulePath "$moduleName.psd1") -ModuleVersion '1.0.0' -Description 'AIOps JEA role capabilities'
}

# The generated session configuration is written into the module directory under Program
# Files (writable by SYSTEM/Administrators only), not %TEMP% and not the transcript directory:
# the latter grants $Principal Modify, and whoever can rewrite this file between validation and
# registration decides who may connect and under which account the session runs.
$psscContent = (Get-Content -Path (Join-Path $scriptRoot 'AIOpsScomReader.pssc') -Raw) -replace '__AIOPS_PRINCIPAL__', $Principal
$psscPath    = Join-Path $modulePath 'AIOpsScomReader.generated.pssc'
if (Test-Path -LiteralPath $psscPath) { Remove-Item -LiteralPath $psscPath -Force }

try {
    Set-Content -Path $psscPath -Value $psscContent -Encoding UTF8
    if (-not (Test-PSSessionConfigurationFile -Path $psscPath)) { throw "Generated session configuration file is invalid: $psscPath" }

    if (Get-PSSessionConfiguration -Name $endpointName -ErrorAction SilentlyContinue) {
        Unregister-PSSessionConfiguration -Name $endpointName -Force
    }
    Register-PSSessionConfiguration -Name $endpointName -Path $psscPath -Force | Out-Null
} finally {
    # Registration keeps its own copy; the generated file is not needed afterwards.
    Remove-Item -LiteralPath $psscPath -Force -ErrorAction SilentlyContinue
}

Write-Output "JEA endpoint '$endpointName' registered for principal '$Principal'."
