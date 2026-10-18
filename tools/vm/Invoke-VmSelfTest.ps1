<#
.SYNOPSIS
    Runs "Debloater.Cli selftest" inside the test VM, brings back the JSON report and restores the VM.

.DESCRIPTION
    1. dotnet publish of the CLI (Release, win-x64, self-contained) into a temporary folder, zipped.
    2. Starts/resumes the VM if needed and waits for PowerShell Direct (New-PSSession -VMName -Credential).
       Removes the autounattend ISO if it is still attached (it contains the password).
    3. Checks that the session is an ELEVATED administrator (the selftest requires it; the local account
       depends on LocalAccountTokenFilterPolicy=1, which autounattend sets).
    4. Creates the "before-selftest-<date-time>" checkpoint.
    5. Copies the zip to C:\DebloaterSelfTest\<date-time> (Copy-Item -ToSession), extracts it and runs
       "Debloater.Cli selftest <args> --json ...", showing the output live.
       With -Persistence, runs the persistence test instead of the full cycle:
         a. selftest --phase apply-only (applies everything and writes the state file);
         b. enables automatic logon of the test account (interactive session with Explorer) and restarts;
         c. verify "after-restart-1";
         d. gpupdate /force, Windows Update scan and WaaSMedic remediation; verify "after-triggers";
         e. waits -WaitMinutes with the VM idle; verify "after-wait";
         f. restarts again; verify "after-restart-2";
         g. selftest --phase revert (reverts and compares with the original state).
    6. Copies the JSON report and the log to -ReportDir.
    7. Restores the checkpoint (Restore-VMCheckpoint), even if something fails midway, and deletes it,
       unless -KeepChanges (keeps the changes and the checkpoint) or -KeepCheckpoint is passed.
    8. Shows a summary by verdict and the tweak table (and, with -Persistence, what Windows undid).

    Nothing runs on the host besides dotnet publish and the Hyper-V cmdlets. The selftest itself
    refuses to run outside a Hyper-V VM.

.PARAMETER Name
    VM name. Default: DebloaterTest.

.PARAMETER CredentialPath
    Credential of the VM's local account (Export-Clixml). Default: tools/vm/.vmcred.xml.

.PARAMETER SelfTestArgs
    Extra arguments for the selftest (alias -Args). E.g.: -SelfTestArgs '--category','Privacy','--verbose'
    or -SelfTestArgs '--id','privacy.telemetry.feedback-requests','--include-irreversible'.
    Do not pass --json, --phase, --state or --label: the script sets them.

.PARAMETER Persistence
    Persistence test (see DESCRIPTION, step 5). Takes -WaitMinutes + ~15 min.

.PARAMETER WaitMinutes
    With -Persistence: how long to wait with the VM idle between restarts. Default: 10.

.PARAMETER NoTriggers
    With -Persistence: does not trigger gpupdate / Windows Update / WaaSMedic (only restarts and waits).

.PARAMETER NoAutoLogon
    With -Persistence: does not enable automatic logon (no interactive session or Explorer after restarting).

.PARAMETER ReportDir
    Where to save the report and the log. Default: tests/vm-reports (git-ignored).

.PARAMETER KeepChanges
    Does not restore the checkpoint: the VM keeps the state left by the selftest, and the
    "before-selftest-..." checkpoint is kept to roll back manually.

.PARAMETER KeepCheckpoint
    Restores, but does not delete the "before-selftest-..." checkpoint.

.PARAMETER PublishDir
    Uses an existing publish output (folder with Debloater.Cli.exe and catalog\) instead of running dotnet publish.

.PARAMETER BootTimeoutMinutes
    How long to wait for PowerShell Direct to respond (also after each restart). Default: 10.

.EXAMPLE
    .\tools\vm\Invoke-VmSelfTest.ps1

.EXAMPLE
    .\tools\vm\Invoke-VmSelfTest.ps1 -Args '--category','System/Services','--verbose' -KeepChanges

.EXAMPLE
    .\tools\vm\Invoke-VmSelfTest.ps1 -Args '--include-irreversible','--restore-point'

.EXAMPLE
    .\tools\vm\Invoke-VmSelfTest.ps1 -Persistence -WaitMinutes 15
#>
[CmdletBinding()]
param(
    [string]$Name = 'DebloaterTest',
    [string]$CredentialPath,
    [Alias('Args')][string[]]$SelfTestArgs = @(),
    [switch]$Persistence,
    [ValidateRange(0, 240)][int]$WaitMinutes = 10,
    [switch]$NoTriggers,
    [switch]$NoAutoLogon,
    [string]$ReportDir,
    [switch]$KeepChanges,
    [switch]$KeepCheckpoint,
    [string]$PublishDir,
    [ValidateRange(1, 120)][int]$BootTimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DebloaterVm.psm1') -Force

$repo = Get-DebloaterRepoRoot
if (-not $CredentialPath) { $CredentialPath = Get-DebloaterDefaultCredentialPath }
if (-not $ReportDir) { $ReportDir = Join-Path $repo 'tests\vm-reports' }
foreach ($reserved in @('--json', '--phase', '--state', '--label')) {
    if ($SelfTestArgs -contains $reserved) { throw "Do not pass $reserved in -SelfTestArgs: the script sets that argument." }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$kind = if ($Persistence) { 'persistence' } else { 'selftest' }
$checkpointName = "before-selftest-$stamp"
$guestDir = "C:\DebloaterSelfTest\$stamp"
$guestReport = "$guestDir\report.json"
$guestState = "$guestDir\state.json"
$null = New-Item -ItemType Directory -Path $ReportDir -Force
$localReport = Join-Path $ReportDir "$kind-$Name-$stamp.json"
$localLog = Join-Path $ReportDir "$kind-$Name-$stamp.log"

$cred = Import-DebloaterVmCredential -Path $CredentialPath
$vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
if (-not $vm) { throw "VM '$Name' not found. Create it with New-TestVM.ps1." }

$script:session = $null
$script:logLines = New-Object System.Collections.Generic.List[string]

function Write-Log([string]$Line) {
    Write-Host $Line
    $script:logLines.Add($Line)
}

function Invoke-GuestCli([string[]]$CliArgs) {
    <# Runs the CLI in the VM through the current session, showing and keeping the output. Returns the exit code. #>
    Write-Log "Running in the VM: Debloater.Cli $($CliArgs -join ' ')"
    Write-Log ('-' * 78)
    $code = $null
    Invoke-Command -Session $script:session -ScriptBlock {
        param($d, $a)
        try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
        & "$d\app\Debloater.Cli.exe" @a 2>&1 | ForEach-Object { "$_" }
        "__EXITCODE__=$LASTEXITCODE"
    } -ArgumentList $guestDir, $CliArgs | ForEach-Object {
        $line = "$_"
        if ($line -match '^__EXITCODE__=(-?\d+)$') { $code = [int]$Matches[1] }
        else { Write-Log $line }
    }
    Write-Log ('-' * 78)
    return $code
}

function Restart-Guest([string]$Why) {
    Write-Log "Restarting the VM's Windows ($Why)..."
    $script:session = Restart-DebloaterVmGuest -VMName $Name -Credential $cred -Session $script:session -TimeoutMinutes $BootTimeoutMinutes
    Write-Log ("VM is back (booted at {0:HH:mm:ss})." -f (Get-DebloaterGuestBootTime -Session $script:session))
    if (-not $NoAutoLogon) {
        $desktop = Wait-DebloaterGuestDesktop -Session $script:session
        Write-Log $(if ($desktop) { 'Interactive session with Explorer active (automatic logon).' } else { 'WARNING: Explorer did not appear within 3 min (automatic logon failed?).' })
        # Gives Explorer and the logon tasks time to do what they do.
        Start-Sleep -Seconds 60
    }
}

# ---------- 1. publish ----------
$work = Join-Path ([System.IO.Path]::GetTempPath()) "debloater-selftest-$stamp"
$null = New-Item -ItemType Directory -Path $work -Force
$zip = Join-Path $work 'debloater.zip'
try {
    if ($PublishDir) {
        $publish = (Resolve-Path -LiteralPath $PublishDir).ProviderPath
    }
    else {
        $publish = Join-Path $work 'publish'
        $dotnet = Get-DebloaterDotNet
        $project = Join-Path $repo 'src\Debloater.Cli\Debloater.Cli.csproj'
        Write-Host "Publishing the CLI (Release, win-x64, self-contained)..."
        & $dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'Debloater.Cli.exe'))) { throw "Debloater.Cli.exe not found in '$publish'." }
    # The catalog normally comes with the publish output; ensures the repository version if it is missing.
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'catalog'))) {
        Copy-Item -LiteralPath (Join-Path $repo 'catalog') -Destination (Join-Path $publish 'catalog') -Recurse
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Fastest, $false)
    Write-Host ("Package: {0:N1} MB" -f ((Get-Item -LiteralPath $zip).Length / 1MB))

    # ---------- 2. VM up ----------
    $vm = Get-VM -Name $Name
    switch ($vm.State) {
        'Running' { }
        'Paused' { Resume-VM -Name $Name }
        default { Write-Host "Starting the VM '$Name' (state: $($vm.State))..."; Start-VM -Name $Name }
    }
    # Before waiting for the session: may turn the VM off and on again if installation media is still attached.
    Remove-DebloaterSetupMedia -VMName $Name
    $s0 = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
    try {
        $info = Get-DebloaterGuestInfo -Session $s0
    }
    finally {
        Remove-PSSession $s0
    }
    Write-Host ("VM: Windows build {0} ({1} {2}), user {3}, model '{4}'." -f $info.Build, $info.Edition, $info.DisplayVersion, $info.User, $info.Model)
    if (-not $info.IsElevated) {
        throw ('The PowerShell Direct session is not elevated (UAC filtered the token). Inside the VM, run as administrator: ' +
            'reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f')
    }
    if ($info.Model -ne 'Virtual Machine') {
        Write-Warning "Unexpected guest model '$($info.Model)'; the selftest will do its own check."
    }

    # ---------- 3. checkpoint ----------
    Write-Host "Creating the checkpoint '$checkpointName'..."
    Checkpoint-VM -Name $Name -SnapshotName $checkpointName
    try {
        $script:session = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
        try {
            # ---------- 4. copy ----------
            Invoke-Command -Session $script:session -ScriptBlock { param($d) $null = New-Item -ItemType Directory -Path $d -Force } -ArgumentList $guestDir
            Write-Host "Copying to the VM ($guestDir)..."
            Copy-Item -LiteralPath $zip -Destination "$guestDir\debloater.zip" -ToSession $script:session
            Invoke-Command -Session $script:session -ScriptBlock {
                param($d)
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                [System.IO.Compression.ZipFile]::ExtractToDirectory("$d\debloater.zip", "$d\app")
            } -ArgumentList $guestDir

            # ---------- 5. selftest ----------
            if (-not $Persistence) {
                $null = Invoke-GuestCli (@('selftest') + $SelfTestArgs + @('--json', $guestReport))
            }
            else {
                $started = Get-Date
                $code = Invoke-GuestCli (@('selftest', '--phase', 'apply-only', '--state', $guestState) + $SelfTestArgs)
                $hasState = Invoke-Command -Session $script:session -ScriptBlock { param($p) Test-Path -LiteralPath $p } -ArgumentList $guestState
                if (-not $hasState) { throw "The apply-only phase did not write the state file (exit code $code)." }

                if (-not $NoAutoLogon) {
                    Set-DebloaterGuestAutoLogon -Session $script:session -Credential $cred
                    Write-Log 'Automatic logon of the test account enabled (undone when the checkpoint is restored).'
                }
                Restart-Guest 'restart 1'
                $null = Invoke-GuestCli @('selftest', '--phase', 'verify', '--state', $guestState, '--label', 'after-restart-1')

                if (-not $NoTriggers) {
                    Write-Log 'Triggering gpupdate, a Windows Update scan and the WaaSMedic remediation...'
                    Invoke-DebloaterGuestMaintenance -Session $script:session | ForEach-Object { Write-Log "  $_" }
                    Start-Sleep -Seconds 60
                    $null = Invoke-GuestCli @('selftest', '--phase', 'verify', '--state', $guestState, '--label', 'after-triggers')
                }

                if ($WaitMinutes -gt 0) {
                    Write-Log "Waiting $WaitMinutes min with the VM idle..."
                    Remove-PSSession $script:session -ErrorAction SilentlyContinue
                    for ($m = 0; $m -lt $WaitMinutes; $m++) {
                        Write-Progress -Activity 'VM idle (persistence test)' -Status "$m of $WaitMinutes min" -PercentComplete (100 * $m / $WaitMinutes)
                        Start-Sleep -Seconds 60
                    }
                    Write-Progress -Activity 'VM idle (persistence test)' -Completed
                    $script:session = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
                    $null = Invoke-GuestCli @('selftest', '--phase', 'verify', '--state', $guestState, '--label', 'after-wait')
                }

                Restart-Guest 'restart 2'
                $null = Invoke-GuestCli @('selftest', '--phase', 'verify', '--state', $guestState, '--label', 'after-restart-2')
                $null = Invoke-GuestCli @('selftest', '--phase', 'revert', '--state', $guestState, '--json', $guestReport)
                Write-Log ("Persistence test: {0:N0} min." -f ((Get-Date) - $started).TotalMinutes)
            }
            [System.IO.File]::WriteAllLines($localLog, $script:logLines, (New-Object System.Text.UTF8Encoding($true)))

            # ---------- 6. report ----------
            $hasReport = Invoke-Command -Session $script:session -ScriptBlock { param($p) Test-Path -LiteralPath $p } -ArgumentList $guestReport
            if (-not $hasReport -and $Persistence) {
                # Without the revert phase, the state file is the best report available.
                $hasReport = Invoke-Command -Session $script:session -ScriptBlock { param($p, $q) if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination $q; $true } else { $false } } -ArgumentList $guestState, $guestReport
            }
            if ($hasReport) {
                Copy-Item -FromSession $script:session -LiteralPath $guestReport -Destination $localReport
                Write-Host "Report: $localReport"
            }
            else {
                Write-Warning "The selftest did not produce a report. See the log: $localLog"
            }
        }
        finally {
            if ($script:logLines.Count -gt 0 -and -not (Test-Path -LiteralPath $localLog)) {
                [System.IO.File]::WriteAllLines($localLog, $script:logLines, (New-Object System.Text.UTF8Encoding($true)))
            }
            if ($script:session) { Remove-PSSession $script:session -ErrorAction SilentlyContinue }
        }
    }
    finally {
        # ---------- 7. restore ----------
        if ($KeepChanges) {
            Write-Host "Changes kept (-KeepChanges). To roll back: Restore-VMCheckpoint -VMName '$Name' -Name '$checkpointName' -Confirm:`$false"
        }
        else {
            Write-Host "Restoring the checkpoint '$checkpointName'..."
            Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
            Restore-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false
            if (-not $KeepCheckpoint) { Remove-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false }
            Write-Host "VM restored (state: $((Get-VM -Name $Name).State); a Standard checkpoint comes back with the saved memory)."
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------- 8. summary ----------
if (-not (Test-Path -LiteralPath $localReport)) {
    Write-Warning 'No report to summarize.'
    exit 2
}
$report = Get-Content -LiteralPath $localReport -Raw -Encoding UTF8 | ConvertFrom-Json
$results = @($report.results)
Write-Host ''
Write-Host ("Windows: {0}" -f $report.windows)
Write-Host ("VM: {0}" -f $report.vmCheck)
if ($report.restorePoint) { Write-Host ("Restore point: created={0} - {1}" -f $report.restorePoint.created, $report.restorePoint.message) }
if ($report.phaseLog) { Write-Host ("Phases: {0}" -f (@($report.phaseLog) -join ' | ')) }
Write-Host ''
Write-Host 'Summary by verdict:'
$results | Group-Object verdict | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-21} {1,4}" -f $_.Name, $_.Count) }

function Format-Short([string]$Text, [int]$Max = 90) {
    if (-not $Text) { return '' }
    $t = $Text -replace '\s+', ' '
    if ($t.Length -gt $Max) { return $t.Substring(0, $Max - 3) + '...' }
    return $t
}

$tested = $results | Where-Object { $_.verdict -ne 'Skipped' }
if ($tested) {
    Write-Host ''
    $tested | Sort-Object @{ Expression = { if ($_.verdict -eq 'Pass') { 1 } else { 0 } } }, id |
        Select-Object @{ n = 'Verdict'; e = { $_.verdict } }, @{ n = 'Id'; e = { $_.id } }, @{ n = 'Risk'; e = { $_.risk } },
            @{ n = 'ms'; e = { $_.durationMs } }, @{ n = 'Reboot'; e = { if ($_.rebootPending) { 'yes' } else { '' } } },
            @{ n = 'Reason'; e = { Format-Short $_.reason } } |
        Format-Table -AutoSize | Out-String -Width 220 | Write-Host
}
$skipped = $results | Where-Object { $_.verdict -eq 'Skipped' }
if ($skipped) {
    Write-Host 'Skipped by reason:'
    $skipped | Group-Object { ($_.reason -split ':')[0] } | Sort-Object Count -Descending |
        ForEach-Object { Write-Host ("  {0,4}  {1}" -f $_.Count, (Format-Short $_.Name 100)) }
}

$persistenceFailures = 0
if ($Persistence) {
    $checked = @($results | Where-Object { $_.persistenceVerdict })
    Write-Host ''
    Write-Host 'Persistence (last check):'
    $checked | Group-Object persistenceVerdict | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-21} {1,4}" -f $_.Name, $_.Count) }
    $undone = @($checked | Where-Object { $_.persistenceVerdict -ne 'Persisted' -or $_.revertedAfter })
    $persistenceFailures = @($checked | Where-Object { $_.persistenceVerdict -ne 'Persisted' }).Count
    if ($undone.Count -gt 0) {
        Write-Host ''
        $undone | Sort-Object id |
            Select-Object @{ n = 'Persistence'; e = { $_.persistenceVerdict } }, @{ n = 'Id'; e = { $_.id } },
                @{ n = 'Since'; e = { $_.revertedAfter } },
                @{ n = 'Undone actions'; e = { Format-Short ((@($_.persistence) | Where-Object { $_.revertedActions } | Select-Object -Last 1).revertedActions -join '; ') 120 } } |
            Format-Table -AutoSize | Out-String -Width 260 | Write-Host
    }
}

$failures = @($results | Where-Object { $_.verdict -notin @('Pass', 'Skipped') }).Count
Write-Host ''
if ($failures -gt 0 -or $persistenceFailures -gt 0) {
    Write-Host "$failures tweak(s) failed the cycle; $persistenceFailures undone by Windows. Per-action details in the JSON report." -ForegroundColor Yellow
    exit 1
}
Write-Host 'All tested tweaks passed.' -ForegroundColor Green
exit 0
