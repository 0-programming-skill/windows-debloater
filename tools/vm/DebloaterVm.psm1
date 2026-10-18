<#
.SYNOPSIS
    Functions shared by the Debloater test VM scripts (New-TestVM, Invoke-VmSelfTest,
    Remove-TestVM). Compatible with Windows PowerShell 5.1.

.DESCRIPTION
    - Import-DebloaterVmCredential: loads the VM credential saved with Export-Clixml (DPAPI).
    - New-DebloaterUnattendXml: generates autounattend.xml (unattended Windows 11 installation).
    - New-DebloaterIsoFile: creates an ISO from a folder using IMAPI2FS (native Windows COM).
    - Get-DebloaterDefaultVmPath, Get-DebloaterVmSwitch, Send-DebloaterVmKey, Wait-DebloaterVmSession,
      Get-DebloaterGuestInfo, Remove-DebloaterSetupMedia, Get-DebloaterDotNet, Get-DebloaterRepoRoot.

    No function writes a password to the repository: the credential lives in a git-ignored file
    (tools/vm/.vmcred.xml) and autounattend.xml only exists inside the ISO in the VM folder, which is
    deleted at the end of the installation.
#>

Set-StrictMode -Version 2.0

# Name of the file that marks a folder as created by New-TestVM (Remove-TestVM only deletes the whole folder if it is present).
$script:MarkerFileName = '.debloater-testvm'

# Generic Windows 11 Pro INSTALLATION key published by Microsoft (does not activate Windows;
# it only selects the edition and skips the product key screen).
$script:GenericProKey = 'VK7JG-NPHTM-C97JM-9MPGT-3V66T'

# Fixed Id of the Hyper-V "Default Switch" (the name may vary between languages).
$script:DefaultSwitchId = 'c08cb7b8-9b3c-408e-8e30-5e16a3aeb444'

# Id of the "Guest Service Interface" integration service (the name is localized on a pt-BR host).
$script:GuestServiceInterfaceId = '6C09BB55-D683-4DA0-8931-C9BF705F6480'

function Get-DebloaterMarkerFileName { $script:MarkerFileName }
function Get-DebloaterGenericProKey { $script:GenericProKey }

function Get-DebloaterRepoRoot {
    <# .SYNOPSIS Repository root (two levels above tools/vm). #>
    (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).ProviderPath
}

function Get-DebloaterDefaultCredentialPath {
    <# .SYNOPSIS Default credential path: tools/vm/.vmcred.xml (git-ignored). #>
    Join-Path $PSScriptRoot '.vmcred.xml'
}

function Get-DebloaterDefaultVmPath {
    <#
    .SYNOPSIS
        Base folder for VMs: D:\Hyper-V if drive D: exists (and is a fixed disk), otherwise C:\Hyper-V.
        The VM lives in <base>\<Name> (e.g. D:\Hyper-V\DebloaterTest).
    #>
    $d = [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.Name -eq 'D:\' -and $_.DriveType -eq 'Fixed' -and $_.IsReady }
    if ($d) { return 'D:\Hyper-V' }
    return 'C:\Hyper-V'
}

function Import-DebloaterVmCredential {
    <#
    .SYNOPSIS
        Loads the credential of the VM's administrator account (file created with Export-Clixml).
    .DESCRIPTION
        The file is protected by DPAPI: it can only be read by the same user, on the same PC, that created it.
        To create it: Get-Credential -UserName teste -Message "VM account" | Export-Clixml -Path tools\vm\.vmcred.xml
    #>
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ("Credential file not found: '{0}'. Create it with:`n" +
               "  Get-Credential -UserName teste -Message 'Test VM account' | Export-Clixml -Path '{0}'") -f $Path
    }
    $cred = Import-Clixml -LiteralPath $Path
    if (-not ($cred -is [System.Management.Automation.PSCredential])) {
        throw "The file '$Path' does not contain a PSCredential (use Get-Credential | Export-Clixml)."
    }
    return $cred
}

function Test-DebloaterUserName {
    <# .SYNOPSIS Validates the name of the local account that autounattend will create. #>
    param([Parameter(Mandatory)][string]$UserName)

    $name = $UserName
    if ($name.Contains('\')) { $name = $name.Split('\')[-1] }
    if ($name.Length -lt 1 -or $name.Length -gt 20) { throw "Invalid user name '$name': use 1 to 20 characters." }
    if ($name -match '[\\/\[\]:;|=,+*?<>"@%]') { throw "User name '$name' contains characters that are not allowed." }
    if ($name.TrimEnd('.').Length -eq 0) { throw "Invalid user name '$name'." }
    if (@('Administrator', 'Administrador', 'Guest', 'Convidado', 'DefaultAccount', 'WDAGUtilityAccount') -contains $name) {
        throw "Use a name that is not a built-in Windows account (got '$name')."
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
        Name of the Administrators group in the installation language (autounattend uses the name, not the SID).
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
        Generates the autounattend.xml text to install Windows 11 unattended on a Hyper-V Gen 2 VM.
    .DESCRIPTION
        windowsPE: language/keyboard, LabConfig (BypassTPMCheck, BypassSecureBootCheck, BypassCPUCheck,
        BypassRAMCheck, BypassStorageCheck: for hosts whose CPU is not on the Windows 11 list),
        wipes disk 0 and creates EFI/MSR/Windows (GPT), selects the edition by image name.
        specialize: computer name, time zone, LocalAccountTokenFilterPolicy=1 (full administrator
        token for remote logons of local accounts; the VM is disposable) and BypassNRO.
        oobeSystem: skips the OOBE and online account screens, creates the local administrator account,
        logs on automatically once and, in that logon, disables sleep and clears the auto-logon password.
    .OUTPUTS
        [string] with the XML (UTF-8). Contains the password in plain text: only save it outside the repository.
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
    if ([string]::IsNullOrEmpty($password)) { throw 'The credential needs a password (PowerShell Direct does not accept an empty password).' }
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
<!-- Generated by tools/vm/New-TestVM.ps1 (Debloater). Only for the disposable test VM. -->
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
          <Description>No sleep</Description>
        </SynchronousCommand>
        <SynchronousCommand wcm:action="add">
          <Order>2</Order>
          <CommandLine>powercfg.exe /change monitor-timeout-ac 0</CommandLine>
          <Description>No display timeout</Description>
        </SynchronousCommand>
        <SynchronousCommand wcm:action="add">
          <Order>3</Order>
          <CommandLine>reg.exe add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon /t REG_SZ /d 0 /f</CommandLine>
          <Description>Automatic logon only once</Description>
        </SynchronousCommand>
        <SynchronousCommand wcm:action="add">
          <Order>4</Order>
          <CommandLine>reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultPassword /f</CommandLine>
          <Description>Remove the automatic logon password</Description>
        </SynchronousCommand>
      </FirstLogonCommands>
      <TimeZone>$($x.Tz)</TimeZone>
    </component>
  </settings>
</unattend>
"@

    # Makes sure the result is well-formed XML before using it.
    $null = [xml]$xml
    return $xml
}

function New-DebloaterIsoFile {
    <#
    .SYNOPSIS
        Creates an ISO (ISO 9660 + Joliet) with the contents of a folder, using the Windows IMAPI2FS COM
        (no ADK/oscdimg and no downloads).
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
    // Copies the IStream returned by IMAPI2FS to a file, block by block.
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
    <# .SYNOPSIS Returns the switch by name; for 'Default Switch', also looks it up by its fixed Id. #>
    param([string]$Name = 'Default Switch')
    $sw = Get-VMSwitch -Name $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $sw -and $Name -eq 'Default Switch') {
        $sw = Get-VMSwitch -ErrorAction SilentlyContinue | Where-Object { "$($_.Id)" -eq $script:DefaultSwitchId } | Select-Object -First 1
    }
    if (-not $sw) { throw "Virtual switch '$Name' not found. See Get-VMSwitch." }
    return $sw
}

function Enable-DebloaterGuestServices {
    <# .SYNOPSIS Enables the "Guest Service Interface" integration service (by Id; the name is localized). #>
    param([Parameter(Mandatory)][string]$VMName)
    $svc = Get-VMIntegrationService -VMName $VMName | Where-Object {
        "$($_.Id)" -match $script:GuestServiceInterfaceId -or $_.Name -match 'Guest Service|Convidado'
    } | Select-Object -First 1
    if (-not $svc) { Write-Warning 'Integration service "Guest Service Interface" not found.'; return }
    if (-not $svc.Enabled) { Enable-VMIntegrationService -VMIntegrationService $svc }
}

function Send-DebloaterVmKey {
    <#
    .SYNOPSIS
        Presses a key on the VM's virtual keyboard (Msvm_Keyboard.TypeKey) repeatedly for a few
        seconds. Used right after starting the VM to get past "Press any key to boot from CD or DVD".
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
            Write-Verbose "TypeKey not available yet: $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    }
    Write-Verbose "Keys sent: $sent"
    return $sent
}

function Wait-DebloaterVmSession {
    <#
    .SYNOPSIS
        Tries to open a PowerShell Direct session (New-PSSession -VMName) until it succeeds or times out.
    #>
    param(
        [Parameter(Mandatory)][string]$VMName,
        [Parameter(Mandatory)][pscredential]$Credential,
        [int]$TimeoutMinutes = 10,
        [int]$IntervalSeconds = 15,
        [string]$Activity = 'Waiting for the VM to accept PowerShell Direct'
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
            throw "The VM '$VMName' did not accept PowerShell Direct within $TimeoutMinutes min. Last error: $last"
        }
        Write-Progress -Activity $Activity -Status ("{0:hh\:mm\:ss} elapsed (limit {1} min). Last error: {2}" -f $elapsed, $TimeoutMinutes, $last)
        Start-Sleep -Seconds $IntervalSeconds
    }
}

function Get-DebloaterGuestInfo {
    <# .SYNOPSIS User, elevation, model (VM?) and Windows version inside the session. #>
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
        Ejects the Windows ISO and removes the drive holding the autounattend ISO, deleting that file
        (it contains the VM password in plain text).
    #>
    param([Parameter(Mandatory)][string]$VMName)
    $drives = @(Get-VMDvdDrive -VMName $VMName | Where-Object { $_.Path })
    if ($drives.Count -eq 0) { return }

    # Ejecting/removing a DVD while the VM is running can hang the whole VMMS (the guest holds the media).
    # So the VM is turned off first and started again afterwards, if it was running.
    $wasRunning = (Get-VM -Name $VMName).State -eq 'Running'
    if ($wasRunning) {
        Write-Host 'Turning off the VM to remove the installation media...'
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
            Write-Host "autounattend ISO removed: $path"
        }
        else {
            Set-VMDvdDrive -VMDvdDrive $dvd -Path $null
            Write-Host "ISO ejected: $path"
        }
    }

    if ($wasRunning) { Start-VM -Name $VMName }
}

function Get-DebloaterGuestBootTime {
    <# .SYNOPSIS Date/time of the guest's last boot (Win32_OperatingSystem.LastBootUpTime). #>
    param([Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$Session)
    Invoke-Command -Session $Session -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime }
}

function Restart-DebloaterVmGuest {
    <#
    .SYNOPSIS
        Restarts the guest Windows cleanly (shutdown /r from inside the VM, not a Hyper-V reset, so the
        registry is flushed as in a normal restart) and returns a new PowerShell Direct session, opened
        only after the new boot happened (LastBootUpTime changed).
    #>
    param(
        [Parameter(Mandatory)][string]$VMName,
        [Parameter(Mandatory)][pscredential]$Credential,
        [Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$Session,
        [int]$TimeoutMinutes = 10
    )
    $before = Get-DebloaterGuestBootTime -Session $Session
    # Start-Process returns immediately; the session drops when Windows shuts down.
    Invoke-Command -Session $Session -ScriptBlock { Start-Process -FilePath shutdown.exe -ArgumentList '/r', '/t', '3', '/f' -WindowStyle Hidden }
    Remove-PSSession $Session -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 20
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ($true) {
        $remaining = [math]::Max(1, [int][math]::Ceiling(($deadline - (Get-Date)).TotalMinutes))
        $s = Wait-DebloaterVmSession -VMName $VMName -Credential $Credential -TimeoutMinutes $remaining -IntervalSeconds 10 -Activity 'Waiting for the VM to restart'
        try {
            $now = Get-DebloaterGuestBootTime -Session $s
            if ($now -gt $before) { return $s }
        }
        catch {
            Write-Verbose "Session still unstable: $($_.Exception.Message)"
        }
        Remove-PSSession $s -ErrorAction SilentlyContinue
        if ((Get-Date) -gt $deadline) { throw "The VM '$VMName' did not restart within $TimeoutMinutes min." }
        Start-Sleep -Seconds 10
    }
}

function Set-DebloaterGuestAutoLogon {
    <#
    .SYNOPSIS
        Enables automatic logon of the test account in the guest (Winlogon AutoAdminLogon/DefaultPassword),
        so that after a restart there is an interactive session with Explorer running (it may rewrite
        HKCU values). The password stays in the VM registry only until the checkpoint is restored.
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
    <# .SYNOPSIS Waits for Explorer of the interactive session to appear (automatic logon). Returns $true if it did. #>
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
        Triggers, from inside the guest, what usually undoes tweaks: policy refresh (gpupdate /force),
        a Windows Update scan (UsoClient StartScan and the "Schedule Scan" task) and the WaaSMedic
        remediation. All best-effort: returns one line per trigger with the result.
    #>
    param([Parameter(Mandatory)][System.Management.Automation.Runspaces.PSSession]$Session)
    Invoke-Command -Session $Session -ScriptBlock {
        function Try-Step([string]$Name, [scriptblock]$Body) {
            try { $r = & $Body 2>&1 | Out-String; "{0}: ok {1}" -f $Name, (($r -replace '\s+', ' ').Trim()) }
            catch { "{0}: failed ({1})" -f $Name, $_.Exception.Message }
        }
        Try-Step 'gpupdate /force' { gpupdate.exe /force /wait:120 | Select-Object -Last 2 }
        Try-Step 'UsoClient StartScan' { Start-Process -FilePath UsoClient.exe -ArgumentList 'StartScan' -Wait -WindowStyle Hidden; 'triggered' }
        foreach ($t in @('\Microsoft\Windows\UpdateOrchestrator\Schedule Scan', '\Microsoft\Windows\WaaSMedic\PerformRemediation')) {
            Try-Step "task $t" {
                $path = $t.Substring(0, $t.LastIndexOf('\') + 1); $name = $t.Substring($t.LastIndexOf('\') + 1)
                $task = Get-ScheduledTask -TaskPath $path -TaskName $name -ErrorAction Stop
                Start-ScheduledTask -InputObject $task -ErrorAction Stop
                'started'
            }
        }
        Try-Step 'service WaaSMedicSvc' { Start-Service -Name WaaSMedicSvc -ErrorAction Stop; (Get-Service WaaSMedicSvc).Status }
    }
}

function Get-DebloaterDotNet {
    <# .SYNOPSIS Path to dotnet.exe (C:\Program Files\dotnet or the one on PATH). #>
    $candidates = @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe'
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'dotnet.exe not found (install the .NET 10 SDK).'
}

Export-ModuleMember -Function *-Debloater*
