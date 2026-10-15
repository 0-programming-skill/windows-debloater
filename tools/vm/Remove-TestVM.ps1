<#
.SYNOPSIS
    Apaga a VM de teste do Debloater: desliga, remove a VM e os checkpoints, e apaga os discos
    (VHDX/AVHDX) e a pasta criada por New-TestVM.ps1.

.DESCRIPTION
    A pasta da VM (<VmPath>\<Name>) só é apagada inteira se tiver o arquivo-marcador
    ".debloater-testvm" que New-TestVM.ps1 cria. Sem o marcador, apaga apenas os arquivos de disco
    ligados à VM e a ISO do autounattend, e avisa sobre o resto.
    Pede confirmação (ConfirmImpact High); use -Force para não perguntar.

.PARAMETER Name
    Nome da VM. Padrão: DebloaterTest.

.PARAMETER Force
    Não pede confirmação.

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
if (-not $vm) { Write-Host "VM '$Name' não existe; nada a fazer."; return }

# Coleta os caminhos ANTES de remover a VM.
$disks = @(Get-VMHardDiskDrive -VMName $Name | Where-Object { $_.Path } | ForEach-Object { $_.Path })
$diskDirs = @($disks | ForEach-Object { Split-Path -Parent $_ } | Sort-Object -Unique)
$isos = @(Get-VMDvdDrive -VMName $Name | Where-Object { $_.Path -and ((Split-Path -Leaf $_.Path) -like '*autounattend*.iso') } | ForEach-Object { $_.Path })
$vmDir = Join-Path $vm.Path $Name
if (-not (Test-Path -LiteralPath $vmDir)) { $vmDir = $vm.Path }
$marker = Join-Path $vmDir (Get-DebloaterMarkerFileName)
$hasMarker = Test-Path -LiteralPath $marker

$what = "VM '$Name' (pasta $vmDir; discos: $($disks -join ', '))"
if (-not $PSCmdlet.ShouldProcess($what, 'Desligar, remover e APAGAR os discos')) { return }

if ($vm.State -ne 'Off') {
    Write-Host 'Desligando a VM...'
    Stop-VM -Name $Name -TurnOff -Force
}
# Remove-VM apaga a configuração e os checkpoints sem mesclar os discos (inútil, já que serão apagados);
# os AVHDX ficam na pasta dos discos e são apagados abaixo.
Remove-VM -Name $Name -Force
Write-Host "VM '$Name' removida do Hyper-V."

# Discos: o VHDX e os AVHDX que tenham sobrado na mesma pasta.
foreach ($dir in $diskDirs) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Extension -in '.vhdx', '.avhdx', '.vhd' -and $_.BaseName -like "$Name*" } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; Write-Host "Apagado: $($_.FullName)" }
}
foreach ($iso in $isos) {
    if (Test-Path -LiteralPath $iso) { Remove-Item -LiteralPath $iso -Force; Write-Host "Apagado: $iso" }
}

if ($hasMarker) {
    Remove-Item -LiteralPath $vmDir -Recurse -Force
    Write-Host "Pasta apagada: $vmDir"
}
else {
    Write-Warning "A pasta '$vmDir' não tem o marcador de New-TestVM.ps1; apaguei só os discos. Confira o que sobrou."
}
