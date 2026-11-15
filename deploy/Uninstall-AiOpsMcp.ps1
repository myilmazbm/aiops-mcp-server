<#
.SYNOPSIS
    Removes the AiOps MCP Windows Service from the MCP host. Run elevated on that host.

.DESCRIPTION
    Stops and deletes the AiOpsMcp service. The install directory (binaries,
    appsettings.Production.json, logs) is kept unless -RemoveFiles is passed.

    MCP host only. It does not touch the fleet's JEA endpoints, the inventory database, the
    gMSA or Qdrant - the commands for those are in deploy/README.md (section 7).

.PARAMETER InstallDir
    The directory Install-AiOpsMcp.ps1 installed into. Default C:\AiOpsMcp.

.PARAMETER RemoveFiles
    Also delete the install directory, including the configuration and the logs.

.EXAMPLE
    .\Uninstall-AiOpsMcp.ps1

.EXAMPLE
    .\Uninstall-AiOpsMcp.ps1 -RemoveFiles -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$InstallDir = 'C:\AiOpsMcp',
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AiOpsMcp'

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script elevated ("Run as administrator").'
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service $serviceName is not installed."
} elseif ($PSCmdlet.ShouldProcess($serviceName, 'Stop and delete the Windows Service')) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
    }
    # The SCM reports Stopped shortly before the process image is actually released.
    for ($i = 0; $i -lt 30 -and (Get-Process -Name 'AiOps.Mcp' -ErrorAction SilentlyContinue); $i++) {
        Start-Sleep -Seconds 1
    }
    sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete $serviceName failed with exit code $LASTEXITCODE." }
    Write-Host "Service $serviceName removed."
}

if ($RemoveFiles) {
    # Guard against a mistyped -InstallDir: only a directory that holds this application is deleted.
    if (-not (Test-Path -LiteralPath $InstallDir -PathType Container)) {
        Write-Host "$InstallDir does not exist; nothing to delete."
    } elseif (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'AiOps.Mcp.exe'))) {
        throw "$InstallDir does not contain AiOps.Mcp.exe; refusing to delete it."
    } elseif ($PSCmdlet.ShouldProcess($InstallDir, 'Delete the install directory, including appsettings.Production.json and logs')) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
        Write-Host "$InstallDir deleted."
    }
} else {
    Write-Host "$InstallDir was kept (configuration and logs). Pass -RemoveFiles to delete it."
}
