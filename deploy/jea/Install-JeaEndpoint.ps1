<#
.SYNOPSIS
    Rolls the AIOpsReader JEA endpoint out to a handful of servers WITHOUT SCCM.
    Run by an administrator of the target servers from any admin workstation.

.DESCRIPTION
    For labs and small environments. A fleet should use the SCCM baseline (deploy/sccm), which
    also keeps the role capability in sync afterwards; this script is a one-shot push.

    Per server, as the operator (the default PowerShell remoting endpoint):
      1. connect - an unreachable server is reported and the run continues with the rest;
      2. skip domain controllers (out of scope by policy; Register-AIOpsReader.ps1 refuses them
         as well, and this script never passes its override switch);
      3. write AIOpsReader.psrc, AIOpsReader.pssc and Register-AIOpsReader.ps1 into
         C:\ProgramData\AIOps\Deploy, which is first locked to SYSTEM + Administrators;
      4. run Register-AIOpsReader.ps1 there, detached from the remoting session (see below);
      5. reconnect until the registration reports back or -TimeoutSeconds passes;
      6. verify from this workstation that the AIOpsReader configuration answers.

    WinRM must already be enabled on the targets: this script does not configure WinRM and
    never touches Windows Firewall. Nothing is installed besides the session configuration.

    HOW THE WINRM RESTART IS SURVIVED
    Registering (and, on a re-run, first unregistering) a session configuration restarts the
    WinRM service on the target. That kills every remoting session on it - including the one
    used to start the registration - and every process inside that session's job object. Run
    in-session, a re-run would unregister the endpoint, lose its own session at that moment and
    never reach the register step; a disconnected session does not help either, because it is
    hosted by the same WinRM service. So the registration runs as a one-shot scheduled task
    under SYSTEM - the same identity the SCCM baseline uses - which both outlives the restart
    and holds the full local token the WSMan: provider needs. (A process started over WMI
    from the session survives the restart too, but inherits a network-logon token and cannot
    reach the local WSMan: provider.) The task writes its outcome to a status file in the
    locked staging directory; this script polls for that file over fresh connections, which
    simply fail and are retried while WinRM is down, and then deletes the task.

.PARAMETER ComputerName
    Target servers. Use DNS names, not IP addresses (Kerberos).

.PARAMETER Principal
    The identity allowed to connect to the endpoint: the MCP service account
    ('CONTOSO\gMSA-AIOps$', with the trailing $) or an AD group that contains it.

.PARAMETER Credential
    Optional. Used only to open the remoting sessions when the operator's own logon is not an
    administrator of the targets. Passed straight through; never written anywhere.

.PARAMETER TimeoutSeconds
    How long to wait per server for the detached registration to report back. Default 180.

.PARAMETER PassThru
    Also emit the per-server result objects (ComputerName, Result, Detail) to the pipeline.

.EXAMPLE
    .\Install-JeaEndpoint.ps1 -ComputerName web01.contoso.local,app01.contoso.local -Principal 'CONTOSO\gMSA-AIOps$'

.EXAMPLE
    .\Install-JeaEndpoint.ps1 -ComputerName web01.contoso.local -Principal 'CONTOSO\gMSA-AIOps$' -WhatIf

    Connects, checks the role and the principal, and reports what would be done. Changes nothing.

.NOTES
    Result values: Registered, Skipped-DomainController, Failed, WhatIf.
    The script exits with code 1 when any server ended as Failed.
    Runs on Windows PowerShell 5.1 or PowerShell 7 on the workstation; everything sent to the
    targets is Windows PowerShell 5.1 compatible.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$ComputerName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Principal,

    [System.Management.Automation.PSCredential]$Credential,

    [ValidateRange(30, 3600)]
    [int]$TimeoutSeconds = 180,

    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'

$endpointName = 'AIOpsReader'
$stageDir     = 'C:\ProgramData\AIOps\Deploy'
$taskName     = 'AIOps-InstallJeaEndpoint'   # one-shot, deleted when the run finishes
$jeaFiles     = @('AIOpsReader.psrc', 'AIOpsReader.pssc', 'Register-AIOpsReader.ps1')
$scriptRoot   = Split-Path -Parent $MyInvocation.MyCommand.Path

# The principal ends up on a command line on the target; accept only DOMAIN\name made of
# characters that are legal in an account name. This also rejects an unedited <placeholder>.
if ($Principal -notmatch '^[^"''`\\/\[\]:;|=,+*?<>@\s]+\\[^"''`\\/\[\]:;|=,+*?<>@]+$') {
    throw "Principal '$Principal' is not in DOMAIN\name form (for a gMSA: 'CONTOSO\gMSA-AIOps`$', including the trailing `$)."
}

# Read the three files once; they are sent as bytes so that what lands on the target is
# byte-identical to what is in this folder (the role capability is the security boundary).
$payload = @{}
foreach ($file in $jeaFiles) {
    $localPath = Join-Path $scriptRoot $file
    if (-not (Test-Path -LiteralPath $localPath)) { throw "$file was not found next to this script ($scriptRoot)." }
    $payload[$file] = [System.IO.File]::ReadAllBytes($localPath)
}
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $expectedPsrcHash = ([System.BitConverter]::ToString($sha256.ComputeHash($payload['AIOpsReader.psrc']))).Replace('-', '')
} finally {
    $sha256.Dispose()
}

$sessionOption = New-PSSessionOption -OpenTimeout 20000 -OperationTimeout 120000

function New-OperatorSession {
    param([string]$Server, [string]$ConfigurationName)
    $arguments = @{ ComputerName = $Server; SessionOption = $sessionOption; ErrorAction = 'Stop' }
    if ($ConfigurationName) { $arguments['ConfigurationName'] = $ConfigurationName }
    if ($Credential)        { $arguments['Credential'] = $Credential }
    New-PSSession @arguments
}

function Remove-OperatorSession {
    param($Session)
    # Remove-PSSession honours -WhatIf; the session must be closed in a -WhatIf run too.
    if ($Session) { Remove-PSSession -Session $Session -WhatIf:$false -Confirm:$false -ErrorAction SilentlyContinue }
}

function New-Result {
    param([string]$Server, [string]$Result, [string]$Detail)
    New-Object psobject -Property ([ordered]@{ ComputerName = $Server; Result = $Result; Detail = $Detail })
}

# ---- script blocks that run on the target (Windows PowerShell 5.1) ---------------------------

# Read-only: is this a DC, and does the principal resolve there?
$inspectScript = {
    param([string]$Principal)
    $principalError = $null
    try {
        [void](New-Object System.Security.Principal.NTAccount($Principal)).Translate([System.Security.Principal.SecurityIdentifier])
    } catch {
        $principalError = $_.Exception.Message
    }
    New-Object psobject -Property @{
        IsDomainController = ((Get-CimInstance Win32_OperatingSystem).ProductType -eq 2)
        PrincipalError     = $principalError
    }
}

# Locks the staging directory, writes the files, starts the detached registration.
$stageScript = {
    param([string]$StageDir, [hashtable]$Files, [string]$Principal, [string]$TaskName)
    $ErrorActionPreference = 'Stop'

    # Same rules as deploy/sccm/Remediate-AIOpsReader.ps1: the register script is executed from
    # this directory with administrative rights, so nobody else may be able to write to it or
    # replace it. An existing directory must be a real directory owned by SYSTEM, Administrators
    # or the operator running this session; the DACL is then replaced with a protected one
    # (well-known SIDs, not localized names) and the owner is set to Administrators.
    function Set-AIOpsLockedDirectory {
        param([string]$Path, [switch]$AllowTraverse)

        $trustedOwners = @('S-1-5-18', 'S-1-5-32-544', [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value)

        if (Test-Path -LiteralPath $Path) {
            $item = Get-Item -LiteralPath $Path -Force
            if (-not $item.PSIsContainer) { throw "$Path exists but is not a directory; refusing to stage into it." }
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "$Path is a junction or symbolic link; refusing to stage into it."
            }
            $owner = $item.GetAccessControl([System.Security.AccessControl.AccessControlSections]::Owner).GetOwner([System.Security.Principal.SecurityIdentifier])
            if ($trustedOwners -notcontains $owner.Value) {
                throw "$Path already exists and is owned by $($owner.Value), not by SYSTEM, Administrators or you; refusing to run a script from it. Inspect and remove the directory, then re-run."
            }
        } else {
            New-Item -ItemType Directory -Path $Path -Force | Out-Null
        }

        $acl = New-Object System.Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
        foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {       # Local System, BUILTIN\Administrators
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                (New-Object System.Security.Principal.SecurityIdentifier($sid)),
                [System.Security.AccessControl.FileSystemRights]::FullControl,
                $inherit,
                [System.Security.AccessControl.PropagationFlags]::None,
                [System.Security.AccessControl.AccessControlType]::Allow)))
        }
        if ($AllowTraverse) {
            # This folder only, nothing inherited: the SCOM endpoint's transcript directory
            # lives below the root and is written by a non-administrator service identity.
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')),      # Authenticated Users
                [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
                [System.Security.AccessControl.InheritanceFlags]::None,
                [System.Security.AccessControl.PropagationFlags]::None,
                [System.Security.AccessControl.AccessControlType]::Allow)))
        }
        (Get-Item -LiteralPath $Path -Force).SetAccessControl($acl)
    }

    Set-AIOpsLockedDirectory -Path (Split-Path -Parent $StageDir) -AllowTraverse
    Set-AIOpsLockedDirectory -Path $StageDir

    $launcherName = 'Install-JeaEndpoint.Launcher.ps1'
    $statusPath   = Join-Path $StageDir 'Install-JeaEndpoint.status'
    $logPath      = Join-Path $StageDir 'Install-JeaEndpoint.log'

    # Runs detached; reports through the status file because nobody is attached to its output.
    $launcher = @'
param([Parameter(Mandatory = $true)] [string]$Principal)
$ErrorActionPreference = 'Stop'
$dir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$log    = Join-Path $dir 'Install-JeaEndpoint.log'
$status = Join-Path $dir 'Install-JeaEndpoint.status'
try {
    & (Join-Path $dir 'Register-AIOpsReader.ps1') -Principal $Principal *> $log
    Set-Content -LiteralPath $status -Value 'OK' -Encoding UTF8
} catch {
    Add-Content -LiteralPath $log -Value ($_ | Out-String)
    Set-Content -LiteralPath $status -Value ('FAILED: ' + $_.Exception.Message) -Encoding UTF8
}
'@
    $Files[$launcherName] = [System.Text.Encoding]::UTF8.GetPreamble() + [System.Text.Encoding]::UTF8.GetBytes($launcher)

    # Delete before writing: a file planted before the directory was locked keeps its own
    # owner and DACL (or is a link elsewhere). File.Delete removes a link, never its target.
    foreach ($name in (@($Files.Keys) + @('Install-JeaEndpoint.status', 'Install-JeaEndpoint.log'))) {
        $target = Join-Path $StageDir $name
        if (Test-Path -LiteralPath $target) {
            if ((Get-Item -LiteralPath $target -Force).PSIsContainer) { throw "$target is a directory; refusing to stage over it." }
            [System.IO.File]::Delete($target)
        }
    }
    foreach ($name in $Files.Keys) {
        [System.IO.File]::WriteAllBytes((Join-Path $StageDir $name), [byte[]]$Files[$name])
    }

    # One-shot scheduled task running as SYSTEM. Two things have to hold at once: the process
    # must outlive the WinRM restart (so it cannot live inside this remoting session), and it
    # must hold a full local token, because Register-PSSessionConfiguration drives the local
    # WSMan: provider. A process started through Win32_Process.Create satisfies the first but
    # not the second - it inherits this session's network-logon token and fails with
    # "Cannot find path 'localhost\Plugin'" (observed on a real server). SYSTEM is also exactly
    # the identity the SCCM baseline runs the same register script under.
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments  = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Principal "{1}"' -f `
        (Join-Path $StageDir $launcherName), $Principal
    $action    = New-ScheduledTaskAction -Execute $powershell -Argument $arguments
    $runAs     = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $runAs -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
}

# Read-only: how far did the detached registration get?
$pollScript = {
    param([string]$StageDir, [string]$TaskName, [string]$EndpointName)
    # Liveness first, status second: a task that finishes in between has written its status.
    # 267011 (0x41303) is "has not run yet": the task was started a moment ago and the
    # scheduler has not flipped it to Running, which must not be read as "finished".
    $alive = $false
    $task  = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task) {
        $info  = $task | Get-ScheduledTaskInfo -ErrorAction SilentlyContinue
        $alive = ("$($task.State)" -eq 'Running') -or ($info -and $info.LastTaskResult -eq 267011)
    }

    $status     = $null
    $statusPath = Join-Path $StageDir 'Install-JeaEndpoint.status'
    if (Test-Path -LiteralPath $statusPath) {
        $status = (Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8)
        if ($status) { $status = $status.Trim() }
    }

    $logTail = $null
    $logPath = Join-Path $StageDir 'Install-JeaEndpoint.log'
    if (Test-Path -LiteralPath $logPath) {
        $logTail = ((Get-Content -LiteralPath $logPath -Tail 15 -ErrorAction SilentlyContinue) -join "`n")
    }

    $registered = $false
    $psrcHash   = $null
    if ($status) {
        # The plugin's registry key, not Get-PSSessionConfiguration: that cmdlet goes through
        # the local WSMan provider, which is unreliable from inside a remoting session.
        $registered = Test-Path -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WSMAN\Plugin\$EndpointName"
        $psrc = Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules\AIOpsJea\RoleCapabilities\AIOpsReader.psrc'
        if (Test-Path -LiteralPath $psrc) { $psrcHash = (Get-FileHash -LiteralPath $psrc -Algorithm SHA256).Hash }
    }

    New-Object psobject -Property @{
        Alive = $alive; Status = $status; LogTail = $logTail; Registered = $registered; PsrcHash = $psrcHash
    }
}

# Removes the launcher and the status file; the three JEA files and the log stay in the
# locked staging directory.
$cleanupScript = {
    param([string]$StageDir, [string]$TaskName)
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    foreach ($name in @('Install-JeaEndpoint.Launcher.ps1', 'Install-JeaEndpoint.status')) {
        $target = Join-Path $StageDir $name
        if (Test-Path -LiteralPath $target) { [System.IO.File]::Delete($target) }
    }
}

# ---- workstation-side probe -------------------------------------------------------------------

# Does the AIOpsReader configuration answer? The probe connects as the OPERATOR, who is normally
# not the endpoint's principal, so "access denied" is the expected, healthy answer: WinRM found
# the configuration and its ACL turned the caller away. A configuration that does not exist is
# reported by WinRM as an invalid resource URI instead. The error IDs are not localized.
# This proves the endpoint exists, not that the gMSA can use it.
function Test-EndpointAnswers {
    param([string]$Server)
    $session = $null
    try {
        $session = New-OperatorSession -Server $Server -ConfigurationName $endpointName
        return New-Object psobject -Property @{ Answers = $true; Detail = 'endpoint answers (a session opened for the operator)' }
    } catch {
        $errorId = [string]$_.FullyQualifiedErrorId
        if ($errorId -like 'AccessDenied*') {
            return New-Object psobject -Property @{ Answers = $true; Detail = 'endpoint answers (it refused the operator, as expected for anyone but the principal)' }
        }
        if ($errorId -like 'InvalidResourceUri*') {
            return New-Object psobject -Property @{ Answers = $false; Detail = "WinRM on $Server does not know a session configuration named $endpointName" }
        }
        return New-Object psobject -Property @{ Answers = $false; Detail = "probe of $endpointName failed: $($_.Exception.Message)" }
    } finally {
        Remove-OperatorSession $session
    }
}

# ---- main loop --------------------------------------------------------------------------------

$results = New-Object System.Collections.Generic.List[object]

foreach ($server in $ComputerName) {
    $server  = $server.Trim()
    $session = $null
    Write-Host "[$server] connecting"

    try {
        try {
            $session = New-OperatorSession -Server $server
        } catch {
            $results.Add((New-Result $server 'Failed' "unreachable over PowerShell remoting: $($_.Exception.Message)"))
            continue
        }

        $info = Invoke-Command -Session $session -ScriptBlock $inspectScript -ArgumentList $Principal
        if ($info.IsDomainController) {
            $results.Add((New-Result $server 'Skipped-DomainController' 'domain controllers are out of scope; nothing was changed'))
            continue
        }
        if ($info.PrincipalError) {
            $results.Add((New-Result $server 'Failed' "principal '$Principal' does not resolve on the server: $($info.PrincipalError)"))
            continue
        }

        if (-not $PSCmdlet.ShouldProcess($server, "Stage the JEA files in $stageDir and register the $endpointName session configuration for $Principal (restarts WinRM)")) {
            $results.Add((New-Result $server 'WhatIf' 'reachable, not a domain controller, principal resolves; nothing was changed'))
            continue
        }

        Write-Host "[$server] staging files and starting the registration"
        Invoke-Command -Session $session -ScriptBlock $stageScript -ArgumentList $stageDir, $payload, $Principal, $taskName | Out-Null
        Remove-OperatorSession $session
        $session = $null

        # The registration restarts WinRM; connections fail while it is down and are retried.
        Write-Host "[$server] waiting for the registration (WinRM restarts on the server; up to $TimeoutSeconds s)"
        $deadline  = (Get-Date).AddSeconds($TimeoutSeconds)
        $state     = $null
        $lastError = $null
        $finished  = $false
        while (-not $finished -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 5
            $pollSession = $null
            try {
                $pollSession = New-OperatorSession -Server $server
                $state = Invoke-Command -Session $pollSession -ScriptBlock $pollScript -ArgumentList $stageDir, $taskName, $endpointName -ErrorAction Stop
                $finished = ([bool]$state.Status -or -not $state.Alive)
            } catch {
                $lastError = $_.Exception.Message
            } finally {
                Remove-OperatorSession $pollSession
            }
        }

        if (-not $finished) {
            $reason = "no result within $TimeoutSeconds s"
            if ($lastError) { $reason += "; last connection error: $lastError" }
            $results.Add((New-Result $server 'Failed' "$reason. Log on the server: $stageDir\Install-JeaEndpoint.log"))
            continue
        }

        if (-not $state.Status) {
            $results.Add((New-Result $server 'Failed' "the registration process ended without reporting a result. Log tail: $($state.LogTail)"))
            continue
        }
        if ($state.Status -ne 'OK') {
            $results.Add((New-Result $server 'Failed' "$($state.Status) (log on the server: $stageDir\Install-JeaEndpoint.log)"))
            continue
        }
        if (-not $state.Registered) {
            $results.Add((New-Result $server 'Failed' "the register script finished but $endpointName is not registered. Log tail: $($state.LogTail)"))
            continue
        }
        if (-not [string]::Equals([string]$state.PsrcHash, $expectedPsrcHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            $results.Add((New-Result $server 'Failed' "the role capability on the server (SHA256 $($state.PsrcHash)) differs from the local AIOpsReader.psrc ($expectedPsrcHash)"))
            continue
        }

        $probe = Test-EndpointAnswers -Server $server
        if ($probe.Answers) {
            $results.Add((New-Result $server 'Registered' "$($probe.Detail); role capability matches the local file"))
        } else {
            $results.Add((New-Result $server 'Failed' "registered on the server, but $($probe.Detail)"))
        }

        try {
            $session = New-OperatorSession -Server $server
            Invoke-Command -Session $session -ScriptBlock $cleanupScript -ArgumentList $stageDir, $taskName
        } catch {
            Write-Warning "[$server] could not remove the launcher from ${stageDir}: $($_.Exception.Message)"
        }
    } catch {
        $results.Add((New-Result $server 'Failed' $_.Exception.Message))
    } finally {
        Remove-OperatorSession $session
    }
}

Write-Host ''
Write-Host (($results | Format-Table -Property ComputerName, Result, Detail -AutoSize -Wrap | Out-String).Trim())
Write-Host ''
$operator = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
if ($Credential) { $operator = $Credential.UserName }
Write-Host "The probe ran as $operator, not as $Principal. It shows that the endpoint exists; that the service account can use it is proven by the MCP service itself (deploy/README.md, Quick start step 8)."

if ($PassThru) { $results }
if (@($results | Where-Object { $_.Result -eq 'Failed' }).Count -gt 0) { exit 1 }
