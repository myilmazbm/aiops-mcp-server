<#
.SYNOPSIS
    Read-only preflight for the AiOps MCP host. Run it elevated ON the MCP host BEFORE
    Install-AiOpsMcp.ps1. Prints one PASS / WARN / FAIL line per check and exits with code 1
    when any check failed. It changes nothing.

.DESCRIPTION
    The parameters carry the same names as Install-AiOpsMcp.ps1, so one hashtable can be
    splatted into both scripts and every value is typed once (see the example).

    Checks:
      - elevated, 64-bit, Windows PowerShell 5.1 or later, host is domain-joined
      - the gMSA is installed and usable on this host (needs the ActiveDirectory module;
        without it the check is a WARN that prints the command to run by hand)
      - TCP reachability of SQL Server
      - per fleet server: TCP 5985 and whether the AIOpsReader session configuration answers
      - optional: the SCOM management server (TCP 5985 and AIOpsScomReader), the embedding
        and Qdrant endpoints
      - the allow-lists contain exact IP addresses (the service compares them as text)
      - the install directory's parent exists, the port is free, the publish folder is usable

    WHAT THE REMOTE CHECKS CAN AND CANNOT PROVE
    Every network check runs as YOU, the operator - not as the gMSA. TCP checks are therefore
    valid for the host (the firewall appliances filter by source address, not by account). The
    session-configuration check shows that the endpoint EXISTS: a server that knows the
    configuration answers "access denied" to anyone but its principal, and that answer is
    reported as PASS. Whether the gMSA itself is let in (and has its SQL and SCOM roles) is
    only proven once the service runs - see deploy/README.md, Quick start step 8.

.PARAMETER ServiceAccount
    The gMSA the service will run as: 'DOMAIN\gMSA-AIOps$'.

.PARAMETER SqlServer
    SQL Server host name, optionally 'host,port' or 'host\instance'.

.PARAMETER Servers
    Fleet servers to probe (DNS names). Pass the same list as for the installer, or a sample.

.PARAMETER PublishDir
    Optional here (mandatory for the installer): checked for AiOps.Mcp.exe when given.

.PARAMETER WinRmPort
    WinRM HTTP port used by the fleet (Remoting:WinRmPort). Default 5985.

.EXAMPLE
    $p = @{
        PublishDir       = '.\publish'
        ServiceAccount   = 'CONTOSO\gMSA-AIOps$'
        SqlServer        = 'sql01.contoso.local'
        Servers          = 'web01.contoso.local', 'app01.contoso.local'
        AllowedClientIps = '10.10.0.21'
    }
    .\deploy\Test-AiOpsPrerequisites.ps1 @p
    .\deploy\Install-AiOpsMcp.ps1 @p
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$ServiceAccount,
    [Parameter(Mandatory = $true)] [string]$SqlServer,
    [string[]]$Servers = @(),
    [string]$PublishDir = '',
    [string]$InstallDir = 'C:\AiOpsMcp',
    [int]$Port = 5100,
    [string[]]$AllowedClientIps = @(),
    [string[]]$ScomSourceIps = @(),
    [string]$ScomServer = '',
    [string]$EmbeddingEndpoint = '',
    [string]$QdrantEndpoint = '',
    [int]$WinRmPort = 5985,
    [int]$TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
$script:counts = @{ PASS = 0; WARN = 0; FAIL = 0 }

function Write-Check {
    param(
        [ValidateSet('PASS', 'WARN', 'FAIL')] [string]$Status,
        [string]$Name,
        [string]$Detail
    )
    $script:counts[$Status]++
    $color = 'Green'
    if ($Status -eq 'WARN') { $color = 'Yellow' }
    if ($Status -eq 'FAIL') { $color = 'Red' }
    Write-Host ('[{0}] ' -f $Status) -ForegroundColor $color -NoNewline
    if ($Detail) { Write-Host ('{0}: {1}' -f $Name, $Detail) } else { Write-Host $Name }
}

# Plain TCP connect with a timeout. Returns $null on success, otherwise the reason.
function Test-TcpPort {
    param([string]$HostName, [int]$PortNumber)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($HostName, $PortNumber, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds))) {
            return "no answer within $TimeoutSeconds s (filtered by a firewall, or the host is down)"
        }
        $client.EndConnect($async)
        return $null
    } catch {
        $inner = $_.Exception
        while ($inner.InnerException) { $inner = $inner.InnerException }
        return $inner.Message
    } finally {
        $client.Close()
    }
}

# Does a JEA session configuration answer on the server? Runs as the operator: "access denied"
# means WinRM found the configuration and its ACL refused the caller, which is the expected
# answer for anyone but the principal; a missing configuration is an invalid resource URI.
# The error IDs are not localized. Opening (and at once closing) a session changes nothing.
function Test-SessionConfiguration {
    param([string]$Server, [string]$ConfigurationName, [string]$CheckName, [string]$HowToFix)
    $session = $null
    try {
        $option  = New-PSSessionOption -OpenTimeout ($TimeoutSeconds * 1000) -OperationTimeout ($TimeoutSeconds * 1000)
        $session = New-PSSession -ComputerName $Server -Port $WinRmPort -ConfigurationName $ConfigurationName -SessionOption $option -ErrorAction Stop
        Write-Check PASS $CheckName "answers (a session opened for $operatorName; this does not prove the gMSA's access)"
    } catch {
        $errorId = [string]$_.FullyQualifiedErrorId
        if ($errorId -like 'AccessDenied*') {
            Write-Check PASS $CheckName "answers (refused $operatorName, as expected for anyone but the principal; this does not prove the gMSA's access)"
        } elseif ($errorId -like 'InvalidResourceUri*') {
            Write-Check FAIL $CheckName "WinRM answers but has no session configuration named $ConfigurationName. $HowToFix"
        } else {
            Write-Check FAIL $CheckName "probe failed as ${operatorName}: $($_.Exception.Message)"
        }
    } finally {
        if ($session) { Remove-PSSession -Session $session -Confirm:$false -ErrorAction SilentlyContinue }
    }
}

# Mcp:AllowedClientIps / Alerts:AllowedSourceIps are compared as exact text against the
# canonical form of the caller's address, so a CIDR range, a host name or a non-canonical
# spelling silently never matches.
function Test-IpList {
    param([string]$Name, [string[]]$Values)
    foreach ($value in $Values) {
        $parsed = $null
        if (-not [System.Net.IPAddress]::TryParse($value, [ref]$parsed)) {
            Write-Check FAIL $Name "'$value' is not an IP address (ranges and host names are not supported; list each address)"
        } elseif ($parsed.ToString() -ne $value) {
            Write-Check FAIL $Name "'$value' must be written in its canonical form '$($parsed.ToString())' or it will never match"
        } else {
            Write-Check PASS $Name $value
        }
    }
}

function Test-HttpEndpoint {
    param([string]$Name, [string]$Url)
    $uri = $null
    if (-not [System.Uri]::TryCreate($Url, [System.UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notmatch '^https?$') {
        Write-Check FAIL $Name "'$Url' is not an absolute http(s) URL"
        return
    }
    $reason = Test-TcpPort -HostName $uri.Host -PortNumber $uri.Port
    if ($reason) { Write-Check FAIL $Name "TCP $($uri.Host):$($uri.Port) - $reason" }
    else         { Write-Check PASS $Name "TCP $($uri.Host):$($uri.Port) reachable" }
}

$identity     = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$operatorName = $identity.Name
Write-Host "AiOps MCP preflight on $env:COMPUTERNAME, running as $operatorName (not as $ServiceAccount)."
Write-Host ''

# ---- this host --------------------------------------------------------------------------------

$isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) { Write-Check PASS 'Elevated' }
else          { Write-Check FAIL 'Elevated' 'start PowerShell with "Run as administrator"; the installer needs it and some checks below are incomplete without it' }

$psVersion = $PSVersionTable.PSVersion
if ($psVersion -ge [version]'5.1' -and [Environment]::Is64BitProcess) {
    Write-Check PASS 'PowerShell' "$psVersion, 64-bit"
} elseif (-not [Environment]::Is64BitProcess) {
    Write-Check FAIL 'PowerShell' 'this is a 32-bit process; start the 64-bit Windows PowerShell'
} else {
    Write-Check FAIL 'PowerShell' "version $psVersion; Windows PowerShell 5.1 or later is required"
}

$domainJoined = $false
try {
    $computerSystem = Get-CimInstance Win32_ComputerSystem
    $domainJoined   = [bool]$computerSystem.PartOfDomain
    if ($domainJoined) { Write-Check PASS 'Domain-joined' $computerSystem.Domain }
    else               { Write-Check FAIL 'Domain-joined' 'this host is in a workgroup; the gMSA, Kerberos and LDAP all need a domain member' }
} catch {
    Write-Check FAIL 'Domain-joined' "could not be determined: $($_.Exception.Message)"
}

# ---- gMSA -------------------------------------------------------------------------------------

$accountName = ($ServiceAccount -split '\\')[-1]
if ($ServiceAccount -match '[<>]' -or $ServiceAccount -notmatch '^[^\\]+\\[^\\]+\$$') {
    Write-Check FAIL 'gMSA name' "'$ServiceAccount' is not in 'DOMAIN\name`$' form (a gMSA is passed with its domain and the trailing `$)"
} elseif (-not (Get-Command Test-ADServiceAccount -ErrorAction SilentlyContinue)) {
    $manual = "Test-ADServiceAccount '$($accountName.TrimEnd('$'))'"
    Write-Check WARN 'gMSA usable on this host' "the ActiveDirectory module is not installed here, so this was not checked. Check by hand (must print True): Install-WindowsFeature RSAT-AD-PowerShell; $manual"
} else {
    try {
        if (Test-ADServiceAccount -Identity $accountName.TrimEnd('$') -WarningAction SilentlyContinue) {
            Write-Check PASS 'gMSA usable on this host' $ServiceAccount
        } else {
            Write-Check FAIL 'gMSA usable on this host' "Test-ADServiceAccount returned False: this host may not retrieve the managed password. Add '$env:COMPUTERNAME`$' to PrincipalsAllowedToRetrieveManagedPassword (reboot if it came through a new group membership), then run Install-ADServiceAccount '$($accountName.TrimEnd('$'))'"
        }
    } catch {
        Write-Check FAIL 'gMSA usable on this host' $_.Exception.Message
    }
}

# ---- SQL Server -------------------------------------------------------------------------------

$sqlTarget = $SqlServer -replace '^(?i)tcp:', ''
$sqlPort   = 1433
$sqlNamed  = $false
if ($sqlTarget -match '^(?<h>[^,]+),(?<p>\d+)$') {
    $sqlTarget = $Matches['h']; $sqlPort = [int]$Matches['p']
}
if ($sqlTarget -match '^(?<h>[^\\]+)\\') {
    $sqlNamed  = ($sqlPort -eq 1433)
    $sqlTarget = $Matches['h']
}
if ($SqlServer -match '[<>]') {
    Write-Check FAIL 'SQL Server' "'$SqlServer' still contains a placeholder"
} elseif ($sqlNamed) {
    Write-Check WARN 'SQL Server' "'$SqlServer' is a named instance without a port; its TCP port is dynamic and was not checked. Give the instance a fixed port and pass 'host,port' so the firewall rule can be exact"
} else {
    $reason = Test-TcpPort -HostName $sqlTarget -PortNumber $sqlPort
    if ($reason) { Write-Check FAIL 'SQL Server' "TCP ${sqlTarget}:$sqlPort - $reason" }
    else         { Write-Check PASS 'SQL Server' "TCP ${sqlTarget}:$sqlPort reachable (the gMSA's database roles are not checked here)" }
}

# ---- fleet servers ----------------------------------------------------------------------------

if (@($Servers).Count -eq 0) {
    Write-Check WARN 'Fleet servers' 'no -Servers given, so no JEA endpoint was probed'
}
foreach ($server in $Servers) {
    $address = $null
    if ([System.Net.IPAddress]::TryParse($server, [ref]$address)) {
        Write-Check WARN "$server name" 'this is an IP address; Kerberos needs a DNS name, list servers by name'
    }
    $reason = Test-TcpPort -HostName $server -PortNumber $WinRmPort
    if ($reason) {
        Write-Check FAIL "$server WinRM" "TCP $WinRmPort - $reason"
        continue
    }
    Write-Check PASS "$server WinRM" "TCP $WinRmPort reachable"
    Test-SessionConfiguration -Server $server -ConfigurationName 'AIOpsReader' -CheckName "$server AIOpsReader" `
        -HowToFix 'Roll the endpoint out first (deploy/README.md, Quick start step 4); domain controllers never get it'
}

# ---- SCOM management server (optional) --------------------------------------------------------

if ($ScomServer) {
    $reason = Test-TcpPort -HostName $ScomServer -PortNumber $WinRmPort
    if ($reason) {
        Write-Check FAIL "$ScomServer WinRM (SCOM)" "TCP $WinRmPort - $reason"
    } else {
        Write-Check PASS "$ScomServer WinRM (SCOM)" "TCP $WinRmPort reachable"
        Test-SessionConfiguration -Server $ScomServer -ConfigurationName 'AIOpsScomReader' -CheckName "$ScomServer AIOpsScomReader" `
            -HowToFix 'Register it on the management server first (deploy/README.md, Quick start step 5)'
    }
}

# ---- semantic search (optional) ---------------------------------------------------------------

if ([bool]$EmbeddingEndpoint -ne [bool]$QdrantEndpoint) {
    Write-Check WARN 'search_inventory' 'only one of -EmbeddingEndpoint / -QdrantEndpoint was given; the tool needs both and stays disabled'
}
if ($EmbeddingEndpoint) { Test-HttpEndpoint -Name 'Embedding endpoint' -Url $EmbeddingEndpoint }
if ($QdrantEndpoint)    { Test-HttpEndpoint -Name 'Qdrant endpoint' -Url $QdrantEndpoint }

# ---- allow-lists ------------------------------------------------------------------------------

if (@($AllowedClientIps).Count -eq 0) {
    Write-Check WARN 'AllowedClientIps' "empty: every host that can reach port $Port may call every tool on the fleet. Pass the LLM client IPs unless the network appliances already restrict the port"
} else {
    Test-IpList -Name 'AllowedClientIps' -Values $AllowedClientIps
}
if (@($ScomSourceIps).Count -gt 0) { Test-IpList -Name 'ScomSourceIps' -Values $ScomSourceIps }

# ---- install location, port, package ----------------------------------------------------------

$installParent = $null
try { $installParent = Split-Path -Parent ([System.IO.Path]::GetFullPath($InstallDir)) } catch { $installParent = $null }
if (Test-Path -LiteralPath $InstallDir -PathType Container) {
    Write-Check PASS 'Install directory' "$InstallDir exists (the installer will upgrade it and keep appsettings.Production.json and logs)"
} elseif ($installParent -and (Test-Path -LiteralPath $installParent -PathType Container)) {
    Write-Check PASS 'Install directory' "$InstallDir will be created under $installParent"
} else {
    Write-Check FAIL 'Install directory' "the parent of $InstallDir does not exist"
}

$service   = Get-Service -Name 'AiOpsMcp' -ErrorAction SilentlyContinue
$listeners = @([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
    Where-Object { $_.Port -eq $Port })
if ($listeners.Count -eq 0) {
    Write-Check PASS "Port $Port" 'free'
} else {
    $ownerName = $null
    if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
        $connection = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($connection) {
            $ownerProcess = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
            if ($ownerProcess) { $ownerName = $ownerProcess.ProcessName }
        }
    }
    if ($ownerName -eq 'AiOps.Mcp' -or (-not $ownerName -and $service -and $service.Status -eq 'Running')) {
        Write-Check PASS "Port $Port" 'in use by the existing AiOpsMcp service (upgrade)'
    } else {
        $who = 'another process'
        if ($ownerName) { $who = "process '$ownerName'" }
        Write-Check FAIL "Port $Port" "already in use by $who; free it or choose another -Port"
    }
}

if ($PublishDir) {
    if (Test-Path -LiteralPath (Join-Path $PublishDir 'AiOps.Mcp.exe')) {
        Write-Check PASS 'Publish folder' "$PublishDir contains AiOps.Mcp.exe"
    } else {
        Write-Check FAIL 'Publish folder' "$PublishDir does not contain AiOps.Mcp.exe; pass the 'publish' folder of the extracted package"
    }
}

# ---- summary ----------------------------------------------------------------------------------

Write-Host ''
Write-Host ("{0} passed, {1} warning(s), {2} failed." -f $script:counts.PASS, $script:counts.WARN, $script:counts.FAIL)
if ($script:counts.FAIL -gt 0) {
    Write-Host 'Fix the FAIL lines before running Install-AiOpsMcp.ps1.'
    exit 1
}
exit 0
