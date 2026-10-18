<#
.SYNOPSIS
    Deletes the Debloater test VM: turns it off, removes the VM and its checkpoints, and deletes the
    disks (VHDX/AVHDX) and the folder created by New-TestVM.ps1.

.DESCRIPTION
    The VM folder (<VmPath>\<Name>) is only deleted entirely if it has the ".debloater-testvm" marker
    file that New-TestVM.ps1 creates. Without the marker, only the disk files attached to the VM and
    the autounattend ISO are deleted, and it warns about the rest.
    Asks for confirmation (ConfirmImpact High); use -Force to skip the prompt.

.PARAMETER Name
    VM name. Default: DebloaterTest.

.PARAMETER Force
    Does not ask for confirmation.

.EXAMPLE
    .\tools\vm\Remove-TestVM.ps1

.EXAMPLE
    .\tools\vm\Remove-TestVM.ps1 -Name DebloaterTest -Force
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$Name = 'DebloaterTest',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DebloaterVm.psm1') -Force
if ($Force -and -not $PSBoundParameters.ContainsKey('Confirm')) { $ConfirmPreference = 'None' }

$vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
if (-not $vm) { Write-Host "VM '$Name' does not exist; nothing to do."; return }

# Collects the paths BEFORE removing the VM.
$disks = @(Get-VMHardDiskDrive -VMName $Name | Where-Object { $_.Path } | ForEach-Object { $_.Path })
$diskDirs = @($disks | ForEach-Object { Split-Path -Parent $_ } | Sort-Object -Unique)
$isos = @(Get-VMDvdDrive -VMName $Name | Where-Object { $_.Path -and ((Split-Path -Leaf $_.Path) -like '*autounattend*.iso') } | ForEach-Object { $_.Path })
$vmDir = Join-Path $vm.Path $Name
if (-not (Test-Path -LiteralPath $vmDir)) { $vmDir = $vm.Path }
$marker = Join-Path $vmDir (Get-DebloaterMarkerFileName)
$hasMarker = Test-Path -LiteralPath $marker

$what = "VM '$Name' (folder $vmDir; disks: $($disks -join ', '))"
if (-not $PSCmdlet.ShouldProcess($what, 'Turn off, remove and DELETE the disks')) { return }

if ($vm.State -ne 'Off') {
    Write-Host 'Turning off the VM...'
    Stop-VM -Name $Name -TurnOff -Force
}
# Remove-VM deletes the configuration and the checkpoints without merging the disks (pointless, since they
# will be deleted); the AVHDX files stay in the disk folder and are deleted below.
Remove-VM -Name $Name -Force
Write-Host "VM '$Name' removed from Hyper-V."

# Disks: the VHDX and any AVHDX left over in the same folder.
foreach ($dir in $diskDirs) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Extension -in '.vhdx', '.avhdx', '.vhd' -and $_.BaseName -like "$Name*" } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; Write-Host "Deleted: $($_.FullName)" }
}
foreach ($iso in $isos) {
    if (Test-Path -LiteralPath $iso) { Remove-Item -LiteralPath $iso -Force; Write-Host "Deleted: $iso" }
}

if ($hasMarker) {
    Remove-Item -LiteralPath $vmDir -Recurse -Force
    Write-Host "Folder deleted: $vmDir"
}
else {
    Write-Warning "The folder '$vmDir' does not have the New-TestVM.ps1 marker; only the disks were deleted. Check what is left."
}
