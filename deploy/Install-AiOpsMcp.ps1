<#
.SYNOPSIS
    Installs (or updates) the AiOps MCP server as a Windows Service on a domain-joined host.
    Run elevated on the target host. Idempotent: safe to re-run for upgrades.

.DESCRIPTION
    Upgrade order matters: the service is stopped before any file is touched, because the
    running host locks AiOps.Mcp.exe and its DLLs and a mirroring copy would otherwise fail
    halfway through with the old files already deleted. The previous installation is copied
    aside first, so a failed mirror is rolled back instead of leaving a half-written InstallDir.

    appsettings.Production.json is never overwritten wholesale on an upgrade. Values that have
    no parameter here (Scom.ManagementServer when -ScomServer is omitted, Alerts.ForwardUrl,
    the whole Directory section, tuning under Remoting, ...) are hand-edited on the host and
    must survive a re-run, so only the parameters the caller actually passed are merged in.

.EXAMPLE
    .\Install-AiOpsMcp.ps1 -PublishDir .\publish -ServiceAccount 'CONTOSO\gMSA-AIOps$' `
        -SqlServer 'sql01.contoso.local' -Servers 'web01.contoso.local','sql01.contoso.local' `
        -AllowedClientIps '10.10.0.21','10.10.0.22'

    PublishDir is the 'publish' folder inside the package that .\build.ps1 produces
    (artifacts\publish, or the same folder after unzipping artifacts\AiOpsMcp-<version>.zip).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PublishDir,
    [Parameter(Mandatory)] [string]$ServiceAccount,     # 'DOMAIN\gMSA-AIOps$'
    [Parameter(Mandatory)] [string]$SqlServer,
    [Parameter(Mandatory)] [string[]]$Servers,          # fleet servers for the nightly sweep
    [string]$InstallDir = 'C:\AiOpsMcp',
    [int]$Port = 5100,
    [string[]]$AllowedClientIps = @(),                  # client IPs allowed to call the MCP endpoint; empty = any source
    [string[]]$ScomSourceIps = @(),
    [string]$ScomServer = '',                           # SCOM management server FQDN (enables get_scom_* tools; needs AIOpsScomReader there)
    [string]$EmbeddingEndpoint = '',
    [string]$QdrantEndpoint = ''
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AiOpsMcp'
$configName  = 'appsettings.Production.json'
$configPath  = Join-Path $InstallDir $configName

# Sets $Config.<Path[0]>.<Path[1]>... = $Value and creates the intermediate objects when the
# existing file does not have them yet. ConvertFrom-Json returns a PSCustomObject on Windows
# PowerShell 5.1 (there is no -AsHashtable), so the merge works on note properties.
function Set-ConfigValue {
    param(
        [Parameter(Mandatory)] [psobject]$Config,
        [Parameter(Mandatory)] [string[]]$Path,
        $Value
    )
    $node = $Config
    for ($i = 0; $i -lt ($Path.Length - 1); $i++) {
        $name  = $Path[$i]
        $child = $null
        if ($node.PSObject.Properties.Match($name).Count -gt 0) { $child = $node.$name }
        if ($null -eq $child -or -not ($child -is [System.Management.Automation.PSCustomObject])) {
            $child = New-Object psobject
            $node | Add-Member -NotePropertyName $name -NotePropertyValue $child -Force
        }
        $node = $child
    }
    $node | Add-Member -NotePropertyName $Path[$Path.Length - 1] -NotePropertyValue $Value -Force
}

if (-not (Test-Path $PublishDir)) { throw "PublishDir not found: $PublishDir" }
New-Item -ItemType Directory -Force $InstallDir | Out-Null

# 1. Stop the service before touching any file: the running host holds AiOps.Mcp.exe and its
#    DLLs open, so a copy over a running installation fails with the files already deleted.
Write-Host "[1/5] Stopping $serviceName (if installed)"
$service        = Get-Service $serviceName -ErrorAction SilentlyContinue
$serviceExisted = [bool]$service
$wasRunning     = $false
if ($service) {
    $wasRunning = ($service.Status -ne 'Stopped')
    if ($wasRunning) {
        Stop-Service $serviceName -Force
        (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
    }
    # The SCM reports Stopped shortly before the process image is actually released.
    for ($i = 0; $i -lt 30 -and (Get-Process -Name 'AiOps.Mcp' -ErrorAction SilentlyContinue); $i++) {
        Start-Sleep -Seconds 1
    }
}

# 2. Files. A rollback copy is taken first so that a failed mirror can be undone; the
#    environment config and the logs directory are kept across upgrades.
Write-Host "[2/5] Copying files to $InstallDir"
$rollbackDir = $null
if (Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue) {
    $rollbackDir = Join-Path $env:TEMP ("AiOpsMcp-rollback-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    robocopy $InstallDir $rollbackDir /MIR /XD logs /R:2 /W:2 /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Could not take a rollback copy of $InstallDir (robocopy exit code $LASTEXITCODE); nothing was changed."
    }
}

robocopy $PublishDir $InstallDir /MIR /XF $configName /XD logs /R:2 /W:2 /NFL /NDL /NJH /NJS | Out-Null
$copyExitCode = $LASTEXITCODE
if ($copyExitCode -ge 8) {
    if ($rollbackDir) {
        Write-Warning "Copy failed (robocopy exit code $copyExitCode); restoring the previous installation from $rollbackDir"
        robocopy $rollbackDir $InstallDir /MIR /XD logs /R:2 /W:2 /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) {
            throw "Copy failed and the rollback failed as well. The previous installation is still in $rollbackDir - restore it by hand before starting $serviceName."
        }
        if ($wasRunning) { Start-Service $serviceName -ErrorAction SilentlyContinue }
        throw "robocopy failed with exit code $copyExitCode; $InstallDir was restored to its previous content and the service was restarted."
    }
    throw "robocopy failed with exit code $copyExitCode; $InstallDir holds an incomplete first install and the service was not started."
}
if ($rollbackDir) { Remove-Item -LiteralPath $rollbackDir -Recurse -Force -ErrorAction SilentlyContinue }

# 3. Environment config next to the exe. On an upgrade only the parameters that were actually
#    passed on the command line are merged in, so hand-edited settings are not reverted.
if (Test-Path $configPath) {
    Write-Host "[3/5] Merging the supplied parameters into the existing $configName"
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json

    if ($PSBoundParameters.ContainsKey('Port')) { Set-ConfigValue -Config $config -Path @('Urls') -Value "http://0.0.0.0:$Port" }
    Set-ConfigValue -Config $config -Path @('Inventory', 'SqlConnectionString') -Value "Server=$SqlServer;Database=AiOpsInventory;Integrated Security=true;TrustServerCertificate=true"
    Set-ConfigValue -Config $config -Path @('Inventory', 'Servers') -Value @($Servers)
    if ($PSBoundParameters.ContainsKey('AllowedClientIps'))  { Set-ConfigValue -Config $config -Path @('Mcp', 'AllowedClientIps') -Value @($AllowedClientIps) }
    if ($PSBoundParameters.ContainsKey('ScomSourceIps'))     { Set-ConfigValue -Config $config -Path @('Alerts', 'AllowedSourceIps') -Value @($ScomSourceIps) }
    if ($PSBoundParameters.ContainsKey('ScomServer'))        { Set-ConfigValue -Config $config -Path @('Scom', 'ManagementServer') -Value $ScomServer }
    if ($PSBoundParameters.ContainsKey('EmbeddingEndpoint')) { Set-ConfigValue -Config $config -Path @('Inventory', 'EmbeddingEndpoint') -Value $EmbeddingEndpoint }
    if ($PSBoundParameters.ContainsKey('QdrantEndpoint'))    { Set-ConfigValue -Config $config -Path @('Inventory', 'QdrantEndpoint') -Value $QdrantEndpoint }
} else {
    Write-Host "[3/5] Writing a fresh $configName"
    $config = New-Object psobject
    Set-ConfigValue -Config $config -Path @('Urls') -Value "http://0.0.0.0:$Port"
    Set-ConfigValue -Config $config -Path @('Mcp', 'AllowedClientIps') -Value @($AllowedClientIps)
    Set-ConfigValue -Config $config -Path @('Alerts', 'AllowedSourceIps') -Value @($ScomSourceIps)
    Set-ConfigValue -Config $config -Path @('Scom', 'ManagementServer') -Value $ScomServer
    Set-ConfigValue -Config $config -Path @('Inventory', 'SqlConnectionString') -Value "Server=$SqlServer;Database=AiOpsInventory;Integrated Security=true;TrustServerCertificate=true"
    Set-ConfigValue -Config $config -Path @('Inventory', 'Servers') -Value @($Servers)
    Set-ConfigValue -Config $config -Path @('Inventory', 'NightlyHourLocal') -Value 2
    Set-ConfigValue -Config $config -Path @('Inventory', 'EmbeddingEndpoint') -Value $EmbeddingEndpoint
    Set-ConfigValue -Config $config -Path @('Inventory', 'QdrantEndpoint') -Value $QdrantEndpoint
}
$config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $configPath -Encoding UTF8

if (@($config.Mcp.AllowedClientIps).Count -eq 0) {
    Write-Warning "Mcp:AllowedClientIps is empty: any host that can reach port $Port may call every tool on the fleet. Pass -AllowedClientIps with the LLM client host IPs unless the network appliances already restrict the port."
}

# 4. Least privilege for the service account: read+execute on the installation, modify
#    only on logs. The identity that can reach the whole fleet must not be able to rewrite
#    its own binaries or its own allow-lists. /remove:g first so an upgrade also drops the
#    broader Modify grant that earlier versions of this script placed on the whole directory.
Write-Host "[4/5] Granting $ServiceAccount read on $InstallDir and modify on logs"
$logDir = Join-Path $InstallDir 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
icacls $InstallDir /remove:g "$ServiceAccount" /T /Q | Out-Null
icacls $InstallDir /grant "$($ServiceAccount):(OI)(CI)RX" /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls could not grant read access on $InstallDir to $ServiceAccount (exit code $LASTEXITCODE). Is the account name correct?" }
icacls $logDir /grant "$($ServiceAccount):(OI)(CI)M" /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls could not grant modify access on $logDir to $ServiceAccount (exit code $LASTEXITCODE)." }

# No firewall step by design: firewall is managed at the network layer (appliances).
# Required flows are listed in deploy/README.md §6 for the network team.

# 5. Service (create or update), auto-restart on failure
Write-Host "[5/5] Registering service $serviceName as $ServiceAccount"
$binPath = "`"$InstallDir\AiOps.Mcp.exe`" --http"
if ($serviceExisted) {
    sc.exe config $serviceName binPath= $binPath obj= $ServiceAccount start= auto | Out-Null
} else {
    sc.exe create $serviceName binPath= $binPath obj= $ServiceAccount start= auto DisplayName= 'AiOps MCP Server' | Out-Null
}
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Start-Service $serviceName

# Smoke test
Start-Sleep -Seconds 8
$body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"install","version":"1"}}}'
$smokeNote = ''
$smokeOk   = $false
try {
    $resp = Invoke-WebRequest "http://127.0.0.1:$Port/" -Method Post -ContentType 'application/json' `
        -Headers @{ Accept = 'application/json, text/event-stream' } -Body $body -UseBasicParsing -TimeoutSec 15
    $smokeOk = ($resp.StatusCode -eq 200 -and $resp.Content -match 'AiOps.Mcp')
} catch {
    $status = $null
    if ($_.Exception.PSObject.Properties.Match('Response').Count -gt 0 -and $_.Exception.Response) {
        $status = [int]$_.Exception.Response.StatusCode
    }
    # 403 means the listener is up and rejected loopback, which is expected when
    # Mcp:AllowedClientIps is set and does not list 127.0.0.1: a healthy endpoint.
    if ($status -eq 403) {
        $smokeOk   = $true
        $smokeNote = ' (endpoint answered 403 to loopback: 127.0.0.1 is not in Mcp:AllowedClientIps, which is expected)'
    } else {
        throw
    }
}
if (-not $smokeOk) { throw "Service started but MCP endpoint did not answer" }
Write-Host "OK: $serviceName running, MCP endpoint answering on port $Port.$smokeNote Logs: $InstallDir\logs"
