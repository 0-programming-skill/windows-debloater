<#
.SYNOPSIS
    Cria a VM Hyper-V descartável usada para testar aplicar/reverter do Debloater e instala o
    Windows 11 sem interação.

.DESCRIPTION
    1. Carrega a credencial da conta local administradora (arquivo DPAPI criado com Export-Clixml).
    2. Gera um autounattend.xml (idioma/teclado, LabConfig para CPU/TPM/Secure Boot/RAM fora dos
       requisitos, apaga o disco 0, Windows 11 Pro, pula OOBE e conta online, cria a conta local,
       logon automático uma vez, LocalAccountTokenFilterPolicy=1) e grava numa ISO pequena criada
       com IMAPI2FS (nativo do Windows, sem ADK e sem downloads).
    3. Cria a VM: Geração 2, Secure Boot (modelo Microsoft Windows), vTPM, memória fixa, VHDX
       dinâmico, "Default Switch", checkpoints Standard, sem checkpoints automáticos, serviço de
       integração "Guest Service Interface" ativado.
    4. Liga a VM e pressiona Enter algumas vezes para passar do "Press any key to boot from CD".
    5. (padrão) Espera a instalação terminar (PowerShell Direct responde), confere que a sessão é de
       administrador elevado, ejeta as ISOs, APAGA a ISO do autounattend (ela tem a senha em texto)
       e cria o checkpoint "base-limpa".

    Checkpoints Standard (e não Production): o selftest restaura o checkpoint depois de cada execução;
    o Standard guarda também a memória, então a VM volta em segundos já ligada, exatamente no estado
    de antes (serviços em execução, sessão etc.), sem reiniciar. O Production usa VSS dentro do
    convidado e sempre volta com a VM desligada (boot completo a cada execução). Para uma VM de teste
    descartável, reprodutibilidade e velocidade valem mais que a consistência de aplicação do VSS.

    Requisitos: Hyper-V ativado, usuário no grupo "Administradores do Hyper-V" (não precisa elevar),
    ISO do Windows 11 no MESMO idioma de -UILanguage (padrão pt-BR).

.PARAMETER IsoPath
    ISO de instalação do Windows 11 (Microsoft). Deve conter a edição -ImageName.

.PARAMETER Name
    Nome da VM. Padrão: DebloaterTest.

.PARAMETER CredentialPath
    Credencial da conta local (Export-Clixml). Padrão: tools/vm/.vmcred.xml.
    Crie com: Get-Credential -UserName teste -Message 'Conta da VM de teste' | Export-Clixml -Path tools\vm\.vmcred.xml

.PARAMETER MemoryGB
    Memória fixa da VM em GB. Padrão: 8.

.PARAMETER CpuCount
    Processadores virtuais. Padrão: 4.

.PARAMETER DiskGB
    Tamanho máximo do VHDX dinâmico em GB. Padrão: 80 (ocupa ~20-25 GB após a instalação).

.PARAMETER VmPath
    Pasta base; a VM fica em <VmPath>\<Name>. Padrão: D:\Hyper-V se D: existir, senão C:\Hyper-V.
    Caminhos com espaços são aceitos.

.PARAMETER SwitchName
    Switch virtual. Padrão: Default Switch (NAT com acesso à internet).

.PARAMETER ImageName
    Nome da imagem no install.wim/esd. Padrão: Windows 11 Pro.

.PARAMETER UILanguage
    Idioma do Setup e do sistema. Deve existir na ISO. Padrão: pt-BR.

.PARAMETER InputLocale
    Teclado. Padrão: 0416:00010416 (Português Brasil ABNT2).

.PARAMETER ProductKey
    Chave usada só para escolher a edição. Padrão: chave genérica de instalação do Pro (não ativa).
    Use '' para omitir.

.PARAMETER AdminGroup
    Nome do grupo Administradores no idioma da ISO. Padrão: deduzido de -UILanguage
    (pt-BR: Administradores; en-US: Administrators).

.PARAMETER NoWait
    Só cria e liga a VM; não espera a instalação. Invoke-VmSelfTest.ps1 remove a ISO do autounattend
    na primeira execução, se ela ainda estiver conectada.

.PARAMETER InstallTimeoutMinutes
    Quanto esperar a instalação. Padrão: 120.

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

# ---------- verificações ----------
if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) {
    throw 'Módulo Hyper-V não encontrado. Ative o Hyper-V (inclusive "Módulo do Hyper-V para Windows PowerShell").'
}
if (-not (Test-Path -LiteralPath $IsoPath -PathType Leaf)) { throw "ISO não encontrada: '$IsoPath'." }
$IsoPath = (Resolve-Path -LiteralPath $IsoPath).ProviderPath
if (Get-VM -Name $Name -ErrorAction SilentlyContinue) {
    throw "Já existe uma VM chamada '$Name'. Use Remove-TestVM.ps1 ou outro -Name."
}
$cred = Import-DebloaterVmCredential -Path $CredentialPath
$null = Test-DebloaterUserName $cred.UserName
$switch = Get-DebloaterVmSwitch -Name $SwitchName

$vmDir = Join-Path $VmPath $Name
if (Test-Path -LiteralPath $vmDir) {
    if (Get-ChildItem -LiteralPath $vmDir -Force | Select-Object -First 1) {
        throw "A pasta '$vmDir' já existe e não está vazia. Apague-a ou use outro -VmPath/-Name."
    }
}
$root = [System.IO.Path]::GetPathRoot($vmDir)
$drive = New-Object System.IO.DriveInfo($root)
$freeGB = [math]::Round($drive.AvailableFreeSpace / 1GB, 1)
# Instalação ~25 GB + memória salva dos checkpoints Standard (MemoryGB cada) + folga.
$neededGB = 30 + 2 * $MemoryGB
if ($freeGB -lt $neededGB) {
    throw "Pouco espaço livre em $root ($freeGB GB). Recomendado ao menos $neededGB GB. Use -VmPath em outra unidade."
}
Write-Host "Espaço livre em ${root}: $freeGB GB (VHDX dinâmico de até $DiskGB GB)."

$vhdDir = Join-Path $vmDir 'Virtual Hard Disks'
$vhdPath = Join-Path $vhdDir "$Name.vhdx"
$unattendIso = Join-Path $vmDir "$Name-autounattend.iso"

# ---------- autounattend ----------
$null = New-Item -ItemType Directory -Path $vhdDir -Force
Set-Content -LiteralPath (Join-Path $vmDir (Get-DebloaterMarkerFileName)) -Value "Criada por tools/vm/New-TestVM.ps1 em $(Get-Date -Format s). Remove-TestVM.ps1 apaga esta pasta." -Encoding UTF8

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
    Write-Host "ISO do autounattend: $unattendIso (contém a senha; será apagada ao fim da instalação)"
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------- VM ----------
Write-Host "Criando a VM '$Name' em '$vmDir'..."
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
    Write-Host 'vTPM ativado.'
}
catch {
    # Criar o "guardião local" (certificados do HGS) pode exigir elevação na primeira vez.
    $msg = ("Não foi possível ativar o vTPM: {0}`nA instalação continua (o autounattend usa BypassTPMCheck). " +
        "Para ter vTPM, rode uma vez num PowerShell elevado (com a VM desligada): " +
        "Set-VMKeyProtector -VMName '{1}' -NewLocalKeyProtector; Enable-VMTPM -VMName '{1}'") -f $_.Exception.Message, $Name
    Write-Warning $msg
}

Enable-DebloaterGuestServices -VMName $Name

$windowsDvd = Add-VMDvdDrive -VMName $Name -Path $IsoPath -Passthru
$null = Add-VMDvdDrive -VMName $Name -Path $unattendIso -Passthru
Set-VMFirmware -VMName $Name -FirstBootDevice $windowsDvd

# ---------- instalação ----------
Write-Host 'Ligando a VM e pressionando Enter para iniciar pelo DVD...'
Start-VM -Name $Name
$vm = Get-VM -Name $Name
$keys = Send-DebloaterVmKey -VM $vm -Seconds 30
if ($keys -eq 0) {
    Write-Warning 'Não foi possível enviar teclas à VM. Se aparecer "Press any key to boot from CD or DVD", abra a VM (vmconnect) e pressione uma tecla.'
}
Write-Host "A instalação começou. Acompanhe com: vmconnect.exe localhost '$Name'"

if ($NoWait) {
    Write-Host 'Sem esperar (-NoWait). Quando terminar, rode Invoke-VmSelfTest.ps1 (ele remove a ISO do autounattend).'
    return
}

$session = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $InstallTimeoutMinutes -IntervalSeconds 30 `
    -Activity "Instalando o Windows na VM '$Name'"
try {
    $info = Get-DebloaterGuestInfo -Session $session
    Write-Host ("Windows instalado: build {0} ({1} {2}); usuário {3}; modelo '{4}'." -f $info.Build, $info.Edition, $info.DisplayVersion, $info.User, $info.Model)
    if (-not $info.IsElevated) {
        Write-Warning ('A sessão PowerShell Direct NÃO está elevada. O selftest precisa de administrador. Verifique se a conta está ' +
            'no grupo Administradores e se LocalAccountTokenFilterPolicy=1 (HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System).')
    }
}
finally {
    Remove-PSSession $session
}

# Deixa o primeiro logon (FirstLogonCommands) terminar antes do checkpoint.
Start-Sleep -Seconds 60
Remove-DebloaterSetupMedia -VMName $Name
# A remoção das mídias desliga e religa a VM; espera o Windows voltar antes do checkpoint.
Remove-PSSession (Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes 10)
Start-Sleep -Seconds 30
Checkpoint-VM -Name $Name -SnapshotName 'base-limpa'
Write-Host "Pronto. VM '$Name' instalada, ligada e com o checkpoint 'base-limpa'. Próximo passo: .\tools\vm\Invoke-VmSelfTest.ps1"
