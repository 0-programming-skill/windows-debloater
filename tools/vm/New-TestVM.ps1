<#
.SYNOPSIS
    Creates the disposable Hyper-V VM used to test Debloater apply/revert and installs
    Windows 11 unattended.

.DESCRIPTION
    1. Loads the credential of the local administrator account (DPAPI file created with Export-Clixml).
    2. Generates an autounattend.xml (language/keyboard, LabConfig for CPU/TPM/Secure Boot/RAM outside
       the requirements, wipes disk 0, Windows 11 Pro, skips OOBE and the online account, creates the
       local account, automatic logon once, LocalAccountTokenFilterPolicy=1) and writes it to a small
       ISO created with IMAPI2FS (native to Windows, no ADK and no downloads).
    3. Creates the VM: Generation 2, Secure Boot (Microsoft Windows template), vTPM, fixed memory,
       dynamic VHDX, "Default Switch", Standard checkpoints, no automatic checkpoints, "Guest Service
       Interface" integration service enabled.
    4. Starts the VM and presses Enter a few times to get past "Press any key to boot from CD".
    5. (default) Waits for the installation to finish (PowerShell Direct responds), checks that the
       session is an elevated administrator, ejects the ISOs, DELETES the autounattend ISO (it holds
       the password in plain text) and creates the "clean-base" checkpoint.

    Standard checkpoints (not Production): the selftest restores the checkpoint after each run;
    Standard also saves memory, so the VM comes back in seconds already running, exactly in the
    previous state (running services, session, etc.), without restarting. Production uses VSS inside
    the guest and always comes back with the VM off (full boot on every run). For a disposable test
    VM, reproducibility and speed matter more than VSS application consistency.

    Requirements: Hyper-V enabled, user in the "Hyper-V Administrators" group (no elevation needed),
    Windows 11 ISO in the SAME language as -UILanguage (default pt-BR).

.PARAMETER IsoPath
    Windows 11 installation ISO (Microsoft). Must contain the -ImageName edition.

.PARAMETER Name
    VM name. Default: DebloaterTest.

.PARAMETER CredentialPath
    Local account credential (Export-Clixml). Default: tools/vm/.vmcred.xml.
    Create it with: Get-Credential -UserName teste -Message 'Test VM account' | Export-Clixml -Path tools\vm\.vmcred.xml

.PARAMETER MemoryGB
    Fixed VM memory in GB. Default: 8.

.PARAMETER CpuCount
    Virtual processors. Default: 4.

.PARAMETER DiskGB
    Maximum size of the dynamic VHDX in GB. Default: 80 (uses ~20-25 GB after installation).

.PARAMETER VmPath
    Base folder; the VM lives in <VmPath>\<Name>. Default: D:\Hyper-V if D: exists, otherwise C:\Hyper-V.
    Paths with spaces are accepted.

.PARAMETER SwitchName
    Virtual switch. Default: Default Switch (NAT with internet access).

.PARAMETER ImageName
    Image name in install.wim/esd. Default: Windows 11 Pro.

.PARAMETER UILanguage
    Setup and system language. Must exist in the ISO. Default: pt-BR.

.PARAMETER InputLocale
    Keyboard. Default: 0416:00010416 (Portuguese Brazil ABNT2).

.PARAMETER ProductKey
    Key used only to pick the edition. Default: generic Pro installation key (does not activate).
    Use '' to omit it.

.PARAMETER AdminGroup
    Name of the Administrators group in the ISO language. Default: derived from -UILanguage
    (pt-BR: Administradores; en-US: Administrators).

.PARAMETER NoWait
    Only creates and starts the VM; does not wait for the installation. Invoke-VmSelfTest.ps1 removes
    the autounattend ISO on its first run, if it is still attached.

.PARAMETER InstallTimeoutMinutes
    How long to wait for the installation. Default: 120.

.EXAMPLE
    .\tools\vm\New-TestVM.ps1 -IsoPath 'D:\ISOs\Win11_25H2_BrazilianPortuguese_x64.iso'

.EXAMPLE
    .\tools\vm\New-TestVM.ps1 -IsoPath C:\ISOs\Win11_en-US.iso -UILanguage en-US -InputLocale 0409:00000409 -NoWait
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$IsoPath,
    [string]$Name = 'DebloaterTest',
    [string]$CredentialPath,
    [ValidateRange(4, 64)][int]$MemoryGB = 8,
    [ValidateRange(2, 64)][int]$CpuCount = 4,
    [ValidateRange(64, 1024)][int]$DiskGB = 80,
    [string]$VmPath,
    [string]$SwitchName = 'Default Switch',
    [string]$ImageName = 'Windows 11 Pro',
    [string]$UILanguage = 'pt-BR',
    [string]$InputLocale = '0416:00010416',
    [string]$ProductKey,
    [string]$AdminGroup,
    [switch]$NoWait,
    [ValidateRange(10, 600)][int]$InstallTimeoutMinutes = 120
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DebloaterVm.psm1') -Force

if (-not $CredentialPath) { $CredentialPath = Get-DebloaterDefaultCredentialPath }
if (-not $VmPath) { $VmPath = Get-DebloaterDefaultVmPath }
if (-not $PSBoundParameters.ContainsKey('ProductKey')) { $ProductKey = Get-DebloaterGenericProKey }

# ---------- checks ----------
if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) {
    throw 'Hyper-V module not found. Enable Hyper-V (including "Hyper-V Module for Windows PowerShell").'
}
if (-not (Test-Path -LiteralPath $IsoPath -PathType Leaf)) { throw "ISO not found: '$IsoPath'." }
$IsoPath = (Resolve-Path -LiteralPath $IsoPath).ProviderPath
if (Get-VM -Name $Name -ErrorAction SilentlyContinue) {
    throw "A VM named '$Name' already exists. Use Remove-TestVM.ps1 or another -Name."
}
$cred = Import-DebloaterVmCredential -Path $CredentialPath
$null = Test-DebloaterUserName $cred.UserName
$switch = Get-DebloaterVmSwitch -Name $SwitchName

$vmDir = Join-Path $VmPath $Name
if (Test-Path -LiteralPath $vmDir) {
    if (Get-ChildItem -LiteralPath $vmDir -Force | Select-Object -First 1) {
        throw "The folder '$vmDir' already exists and is not empty. Delete it or use another -VmPath/-Name."
    }
}
$root = [System.IO.Path]::GetPathRoot($vmDir)
$drive = New-Object System.IO.DriveInfo($root)
$freeGB = [math]::Round($drive.AvailableFreeSpace / 1GB, 1)
# Installation ~25 GB + saved memory of the Standard checkpoints (MemoryGB each) + headroom.
$neededGB = 30 + 2 * $MemoryGB
if ($freeGB -lt $neededGB) {
    throw "Not enough free space on $root ($freeGB GB). At least $neededGB GB recommended. Use -VmPath on another drive."
}
Write-Host "Free space on ${root}: $freeGB GB (dynamic VHDX of up to $DiskGB GB)."

$vhdDir = Join-Path $vmDir 'Virtual Hard Disks'
$vhdPath = Join-Path $vhdDir "$Name.vhdx"
$unattendIso = Join-Path $vmDir "$Name-autounattend.iso"

# ---------- autounattend ----------
$null = New-Item -ItemType Directory -Path $vhdDir -Force
Set-Content -LiteralPath (Join-Path $vmDir (Get-DebloaterMarkerFileName)) -Value "Created by tools/vm/New-TestVM.ps1 on $(Get-Date -Format s). Remove-TestVM.ps1 deletes this folder." -Encoding UTF8

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("debloater-unattend-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage
try {
    $unattendParams = @{
        Credential   = $cred
        ComputerName = $Name
        ImageName    = $ImageName
        UILanguage   = $UILanguage
        InputLocale  = $InputLocale
        ProductKey   = $ProductKey
    }
    if ($AdminGroup) { $unattendParams.AdminGroup = $AdminGroup }
    $xml = New-DebloaterUnattendXml @unattendParams
    [System.IO.File]::WriteAllText((Join-Path $stage 'autounattend.xml'), $xml, (New-Object System.Text.UTF8Encoding($false)))
    $null = New-DebloaterIsoFile -SourceFolder $stage -IsoPath $unattendIso -VolumeName 'UNATTEND'
    Write-Host "autounattend ISO: $unattendIso (contains the password; it will be deleted at the end of the installation)"
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------- VM ----------
Write-Host "Creating the VM '$Name' in '$vmDir'..."
$vm = New-VM -Name $Name -Generation 2 -Path $VmPath -MemoryStartupBytes ([int64]$MemoryGB * 1GB) `
    -NewVHDPath $vhdPath -NewVHDSizeBytes ([uint64]$DiskGB * 1GB) -SwitchName $switch.Name
Set-VMMemory -VMName $Name -DynamicMemoryEnabled $false -StartupBytes ([int64]$MemoryGB * 1GB)
Set-VMProcessor -VMName $Name -Count $CpuCount
Set-VM -Name $Name -CheckpointType Standard -AutomaticCheckpointsEnabled $false `
    -AutomaticStartAction Nothing -AutomaticStopAction ShutDown `
    -SnapshotFileLocation $vmDir -SmartPagingFilePath $vmDir
Set-VMFirmware -VMName $Name -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows

try {
    Set-VMKeyProtector -VMName $Name -NewLocalKeyProtector
    Enable-VMTPM -VMName $Name
    Write-Host 'vTPM enabled.'
}
catch {
    # Creating the "local guardian" (HGS certificates) may require elevation the first time.
    $msg = ("Could not enable the vTPM: {0}`nThe installation continues (autounattend uses BypassTPMCheck). " +
        "To get a vTPM, run once in an elevated PowerShell (with the VM off): " +
        "Set-VMKeyProtector -VMName '{1}' -NewLocalKeyProtector; Enable-VMTPM -VMName '{1}'") -f $_.Exception.Message, $Name
    Write-Warning $msg
}

Enable-DebloaterGuestServices -VMName $Name

$windowsDvd = Add-VMDvdDrive -VMName $Name -Path $IsoPath -Passthru
$null = Add-VMDvdDrive -VMName $Name -Path $unattendIso -Passthru
Set-VMFirmware -VMName $Name -FirstBootDevice $windowsDvd

# ---------- installation ----------
Write-Host 'Starting the VM and pressing Enter to boot from the DVD...'
Start-VM -Name $Name
$vm = Get-VM -Name $Name
$keys = Send-DebloaterVmKey -VM $vm -Seconds 30
if ($keys -eq 0) {
    Write-Warning 'Could not send keys to the VM. If "Press any key to boot from CD or DVD" appears, open the VM (vmconnect) and press a key.'
}
Write-Host "The installation has started. Follow it with: vmconnect.exe localhost '$Name'"

if ($NoWait) {
    Write-Host 'Not waiting (-NoWait). When it finishes, run Invoke-VmSelfTest.ps1 (it removes the autounattend ISO).'
    return
}

$session = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $InstallTimeoutMinutes -IntervalSeconds 30 `
    -Activity "Installing Windows on the VM '$Name'"
try {
    $info = Get-DebloaterGuestInfo -Session $session
    Write-Host ("Windows installed: build {0} ({1} {2}); user {3}; model '{4}'." -f $info.Build, $info.Edition, $info.DisplayVersion, $info.User, $info.Model)
    if (-not $info.IsElevated) {
        Write-Warning ('The PowerShell Direct session is NOT elevated. The selftest needs administrator rights. Check that the account is ' +
            'in the Administrators group and that LocalAccountTokenFilterPolicy=1 (HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System).')
    }
}
finally {
    Remove-PSSession $session
}

# Lets the first logon (FirstLogonCommands) finish before the checkpoint.
Start-Sleep -Seconds 60
Remove-DebloaterSetupMedia -VMName $Name
# Removing the media turns the VM off and on again; wait for Windows to come back before the checkpoint.
Remove-PSSession (Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes 10)
Start-Sleep -Seconds 30
Checkpoint-VM -Name $Name -SnapshotName 'clean-base'
Write-Host "Done. VM '$Name' installed, running and with the 'clean-base' checkpoint. Next step: .\tools\vm\Invoke-VmSelfTest.ps1"
