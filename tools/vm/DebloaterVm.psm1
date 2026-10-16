<#
.SYNOPSIS
    Funções compartilhadas pelos scripts de VM de teste do Debloater (New-TestVM, Invoke-VmSelfTest,
    Remove-TestVM). Compatível com Windows PowerShell 5.1.

.DESCRIPTION
    - Import-DebloaterVmCredential: carrega a credencial da VM gravada com Export-Clixml (DPAPI).
    - New-DebloaterUnattendXml: gera o autounattend.xml (instalação sem interação do Windows 11).
    - New-DebloaterIsoFile: cria uma ISO a partir de uma pasta usando IMAPI2FS (COM nativo do Windows).
    - Get-DebloaterDefaultVmPath, Get-DebloaterVmSwitch, Send-DebloaterVmKey, Wait-DebloaterVmSession,
      Get-DebloaterGuestInfo, Remove-DebloaterSetupMedia, Get-DebloaterDotNet, Get-DebloaterRepoRoot.

    Nenhuma função grava senha no repositório: a credencial fica num arquivo ignorado pelo git
    (tools/vm/.vmcred.xml) e o autounattend.xml só existe dentro da ISO na pasta da VM, apagada ao
    final da instalação.
#>

Set-StrictMode -Version 2.0

# Nome do arquivo que marca uma pasta como criada por New-TestVM (Remove-TestVM só apaga a pasta inteira com ele).
$script:MarkerFileName = '.debloater-testvm'

# Chave genérica de INSTALAÇÃO do Windows 11 Pro publicada pela Microsoft (não ativa o Windows;
# só seleciona a edição e evita a tela de chave do produto).
$script:GenericProKey = 'VK7JG-NPHTM-C97JM-9MPGT-3V66T'

# Id fixo do "Default Switch" do Hyper-V (o nome pode variar entre idiomas).
$script:DefaultSwitchId = 'c08cb7b8-9b3c-408e-8e30-5e16a3aeb444'

# Id do serviço de integração "Guest Service Interface" (o nome é traduzido no host pt-BR).
$script:GuestServiceInterfaceId = '6C09BB55-D683-4DA0-8931-C9BF705F6480'

function Get-DebloaterMarkerFileName { $script:MarkerFileName }
function Get-DebloaterGenericProKey { $script:GenericProKey }

function Get-DebloaterRepoRoot {
    <# .SYNOPSIS Raiz do repositório (dois níveis acima de tools/vm). #>
    (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).ProviderPath
}

function Get-DebloaterDefaultCredentialPath {
    <# .SYNOPSIS Caminho padrão da credencial: tools/vm/.vmcred.xml (ignorado pelo git). #>
    Join-Path $PSScriptRoot '.vmcred.xml'
}

function Get-DebloaterDefaultVmPath {
    <#
    .SYNOPSIS
        Pasta base das VMs: D:\Hyper-V se a unidade D: existir (e for disco fixo), senão C:\Hyper-V.
        A VM fica em <base>\<Nome> (ex.: D:\Hyper-V\DebloaterTest).
    #>
    $d = [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.Name -eq 'D:\' -and $_.DriveType -eq 'Fixed' -and $_.IsReady }
    if ($d) { return 'D:\Hyper-V' }
    return 'C:\Hyper-V'
}

function Import-DebloaterVmCredential {
    <#
    .SYNOPSIS
        Carrega a credencial da conta administradora da VM (arquivo criado com Export-Clixml).
    .DESCRIPTION
        O arquivo é protegido por DPAPI: só pode ser lido pelo mesmo usuário, no mesmo PC, que o criou.
        Para criar: Get-Credential -UserName teste -Message "Conta da VM" | Export-Clixml -Path tools\vm\.vmcred.xml
    #>
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ("Arquivo de credencial não encontrado: '{0}'. Crie-o com:`n" +
               "  Get-Credential -UserName teste -Message 'Conta da VM de teste' | Export-Clixml -Path '{0}'") -f $Path
    }
    $cred = Import-Clixml -LiteralPath $Path
    if (-not ($cred -is [System.Management.Automation.PSCredential])) {
        throw "O arquivo '$Path' não contém uma PSCredential (use Get-Credential | Export-Clixml)."
    }
    return $cred
}

function Test-DebloaterUserName {
    <# .SYNOPSIS Valida o nome da conta local que o autounattend vai criar. #>
    param([Parameter(Mandatory)][string]$UserName)

    $name = $UserName
    if ($name.Contains('\')) { $name = $name.Split('\')[-1] }
    if ($name.Length -lt 1 -or $name.Length -gt 20) { throw "Nome de usuário '$name' inválido: use de 1 a 20 caracteres." }
    if ($name -match '[\\/\[\]:;|=,+*?<>"@%]') { throw "Nome de usuário '$name' contém caracteres não permitidos." }
    if ($name.TrimEnd('.').Length -eq 0) { throw "Nome de usuário '$name' inválido." }
    if (@('Administrator', 'Administrador', 'Guest', 'Convidado', 'DefaultAccount', 'WDAGUtilityAccount') -contains $name) {
        throw "Use um nome que não seja de uma conta interna do Windows (recebido '$name')."
    }
    return $name
}

function ConvertTo-DebloaterXmlText {
    param([AllowEmptyString()][string]$Text)
    [System.Security.SecurityElement]::Escape($Text)
}

function Get-DebloaterAdminGroupName {
    <#
    .SYNOPSIS
        Nome do grupo Administradores no idioma da instalação (o autounattend usa o nome, não o SID).
    #>
    param([Parameter(Mandatory)][string]$UILanguage)
    switch -Regex ($UILanguage) {
        '^pt-'  { 'Administradores'; break }
        '^es-'  { 'Administradores'; break }
        '^de-'  { 'Administratoren'; break }
        '^fr-'  { 'Administrateurs'; break }
        '^it-'  { 'Administratori'; break }
        default { 'Administrators' }
    }
}

function New-DebloaterUnattendXml {
    <#
    .SYNOPSIS
        Gera o texto do autounattend.xml para instalar o Windows 11 sem interação numa VM Hyper-V Gen 2.
    .DESCRIPTION
        windowsPE: idioma/teclado, LabConfig (BypassTPMCheck, BypassSecureBootCheck, BypassCPUCheck,
        BypassRAMCheck, BypassStorageCheck: CPUs mais antigas não estão na lista do Windows 11),
        apaga o disco 0 e cria EFI/MSR/Windows (GPT), seleciona a edição pelo nome da imagem.
        specialize: nome do computador, fuso, LocalAccountTokenFilterPolicy=1 (token completo de
        administrador em logons remotos de contas locais; a VM é descartável) e BypassNRO.
        oobeSystem: pula telas do OOBE e da conta online, cria a conta local administradora, faz logon
        automático uma única vez e, nesse logon, desativa suspensão e limpa a senha do logon automático.
    .OUTPUTS
        [string] com o XML (UTF-8). Contém a senha em texto: grave apenas fora do repositório.
    #>
    param(
        [Parameter(Mandatory)][pscredential]$Credential,
        [string]$ComputerName = 'DEBLOATERTEST',
        [string]$ImageName = 'Windows 11 Pro',
        [string]$UILanguage = 'pt-BR',
        [string]$InputLocale = '0416:00010416',
        [string]$TimeZone = 'E. South America Standard Time',
        [AllowEmptyString()][string]$ProductKey = $script:GenericProKey,
        [string]$AdminGroup
    )

    $user = Test-DebloaterUserName $Credential.UserName
    $password = $Credential.GetNetworkCredential().Password
    if ([string]::IsNullOrEmpty($password)) { throw 'A credencial precisa ter senha (PowerShell Direct não aceita senha vazia).' }
    if (-not $AdminGroup) { $AdminGroup = Get-DebloaterAdminGroupName $UILanguage }

    $cn = ($ComputerName -replace '[^A-Za-z0-9-]', '')
    if ($cn.Length -gt 15) { $cn = $cn.Substring(0, 15) }
    if ($cn.Length -eq 0) { $cn = 'DEBLOATERTEST' }

    $x = @{
        User = ConvertTo-DebloaterXmlText $user
        Pass = ConvertTo-DebloaterXmlText $password
        Group = ConvertTo-DebloaterXmlText $AdminGroup
        Lang = ConvertTo-DebloaterXmlText $UILanguage
        Input = ConvertTo-DebloaterXmlText $InputLocale
        Tz = ConvertTo-DebloaterXmlText $TimeZone
        Image = ConvertTo-DebloaterXmlText $ImageName
        Cn = $cn
    }
    $arch = 'processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"'

    $productKeyXml = ''
    if ($ProductKey) {
        $productKeyXml = @"
        <ProductKey>
          <Key>$(ConvertTo-DebloaterXmlText $ProductKey)</Key>
          <WillShowUI>OnError</WillShowUI>
        </ProductKey>
"@
    }

    $labConfig = @('BypassTPMCheck', 'BypassSecureBootCheck', 'BypassCPUCheck', 'BypassRAMCheck', 'BypassStorageCheck')
    $order = 0
    $labCommands = ($labConfig | ForEach-Object {
        $order++
        @"
        <RunSynchronousCommand wcm:action="add">
          <Order>$order</Order>
          <Path>reg.exe add HKLM\SYSTEM\Setup\LabConfig /v $_ /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
"@
    }) -join "`r`n"

    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Gerado por tools/vm/New-TestVM.ps1 (Debloater). Somente para a VM de teste descartável. -->
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
  <settings pass="windowsPE">
    <component name="Microsoft-Windows-International-Core-WinPE" $arch>
      <SetupUILanguage>
        <UILanguage>$($x.Lang)</UILanguage>
      </SetupUILanguage>
      <InputLocale>$($x.Input)</InputLocale>
      <SystemLocale>$($x.Lang)</SystemLocale>
      <UILanguage>$($x.Lang)</UILanguage>
      <UserLocale>$($x.Lang)</UserLocale>
    </component>
    <component name="Microsoft-Windows-Setup" $arch>
      <RunSynchronous>
$labCommands
      </RunSynchronous>
      <DiskConfiguration>
        <Disk wcm:action="add">
          <DiskID>0</DiskID>
          <WillWipeDisk>true</WillWipeDisk>
          <CreatePartitions>
            <CreatePartition wcm:action="add">
              <Order>1</Order>
              <Type>EFI</Type>
              <Size>300</Size>
            </CreatePartition>
            <CreatePartition wcm:action="add">
              <Order>2</Order>
              <Type>MSR</Type>
              <Size>16</Size>
            </CreatePartition>
            <CreatePartition wcm:action="add">
              <Order>3</Order>
              <Type>Primary</Type>
              <Extend>true</Extend>
            </CreatePartition>
          </CreatePartitions>
          <ModifyPartitions>
            <ModifyPartition wcm:action="add">
              <Order>1</Order>
              <PartitionID>1</PartitionID>
              <Label>System</Label>
              <Format>FAT32</Format>
            </ModifyPartition>
            <ModifyPartition wcm:action="add">
              <Order>2</Order>
              <PartitionID>2</PartitionID>
            </ModifyPartition>
            <ModifyPartition wcm:action="add">
              <Order>3</Order>
              <PartitionID>3</PartitionID>
              <Label>Windows</Label>
              <Letter>C</Letter>
              <Format>NTFS</Format>
            </ModifyPartition>
          </ModifyPartitions>
        </Disk>
      </DiskConfiguration>
      <ImageInstall>
        <OSImage>
          <InstallFrom>
            <MetaData wcm:action="add">
              <Key>/IMAGE/NAME</Key>
              <Value>$($x.Image)</Value>
            </MetaData>
          </InstallFrom>
          <InstallTo>
            <DiskID>0</DiskID>
            <PartitionID>3</PartitionID>
          </InstallTo>
        </OSImage>
      </ImageInstall>
      <UserData>
$productKeyXml
        <AcceptEula>true</AcceptEula>
      </UserData>
    </component>
  </settings>
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" $arch>
      <ComputerName>$($x.Cn)</ComputerName>
      <TimeZone>$($x.Tz)</TimeZone>
    </component>
    <component name="Microsoft-Windows-Deployment" $arch>
      <RunSynchronous>
        <RunSynchronousCommand wcm:action="add">
          <Order>1</Order>
          <Path>reg.exe add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action="add">
          <Order>2</Order>
          <Path>reg.exe add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
      </RunSynchronous>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" $arch>
      <InputLocale>$($x.Input)</InputLocale>
      <SystemLocale>$($x.Lang)</SystemLocale>
      <UILanguage>$($x.Lang)</UILanguage>
      <UserLocale>$($x.Lang)</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" $arch>
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
      <UserAccounts>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Name>$($x.User)</Name>
            <DisplayName>$($x.User)</DisplayName>
            <Group>$($x.Group)</Group>
            <Password>
              <Value>$($x.Pass)</Value>
              <PlainText>true</PlainText>
            </Password>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
      <AutoLogon>
        <Enabled>true</Enabled>
        <LogonCount>1</LogonCount>
        <Username>$($x.User)</Username>
        <Password>
          <Value>$($x.Pass)</Value>
          <PlainText>true</PlainText>
        </Password>
      </AutoLogon>
      <FirstLogonCommands>
        <SynchronousCommand wcm:action="add">
          <Order>1</Order>
          <CommandLine>powercfg.exe /change standby-timeout-ac 0</CommandLine>
          <Description>Sem suspensão</Description>
        </SynchronousCommand>
        <SynchronousCommand wcm:action="add">
          <Order>2</Order>
          <CommandLine>powercfg.exe /change monitor-timeout-ac 0</CommandLine>
          <Description>Sem desligar o vídeo</Description>
        </SynchronousCommand>
        <SynchronousCommand wcm:action="add">
          <Order>3</Order>
          <CommandLine>reg.exe add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon /t REG_SZ /d 0 /f</CommandLine>
          <Description>Logon automático só uma vez</Description>
        </SynchronousCommand>
        <SynchronousCommand wcm:action="add">
          <Order>4</Order>
          <CommandLine>reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultPassword /f</CommandLine>
          <Description>Remove a senha do logon automático</Description>
        </SynchronousCommand>
      </FirstLogonCommands>
      <TimeZone>$($x.Tz)</TimeZone>
    </component>
  </settings>
</unattend>
"@

    # Garante que o resultado é XML bem formado antes de usá-lo.
    $null = [xml]$xml
    return $xml
}

function New-DebloaterIsoFile {
    <#
    .SYNOPSIS
        Cria uma ISO (ISO 9660 + Joliet) com o conteúdo de uma pasta, usando o COM IMAPI2FS do Windows
        (sem ADK/oscdimg e sem baixar nada).
    #>
    param(
        [Parameter(Mandatory)][string]$SourceFolder,
        [Parameter(Mandatory)][string]$IsoPath,
        [string]$VolumeName = 'UNATTEND'
    )

    if (-not ('DebloaterIsoWriter' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices.ComTypes;

public static class DebloaterIsoWriter
{
    // Copia o IStream devolvido por IMAPI2FS para um arquivo, bloco a bloco.
    public static void Write(string path, object stream, int blockSize, int totalBlocks)
    {
        IStream source = (IStream)stream;
        byte[] buffer = new byte[blockSize];
        using (FileStream target = File.Create(path))
        {
            for (int i = 0; i < totalBlocks; i++)
            {
                source.Read(buffer, blockSize, IntPtr.Zero);
                target.Write(buffer, 0, blockSize);
            }
        }
    }
}
'@
    }

    $source = (Resolve-Path -LiteralPath $SourceFolder).ProviderPath
    $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    try {
        $fsi.FileSystemsToCreate = 3   # FsiFileSystemISO9660 (1) + FsiFileSystemJoliet (2)
        $fsi.VolumeName = $VolumeName
        $fsi.Root.AddTree($source, $false)
        $result = $fsi.CreateResultImage()
        [DebloaterIsoWriter]::Write($IsoPath, $result.ImageStream, $result.BlockSize, $result.TotalBlocks)
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($fsi)
    }
    Get-Item -LiteralPath $IsoPath
}

function Get-DebloaterVmSwitch {
    <# .SYNOPSIS Retorna o switch pelo nome; para 'Default Switch', também procura pelo Id fixo. #>
    param([string]$Name = 'Default Switch')
    $sw = Get-VMSwitch -Name $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $sw -and $Name -eq 'Default Switch') {
        $sw = Get-VMSwitch -ErrorAction SilentlyContinue | Where-Object { "$($_.Id)" -eq $script:DefaultSwitchId } | Select-Object -First 1
    }
    if (-not $sw) { throw "Switch virtual '$Name' não encontrado. Veja Get-VMSwitch." }
    return $sw
}

function Enable-DebloaterGuestServices {
    <# .SYNOPSIS Ativa o serviço de integração "Guest Service Interface" (pelo Id; o nome é traduzido). #>
    param([Parameter(Mandatory)][string]$VMName)
    $svc = Get-VMIntegrationService -VMName $VMName | Where-Object {
        "$($_.Id)" -match $script:GuestServiceInterfaceId -or $_.Name -match 'Guest Service|Convidado'
    } | Select-Object -First 1
    if (-not $svc) { Write-Warning 'Serviço de integração "Guest Service Interface" não encontrado.'; return }
    if (-not $svc.Enabled) { Enable-VMIntegrationService -VMIntegrationService $svc }
}

function Send-DebloaterVmKey {
    <#
    .SYNOPSIS
        Pressiona uma tecla no teclado virtual da VM (Msvm_Keyboard.TypeKey) repetidamente por alguns
        segundos. Usado logo após ligar a VM para passar do "Press any key to boot from CD or DVD".
    #>
    param(
        [Parameter(Mandatory)]$VM,   # Microsoft.HyperV.PowerShell.VirtualMachine (Get-VM)
        [int]$Seconds = 30,
        [int]$IntervalMilliseconds = 500,
        [uint32]$KeyCode = 0x0D   # Enter
    )
    $deadline = (Get-Date).AddSeconds($Seconds)
    $sent = 0
    while ((Get-Date) -lt $deadline) {
        try {
            $kb = Get-CimInstance -Namespace 'root\virtualization\v2' -ClassName 'Msvm_Keyboard' -Filter "SystemName='$($VM.Id)'" -ErrorAction Stop |
                Select-Object -First 1
            if ($kb) {
                $null = Invoke-CimMethod -InputObject $kb -MethodName 'TypeKey' -Arguments @{ keyCode = $KeyCode } -ErrorAction Stop
                $sent++
            }
        }
        catch {
            Write-Verbose "TypeKey ainda indisponível: $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    }
    Write-Verbose "Teclas enviadas: $sent"
    return $sent
}

function Wait-DebloaterVmSession {
    <#
    .SYNOPSIS
        Tenta abrir uma sessão PowerShell Direct (New-PSSession -VMName) até conseguir ou estourar o tempo.
    #>
    param(
        [Parameter(Mandatory)][string]$VMName,
        [Parameter(Mandatory)][pscredential]$Credential,
        [int]$TimeoutMinutes = 10,
        [int]$IntervalSeconds = 15,
        [string]$Activity = 'Aguardando a VM aceitar PowerShell Direct'
    )
    $start = Get-Date
    $deadline = $start.AddMinutes($TimeoutMinutes)
    $last = $null
    while ($true) {
        try {
            $s = New-PSSession -VMName $VMName -Credential $Credential -ErrorAction Stop
            Write-Progress -Activity $Activity -Completed
            return $s
        }
        catch {
            $last = $_.Exception.Message
        }
        $elapsed = (Get-Date) - $start
        if ((Get-Date) -gt $deadline) {
            Write-Progress -Activity $Activity -Completed
            throw "A VM '$VMName' não aceitou PowerShell Direct em $TimeoutMinutes min. Último erro: $last"
        }
        Write-Progress -Activity $Activity -Status ("{0:hh\:mm\:ss} decorridos (limite {1} min). Último erro: {2}" -f $elapsed, $TimeoutMinutes, $last)
        Start-Sleep -Seconds $IntervalSeconds
    }
}

function Get-DebloaterGuestInfo {
    <# .SYNOPSIS Usuário, elevação, modelo (VM?) e versão do Windows dentro da sessão. #>
    param([Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$Session)
    Invoke-Command -Session $Session -ScriptBlock {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($id)
        $cs = Get-CimInstance Win32_ComputerSystem
        $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        [pscustomobject]@{
            User         = $id.Name
            IsElevated   = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            Manufacturer = $cs.Manufacturer
            Model        = $cs.Model
            Build        = '{0}.{1}' -f $cv.CurrentBuildNumber, $cv.UBR
            Edition      = $cv.EditionID
            DisplayVersion = $cv.DisplayVersion
        }
    }
}

function Remove-DebloaterSetupMedia {
    <#
    .SYNOPSIS
        Ejeta a ISO do Windows e remove a unidade com a ISO do autounattend, apagando esse arquivo
        (ele contém a senha da VM em texto).
    #>
    param([Parameter(Mandatory)][string]$VMName)
    $drives = @(Get-VMDvdDrive -VMName $VMName | Where-Object { $_.Path })
    if ($drives.Count -eq 0) { return }

    # Ejetar/remover DVD com a VM ligada pode travar o VMMS inteiro (o convidado segura a mídia).
    # Por isso a VM é desligada antes e religada depois, se estava ligada.
    $wasRunning = (Get-VM -Name $VMName).State -eq 'Running'
    if ($wasRunning) {
        Write-Host 'Desligando a VM para remover as mídias de instalação...'
        Stop-VM -Name $VMName -Force
    }
    elseif ((Get-VM -Name $VMName).State -eq 'Saved') {
        Remove-VMSavedState -VMName $VMName
    }

    foreach ($dvd in $drives) {
        if (-not $dvd.Path) { continue }
        $path = $dvd.Path
        if ((Split-Path -Leaf $path) -like '*autounattend*.iso') {
            Remove-VMDvdDrive -VMDvdDrive $dvd
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
            Write-Host "ISO do autounattend removida: $path"
        }
        else {
            Set-VMDvdDrive -VMDvdDrive $dvd -Path $null
            Write-Host "ISO ejetada: $path"
        }
    }

    if ($wasRunning) { Start-VM -Name $VMName }
}

function Get-DebloaterGuestBootTime {
    <# .SYNOPSIS Data/hora do último boot do convidado (Win32_OperatingSystem.LastBootUpTime). #>
    param([Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$Session)
    Invoke-Command -Session $Session -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime }
}

function Restart-DebloaterVmGuest {
    <#
    .SYNOPSIS
        Reinicia o Windows do convidado de forma limpa (shutdown /r de dentro da VM, não um reset do
        Hyper-V, para o registro ser gravado como numa reinicialização normal) e devolve uma sessão
        PowerShell Direct nova, aberta só depois que o boot novo aconteceu (LastBootUpTime mudou).
    #>
    param(
        [Parameter(Mandatory)][string]$VMName,
        [Parameter(Mandatory)][pscredential]$Credential,
        [Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$Session,
        [int]$TimeoutMinutes = 10
    )
    $before = Get-DebloaterGuestBootTime -Session $Session
    # Start-Process devolve logo; a sessão cai quando o Windows desliga.
    Invoke-Command -Session $Session -ScriptBlock { Start-Process -FilePath shutdown.exe -ArgumentList '/r', '/t', '3', '/f' -WindowStyle Hidden }
    Remove-PSSession $Session -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 20
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ($true) {
        $remaining = [math]::Max(1, [int][math]::Ceiling(($deadline - (Get-Date)).TotalMinutes))
        $s = Wait-DebloaterVmSession -VMName $VMName -Credential $Credential -TimeoutMinutes $remaining -IntervalSeconds 10 -Activity 'Aguardando a VM reiniciar'
        try {
            $now = Get-DebloaterGuestBootTime -Session $s
            if ($now -gt $before) { return $s }
        }
        catch {
            Write-Verbose "Sessão ainda instável: $($_.Exception.Message)"
        }
        Remove-PSSession $s -ErrorAction SilentlyContinue
        if ((Get-Date) -gt $deadline) { throw "A VM '$VMName' não reiniciou em $TimeoutMinutes min." }
        Start-Sleep -Seconds 10
    }
}

function Set-DebloaterGuestAutoLogon {
    <#
    .SYNOPSIS
        Liga o logon automático da conta de teste no convidado (Winlogon AutoAdminLogon/DefaultPassword),
        para que, depois de reiniciar, haja uma sessão interativa com o Explorer rodando (ele pode
        reescrever valores de HKCU). A senha fica no registro da VM só até o checkpoint ser restaurado.
    #>
    param(
        [Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory)][pscredential]$Credential
    )
    $user = $Credential.UserName
    if ($user.Contains('\')) { $user = $user.Split('\')[-1] }
    $password = $Credential.GetNetworkCredential().Password
    Invoke-Command -Session $Session -ScriptBlock {
        param($u, $p)
        $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Set-ItemProperty -Path $k -Name AutoAdminLogon -Value '1' -Type String
        Set-ItemProperty -Path $k -Name DefaultUserName -Value $u -Type String
        Set-ItemProperty -Path $k -Name DefaultDomainName -Value $env:COMPUTERNAME -Type String
        Set-ItemProperty -Path $k -Name DefaultPassword -Value $p -Type String
        Remove-ItemProperty -Path $k -Name AutoLogonCount -ErrorAction SilentlyContinue
    } -ArgumentList $user, $password
}

function Wait-DebloaterGuestDesktop {
    <# .SYNOPSIS Espera o Explorer da sessão interativa aparecer (logon automático). Devolve $true se apareceu. #>
    param(
        [Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$Session,
        [int]$TimeoutSeconds = 180
    )
    Invoke-Command -Session $Session -ScriptBlock {
        param($t)
        $deadline = (Get-Date).AddSeconds($t)
        while ((Get-Date) -lt $deadline) {
            if (Get-Process -Name explorer -IncludeUserName -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -gt 0 }) { return $true }
            Start-Sleep -Seconds 5
        }
        return $false
    } -ArgumentList $TimeoutSeconds
}

function Invoke-DebloaterGuestMaintenance {
    <#
    .SYNOPSIS
        Dispara, de dentro do convidado, o que costuma desfazer ajustes: atualização de política
        (gpupdate /force), busca do Windows Update (UsoClient StartScan e a tarefa "Schedule Scan") e a
        remediação do WaaSMedic. Tudo best-effort: devolve uma linha por gatilho com o resultado.
    #>
    param([Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$Session)
    Invoke-Command -Session $Session -ScriptBlock {
        function Try-Step([string]$Name, [scriptblock]$Body) {
            try { $r = & $Body 2>&1 | Out-String; "{0}: ok {1}" -f $Name, (($r -replace '\s+', ' ').Trim()) }
            catch { "{0}: falhou ({1})" -f $Name, $_.Exception.Message }
        }
        Try-Step 'gpupdate /force' { gpupdate.exe /force /wait:120 | Select-Object -Last 2 }
        Try-Step 'UsoClient StartScan' { Start-Process -FilePath UsoClient.exe -ArgumentList 'StartScan' -Wait -WindowStyle Hidden; 'disparado' }
        foreach ($t in @('\Microsoft\Windows\UpdateOrchestrator\Schedule Scan', '\Microsoft\Windows\WaaSMedic\PerformRemediation')) {
            Try-Step "tarefa $t" {
                $path = $t.Substring(0, $t.LastIndexOf('\') + 1); $name = $t.Substring($t.LastIndexOf('\') + 1)
                $task = Get-ScheduledTask -TaskPath $path -TaskName $name -ErrorAction Stop
                Start-ScheduledTask -InputObject $task -ErrorAction Stop
                'iniciada'
            }
        }
        Try-Step 'serviço WaaSMedicSvc' { Start-Service -Name WaaSMedicSvc -ErrorAction Stop; (Get-Service WaaSMedicSvc).Status }
    }
}

function Get-DebloaterDotNet {
    <# .SYNOPSIS Caminho do dotnet.exe (C:\Program Files\dotnet ou o do PATH). #>
    $candidates = @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe'
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'dotnet.exe não encontrado (instale o SDK do .NET 10).'
}

Export-ModuleMember -Function *-Debloater*
