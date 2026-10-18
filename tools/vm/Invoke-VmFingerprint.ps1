<#
.SYNOPSIS
    Runs "Debloater.Cli fingerprint create" inside the test VM and brings the fingerprints
    (.json.gz) back to tests\fingerprints. Read-only inside the VM's Windows.

.DESCRIPTION
    1. dotnet publish of the CLI (Release, win-x64, self-contained) into a temporary folder, zipped.
    2. Starts/resumes the VM and waits for PowerShell Direct. An elevated session can list provisioned
       appx packages and all capabilities (DISM); without elevation the script warns and continues.
    3. Creates the "before-fingerprint-<date-time>" checkpoint (copying the package to the VM disk is the
       only change; the checkpoint undoes it).
    4. Copies the package to C:\DebloaterFingerprint\<date-time>, runs "fingerprint create" once per
       -Binaries mode (default, all) and copies each .json.gz (and the log) to -OutDir named
       vm-<build>-<mode>.json.gz.
    5. Restores the checkpoint and deletes it (unless -KeepChanges), even if something fails midway.

    Fingerprints must NOT be committed (tests/fingerprints is in .gitignore).

.PARAMETER Name
    VM name. Default: DebloaterTest.

.PARAMETER CredentialPath
    Credential of the VM's local account (Export-Clixml). Default: tools/vm/.vmcred.xml.

.PARAMETER Binaries
    Binary scan modes (one fingerprint per mode). Default: default, all.

.PARAMETER OutDir
    Where to save the fingerprints and the log. Default: tests/fingerprints (git-ignored).

.PARAMETER PublishDir
    Uses an existing publish output (folder with Debloater.Cli.exe) instead of running dotnet publish.

.PARAMETER KeepChanges
    Does not restore the checkpoint (the package stays in the VM; the checkpoint is kept to roll back manually).

.PARAMETER BootTimeoutMinutes
    How long to wait for PowerShell Direct to respond. Default: 10.

.EXAMPLE
    .\tools\vm\Invoke-VmFingerprint.ps1

.EXAMPLE
    .\tools\vm\Invoke-VmFingerprint.ps1 -Binaries all
#>
[CmdletBinding()]
param(
    [string]$Name = 'DebloaterTest',
    [string]$CredentialPath,
    [ValidateSet('default', 'all', 'none')][string[]]$Binaries = @('default', 'all'),
    [string]$OutDir,
    [string]$PublishDir,
    [switch]$KeepChanges,
    [ValidateRange(1, 120)][int]$BootTimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DebloaterVm.psm1') -Force

$repo = Get-DebloaterRepoRoot
if (-not $CredentialPath) { $CredentialPath = Get-DebloaterDefaultCredentialPath }
if (-not $OutDir) { $OutDir = Join-Path $repo 'tests\fingerprints' }
$null = New-Item -ItemType Directory -Path $OutDir -Force

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$checkpointName = "before-fingerprint-$stamp"
$guestDir = "C:\DebloaterFingerprint\$stamp"
$cred = Import-DebloaterVmCredential -Path $CredentialPath
if (-not (Get-VM -Name $Name -ErrorAction SilentlyContinue)) { throw "VM '$Name' not found. Create it with New-TestVM.ps1." }

$logLines = New-Object System.Collections.Generic.List[string]
function Write-Log([string]$Line) { Write-Host $Line; $logLines.Add($Line) }

# ---------- 1. publish ----------
$work = Join-Path ([System.IO.Path]::GetTempPath()) "debloater-fingerprint-$stamp"
$null = New-Item -ItemType Directory -Path $work -Force
$zip = Join-Path $work 'debloater.zip'
$copied = @()
try {
    if ($PublishDir) {
        $publish = (Resolve-Path -LiteralPath $PublishDir).ProviderPath
    }
    else {
        $publish = Join-Path $work 'publish'
        $dotnet = Get-DebloaterDotNet
        Write-Host 'Publishing the CLI (Release, win-x64, self-contained)...'
        & $dotnet publish (Join-Path $repo 'src\Debloater.Cli\Debloater.Cli.csproj') -c Release -r win-x64 --self-contained true -o $publish --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'Debloater.Cli.exe'))) { throw "Debloater.Cli.exe not found in '$publish'." }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Fastest, $false)
    Write-Host ("Package: {0:N1} MB" -f ((Get-Item -LiteralPath $zip).Length / 1MB))

    # ---------- 2. VM up ----------
    switch ((Get-VM -Name $Name).State) {
        'Running' { }
        'Paused' { Resume-VM -Name $Name }
        default { Write-Host "Starting the VM '$Name'..."; Start-VM -Name $Name }
    }
    Remove-DebloaterSetupMedia -VMName $Name   # turns the VM off before touching DVDs, if there are any
    $s0 = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
    try { $info = Get-DebloaterGuestInfo -Session $s0 } finally { Remove-PSSession $s0 }
    Write-Log ("VM: Windows build {0} ({1} {2}), user {3}, elevated: {4}." -f $info.Build, $info.Edition, $info.DisplayVersion, $info.User, $info.IsElevated)
    if (-not $info.IsElevated) { Write-Warning 'Session is not elevated: provisioned appx packages and available capabilities are left out.' }

    # ---------- 3. checkpoint ----------
    Write-Host "Creating the checkpoint '$checkpointName'..."
    Checkpoint-VM -Name $Name -SnapshotName $checkpointName
    try {
        $session = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
        try {
            Invoke-Command -Session $session -ScriptBlock { param($d) $null = New-Item -ItemType Directory -Path $d -Force } -ArgumentList $guestDir
            Copy-Item -LiteralPath $zip -Destination "$guestDir\debloater.zip" -ToSession $session
            Invoke-Command -Session $session -ScriptBlock {
                param($d)
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                [System.IO.Compression.ZipFile]::ExtractToDirectory("$d\debloater.zip", "$d\app")
            } -ArgumentList $guestDir

            # ---------- 4. fingerprint per mode ----------
            foreach ($mode in $Binaries) {
                $fileName = "vm-$($info.Build)-$mode.json.gz"
                $guestFile = "$guestDir\$fileName"
                Write-Log "Running in the VM: Debloater.Cli fingerprint create $fileName --binaries $mode"
                Invoke-Command -Session $session -ScriptBlock {
                    param($d, $out, $m)
                    try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
                    & "$d\app\Debloater.Cli.exe" fingerprint create $out --binaries $m 2>&1 | ForEach-Object { "$_" }
                    "__EXITCODE__=$LASTEXITCODE"
                } -ArgumentList $guestDir, $guestFile, $mode | ForEach-Object {
                    if ("$_" -notmatch '^__EXITCODE__=') { Write-Log "  $_" }
                }
                $exists = Invoke-Command -Session $session -ScriptBlock { param($p) Test-Path -LiteralPath $p } -ArgumentList $guestFile
                if (-not $exists) { Write-Warning "The VM did not produce $fileName."; continue }
                $local = Join-Path $OutDir $fileName
                Copy-Item -FromSession $session -LiteralPath $guestFile -Destination $local -Force
                $copied += $local
                Write-Log ("Copied: {0} ({1:N0} KB)" -f $local, ((Get-Item -LiteralPath $local).Length / 1KB))
            }
        }
        finally {
            if ($session) { Remove-PSSession $session -ErrorAction SilentlyContinue }
        }
    }
    finally {
        # ---------- 5. restore ----------
        if ($KeepChanges) {
            Write-Host "Changes kept (-KeepChanges). To roll back: Restore-VMCheckpoint -VMName '$Name' -Name '$checkpointName' -Confirm:`$false"
        }
        else {
            Write-Host "Restoring the checkpoint '$checkpointName'..."
            Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
            Restore-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false
            Remove-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false
            Write-Host "VM restored (state: $((Get-VM -Name $Name).State))."
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    if ($logLines.Count -gt 0) {
        $log = Join-Path $OutDir "vm-fingerprint-$stamp.log"
        [System.IO.File]::WriteAllLines($log, $logLines, (New-Object System.Text.UTF8Encoding($true)))
    }
}

if ($copied.Count -eq 0) { Write-Warning 'No fingerprint copied.'; exit 2 }
Write-Host ''
Write-Host 'Fingerprints:'
$copied | ForEach-Object { Write-Host "  $_" }
exit 0
