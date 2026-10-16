<#
.SYNOPSIS
    Roda "Debloater.Cli selftest" dentro da VM de teste, traz o relatório JSON e restaura a VM.

.DESCRIPTION
    1. dotnet publish da CLI (Release, win-x64, autocontida) numa pasta temporária, compactada em zip.
    2. Liga/retoma a VM, se preciso, e espera o PowerShell Direct (New-PSSession -VMName -Credential).
       Remove a ISO do autounattend se ainda estiver conectada (ela contém a senha).
    3. Confere que a sessão é de administrador ELEVADO (o selftest exige; a conta local depende de
       LocalAccountTokenFilterPolicy=1, que o autounattend define).
    4. Cria o checkpoint "antes-selftest-<data-hora>".
    5. Copia o zip para C:\DebloaterSelfTest\<data-hora> (Copy-Item -ToSession), extrai e roda
       "Debloater.Cli selftest <args> --json ...", mostrando a saída ao vivo.
    6. Copia o relatório JSON e o log para -ReportDir.
    7. Restaura o checkpoint (Restore-VMCheckpoint), mesmo se algo falhar no meio, e o apaga, a menos
       que se passe -KeepChanges (mantém as alterações e o checkpoint) ou -KeepCheckpoint.
    8. Mostra um resumo por veredito e a tabela dos tweaks.

    Nada roda no host além do dotnet publish e dos cmdlets do Hyper-V. O próprio selftest recusa
    rodar fora de uma VM Hyper-V.

.PARAMETER Name
    Nome da VM. Padrão: DebloaterTest.

.PARAMETER CredentialPath
    Credencial da conta local da VM (Export-Clixml). Padrão: tools/vm/.vmcred.xml.

.PARAMETER SelfTestArgs
    Argumentos extras para o selftest (alias -Args). Ex.: -SelfTestArgs '--category','Privacidade','--verbose'
    ou -SelfTestArgs '--id','privacy.telemetry.feedback-requests','--include-irreversible'.
    Não passe --json: o script define o caminho do relatório.

.PARAMETER ReportDir
    Onde gravar o relatório e o log. Padrão: tests/vm-reports (ignorado pelo git).

.PARAMETER KeepChanges
    Não restaura o checkpoint: a VM fica com o estado deixado pelo selftest, e o checkpoint
    "antes-selftest-..." é mantido para voltar manualmente.

.PARAMETER KeepCheckpoint
    Restaura, mas não apaga o checkpoint "antes-selftest-...".

.PARAMETER PublishDir
    Usa uma publicação já feita (pasta com Debloater.Cli.exe e catalog\) em vez de rodar dotnet publish.

.PARAMETER BootTimeoutMinutes
    Quanto esperar o PowerShell Direct responder. Padrão: 10.

.EXAMPLE
    .\tools\vm\Invoke-VmSelfTest.ps1

.EXAMPLE
    .\tools\vm\Invoke-VmSelfTest.ps1 -Args '--category','Sistema/Serviços','--verbose' -KeepChanges

.EXAMPLE
    .\tools\vm\Invoke-VmSelfTest.ps1 -Args '--include-irreversible','--restore-point'
#>
[CmdletBinding()]
param(
    [string]$Name = 'DebloaterTest',
    [string]$CredentialPath,
    [Alias('Args')][string[]]$SelfTestArgs = @(),
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
if ($SelfTestArgs -contains '--json') { throw 'Não passe --json em -SelfTestArgs: o script define o caminho do relatório.' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$checkpointName = "antes-selftest-$stamp"
$guestDir = "C:\DebloaterSelfTest\$stamp"
$guestReport = "$guestDir\relatorio.json"
$null = New-Item -ItemType Directory -Path $ReportDir -Force
$localReport = Join-Path $ReportDir "selftest-$Name-$stamp.json"
$localLog = Join-Path $ReportDir "selftest-$Name-$stamp.log"

$cred = Import-DebloaterVmCredential -Path $CredentialPath
$vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
if (-not $vm) { throw "VM '$Name' não encontrada. Crie-a com New-TestVM.ps1." }

# ---------- 1. publicação ----------
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
        Write-Host "Publicando a CLI (Release, win-x64, autocontida)..."
        & $dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (código $LASTEXITCODE)." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'Debloater.Cli.exe'))) { throw "Debloater.Cli.exe não encontrado em '$publish'." }
    # O catálogo normalmente já vem na publicação; garante a versão do repositório se faltar.
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'catalog'))) {
        Copy-Item -LiteralPath (Join-Path $repo 'catalog') -Destination (Join-Path $publish 'catalog') -Recurse
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Fastest, $false)
    Write-Host ("Pacote: {0:N1} MB" -f ((Get-Item -LiteralPath $zip).Length / 1MB))

    # ---------- 2. VM no ar ----------
    $vm = Get-VM -Name $Name
    switch ($vm.State) {
        'Running' { }
        'Paused' { Resume-VM -Name $Name }
        default { Write-Host "Ligando a VM '$Name' (estado: $($vm.State))..."; Start-VM -Name $Name }
    }
    # Antes de esperar a sessão: pode desligar e religar a VM se ainda houver mídia de instalação.
    Remove-DebloaterSetupMedia -VMName $Name
    $session = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
    try {
        $info = Get-DebloaterGuestInfo -Session $session
    }
    finally {
        Remove-PSSession $session
    }
    Write-Host ("VM: Windows build {0} ({1} {2}), usuário {3}, modelo '{4}'." -f $info.Build, $info.Edition, $info.DisplayVersion, $info.User, $info.Model)
    if (-not $info.IsElevated) {
        throw ('A sessão PowerShell Direct não está elevada (UAC filtrou o token). Dentro da VM, rode como administrador: ' +
            'reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f')
    }
    if ($info.Model -ne 'Virtual Machine') {
        Write-Warning "Modelo do convidado '$($info.Model)' inesperado; o selftest fará a própria verificação."
    }

    # ---------- 3. checkpoint ----------
    Write-Host "Criando o checkpoint '$checkpointName'..."
    Checkpoint-VM -Name $Name -SnapshotName $checkpointName
    $exitCode = $null
    try {
        $session = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
        try {
            # ---------- 4. cópia ----------
            Invoke-Command -Session $session -ScriptBlock { param($d) $null = New-Item -ItemType Directory -Path $d -Force } -ArgumentList $guestDir
            Write-Host "Copiando para a VM ($guestDir)..."
            Copy-Item -LiteralPath $zip -Destination "$guestDir\debloater.zip" -ToSession $session
            Invoke-Command -Session $session -ScriptBlock {
                param($d)
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                [System.IO.Compression.ZipFile]::ExtractToDirectory("$d\debloater.zip", "$d\app")
            } -ArgumentList $guestDir

            # ---------- 5. selftest ----------
            $cliArgs = @('selftest') + $SelfTestArgs + @('--json', $guestReport)
            Write-Host "Executando na VM: Debloater.Cli $($cliArgs -join ' ')"
            Write-Host ('-' * 78)
            $logLines = New-Object System.Collections.Generic.List[string]
            Invoke-Command -Session $session -ScriptBlock {
                param($d, $a)
                try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
                & "$d\app\Debloater.Cli.exe" @a 2>&1 | ForEach-Object { "$_" }
                "__EXITCODE__=$LASTEXITCODE"
            } -ArgumentList $guestDir, $cliArgs | ForEach-Object {
                $line = "$_"
                if ($line -match '^__EXITCODE__=(-?\d+)$') { $exitCode = [int]$Matches[1] }
                else { Write-Host $line; $logLines.Add($line) }
            }
            Write-Host ('-' * 78)
            [System.IO.File]::WriteAllLines($localLog, $logLines, (New-Object System.Text.UTF8Encoding($true)))

            # ---------- 6. relatório ----------
            $hasReport = Invoke-Command -Session $session -ScriptBlock { param($p) Test-Path -LiteralPath $p } -ArgumentList $guestReport
            if ($hasReport) {
                Copy-Item -FromSession $session -LiteralPath $guestReport -Destination $localReport
                Write-Host "Relatório: $localReport"
            }
            else {
                Write-Warning "O selftest não gerou relatório (código de saída $exitCode). Veja o log: $localLog"
            }
        }
        finally {
            Remove-PSSession $session -ErrorAction SilentlyContinue
        }
    }
    finally {
        # ---------- 7. restauração ----------
        if ($KeepChanges) {
            Write-Host "Alterações mantidas (-KeepChanges). Para voltar: Restore-VMCheckpoint -VMName '$Name' -Name '$checkpointName' -Confirm:`$false"
        }
        else {
            Write-Host "Restaurando o checkpoint '$checkpointName'..."
            Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
            Restore-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false
            if (-not $KeepCheckpoint) { Remove-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false }
            Write-Host "VM restaurada (estado: $((Get-VM -Name $Name).State); checkpoint Standard volta com a memória salva)."
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------- 8. resumo ----------
if (-not (Test-Path -LiteralPath $localReport)) {
    Write-Warning 'Sem relatório para resumir.'
    exit 2
}
$report = Get-Content -LiteralPath $localReport -Raw -Encoding UTF8 | ConvertFrom-Json
$results = @($report.results)
Write-Host ''
Write-Host ("Windows: {0}" -f $report.windows)
Write-Host ("VM: {0}" -f $report.vmCheck)
if ($report.restorePoint) { Write-Host ("Ponto de restauração: criado={0} - {1}" -f $report.restorePoint.created, $report.restorePoint.message) }
Write-Host ''
Write-Host 'Resumo por veredito:'
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
        Select-Object @{ n = 'Veredito'; e = { $_.verdict } }, @{ n = 'Id'; e = { $_.id } }, @{ n = 'Risco'; e = { $_.risk } },
            @{ n = 'ms'; e = { $_.durationMs } }, @{ n = 'Reinic.'; e = { if ($_.rebootPending) { 'sim' } else { '' } } },
            @{ n = 'Motivo'; e = { Format-Short $_.reason } } |
        Format-Table -AutoSize | Out-String -Width 220 | Write-Host
}
$skipped = $results | Where-Object { $_.verdict -eq 'Skipped' }
if ($skipped) {
    Write-Host 'Pulados por motivo:'
    $skipped | Group-Object { ($_.reason -split ':')[0] } | Sort-Object Count -Descending |
        ForEach-Object { Write-Host ("  {0,4}  {1}" -f $_.Count, (Format-Short $_.Name 100)) }
}

$failures = @($results | Where-Object { $_.verdict -notin @('Pass', 'Skipped') }).Count
Write-Host ''
if ($failures -gt 0) { Write-Host "$failures tweak(s) com falha. Detalhes por ação no relatório JSON." -ForegroundColor Yellow; exit 1 }
Write-Host 'Todos os tweaks testados passaram.' -ForegroundColor Green
exit 0
