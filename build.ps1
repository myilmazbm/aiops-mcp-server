<#
.SYNOPSIS
    Builds a deployable AiOps MCP package: one zip that holds the self-contained application
    and the deploy scripts, ready to be carried to a host with no .NET SDK and no internet.

.DESCRIPTION
    1. runs the unit tests (tests/AiOps.Mcp.Tests) and stops when one fails (-SkipTests omits
       this). The lab integration tests (tests/AiOps.Mcp.LabTests) need real servers and are
       never run from here;
    2. dotnet publish src/AiOps.Mcp -c Release -r win-x64 --self-contained  ->  <OutputDir>\publish
    3. copies deploy\  ->  <OutputDir>\deploy
    4. zips both as  <OutputDir>\AiOpsMcp-<version>.zip

    Needs the .NET 10 SDK and, for the first restore, access to a NuGet feed. Works in Windows
    PowerShell 5.1 and PowerShell 7. Nothing outside <OutputDir> is written.

.PARAMETER OutputDir
    Where the package is assembled. Default: .\artifacts next to this script.

.PARAMETER Version
    Label used in the zip file name. Default: the output of "git describe" when the folder is
    a git checkout, otherwise the current date and time (yyyyMMdd-HHmm).

.PARAMETER SkipTests
    Do not run the unit tests first.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Version 1.2.0 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$OutputDir = '',
    [string]$Version = '',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'artifacts' }
# Resolved against the PowerShell location (not the process directory, which can differ).
$OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir).TrimEnd('\')

$project     = Join-Path $repoRoot 'src\AiOps.Mcp'
$testProject = Join-Path $repoRoot 'tests\AiOps.Mcp.Tests'
$deploySrc   = Join-Path $repoRoot 'deploy'
$publishDir  = Join-Path $OutputDir 'publish'
$deployDir   = Join-Path $OutputDir 'deploy'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK (dotnet) was not found on PATH. Install the .NET 10 SDK on the build machine; the target host needs no SDK.'
}
foreach ($required in @($project, $testProject, $deploySrc)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Expected folder not found: $required" }
}
# <OutputDir>\publish and <OutputDir>\deploy are deleted and rebuilt below, so the output
# folder must not be the repository root (its deploy\ is the source) or a source folder.
if ($OutputDir -ieq $repoRoot.TrimEnd('\') -or ($OutputDir + '\').StartsWith((Join-Path $repoRoot 'src') + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
    ($OutputDir + '\').StartsWith($deploySrc + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDir '$OutputDir' is the repository root or one of its source folders; choose a separate folder such as .\artifacts."
}

if (-not $Version) {
    if ((Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
        $described = $null
        try { $described = (& git -C $repoRoot describe --tags --always --dirty 2>$null) } catch { $described = $null }
        if ($LASTEXITCODE -eq 0 -and $described) { $Version = ([string]$described).Trim() }
    }
    if (-not $Version) { $Version = Get-Date -Format 'yyyyMMdd-HHmm' }
}
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]*$') {
    throw "Version '$Version' cannot be used in a file name; use letters, digits, '.', '-', '_' or '+'."
}
$zipPath = Join-Path $OutputDir "AiOpsMcp-$Version.zip"

# 1. Unit tests (mocked, no network). Never the LabTests project.
if ($SkipTests) {
    Write-Host '[1/4] Unit tests skipped (-SkipTests)'
} else {
    Write-Host '[1/4] Running unit tests'
    & dotnet test $testProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed (dotnet test exit code $LASTEXITCODE); no package was built." }
}

# 2. Publish. The output folder is emptied first so that files of an earlier build cannot
#    end up in the package.
Write-Host "[2/4] Publishing to $publishDir"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
foreach ($stale in @($publishDir, $deployDir)) {
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Recurse -Force }
}
& dotnet publish $project -c Release -r win-x64 --self-contained -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'AiOps.Mcp.exe'))) {
    throw "dotnet publish reported success but $publishDir does not contain AiOps.Mcp.exe."
}

# Environment configuration never travels in a package: a developer's local
# appsettings.Production.json holds host names and allow-lists of one environment.
foreach ($localConfig in @(Get-ChildItem -LiteralPath $publishDir -Filter 'appsettings.*.json' -File |
        Where-Object { $_.Name -ne 'appsettings.json' })) {
    Write-Warning "Removed $($localConfig.Name) from the package (environment-specific configuration is created by the installer on the target host)."
    Remove-Item -LiteralPath $localConfig.FullName -Force
}

# 3. Deploy scripts next to the binaries.
Write-Host "[3/4] Copying deploy scripts to $deployDir"
Copy-Item -LiteralPath $deploySrc -Destination $deployDir -Recurse -Force

# 4. Zip. Written entry by entry with forward slashes; the archive root holds publish/ and
#    deploy/.
Write-Host "[4/4] Creating $zipPath"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

$fileCount = 0
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $prefixLength = $OutputDir.TrimEnd('\').Length + 1
    foreach ($folder in @($publishDir, $deployDir)) {
        foreach ($file in (Get-ChildItem -LiteralPath $folder -Recurse -File -Force)) {
            $entryName = $file.FullName.Substring($prefixLength).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $fileCount++
        }
    }
} finally {
    $zip.Dispose()
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
Write-Host ''
Write-Host "Package: $zipPath ($fileCount files, $sizeMb MB)"
Write-Host 'Next: copy the zip to the MCP host, extract it, and follow deploy\README.md from step 2.'
