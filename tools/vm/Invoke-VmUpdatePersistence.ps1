<#
.SYNOPSIS
    Persistence across Windows Update in the test VM: applies every reversible tweak except those that block,
    defer or pause Windows Update, installs the available updates with the Windows Update Agent COM API,
    restarts until nothing is left, checks what Windows reverted and restores the checkpoint.

.DESCRIPTION
    1. dotnet publish of the CLI; restores -BaseCheckpoint (clean-base) and creates "before-update-<date-time>".
    2. selftest --phase apply-only with every catalog tweak except -Exclude (irreversible ones are skipped by
       selftest); enables auto-logon and restarts; verify "before-update-restart".
    3. Rounds (at most -MaxRounds): a scheduled task running as SYSTEM searches
       "IsInstalled=0 and Type='Software'" with Microsoft.Update.Session, downloads and installs everything
       found (the WUA download/install calls are refused from a remote session such as PowerShell Direct);
       restart; verify "after-update-round-N". Stops when a search finds nothing.
       With -FeatureOnDemand NAME, installs that capability (Add-WindowsCapability) instead, as a servicing
       stand-in when no cumulative update is published for the build.
    4. Final restart, verify "after-update-final-restart", selftest --phase revert.
    5. Copies the report (JSON), the log and the WUA log to -ReportDir, restores -BaseCheckpoint and deletes the
       temporary checkpoint.

    Everything that changes state runs in the VM. Takes from ~40 min to hours (download and install time).

.EXAMPLE
    .\tools\vm\Invoke-VmUpdatePersistence.ps1

.EXAMPLE
    .\tools\vm\Invoke-VmUpdatePersistence.ps1 -FeatureOnDemand 'Tools.DeveloperMode.Core~~~~0.0.1.0'
#>
[CmdletBinding()]
param(
    [string]$Name = 'DebloaterTest',
    [string]$CredentialPath,
    [string]$BaseCheckpoint = 'clean-base',
    [string[]]$Exclude = @('services.windows-update.disable', 'update.notify-before-install', 'update.defer-feature-updates',
        'update.defer-quality-updates', 'update.no-early-updates'),
    [ValidateRange(1, 10)][int]$MaxRounds = 4,
    [string]$FeatureOnDemand,
    [string]$ReportDir,
    [ValidateRange(1, 240)][int]$BootTimeoutMinutes = 60
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DebloaterVm.psm1') -Force

$repo = Get-DebloaterRepoRoot
if (-not $CredentialPath) { $CredentialPath = Get-DebloaterDefaultCredentialPath }
if (-not $ReportDir) { $ReportDir = Join-Path $repo 'tests\vm-reports' }
$null = New-Item -ItemType Directory -Path $ReportDir -Force
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$kind = if ($FeatureOnDemand) { 'fod-persistence' } else { 'wu-persistence' }
$logFile = Join-Path $ReportDir "$kind-$Name-$stamp.log"
$checkpointName = "before-update-$stamp"
$cred = Import-DebloaterVmCredential -Path $CredentialPath
$script:s = $null

function Write-Log([string]$Message) {
    $line = "{0:HH:mm:ss} {1}" -f (Get-Date), $Message
    Write-Host $line
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
}

function Get-Session {
    if (-not $script:s -or $script:s.State -ne 'Opened') {
        $script:s = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
    }
    $script:s
}

function Invoke-GuestCli([string[]]$CliArgs) {
    $shown = $CliArgs -join ' '
    if ($shown.Length -gt 200) { $shown = $shown.Substring(0, 200) + '...' }
    Write-Log "Debloater.Cli $shown"
    Invoke-Command -Session (Get-Session) -ScriptBlock {
        param($a)
        & C:\UpdateTest\cli\Debloater.Cli.exe @a --lang en 2>&1 | ForEach-Object { "$_" }
    } -ArgumentList (, $CliArgs) | ForEach-Object {
        Add-Content -LiteralPath $logFile -Value $_ -Encoding UTF8
        $_
    } | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
}

function Restart-Guest {
    $script:s = Restart-DebloaterVmGuest -VMName $Name -Credential $cred -Session (Get-Session) -TimeoutMinutes $BootTimeoutMinutes
    $null = Wait-DebloaterGuestDesktop -Session $script:s -TimeoutSeconds 900
    Start-Sleep -Seconds 90   # Explorer and the logon tasks
    $info = Get-DebloaterGuestInfo -Session $script:s
    Write-Log "VM is back: build $($info.Build)"
}

# Runs in the VM as SYSTEM: search, download and install with the Windows Update Agent API.
$wuScript = @'
$ErrorActionPreference = 'Continue'
$log = 'C:\UpdateTest\wu.log'
function L($m) { Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m) -Encoding UTF8 }
$result = [ordered]@{ found = @(); installed = @(); rebootRequired = $false; error = $null }
try {
    $session = New-Object -ComObject Microsoft.Update.Session
    $session.ClientApplicationID = 'Debloater VM persistence test'
    L "search: IsInstalled=0 and Type='Software'"
    $sr = $session.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Software'")
    L "found $($sr.Updates.Count) (ResultCode $($sr.ResultCode))"
    $coll = New-Object -ComObject Microsoft.Update.UpdateColl
    foreach ($u in $sr.Updates) {
        $kb = ($u.KBArticleIDs | ForEach-Object { "KB$_" }) -join ','
        L "  $($u.Title) [$kb] $([math]::Round($u.MaxDownloadSize / 1MB)) MB"
        $result.found += "$($u.Title) [$kb]"
        if (-not $u.EulaAccepted) { $u.AcceptEula() }
        [void]$coll.Add($u)
    }
    if ($coll.Count -gt 0) {
        $dl = $session.CreateUpdateDownloader(); $dl.Updates = $coll
        $dr = $dl.Download(); L "download: ResultCode $($dr.ResultCode)"
        $inst = $session.CreateUpdateInstaller(); $inst.Updates = $coll
        $ir = $inst.Install(); L "install: ResultCode $($ir.ResultCode), RebootRequired $($ir.RebootRequired)"
        for ($i = 0; $i -lt $coll.Count; $i++) {
            $r = $ir.GetUpdateResult($i)
            $result.installed += [ordered]@{ title = $coll.Item($i).Title; resultCode = $r.ResultCode; hresult = ('0x{0:X8}' -f $r.HResult) }
        }
        $result.rebootRequired = $ir.RebootRequired
    }
}
catch { $result.error = $_.Exception.Message; L "ERROR: $($_.Exception.Message)" }
$result | ConvertTo-Json -Depth 5 | Set-Content C:\UpdateTest\wu-result.json -Encoding UTF8
'@

# ---------- tweak selection and publish ----------
$ids = New-Object System.Collections.Generic.List[string]
foreach ($f in Get-ChildItem -LiteralPath (Join-Path $repo 'catalog') -Filter *.yaml) {
    foreach ($line in Get-Content -LiteralPath $f.FullName -Encoding UTF8) { if ($line -match '^  - id: (\S+)\s*$') { $ids.Add($Matches[1]) } }
}
$ids = @($ids | Where-Object { $Exclude -notcontains $_ })
Write-Log "Tweaks selected: $($ids.Count) (excluded: $($Exclude -join ', '))"

$work = Join-Path ([IO.Path]::GetTempPath()) "debloater-update-$stamp"
$null = New-Item -ItemType Directory -Path $work -Force
try {
    & (Get-DebloaterDotNet) publish (Join-Path $repo 'src\Debloater.Cli') -c Release -r win-x64 --self-contained true -o "$work\cli" --nologo -v quiet
    if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory("$work\cli", "$work\cli.zip", 'Fastest', $false)
    [IO.File]::WriteAllText("$work\wu.ps1", $wuScript, (New-Object Text.UTF8Encoding($true)))

    # ---------- VM ----------
    Write-Log "Restoring '$BaseCheckpoint'"
    Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
    Restore-VMCheckpoint -VMName $Name -Name $BaseCheckpoint -Confirm:$false
    Start-VM -Name $Name
    $info = Get-DebloaterGuestInfo -Session (Get-Session)
    Write-Log "VM build: $($info.Build) ($($info.Edition))"
    if (-not $info.IsElevated) { throw 'The PowerShell Direct session is not elevated (see tools/vm README).' }
    Remove-PSSession $script:s; $script:s = $null
    Checkpoint-VM -Name $Name -SnapshotName $checkpointName
    try {
        Invoke-Command -Session (Get-Session) { $null = New-Item -ItemType Directory C:\UpdateTest -Force }
        Copy-Item -LiteralPath "$work\cli.zip" -Destination C:\UpdateTest\cli.zip -ToSession (Get-Session)
        Copy-Item -LiteralPath "$work\wu.ps1" -Destination C:\UpdateTest\wu.ps1 -ToSession (Get-Session)
        Invoke-Command -Session (Get-Session) {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [IO.Compression.ZipFile]::ExtractToDirectory('C:\UpdateTest\cli.zip', 'C:\UpdateTest\cli')
            Register-ScheduledTask -TaskName DebloaterUpdateTest -Force `
                -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\UpdateTest\wu.ps1') `
                -Principal (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest) `
                -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromHours(6)) -AllowStartIfOnBatteries) | Out-Null
        }

        $idArgs = @(); foreach ($i in $ids) { $idArgs += '--id'; $idArgs += $i }
        Invoke-GuestCli (@('selftest', '--phase', 'apply-only', '--state', 'C:\UpdateTest\state.json') + $idArgs)
        Set-DebloaterGuestAutoLogon -Session (Get-Session) -Credential $cred
        Restart-Guest
        Invoke-GuestCli @('selftest', '--phase', 'verify', '--state', 'C:\UpdateTest\state.json', '--label', 'before-update-restart')

        if ($FeatureOnDemand) {
            Write-Log "Installing the Feature on Demand $FeatureOnDemand"
            Invoke-Command -Session (Get-Session) {
                param($n)
                $t = Get-Date
                try { $r = Add-WindowsCapability -Online -Name $n -ErrorAction Stop; "RestartNeeded=$($r.RestartNeeded) in $([int]((Get-Date) - $t).TotalSeconds) s" }
                catch { "error: $($_.Exception.Message)" }
            } -ArgumentList $FeatureOnDemand | ForEach-Object { Write-Log "  $_" }
            Restart-Guest
            Invoke-GuestCli @('selftest', '--phase', 'verify', '--state', 'C:\UpdateTest\state.json', '--label', 'after-fod-restart')
        }
        else {
            for ($round = 1; $round -le $MaxRounds; $round++) {
                Write-Log "Windows Update, round $round"
                Invoke-Command -Session (Get-Session) { Remove-Item C:\UpdateTest\wu-result.json -ErrorAction SilentlyContinue; Start-ScheduledTask -TaskName DebloaterUpdateTest }
                $deadline = (Get-Date).AddHours(5)
                $json = $null
                while (-not $json -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Seconds 60
                    try { $json = Invoke-Command -Session (Get-Session) { if (Test-Path C:\UpdateTest\wu-result.json) { Get-Content C:\UpdateTest\wu-result.json -Raw } } }
                    catch { $script:s = $null }
                }
                if (-not $json) { Write-Log 'Windows Update round timed out.'; break }
                $r = $json | ConvertFrom-Json
                Write-Log ("Found {0}: {1}" -f @($r.found).Count, (@($r.found) -join ' | '))
                foreach ($i in @($r.installed)) { Write-Log "  installed: $($i.title) (ResultCode $($i.resultCode), $($i.hresult))" }
                if ($r.error) { Write-Log "  error: $($r.error)" }
                if (@($r.found).Count -eq 0) { break }
                Restart-Guest
                Invoke-GuestCli @('selftest', '--phase', 'verify', '--state', 'C:\UpdateTest\state.json', '--label', "after-update-round-$round")
            }
        }

        Restart-Guest
        Invoke-GuestCli @('selftest', '--phase', 'verify', '--state', 'C:\UpdateTest\state.json', '--label', 'after-update-final-restart')
        Invoke-GuestCli @('selftest', '--phase', 'revert', '--state', 'C:\UpdateTest\state.json', '--json', 'C:\UpdateTest\report.json')
        Copy-Item -FromSession (Get-Session) -LiteralPath C:\UpdateTest\report.json -Destination (Join-Path $ReportDir "$kind-$Name-$stamp.json")
        Copy-Item -FromSession (Get-Session) -LiteralPath C:\UpdateTest\wu.log -Destination (Join-Path $ReportDir "$kind-$Name-$stamp-wu.log") -ErrorAction SilentlyContinue
    }
    finally {
        if ($script:s) { Remove-PSSession $script:s -ErrorAction SilentlyContinue }
        Write-Log "Restoring '$BaseCheckpoint'"
        Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
        Restore-VMCheckpoint -VMName $Name -Name $BaseCheckpoint -Confirm:$false
        Remove-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$report = Join-Path $ReportDir "$kind-$Name-$stamp.json"
if (-not (Test-Path -LiteralPath $report)) { Write-Warning 'No report.'; exit 2 }
$results = @((Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json).results)
$checked = @($results | Where-Object { $_.persistenceVerdict })
Write-Host ''
$checked | Group-Object persistenceVerdict | ForEach-Object { Write-Host ("  {0,-20} {1,4}" -f $_.Name, $_.Count) }
$undone = @($checked | Where-Object { $_.persistenceVerdict -ne 'Persisted' })
foreach ($u in $undone) { Write-Host ("  {0}: {1} since {2}" -f $u.id, $u.persistenceVerdict, $u.revertedAfter) -ForegroundColor Yellow }
Write-Host "Report: $report"
exit $(if ($undone.Count) { 1 } else { 0 })
