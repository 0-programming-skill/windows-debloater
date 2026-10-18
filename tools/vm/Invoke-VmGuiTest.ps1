<#
.SYNOPSIS
    Drives the Debloater app (WinUI 3) through its UI inside the test VM: read-only start, restart as
    administrator, Privacy profile, review with high and critical risk confirmations, apply with a restore
    point, per-tweak results, revert from the UI, an ADMX policy through the editor, and screenshots of
    every step. Restores the checkpoint at the end.

.DESCRIPTION
    1. dotnet publish of the app (self-contained x64), the CLI and tools/vm/Debloater.GuiAgent.
    2. Starts/resumes the VM, creates the checkpoint "before-guitest-<date-time>".
    3. Inside the VM (test only, undone by the checkpoint restore):
         - automatic logon of the test account, so there is an interactive desktop;
         - ConsentPromptBehaviorAdmin = 0 ("elevate without prompting" for administrators): the app's
           "Restart as administrator" uses the runas verb, and the UAC consent prompt runs on the secure
           desktop, where UI Automation cannot reach. UAC itself stays on (EnableLUA = 1): the app still
           starts with a filtered token and has to relaunch to get the full one;
         - System Protection on C: (Enable-ComputerRestore), unless -NoSystemProtection, so the restore
           point option can succeed;
         - two scheduled tasks in the user's session: the agent (highest run level, so it can drive the
           elevated app) and the app itself (limited run level = not elevated);
       then restarts the guest and waits for the desktop.
    4. The host sends commands to the agent (JSON files in C:\GuiTest\inbox, results in outbox) through
       PowerShell Direct, and checks each step in the UI and with "Debloater.Cli status".
    5. Copies screenshots to -ScreenshotDir and the report (JSON + log) to -ReportDir, restores the checkpoint.

    Never run the agent on a real PC. Nothing here touches the host besides dotnet publish and Hyper-V.

.PARAMETER Lang
    UI language passed to the app (--lang): pt-BR (default, the VM's language) or en-US.

.EXAMPLE
    .\tools\vm\Invoke-VmGuiTest.ps1
#>
[CmdletBinding()]
param(
    [string]$Name = 'DebloaterTest',
    [string]$CredentialPath,
    [ValidateSet('pt-BR', 'en-US')][string]$Lang = 'pt-BR',
    [string]$ReportDir,
    [string]$ScreenshotDir,
    [switch]$NoSystemProtection,
    [switch]$KeepChanges,
    [ValidateRange(1, 120)][int]$BootTimeoutMinutes = 30
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DebloaterVm.psm1') -Force

$repo = Get-DebloaterRepoRoot
if (-not $CredentialPath) { $CredentialPath = Get-DebloaterDefaultCredentialPath }
if (-not $ReportDir) { $ReportDir = Join-Path $repo 'tests\vm-reports' }
if (-not $ScreenshotDir) { $ScreenshotDir = Join-Path $repo '.claude\docs\screenshots\vm-gui' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$checkpointName = "before-guitest-$stamp"
$null = New-Item -ItemType Directory -Path $ReportDir, $ScreenshotDir -Force
$localReport = Join-Path $ReportDir "guitest-$Name-$stamp.json"
$localLog = Join-Path $ReportDir "guitest-$Name-$stamp.log"

$words = @{
    'pt-BR' = @{ Critical = 'CONFIRMO'; Admin = 'Administrador'; ReadOnly = 'Somente leitura' }
    'en-US' = @{ Critical = 'CONFIRM'; Admin = 'Administrator'; ReadOnly = 'Read-only' }
}[$Lang]

$cred = Import-DebloaterVmCredential -Path $CredentialPath
$script:session = $null
$script:log = New-Object System.Collections.Generic.List[string]
$script:steps = New-Object System.Collections.Generic.List[object]

function Write-Log([string]$Line) { Write-Host $Line; $script:log.Add(("{0:HH:mm:ss} {1}" -f (Get-Date), $Line)) }

function Get-Session {
    if (-not $script:session -or $script:session.State -ne 'Opened') {
        $script:session = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes -IntervalSeconds 5
    }
    $script:session
}

function Invoke-Guest([scriptblock]$Body, [object[]]$Arguments = @()) { Invoke-Command -Session (Get-Session) -ScriptBlock $Body -ArgumentList $Arguments }

function Send-Agent([hashtable]$Command, [int]$WaitSeconds = 120) {
    <# Drops one command for the agent and waits for its result ({ok, output, error}). #>
    $json = $Command | ConvertTo-Json -Compress -Depth 6
    Invoke-Guest {
        param($json, $wait)
        $name = '{0:yyyyMMddHHmmssfff}-{1}.json' -f (Get-Date), (Get-Random)
        [IO.File]::WriteAllText("C:\GuiTest\inbox\$name.tmp", $json)
        Move-Item "C:\GuiTest\inbox\$name.tmp" "C:\GuiTest\inbox\$name"
        $deadline = (Get-Date).AddSeconds($wait)
        while ((Get-Date) -lt $deadline) {
            if (Test-Path "C:\GuiTest\outbox\$name") {
                Start-Sleep -Milliseconds 100
                $r = Get-Content "C:\GuiTest\outbox\$name" -Raw -Encoding UTF8 | ConvertFrom-Json
                Remove-Item "C:\GuiTest\outbox\$name"
                return $r
            }
            Start-Sleep -Milliseconds 300
        }
        [pscustomobject]@{ ok = $false; output = ''; error = 'no answer from the agent' }
    } @($json, $WaitSeconds)
}

function Ui([hashtable]$Command, [int]$WaitSeconds = 120) {
    <# Agent command that must succeed. #>
    $r = Send-Agent $Command $WaitSeconds
    if (-not $r.ok) { throw "UI command failed: $($Command | ConvertTo-Json -Compress) -> $($r.error)" }
    return [string]$r.output
}

$script:shot = 0
function Save-Shot([string]$Label) {
    $script:shot++
    $file = '{0:D2}-{1}.png' -f $script:shot, $Label
    $null = Ui @{ op = 'screenshot'; path = "C:\GuiTest\shots\$file" }
    Copy-Item -FromSession (Get-Session) -LiteralPath "C:\GuiTest\shots\$file" -Destination (Join-Path $ScreenshotDir $file)
    return $file
}

function Step([string]$Title, [scriptblock]$Body) {
    <# Runs a check; records pass/fail, the evidence it returns and a screenshot. Failures do not stop the run. #>
    Write-Log "== $Title"
    $entry = [ordered]@{ step = $Title; result = 'Pass'; evidence = @(); screenshot = $null; error = $null }
    try {
        $entry.evidence = @(& $Body | ForEach-Object { "$_" })
        foreach ($e in $entry.evidence) { Write-Log "   $e" }
    }
    catch {
        $entry.result = 'Fail'
        $entry.error = $_.Exception.Message
        Write-Log "   FAIL: $($_.Exception.Message)"
    }
    try { $entry.screenshot = Save-Shot (($Title -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()) } catch { Write-Log "   (no screenshot: $($_.Exception.Message))" }
    $script:steps.Add([pscustomobject]$entry)
}

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

function Get-CliStatus([string[]]$Ids) {
    <# Tweak id -> state, from "Debloater.Cli status" (the same engine, read from outside the app). #>
    $lines = Invoke-Guest { & C:\GuiTest\cli\Debloater.Cli.exe status --lang en 2>&1 | ForEach-Object { "$_" } }
    $map = @{}
    foreach ($l in $lines) { if ($l -match '^\s+(\S+)\s+(Applied|NotApplied|Partial|NotApplicable|Error)\s') { $map[$Matches[1]] = $Matches[2] } }
    if ($Ids) { $sub = [ordered]@{}; foreach ($i in $Ids) { $sub[$i] = $map[$i] }; return $sub }
    return $map
}

function Set-Desired([string]$Id, [bool]$Value) {
    $null = Ui @{ op = 'select'; id = 'Nav_tweaks' }
    $null = Ui @{ op = 'settext'; id = 'TweaksSearch'; text = $Id }
    Start-Sleep -Milliseconds 800
    $r = Ui @{ op = 'toggle'; id = "Desired_$Id"; value = $Value }
    $null = Ui @{ op = 'settext'; id = 'TweaksSearch'; text = '' }
    return $r
}

function Wait-RunDone([int]$TimeoutSeconds = 1800) {
    # RunText ends with "Log: <file>" (both languages) when the session finishes.
    Ui @{ op = 'waitfor'; id = 'RunText'; contains = 'Log:'; timeout = $TimeoutSeconds } ($TimeoutSeconds + 60)
}

function Wait-ScanDone {
    # After a session the app re-reads every state (~15 s on the VM) and rebuilds the Apply page.
    Start-Sleep -Seconds 25
}

function Assert-NoFailure([string]$Done) {
    if ($Done -notmatch '(\d+) (com falha|failed)') { throw "unexpected result text: $Done" }
    if ([int]$Matches[1] -ne 0) { throw "$($Matches[1]) change(s) failed: $Done" }
}

# ---------------- 1. publish ----------------
$work = Join-Path ([IO.Path]::GetTempPath()) "debloater-guitest-$stamp"
$null = New-Item -ItemType Directory -Path $work -Force
$dotnet = Get-DebloaterDotNet
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    Write-Host 'Publishing the app, the CLI and the UI agent...'
    & $dotnet publish (Join-Path $repo 'src\Debloater.App') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o "$work\app" --nologo -v quiet
    if ($LASTEXITCODE) { throw 'dotnet publish (app) failed.' }
    & $dotnet publish (Join-Path $repo 'src\Debloater.Cli') -c Release -r win-x64 --self-contained true -o "$work\cli" --nologo -v quiet
    if ($LASTEXITCODE) { throw 'dotnet publish (CLI) failed.' }
    & $dotnet publish (Join-Path $PSScriptRoot 'Debloater.GuiAgent') -c Release -o "$work\agent" --nologo -v quiet
    if ($LASTEXITCODE) { throw 'dotnet publish (agent) failed.' }
    foreach ($n in 'app', 'cli', 'agent') { [IO.Compression.ZipFile]::CreateFromDirectory("$work\$n", "$work\$n.zip", 'Fastest', $false) }

    # ---------------- 2. VM and checkpoint ----------------
    $vm = Get-VM -Name $Name
    if ($vm.State -eq 'Paused') { Resume-VM -Name $Name } elseif ($vm.State -ne 'Running') { Start-VM -Name $Name }
    $info = Get-DebloaterGuestInfo -Session (Get-Session)
    Write-Log ("VM: Windows {0} ({1} {2}), user {3}" -f $info.Build, $info.Edition, $info.DisplayVersion, $info.User)
    if (-not $info.IsElevated) { throw 'The PowerShell Direct session is not elevated (see tools/vm README).' }
    Remove-PSSession $script:session; $script:session = $null
    Checkpoint-VM -Name $Name -SnapshotName $checkpointName
    try {
        # ---------------- 3. guest setup ----------------
        Set-DebloaterGuestAutoLogon -Session (Get-Session) -Credential $cred
        foreach ($n in 'app', 'cli', 'agent') {
            Invoke-Guest { param($n) $null = New-Item -ItemType Directory C:\GuiTest -Force } @($n)
            Copy-Item -LiteralPath "$work\$n.zip" -Destination "C:\GuiTest\$n.zip" -ToSession (Get-Session)
        }
        Invoke-Guest {
            param($lang, $sysProt)
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            foreach ($n in 'app', 'cli', 'agent') { [IO.Compression.ZipFile]::ExtractToDirectory("C:\GuiTest\$n.zip", "C:\GuiTest\$n"); Remove-Item "C:\GuiTest\$n.zip" }
            # TEST ONLY: administrators elevate without the consent prompt (secure desktop, out of UI Automation's reach).
            Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name ConsentPromptBehaviorAdmin -Value 0 -Type DWord
            if ($sysProt) {
                Enable-ComputerRestore -Drive 'C:\'
                # TEST ONLY: lift Windows' one-point-per-24-hours limit so this run's point is really created
                # (Windows makes its own point when System Protection is turned on). The app never changes it.
                Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
            }
            $user = "$env:COMPUTERNAME\$env:USERNAME"
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries
            Register-ScheduledTask -TaskName DebloaterGuiAgent -Force -Settings $settings `
                -Principal (New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest) `
                -Action (New-ScheduledTaskAction -Execute 'C:\GuiTest\agent\Debloater.GuiAgent.exe' -Argument 'C:\GuiTest') | Out-Null
            Register-ScheduledTask -TaskName DebloaterGuiApp -Force -Settings $settings `
                -Principal (New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited) `
                -Action (New-ScheduledTaskAction -Execute 'C:\GuiTest\app\Debloater.exe' -Argument "--lang $lang" -WorkingDirectory 'C:\GuiTest\app') | Out-Null
        } @($Lang, (-not $NoSystemProtection))
        Write-Log 'Guest prepared (auto-logon, ConsentPromptBehaviorAdmin=0, tasks). Restarting...'
        $script:session = Restart-DebloaterVmGuest -VMName $Name -Credential $cred -Session (Get-Session) -TimeoutMinutes $BootTimeoutMinutes
        if (-not (Wait-DebloaterGuestDesktop -Session $script:session -TimeoutSeconds 600)) { throw 'No interactive desktop after the restart.' }
        $ready = Invoke-Guest {
            Start-Sleep -Seconds 20
            Start-ScheduledTask -TaskName DebloaterGuiAgent
            $d = (Get-Date).AddMinutes(15)
            while (-not (Test-Path C:\GuiTest\agent.ready) -and (Get-Date) -lt $d) { Start-Sleep -Seconds 2 }
            Test-Path C:\GuiTest\agent.ready
        }
        if (-not $ready) { throw 'The UI agent did not start in the user session.' }
        Write-Log (Ui @{ op = 'resolution'; width = 1600; height = 900 })

        $ids = @('services.print-spooler.disable', 'services.windows-update.disable', 'privacy.ads.advertising-id',
            'privacy.telemetry.diagtrack-service', 'ai.copilot.hide-taskbar-button')
        $before = Get-CliStatus
        Write-Log ("Initial state: {0} tweaks; {1}" -f $before.Count, (($ids | ForEach-Object { "$_=$($before[$_])" }) -join ', '))

        # ---------------- 4. the flow ----------------
        Step 'Start without elevation (read-only)' {
            Invoke-Guest { Start-ScheduledTask -TaskName DebloaterGuiApp }
            $mode = Ui @{ op = 'waitfor'; id = 'ModeText'; contains = $words.ReadOnly; timeout = 600 } 700
            Ui @{ op = 'find'; id = 'ReadOnlyBar' }
            Ui @{ op = 'find'; id = 'ElevateButton' }
            Ui @{ op = 'waitfor'; id = 'ScanText'; contains = ':'; timeout = 900 } 1000
            $mode
        }
        Step 'Restart as administrator' {
            # The app exits while answering (it relaunched itself elevated): a disconnected-object error is expected.
            $null = Send-Agent @{ op = 'invoke'; id = 'ElevateButton' }
            $mode = Ui @{ op = 'waitfor'; id = 'ModeText'; contains = $words.Admin; timeout = 600 } 700
            Ui @{ op = 'waitfor'; id = 'ScanText'; contains = ':'; timeout = 900 } 1000
            $count = Invoke-Guest { @(Get-Process Debloater -ErrorAction SilentlyContinue).Count }
            Assert ($count -eq 1) "expected one app process after the relaunch, found $count"
            $null = Ui @{ op = 'maximize' }
            $mode
        }
        Step 'Load the Privacy profile' {
            $null = Ui @{ op = 'select'; id = 'Nav_profiles' }
            $null = Ui @{ op = 'invoke'; id = 'LoadProfile_Privacy' }
            Start-Sleep -Seconds 2
            Ui @{ op = 'texts'; id = 'ProfilesInfo' }
        }
        Step 'Add a high-risk tweak (Print Spooler)' { Set-Desired 'services.print-spooler.disable' $true }
        Step 'Review: high risk needs the confirmation' {
            $null = Ui @{ op = 'select'; id = 'Nav_apply' }
            $summary = Ui @{ op = 'find'; id = 'ApplySummary' }
            $btn = Ui @{ op = 'find'; id = 'ApplyButton' }
            Assert ($btn -match '\[disabled\]') "Apply should be disabled before the high-risk confirmation: $btn"
            $null = Ui @{ op = 'scrollinto'; id = 'HighConfirm' }
            $summary; $btn
        }
        Step 'Confirm high risk, restore point on' {
            $null = Ui @{ op = 'toggle'; id = 'HighConfirm'; value = $true }
            $null = Ui @{ op = 'toggle'; id = 'RestorePointToggle'; value = $true }
            $btn = Ui @{ op = 'find'; id = 'ApplyButton' }
            Assert ($btn -notmatch '\[disabled\]') "Apply should be enabled: $btn"
            $btn
        }
        Step 'Apply' {
            $t0 = Get-Date
            $null = Ui @{ op = 'invoke'; id = 'ApplyButton' }
            $done = Wait-RunDone
            Wait-ScanDone
            $null = Ui @{ op = 'scrollinto'; id = 'RunText' }
            $results = Ui @{ op = 'findall'; id = 'ApplyResult' }
            Assert-NoFailure $done
            "{0:N0} s" -f ((Get-Date) - $t0).TotalSeconds
            $done
            $results -split "`r?`n" | Where-Object { $_ }
        }
        Step 'Verify with Debloater.Cli status' {
            $after = Get-CliStatus
            $privacy = @($after.Keys | Where-Object { $_ -like 'privacy.*' -or $_ -like 'ai.*' })
            $notApplied = @($privacy | Where-Object { $after[$_] -notin @('Applied', 'NotApplicable') })
            Assert ($after['services.print-spooler.disable'] -eq 'Applied') 'Print Spooler tweak is not Applied'
            Assert ($notApplied.Count -eq 0) ("not applied: " + ($notApplied -join ', '))
            "{0} Privacy/AI tweaks Applied or NotApplicable; Print Spooler Applied" -f $privacy.Count
            Ui @{ op = 'find'; id = 'ApplySummary' }
        }
        $reverts = @('privacy.ads.advertising-id', 'privacy.telemetry.diagtrack-service', 'services.print-spooler.disable', 'ai.copilot.hide-taskbar-button')
        Step 'Revert four tweaks from the Tweaks page' {
            foreach ($i in $reverts) { Set-Desired $i $false }
            $null = Ui @{ op = 'select'; id = 'Nav_apply' }
            Ui @{ op = 'find'; id = 'ApplySummary' }
        }
        Step 'Run the reverts (no restore point)' {
            $null = Ui @{ op = 'toggle'; id = 'RestorePointToggle'; value = $false }
            $null = Ui @{ op = 'toggle'; id = 'HighConfirm'; value = $true }
            $null = Ui @{ op = 'invoke'; id = 'ApplyButton' }
            $done = Wait-RunDone
            Wait-ScanDone
            $null = Ui @{ op = 'scrollinto'; id = 'RunText' }
            Assert-NoFailure $done
            $done
            Ui @{ op = 'findall'; id = 'ApplyResult' }
            $now = Get-CliStatus $reverts
            foreach ($i in $reverts) {
                Assert ($now[$i] -eq $before[$i]) "$i is $($now[$i]) after revert, was $($before[$i]) before apply"
                "$i back to $($now[$i])"
            }
            $summary = Ui @{ op = 'find'; id = 'ApplySummary' }
            Assert (($summary -replace '#ApplySummary', '') -notmatch '[1-9]') "something is still pending or blocked: $summary"
            $summary
        }
        Step 'Policy editor: Search.admx:AllowCloudSearch enabled with value 0' {
            $null = Ui @{ op = 'select'; id = 'Nav_policies' }
            # The page reads every ADMX file and the registry first (minutes on a cold VM).
            $null = Ui @{ op = 'settext'; id = 'PoliciesSearch'; text = 'AllowCloudSearch'; timeout = 300 } 400
            $null = Ui @{ op = 'waitfor'; type = 'ListItem'; scope = @{ id = 'PoliciesList' }; timeout = 900 } 1000
            $null = Ui @{ op = 'select'; type = 'ListItem'; scope = @{ id = 'PoliciesList' } }
            Start-Sleep -Seconds 2
            $null = Ui @{ op = 'select'; type = 'RadioButton'; index = 1; scope = @{ id = 'PolicyStateRadios' } }
            # The enum element is the only visible combo box on the page (the scope box is collapsed for a Machine policy).
            $null = Ui @{ op = 'invoke'; type = 'ComboBox' }
            Start-Sleep -Seconds 1
            $null = Ui @{ op = 'select'; type = 'ListItem'; index = 0; scope = @{ type = 'ComboBox' } }
            Start-Sleep -Seconds 1
            $preview = Ui @{ op = 'find'; id = 'PolicyPreview' }
            Assert ($preview -match 'AllowCloudSearch = 0 \(REG_DWORD\)') "unexpected preview: $preview"
            $null = Ui @{ op = 'invoke'; id = 'PolicyAddPending' }
            $preview
        }
        Step 'Apply the policy' {
            $null = Ui @{ op = 'select'; id = 'Nav_apply' }
            $group = Ui @{ op = 'texts'; id = 'Nav' }
            $null = Ui @{ op = 'invoke'; id = 'ApplyButton' }
            $done = Wait-RunDone
            Wait-ScanDone
            $v = Invoke-Guest { (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search' -ErrorAction SilentlyContinue).AllowCloudSearch }
            Assert ($v -eq 0) "registry AllowCloudSearch = '$v'"
            $done; "registry AllowCloudSearch = $v"
            ($group -split "`r?`n" | Where-Object { $_ -match 'AllowCloudSearch|Pol' }) | Select-Object -First 4
            Ui @{ op = 'findall'; id = 'ApplyResult' }
        }
        Step 'Revert the policy from the editor' {
            $null = Ui @{ op = 'select'; id = 'Nav_policies' }
            # The page reads every ADMX file and the registry first (minutes on a cold VM).
            $null = Ui @{ op = 'settext'; id = 'PoliciesSearch'; text = 'AllowCloudSearch'; timeout = 300 } 400
            $null = Ui @{ op = 'waitfor'; type = 'ListItem'; scope = @{ id = 'PoliciesList' }; timeout = 900 } 1000
            $null = Ui @{ op = 'select'; type = 'ListItem'; scope = @{ id = 'PoliciesList' } }
            Start-Sleep -Seconds 2
            $null = Ui @{ op = 'invoke'; id = 'PolicyRevert' }
            $null = Ui @{ op = 'select'; id = 'Nav_apply' }
            $null = Ui @{ op = 'invoke'; id = 'ApplyButton' }
            $done = Wait-RunDone
            Wait-ScanDone
            $v = Invoke-Guest { $k = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search' -ErrorAction SilentlyContinue; if ($k -and ($k.PSObject.Properties.Name -contains 'AllowCloudSearch')) { $k.AllowCloudSearch } else { 'absent' } }
            Assert ($v -eq 'absent') "registry AllowCloudSearch = '$v' after revert"
            $done; "registry AllowCloudSearch: $v"
        }
        Step 'Critical tweak: dialog, wrong word, cancel' {
            $null = Set-Desired 'services.windows-update.disable' $true
            $null = Ui @{ op = 'select'; id = 'Nav_apply' }
            Ui @{ op = 'find'; id = 'CriticalNote' }
            $null = Ui @{ op = 'invoke'; id = 'ApplyButton' }
            $null = Ui @{ op = 'settext'; id = 'CriticalWordBox'; text = $words.Critical.ToLowerInvariant() }
            $p1 = Ui @{ op = 'find'; id = 'PrimaryButton' }
            $null = Ui @{ op = 'settext'; id = 'CriticalWordBox'; text = $words.Critical.Substring(0, $words.Critical.Length - 1) }
            $p2 = Ui @{ op = 'find'; id = 'PrimaryButton' }
            Assert ($p1 -match '\[disabled\]' -and $p2 -match '\[disabled\]') "primary button enabled with a wrong word: $p1 / $p2"
            $null = Save-Shot 'critical-dialog-wrong-word'
            $null = Ui @{ op = 'invoke'; id = 'CloseButton' }
            Start-Sleep -Seconds 2
            $svc = Invoke-Guest { (Get-Service wuauserv).StartType.ToString() }
            Assert ($svc -ne 'Disabled') 'wuauserv was disabled although the dialog was cancelled'
            "lower case: $p1"; "missing letter: $p2"; "after cancel wuauserv = $svc"
        }
        Step 'Critical tweak: type the word and apply' {
            $null = Ui @{ op = 'invoke'; id = 'ApplyButton' }
            $null = Ui @{ op = 'settext'; id = 'CriticalWordBox'; text = $words.Critical }
            $p = Ui @{ op = 'find'; id = 'PrimaryButton' }
            Assert ($p -notmatch '\[disabled\]') "primary button still disabled: $p"
            $null = Save-Shot 'critical-dialog-word-typed'
            $null = Ui @{ op = 'invoke'; id = 'PrimaryButton' }
            $done = Wait-RunDone
            Wait-ScanDone
            $svc = Invoke-Guest { (Get-Service wuauserv).StartType.ToString() }
            Assert ($svc -eq 'Disabled') "wuauserv = $svc"
            $done; "wuauserv = $svc"
        }
        Step 'Critical tweak: revert from the UI (dialog again)' {
            $null = Set-Desired 'services.windows-update.disable' $false
            $null = Ui @{ op = 'select'; id = 'Nav_apply' }
            $null = Ui @{ op = 'invoke'; id = 'ApplyButton' }
            $null = Ui @{ op = 'settext'; id = 'CriticalWordBox'; text = $words.Critical }
            $null = Ui @{ op = 'invoke'; id = 'PrimaryButton' }
            $done = Wait-RunDone
            Wait-ScanDone
            $now = Get-CliStatus @('services.windows-update.disable')
            $svc = Invoke-Guest { '{0}/{1}' -f (Get-Service wuauserv).StartType, (Get-Service UsoSvc).StartType }
            Assert ($now['services.windows-update.disable'] -eq $before['services.windows-update.disable']) "state $($now['services.windows-update.disable'])"
            $done; "wuauserv/UsoSvc = $svc"
        }
        Step 'Session logs and restore point' {
            Invoke-Guest {
                foreach ($f in Get-ChildItem C:\ProgramData\Debloater\logs -Filter *.log | Sort-Object LastWriteTime) { "--- $($f.Name)"; Get-Content $f.FullName -Encoding UTF8 | Select-Object -First 3 }
                Get-ComputerRestorePoint | ForEach-Object { "restore point: $($_.Description) $($_.CreationTime)" }
            }
        }
    }
    finally {
        try {
            if ($script:session) {
                Copy-Item -FromSession $script:session -LiteralPath 'C:\GuiTest\agent.log' -Destination (Join-Path $ReportDir "guitest-$Name-$stamp-agent.log") -ErrorAction SilentlyContinue
            }
        }
        catch { }
        $report = [ordered]@{ vm = $Name; lang = $Lang; started = $stamp; steps = $script:steps }
        $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $localReport -Encoding UTF8
        [IO.File]::WriteAllLines($localLog, $script:log, (New-Object Text.UTF8Encoding($true)))
        if ($script:session) { Remove-PSSession $script:session -ErrorAction SilentlyContinue }
        if ($KeepChanges) { Write-Host "Changes kept. Roll back: Restore-VMCheckpoint -VMName '$Name' -Name '$checkpointName' -Confirm:`$false" }
        else {
            Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
            Restore-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false
            Remove-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false
            Write-Host "VM restored to the state before the test."
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
$script:steps | Format-Table @{ n = 'Result'; e = { $_.result } }, @{ n = 'Step'; e = { $_.step } }, @{ n = 'Error'; e = { $_.error } } -AutoSize | Out-String -Width 220 | Write-Host
Write-Host "Report: $localReport"
Write-Host "Screenshots: $ScreenshotDir"
$failed = @($script:steps | Where-Object { $_.result -ne 'Pass' }).Count
exit $(if ($failed) { 1 } else { 0 })
