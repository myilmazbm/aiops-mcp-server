# SCCM Configuration Item - REMEDIATION script.
# Copies the three JEA files from the content source into a locked-down staging directory and
# runs Register-AIOpsReader.ps1 from there. Runs as SYSTEM. Windows PowerShell 5.1. Idempotent.
#
# Configuration-item scripts are pasted into the console and cannot take arguments, so the
# environment-specific values are edited in the block below - and ONLY there.

#region ===================== EDIT THESE TWO VALUES, THEN PASTE INTO THE CI =====================
# UNC folder holding AIOpsReader.psrc, AIOpsReader.pssc and Register-AIOpsReader.ps1.
# The script runs as SYSTEM, so the share and NTFS ACL must grant read to the computer
# accounts ("Domain Computers") - and write to administrators only: whatever is in this
# folder is executed as SYSTEM on every server in the collection.
$source    = '<\\fileserver\share\AIOpsJea>'

# The MCP service account (gMSA, with the trailing $) or an AD group that contains it.
$principal = '<DOMAIN>\<gMSA-name>$'
#endregion ======================================================================================

$ErrorActionPreference = 'Stop'

# An unedited placeholder must stop here with a readable message instead of probing a share
# name that belongs to somebody else's environment.
if ($source -match '[<>]' -or $principal -match '[<>]') {
    throw "Remediate-AIOpsReader.ps1 has not been configured: replace the <placeholder> values of `$source and `$principal in the block at the top of the script before pasting it into the configuration item."
}

# Domain controllers are out of scope by policy (the register script refuses them too).
if ((Get-CimInstance Win32_OperatingSystem).ProductType -eq 2) { return }

$jeaFiles = @('AIOpsReader.psrc', 'AIOpsReader.pssc', 'Register-AIOpsReader.ps1')
foreach ($file in $jeaFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file))) {
        throw "Content source '$source' is unreachable or does not contain $file. Check the path and that this computer's account can read the share."
    }
}

# Staging directory. The register script is executed from here as SYSTEM, so the directory must
# not be writable - or replaceable - by anyone but SYSTEM and Administrators. C:\ProgramData
# lets every local user create subdirectories that they then own, so two things are enforced on
# both C:\ProgramData\AIOps and the staging directory below it:
#   * a directory that already exists must be a real directory (no junction) owned by SYSTEM or
#     BUILTIN\Administrators; anything else was created by somebody else and is refused, because
#     an owner can always rewrite the DACL again;
#   * the DACL is replaced with a protected one (inheritance off) built from well-known SIDs,
#     not names - the fleet has localized (Turkish) Windows installations.
# The root keeps read/traverse for Authenticated Users on that one folder only (nothing is
# inherited from it): the SCOM endpoint's transcript directory lives below it and is written by
# a non-administrator service identity.
function Set-AIOpsLockedDirectory {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [switch]$AllowTraverse
    )

    $trustedOwners = @('S-1-5-18', 'S-1-5-32-544')      # Local System, BUILTIN\Administrators

    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (-not $item.PSIsContainer) { throw "$Path exists but is not a directory; refusing to stage into it." }
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "$Path is a junction or symbolic link; refusing to stage into it."
        }
        $owner = $item.GetAccessControl([System.Security.AccessControl.AccessControlSections]::Owner).GetOwner([System.Security.Principal.SecurityIdentifier])
        if ($trustedOwners -notcontains $owner.Value) {
            throw "$Path already exists and is owned by $($owner.Value), not by SYSTEM or Administrators; refusing to run a script from it as SYSTEM. Inspect the directory, remove it (or run: icacls `"$Path`" /setowner *S-1-5-32-544) and let the baseline re-evaluate."
        }
    } else {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }

    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)         # disable inheritance, drop inherited ACEs
    $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in $trustedOwners) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier($sid)),
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            $inherit,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)))
    }
    if ($AllowTraverse) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')),      # Authenticated Users
            [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
            [System.Security.AccessControl.InheritanceFlags]::None,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)))
    }
    (Get-Item -LiteralPath $Path -Force).SetAccessControl($acl)
}

$root  = 'C:\ProgramData\AIOps'
$stage = Join-Path $root 'Deploy'
Set-AIOpsLockedDirectory -Path $root -AllowTraverse
Set-AIOpsLockedDirectory -Path $stage

# Only the three known files are staged, and each destination is deleted first: a file that
# was planted before the directory was locked keeps its own owner and DACL (or is a link to
# somewhere else), and Copy-Item -Force would write through it instead of replacing it.
# [System.IO.File]::Delete removes a link itself, never its target.
foreach ($file in $jeaFiles) {
    $target = Join-Path $stage $file
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target -Force).PSIsContainer) { throw "$target is a directory; refusing to stage over it." }
        [System.IO.File]::Delete($target)
    }
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $target -Force
}

# WinRM is NOT reconfigured: production servers already have it enabled and managed.
# Add -EnsureWinRm only for hosts where WinRM is not otherwise managed.
& (Join-Path $stage 'Register-AIOpsReader.ps1') -Principal $principal
