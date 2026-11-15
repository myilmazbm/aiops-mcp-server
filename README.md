# AIOps MCP Server (agentless)

An [MCP](https://modelcontextprotocol.io) server that lets an LLM run live, read-only
diagnostics against a Windows server fleet. Nothing is installed on the fleet: every call
goes over WinRM to a locked-down [JEA](https://learn.microsoft.com/powershell/scripting/security/remoting/jea/overview)
endpoint, and the JEA role capability — not the server's code — decides what may run.

```
User / SCOM alert → LLM → MCP server (this repo) → JEA over WinRM → fleet
                                 ↘ SQL Server (inventory snapshot) + Qdrant (fleet search)
```

## What it gives the LLM

| Tool | Purpose |
|---|---|
| `resolve_servers` | Find servers in Active Directory by name pattern or OU (domain controllers excluded) |
| `get_events` | Windows event logs, filtered on the target with `Get-WinEvent -FilterXml` |
| `get_counters` | Performance counters as min/max/avg/last, locale-independent |
| `get_inventory` | Services, software, listening ports, GPOs, hotfixes, certificates, scheduled tasks |
| `search_inventory` | Semantic search across the fleet's inventory snapshots (optional: Qdrant + embeddings) |
| `run_readonly` | One allow-listed cmdlet on a server's JEA endpoint |
| `get_scom_*` (7 tools) | Alerts, health, performance history, discovered inventory, alert root cause, monitor state changes and the health of monitoring itself, read from System Center Operations Manager (optional) |

## Design rules

- **Read-only and agentless.** The fleet needs one thing: the `AIOpsReader` JEA endpoint.
- **JEA is the security boundary.** The server never filters command text; every visible
  cmdlet carries an explicit parameter allow-list in `deploy/jea/AIOpsReader.psrc`.
- **No secrets anywhere.** The service runs as a gMSA and uses integrated authentication
  for WinRM, LDAP and SQL. There is nothing to put in a vault.
- **No central log store.** The only persisted data is one inventory snapshot per server.
- **Bounded output.** Every tool caps and truncates what it returns to protect the LLM context.
- **Windows Firewall is never touched.** Required network flows are documented for the
  network team instead.
- **Domain controllers are out of scope** for the fleet endpoint; they are read through SCOM.

## Deploy

Everything needed to stand this up in a new environment is in [`deploy/`](deploy/README.md).
The short version:

```powershell
.\build.ps1                                   # test, publish, and zip a self-contained package
.\deploy\Test-AiOpsPrerequisites.ps1 ...      # read-only preflight on the MCP host
.\deploy\Install-AiOpsMcp.ps1 ...             # install or upgrade the Windows service
```

The JEA endpoint is rolled out to the fleet either by an SCCM configuration baseline
(`deploy/sccm/`) or, for a handful of servers, with `deploy/jea/Install-JeaEndpoint.ps1`.
Start with the quick start in [`deploy/README.md`](deploy/README.md).

## Develop

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build
dotnet test tests/AiOps.Mcp.Tests            # fast, mocked; runs in CI
dotnet run --project src/AiOps.Mcp            # stdio transport, for local debugging
dotnet run --project src/AiOps.Mcp -- --http  # streamable HTTP transport
```

`tests/AiOps.Mcp.LabTests` runs against real servers and is skipped unless its
`AIOPS_*` environment variables are set; it never runs in CI.

| Path | Contents |
|---|---|
| `src/AiOps.Mcp` | MCP host, tools, session pool, inventory collection |
| `src/AiOps.Contracts` | Tool input/output contracts |
| `deploy/` | JEA role and session configuration, SCCM baseline scripts, installer, SQL |
| `tests/` | Unit tests and lab integration tests |

## Security notes

The MCP endpoint itself has no authentication by design, so reaching its port is equivalent
to read access on every server it can reach. Restrict it at the network layer and set
`Mcp:AllowedClientIps`. Changing `deploy/jea/*.psrc` changes the security boundary: review
such changes accordingly, and regenerate the hash in `deploy/sccm/Detect-AIOpsReader.ps1`
(a unit test fails until you do).

## License

[MIT](LICENSE)
