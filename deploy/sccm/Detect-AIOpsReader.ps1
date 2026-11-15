# SCCM Configuration Item — DISCOVERY script.
# Returns 'Compliant' when the AIOpsReader JEA endpoint is registered AND the deployed role
# capability file is byte-identical to the one in this repo, otherwise 'NonCompliant'.
# Existence alone is not enough: AIOpsReader.psrc is the security boundary, so a tightened
# (or tampered) role capability must redeploy on every server, which only a content check
# can trigger — an existence check reports Compliant forever and the old, more permissive
# role stays in place.
# Domain controllers are out of scope by policy and report 'Compliant' so no remediation
# is ever triggered on them (the register script refuses DCs as well).
# Run as SYSTEM (Get-PSSessionConfiguration requires elevation). Windows PowerShell 5.1.
# Exactly one value is written to stdout on every path (SCCM reads compliance from stdout).
$ErrorActionPreference = 'SilentlyContinue'

# SHA256 of deploy/jea/AIOpsReader.psrc as shipped in this repo.
# REGENERATE THIS VALUE WHENEVER AIOpsReader.psrc CHANGES, in the same commit — otherwise the
# baseline reports Compliant on servers still running the previous role capability:
#     (Get-FileHash deploy\jea\AIOpsReader.psrc -Algorithm SHA256).Hash
# The remediation script stages the files from the UNC content source, so the expected value
# cannot be read from there at discovery time; it is embedded here on purpose.
$expectedPsrcHash = '09C1F470ECC00A4D9A158849C42E811C09E8ECAD996D30316AD1B85FFB48B1E5'

$result = 'NonCompliant'

if ((Get-CimInstance Win32_OperatingSystem).ProductType -eq 2) {
    $result = 'Compliant'   # DC: out of scope, never remediate
} else {
    $endpoint = Get-PSSessionConfiguration -Name 'AIOpsReader'
    $psrc     = Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules\AIOpsJea\RoleCapabilities\AIOpsReader.psrc'

    if ($endpoint -and (Test-Path $psrc)) {
        $actualPsrcHash = (Get-FileHash -Path $psrc -Algorithm SHA256).Hash
        # Ordinal compare: hex digests must never go through the current culture's casing
        # rules (Turkish-locale I/i would break a culture-sensitive comparison).
        if ($actualPsrcHash -and [string]::Equals($actualPsrcHash, $expectedPsrcHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            $result = 'Compliant'
        }
    }
}

$result
