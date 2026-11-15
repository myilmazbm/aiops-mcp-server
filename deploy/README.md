# Deployment

Agentless: nothing is installed on the fleet. The MCP server is one Windows Service on one
host; every fleet server only gets a read-only JEA session configuration (`AIOpsReader`).
One gMSA does everything — WinRM/JEA, SQL, LDAP — so there is no password anywhere.
Windows Firewall is never configured by any script here; the flows the network team must
open on the firewall appliances are listed in [§6](#6-network-flows-for-the-firewall-appliances).

## Quick start (new environment)

Replace every `<placeholder>`. Steps 1–8 in order; step 5 is optional.

**Prerequisites**

- An Active Directory domain with a KDS root key (`Get-KdsRootKey` returns one) — gMSAs need it.
- **MCP host**: a domain-joined Windows Server (not a domain controller), 64-bit. No .NET
  runtime or SDK is needed on it; the package is self-contained.
- **SQL Server** reachable from the MCP host, and a DBA for step 3.
- **Fleet servers**: WinRM already enabled (HTTP 5985), Windows PowerShell 5.1.
- **Network**: the flows in [§6](#6-network-flows-for-the-firewall-appliances) are open on the firewall appliances.
- **Build machine** (any Windows machine, only for step 1): .NET 10 SDK and a NuGet feed.
- Optional: SCOM management server (step 5); an embedding service and Qdrant (`search_inventory`).

### Step 1 — Build the package (build machine)

```powershell
.\build.ps1          # unit tests, publish, zip  ->  .\artifacts\AiOpsMcp-<version>.zip
```

Copy the zip to the MCP host and extract it. It contains `publish\` (the application) and
`deploy\` (this folder). All later commands are run from the extracted folder.

### Step 2 — Create the gMSA (domain admin)

```powershell
New-ADServiceAccount -Name 'gMSA-AIOps' -DNSHostName 'gmsa-aiops.<domain-fqdn>' `
    -PrincipalsAllowedToRetrieveManagedPassword '<mcp-host>$'

# then on the MCP host (needs the ActiveDirectory module: Install-WindowsFeature RSAT-AD-PowerShell)
Install-ADServiceAccount 'gMSA-AIOps'
Test-ADServiceAccount 'gMSA-AIOps'        # must print True
```

### Step 3 — Create the database (DBA)

```powershell
sqlcmd -S <sql-server> -E -i deploy\sql\CreateDatabase.sql
sqlcmd -S <sql-server> -E -d AiOpsInventory -Q 'CREATE LOGIN [<DOMAIN>\gMSA-AIOps$] FROM WINDOWS; CREATE USER [<DOMAIN>\gMSA-AIOps$] FOR LOGIN [<DOMAIN>\gMSA-AIOps$]; ALTER ROLE db_datareader ADD MEMBER [<DOMAIN>\gMSA-AIOps$]; ALTER ROLE db_datawriter ADD MEMBER [<DOMAIN>\gMSA-AIOps$]; ALTER ROLE db_ddladmin ADD MEMBER [<DOMAIN>\gMSA-AIOps$];'
```

The collation is pinned by the script; `db_ddladmin` is for the application's own schema migrations.

### Step 4 — Roll out the JEA endpoint to the fleet

Domain controllers never get the endpoint; both options refuse them. Registering restarts
WinRM once on each server (its remote sessions drop at that moment). Pick one option.

**Option A — SCCM Configuration Baseline (a fleet).** Keeps every server in sync afterwards.

```powershell
# 1. content source: readable by "Domain Computers", writable by administrators only
Copy-Item deploy\jea\AIOpsReader.psrc, deploy\jea\AIOpsReader.pssc, deploy\jea\Register-AIOpsReader.ps1 '<\\fileserver\share\AIOpsJea>'
# 2. set $source and $principal in the block at the top (the script refuses to run with placeholders)
notepad deploy\sccm\Remediate-AIOpsReader.ps1
```

Then create a Configuration Item with these settings, add it to a baseline, and deploy the
baseline to the server collection with remediation enabled (details in [§3](#3-fleet-jea-endpoint)):

| Setting | Value |
|---|---|
| Setting type / data type | Script / String |
| Discovery script | `deploy\sccm\Detect-AIOpsReader.ps1` (unchanged) |
| Remediation script | `deploy\sccm\Remediate-AIOpsReader.ps1` (edited in 2.) |
| Run scripts by using the logged on user credentials | **unchecked** (runs as SYSTEM) |
| Compliance rule | value equals `Compliant`; run the remediation script when noncompliant |

**Option B — `Install-JeaEndpoint.ps1` (a handful of servers, labs).** Run from any admin
workstation as an administrator of the target servers:

```powershell
.\deploy\jea\Install-JeaEndpoint.ps1 -ComputerName <server1-fqdn>,<server2-fqdn> -Principal '<DOMAIN>\gMSA-AIOps$'
```

It prints one line per server: `Registered`, `Skipped-DomainController` or `Failed` with the
reason. Add `-WhatIf` for a dry run that only connects and checks.

### Step 5 — SCOM endpoint (optional, on the SCOM management server)

```powershell
# 1. Operations Console -> Administration -> User Roles -> "Read-Only Operators" -> add the gMSA
# 2. copy deploy\jea\scom\ to the management server and run there, elevated:
.\Register-AIOpsScomReader.ps1 -Principal '<DOMAIN>\gMSA-AIOps$'
```

Enables the seven `get_scom_*` tools. Add `ScomServer = '<scom-server-fqdn>'` to the
parameters in step 6.

### Step 6 — Preflight (MCP host, elevated)

```powershell
$p = @{
    PublishDir       = '.\publish'
    ServiceAccount   = '<DOMAIN>\gMSA-AIOps$'
    SqlServer        = '<sql-server>'
    Servers          = '<server1-fqdn>', '<server2-fqdn>'      # the fleet servers of the nightly inventory sweep
    AllowedClientIps = '<llm-client-ip>'                       # who may call the MCP endpoint
    # optional:
    # ScomServer        = '<scom-server-fqdn>'
    # ScomSourceIps     = '<scom-server-ip>'
    # EmbeddingEndpoint = 'http://<embedding-host>:8000'
    # QdrantEndpoint    = 'http://<qdrant-host>:6334'
}
.\deploy\Test-AiOpsPrerequisites.ps1 @p
```

Read-only. One `PASS` / `WARN` / `FAIL` line per check; fix every `FAIL` before continuing.
The network checks run as you, not as the gMSA: they show that a port is open and that the
endpoint exists, not that the gMSA is let in — step 8 shows that.

### Step 7 — Install (MCP host, elevated, same PowerShell window)

```powershell
.\deploy\Install-AiOpsMcp.ps1 @p
```

Copies the files to `C:\AiOpsMcp`, writes `appsettings.Production.json`, registers and starts
the `AiOpsMcp` service under the gMSA, and checks that the endpoint answers on port 5100.

### Step 8 — Verify

```powershell
Get-Service AiOpsMcp                                                   # Running
Get-ChildItem C:\AiOpsMcp\logs | Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content -Tail 30
```

Then connect a client from one of the `AllowedClientIps` hosts ([§9](#9-clients)) and ask for
something that needs the fleet, for example the services of one server (`get_inventory`).
A result means the whole chain works as the gMSA: Kerberos, WinRM, the JEA endpoint.
`unreachable` or an access error: see [§8](#8-troubleshooting).

---

# Reference

## 1. Package contents

```
build.ps1                       (repo root) builds artifacts\AiOpsMcp-<version>.zip
deploy/
  README.md                     this file
  Test-AiOpsPrerequisites.ps1   read-only preflight for the MCP host
  Install-AiOpsMcp.ps1          MCP host: install or upgrade the Windows Service (gMSA)
  Uninstall-AiOpsMcp.ps1        MCP host: remove the Windows Service
  appsettings.Production.example.json   every setting, for reference (§2)
  jea/                          the fleet endpoint
    AIOpsReader.psrc              read-only role capability - THE security boundary
    AIOpsReader.pssc              session configuration (RestrictedRemoteServer, NoLanguage, transcripts)
    Register-AIOpsReader.ps1      idempotent registration, run locally on a server as SYSTEM/admin
    Install-JeaEndpoint.ps1       pushes the three files above to named servers without SCCM
    scom/                         the SCOM management server endpoint (one host, optional)
  sccm/                         Configuration Baseline scripts
    Detect-AIOpsReader.ps1        discovery: prints Compliant / NonCompliant
    Remediate-AIOpsReader.ps1     remediation: stages jea/* and runs the register script
  sql/
    CreateDatabase.sql          one-time database bootstrap (collation pinned)
```

| Script | Runs on / as | Changes |
|---|---|---|
| `build.ps1` | build machine | writes only under `artifacts\` |
| `Test-AiOpsPrerequisites.ps1` | MCP host, operator, elevated | nothing |
| `Install-AiOpsMcp.ps1` | MCP host, elevated | install directory, config, the `AiOpsMcp` service |
| `Uninstall-AiOpsMcp.ps1` | MCP host, elevated | removes the service; the directory only with `-RemoveFiles` |
| `Register-AIOpsReader.ps1` | fleet server, SYSTEM or admin | `AIOpsJea` module, transcript directory, `AIOpsReader` session configuration; restarts WinRM |
| `Install-JeaEndpoint.ps1` | admin workstation, operator | stages into `C:\ProgramData\AIOps\Deploy` on each target and runs the register script there |
| `Detect-` / `Remediate-AIOpsReader.ps1` | fleet server, SYSTEM (SCCM) | detect: nothing; remediate: as `Install-JeaEndpoint.ps1`, from the content source |
| `Register-AIOpsScomReader.ps1` | SCOM management server, admin | `AIOpsScomReader` session configuration; restarts WinRM |

## 2. Configuration (`appsettings.Production.json`)

The installer writes this file next to the executable from its parameters; you normally never
create it by hand. Settings without an installer parameter are edited in the file afterwards,
followed by `Restart-Service AiOpsMcp`. They survive upgrades: a re-run only merges the
parameters that were actually passed (`-SqlServer` and `-Servers` are mandatory and therefore
always rewritten). `appsettings.Production.example.json` lists every setting with its default;
JSON cannot carry comments, so the explanation is here. The file never contains a credential.

| Setting | Installer parameter | Meaning |
|---|---|---|
| `Urls` | `-Port` | Listen address, `http://0.0.0.0:5100`. |
| `Mcp:AllowedClientIps` | `-AllowedClientIps` | Exact IPs allowed to call the MCP endpoint. Empty = any source (see §5). No ranges, no host names. |
| `Alerts:AllowedSourceIps` | `-ScomSourceIps` | Exact IPs allowed to post to `/webhooks/scom`. Empty = any source. |
| `Alerts:ForwardUrl` | — | Orchestrator URL that receives normalized alerts. Empty = alerts are accepted and logged, not forwarded. |
| `Alerts:QueueCapacity`, `MaxBodyBytes`, `ForwardAttempts`, `ForwardRetryDelayMs` | — | In-memory alert queue size, request size limit (larger is rejected with 413), forward retries and delay. |
| `Scom:ManagementServer` | `-ScomServer` | FQDN of the SCOM management server. Empty disables the `get_scom_*` tools. |
| `Scom:JeaEndpointName` | — | `AIOpsScomReader`. |
| `Directory:LdapServer` | — | DC or domain DNS name for `resolve_servers`. Empty = the host's own domain. |
| `Directory:DefaultSearchBase` | — | Search base DN. Empty = the domain's default naming context. |
| `Directory:PageSize`, `TimeoutSeconds` | — | LDAP paging and bind/search timeout. |
| `Inventory:SqlConnectionString` | `-SqlServer` | Integrated security only. Empty disables persistence (`get_inventory` still works live). |
| `Inventory:Servers` | `-Servers` | Fleet servers of the nightly inventory sweep (DNS names). |
| `Inventory:NightlyHourLocal` | — | Local hour (0–23) at which the sweep starts. |
| `Inventory:EmbeddingEndpoint` | `-EmbeddingEndpoint` | OpenAI-compatible embedding service, e.g. `http://<embedding-host>:8000`. |
| `Inventory:QdrantEndpoint` | `-QdrantEndpoint` | e.g. `http://<qdrant-host>:6334`. `search_inventory` needs both endpoints; with either empty it is disabled. |
| `Inventory:EmbeddingModel`, `EmbeddingDimensions`, `QdrantCollection` | — | `bge-m3`, `1024`, `aiops-inventory`. A vector of another length is rejected. |
| `Remoting:JeaEndpointName`, `WinRmPort` | — | `AIOpsReader`, `5985`. |
| `Remoting:MaxConcurrency`, `PerServerTimeoutSeconds`, `MaxSessions`, `OpenTimeoutSeconds` | — | Fan-out tuning (20 / 30 / 100 / 15). Do not raise concurrency past the fleet's WinRM quotas. |

## 3. Fleet JEA endpoint

**Domain controllers are out of scope** (a JEA virtual account on a DC has DC privileges).
Keep them out of the collection; as a safety net the discovery script reports DCs as
Compliant, the remediation script exits on a DC, `Install-JeaEndpoint.ps1` skips them, and
the register script — which every rollout path ends in — refuses a DC unless
`-AllowDomainController` is passed. `resolve_servers` never returns DCs either.

**WinRM is not reconfigured.** The register script only adds a session configuration beside
the existing ones. `-EnsureWinRm` exists for hosts without managed WinRM and is not used by
either rollout option.

**Do not edit `AIOpsReader.psrc` per environment** — only the principal differs, and it is a
parameter. Any change to the role's cmdlet or parameter lists is a security decision.

**The baseline compares file content, not just existence.** `Detect-AIOpsReader.ps1` embeds
the SHA256 of `AIOpsReader.psrc` and reports NonCompliant when the deployed file differs, so
a changed role capability redeploys. Whenever `AIOpsReader.psrc` changes, in the same commit:

```powershell
(Get-FileHash deploy\jea\AIOpsReader.psrc -Algorithm SHA256).Hash    # -> $expectedPsrcHash in Detect-AIOpsReader.ps1
```

then copy the new `.psrc` to the content source and paste the new discovery script into the
Configuration Item. A unit test fails the build while the two disagree. Servers set up with
`Install-JeaEndpoint.ps1` are re-pushed by running it again.

**Staging.** Both rollout options stage the three files in `C:\ProgramData\AIOps\Deploy` and
run the register script from there with administrative rights. That directory and its parent
are therefore given a protected DACL (SYSTEM + Administrators, inheritance off; the parent
additionally keeps read/traverse for Authenticated Users on that folder alone), and a
directory that already exists with any other owner than SYSTEM or Administrators is refused
rather than trusted (`Install-JeaEndpoint.ps1` also accepts the operator as owner). The
generated session configuration file is written under `Program Files`, not `%TEMP%`.

**Transcripts** land in `C:\ProgramData\AIOps\Transcripts`, locked by the register script to
SYSTEM + Administrators with inheritance disabled (the inherited ProgramData ACL would let
any local user read full command output).

**How `Install-JeaEndpoint.ps1` survives the WinRM restart.** Registering restarts WinRM and
kills the session that started it, so the registration runs as a one-shot scheduled task under
SYSTEM (`AIOps-InstallJeaEndpoint`) — the same identity the SCCM baseline uses, and the full
local token the `WSMan:` provider requires. The task writes its result to a status file; the
script reconnects until that file appears or `-TimeoutSeconds` (default 180) passes, deletes
the task, then probes the endpoint from the workstation.
Log on the target: `C:\ProgramData\AIOps\Deploy\Install-JeaEndpoint.log`.

## 4. SCOM endpoint

Enables the seven `get_scom_*` tools — alerts, health, historical performance, discovered
inventory, alert root cause, monitor state changes and the health of monitoring itself.
Everything SCOM monitors, including domain controllers (otherwise out of scope), becomes
readable through the SDK. Not part of the fleet baseline: one host, registered once.

This role runs as the caller (not a virtual account) because SCOM authorizes by SCOM user
role; hence the gMSA must be in "Read-Only Operators". No raw cmdlet is visible: the caller
only sees seven `Get-AIOps*` wrapper functions and the session is `NoLanguage`. Because the
session runs as the caller, the gMSA also gets Modify on the transcript directory there
(SYSTEM + Administrators otherwise); without it JEA cannot start the mandatory transcript
and the session fails to open.

The management server is **not** in the fleet collection, so the WinRM flow to it is a
separate row in §6 — row 1 does not cover it.

## 5. MCP host: install, upgrade, rollback

`Install-AiOpsMcp.ps1` stops the service, copies the files, writes or merges
`appsettings.Production.json`, grants the gMSA modify rights on the install directory (logs),
registers the `AiOpsMcp` service (automatic start, restart on failure), starts it and sends
one MCP `initialize` request to the loopback address.

**Upgrade**: build a new package, extract it on the host, re-run the installer with the same
parameters. Configuration and logs are kept. The previous installation is copied aside before
the files are replaced; a failed copy is rolled back from it and the service is restarted.

**Rollback to an earlier version**: re-run the installer with `-PublishDir` pointing at the
`publish\` folder of the earlier package (keep the previous zip). The database schema is
migrated forward by the application and is not rolled back by this.

**`-AllowedClientIps` (`Mcp:AllowedClientIps`) is the only application-level access control
on this endpoint.** The MCP endpoint has no authentication by design (no credential may live
in config), and reaching port 5100 means read access to every server in the fleet, so list
the LLM client hosts explicitly. An empty list means *any* source may call every tool; the
service logs a warning at startup in that case and so does the installer. The SCOM webhook
has its own separate list (`-ScomSourceIps` → `Alerts:AllowedSourceIps`). When the list is
set, the installer's loopback check is answered with HTTP 403 — that is a healthy endpoint
and the script reports it as such; add `127.0.0.1` only if you want that check to reach the
MCP handler itself.

## 6. Network flows (for the firewall appliances)

TCP unless the row says otherwise; scope every rule to the named source, never "any".
Nothing connects from the fleet toward the MCP host.

| # | Source → Destination | Port | Purpose |
|---|---|---|---|
| 1 | MCP host → every fleet server | 5985/TCP | WinRM/JEA (Kerberos, encrypted) |
| 2 | MCP host → SCOM management server | 5985/TCP | WinRM/JEA to `AIOpsScomReader` (§4; this host is not in the fleet collection, so row 1 does not cover it) |
| 3 | MCP host → domain controllers | 88/UDP + 88/TCP, 389/UDP + 389/TCP | Kerberos (starts on UDP, falls back to TCP for large tickets) and LDAP: the serverless bind locates a DC with CLDAP over UDP 389, then binds over TCP 389 |
| 4 | MCP host → SQL Server | 1433/TCP | inventory snapshots |
| 5 | LLM client host(s) → MCP host | 5100/TCP | MCP endpoint (no authentication by design; keep it to these hosts and to `Mcp:AllowedClientIps`) |
| 6 | SCOM management server(s) → MCP host | 5100/TCP | `/webhooks/scom` |
| 7 | MCP host → embedding host | 8000/TCP | embeddings (only with `search_inventory`) |
| 8 | MCP host → Qdrant host | 6334/TCP | vector index (only with `search_inventory`) |

LDAPS (636) and the global catalog (3268/3269) were **dropped** from the DC row: the code
binds over plain LDAP on the default port 389 and never queries the global catalog. Add them
only if `Directory:LdapServer` is later pointed at a GC or switched to LDAPS.

Rollout-time only, not needed by the running system: `Install-JeaEndpoint.ps1` connects from
the admin workstation to the target servers on 5985/TCP; the SCCM remediation reads the
content source share from each server (SMB, 445/TCP).

## 7. Uninstall

**MCP host** (elevated):

```powershell
.\deploy\Uninstall-AiOpsMcp.ps1                 # stops and deletes the service; keeps C:\AiOpsMcp
.\deploy\Uninstall-AiOpsMcp.ps1 -RemoveFiles    # also deletes the install directory (config and logs)
```

The equivalent by hand: `Stop-Service AiOpsMcp; sc.exe delete AiOpsMcp; Remove-Item C:\AiOpsMcp -Recurse -Force`.

**JEA endpoint on a fleet server** (elevated, locally on the server — unregistering restarts
WinRM and drops remote sessions). With SCCM, remove the baseline deployment first, otherwise
the next evaluation registers the endpoint again.

```powershell
Unregister-PSSessionConfiguration -Name AIOpsReader -Force
Remove-Item "$env:ProgramFiles\WindowsPowerShell\Modules\AIOpsJea" -Recurse -Force
Remove-Item 'C:\ProgramData\AIOps' -Recurse -Force      # transcripts and staging - archive the transcripts first if they must be retained
```

**SCOM management server**: the same three commands with `-Name AIOpsScomReader`, and remove
the gMSA from the "Read-Only Operators" user role.

**The rest**, when the environment is retired:

```powershell
sqlcmd -S <sql-server> -E -Q 'DROP DATABASE AiOpsInventory; DROP LOGIN [<DOMAIN>\gMSA-AIOps$];'
Uninstall-ADServiceAccount 'gMSA-AIOps'         # on the MCP host
Remove-ADServiceAccount 'gMSA-AIOps'            # domain admin
```

plus the Qdrant collection (`aiops-inventory`) if `search_inventory` was used, and the
firewall rules of §6.

## 8. Troubleshooting

**A tool reports a server as `unreachable`, or access is denied.** Run the preflight with
that server in `-Servers`. `WinRM: FAIL` → flow 1 of §6 is not open from the MCP host, or
WinRM is not running there. `AIOpsReader: FAIL` → the endpoint is not registered on that
server (step 4; a domain controller never gets it). Both `PASS` but the service is still
refused → the endpoint was registered for a different principal: re-run step 4 with the
exact account the service runs as (`sc.exe qc AiOpsMcp`), or check that the server is listed
by DNS name, not IP (Kerberos). The server's own view is in
`C:\ProgramData\AIOps\Transcripts` and the Microsoft-Windows-WinRM/Operational event log.

**The service does not start, or the installer ends with "MCP endpoint did not answer".**
Look at the newest file in `C:\AiOpsMcp\logs` first, then the System event log (source
Service Control Manager). Usual causes: the host may not retrieve the gMSA password
(`Test-ADServiceAccount` prints False — fix the gMSA's
`PrincipalsAllowedToRetrieveManagedPassword`; a new group membership of the host needs a
reboot); the account lacks the "Log on as a service" right (event 7041 — grant it by policy);
the gMSA has no login or roles on the database (step 3); the port is taken by another process
(preflight `Port` line). A client that gets HTTP 403 from a running service is not in
`Mcp:AllowedClientIps` — the rejected address is in the log.

**The SCCM baseline stays NonCompliant or the remediation reports an error.** The
remediation's error text says which: *"has not been configured"* → the placeholders at the
top of `Remediate-AIOpsReader.ps1` were not replaced before pasting; *"Content source … is
unreachable"* → the share or NTFS ACL does not grant read to the computer accounts; *"already
exists and is owned by …"* → `C:\ProgramData\AIOps` or its `Deploy` subfolder was created by
someone other than SYSTEM/Administrators — inspect it, then remove it or reset its owner as
the message says. Remediation succeeds but the server is NonCompliant again at the next
evaluation → the `.psrc` in the content source is not the one whose hash is embedded in the
discovery script (§3). If scripts are blocked outright, check the PowerShell execution
policy the configuration item runs under (Client Settings → Computer Agent).

## 9. Clients

- **Claude Code**: `claude mcp add --transport http aiops http://<mcp-host>:5100/`
- **Onyx**: Admin Panel → MCP Actions → URL `http://<mcp-host>:5100/`, No Auth; add
  `http://<mcp-host>:5100/webhooks/scom` as the SCOM command-channel target.

The full integration suite (every tool over the real JEA endpoint, including the proof that
JEA rejects write cmdlets, and the collect → SQL round trip) needs the repository and the
SDK, one server that has the endpoint, and — because the tests connect as whoever runs them —
an account that is a principal of that endpoint:

```powershell
$env:AIOPS_LAB_SERVER = '<fleet-server-fqdn>'
$env:AIOPS_SQL_CONNECTION = 'Server=<sql-server>;Database=AiOpsInventory;Integrated Security=true;TrustServerCertificate=true'
dotnet test tests/AiOps.Mcp.LabTests
```
