<#
.SYNOPSIS
    Roda "Debloater.Cli fingerprint create" dentro da VM de teste e traz as impressões digitais
    (.json.gz) para tests\fingerprints. Somente leitura dentro do Windows da VM.

.DESCRIPTION
    1. dotnet publish da CLI (Release, win-x64, autocontida) numa pasta temporária, compactada em zip.
    2. Liga/retoma a VM e espera o PowerShell Direct. A sessão elevada permite listar appx
       provisionados e todas as capacidades (DISM); sem elevação o script avisa e segue.
    3. Cria o checkpoint "antes-fingerprint-<data-hora>" (a cópia do pacote para o disco da VM é a
       única alteração; o checkpoint a desfaz).
    4. Copia o pacote para C:\DebloaterFingerprint\<data-hora>, roda "fingerprint create" uma vez por
       modo de -Binaries (default, all) e copia cada .json.gz (e o log) para -OutDir com o nome
       vm-<build>-<modo>.json.gz.
    5. Restaura o checkpoint e o apaga (a menos que -KeepChanges), mesmo se algo falhar no meio.

    As impressões digitais NÃO devem ser commitadas (tests/fingerprints está no .gitignore).

.PARAMETER Name
    Nome da VM. Padrão: DebloaterTest.

.PARAMETER CredentialPath
    Credencial da conta local da VM (Export-Clixml). Padrão: tools/vm/.vmcred.xml.

.PARAMETER Binaries
    Modos de varredura de binários (um fingerprint por modo). Padrão: default, all.

.PARAMETER OutDir
    Onde gravar as impressões digitais e o log. Padrão: tests/fingerprints (ignorado pelo git).

.PARAMETER PublishDir
    Usa uma publicação já feita (pasta com Debloater.Cli.exe) em vez de rodar dotnet publish.

.PARAMETER KeepChanges
    Não restaura o checkpoint (o pacote fica na VM; o checkpoint é mantido para voltar manualmente).

.PARAMETER BootTimeoutMinutes
    Quanto esperar o PowerShell Direct responder. Padrão: 10.

.EXAMPLE
    .\tools\vm\Invoke-VmFingerprint.ps1

.EXAMPLE
    .\tools\vm\Invoke-VmFingerprint.ps1 -Binaries all
#>
[CmdletBinding()]
param(
    [string]$Name = 'DebloaterTest',
    [string]$CredentialPath,
    [ValidateSet('default', 'all', 'none')][string[]]$Binaries = @('default', 'all'),
    [string]$OutDir,
    [string]$PublishDir,
    [switch]$KeepChanges,
    [ValidateRange(1, 120)][int]$BootTimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DebloaterVm.psm1') -Force

$repo = Get-DebloaterRepoRoot
if (-not $CredentialPath) { $CredentialPath = Get-DebloaterDefaultCredentialPath }
if (-not $OutDir) { $OutDir = Join-Path $repo 'tests\fingerprints' }
$null = New-Item -ItemType Directory -Path $OutDir -Force

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$checkpointName = "antes-fingerprint-$stamp"
$guestDir = "C:\DebloaterFingerprint\$stamp"
$cred = Import-DebloaterVmCredential -Path $CredentialPath
if (-not (Get-VM -Name $Name -ErrorAction SilentlyContinue)) { throw "VM '$Name' não encontrada. Crie-a com New-TestVM.ps1." }

$logLines = New-Object System.Collections.Generic.List[string]
function Write-Log([string]$Line) { Write-Host $Line; $logLines.Add($Line) }

# ---------- 1. publicação ----------
$work = Join-Path ([System.IO.Path]::GetTempPath()) "debloater-fingerprint-$stamp"
$null = New-Item -ItemType Directory -Path $work -Force
$zip = Join-Path $work 'debloater.zip'
$copied = @()
try {
    if ($PublishDir) {
        $publish = (Resolve-Path -LiteralPath $PublishDir).ProviderPath
    }
    else {
        $publish = Join-Path $work 'publish'
        $dotnet = Get-DebloaterDotNet
        Write-Host 'Publicando a CLI (Release, win-x64, autocontida)...'
        & $dotnet publish (Join-Path $repo 'src\Debloater.Cli\Debloater.Cli.csproj') -c Release -r win-x64 --self-contained true -o $publish --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (código $LASTEXITCODE)." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'Debloater.Cli.exe'))) { throw "Debloater.Cli.exe não encontrado em '$publish'." }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Fastest, $false)
    Write-Host ("Pacote: {0:N1} MB" -f ((Get-Item -LiteralPath $zip).Length / 1MB))

    # ---------- 2. VM no ar ----------
    switch ((Get-VM -Name $Name).State) {
        'Running' { }
        'Paused' { Resume-VM -Name $Name }
        default { Write-Host "Ligando a VM '$Name'..."; Start-VM -Name $Name }
    }
    Remove-DebloaterSetupMedia -VMName $Name   # desliga a VM antes de mexer em DVDs, se houver algum
    $s0 = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
    try { $info = Get-DebloaterGuestInfo -Session $s0 } finally { Remove-PSSession $s0 }
    Write-Log ("VM: Windows build {0} ({1} {2}), usuário {3}, elevado: {4}." -f $info.Build, $info.Edition, $info.DisplayVersion, $info.User, $info.IsElevated)
    if (-not $info.IsElevated) { Write-Warning 'Sessão sem elevação: appx provisionados e capacidades disponíveis ficam de fora.' }

    # ---------- 3. checkpoint ----------
    Write-Host "Criando o checkpoint '$checkpointName'..."
    Checkpoint-VM -Name $Name -SnapshotName $checkpointName
    try {
        $session = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
        try {
            Invoke-Command -Session $session -ScriptBlock { param($d) $null = New-Item -ItemType Directory -Path $d -Force } -ArgumentList $guestDir
            Copy-Item -LiteralPath $zip -Destination "$guestDir\debloater.zip" -ToSession $session
            Invoke-Command -Session $session -ScriptBlock {
                param($d)
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                [System.IO.Compression.ZipFile]::ExtractToDirectory("$d\debloater.zip", "$d\app")
            } -ArgumentList $guestDir

            # ---------- 4. fingerprint por modo ----------
            foreach ($mode in $Binaries) {
                $fileName = "vm-$($info.Build)-$mode.json.gz"
                $guestFile = "$guestDir\$fileName"
                Write-Log "Executando na VM: Debloater.Cli fingerprint create $fileName --binaries $mode"
                Invoke-Command -Session $session -ScriptBlock {
                    param($d, $out, $m)
                    try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
                    & "$d\app\Debloater.Cli.exe" fingerprint create $out --binaries $m 2>&1 | ForEach-Object { "$_" }
                    "__EXITCODE__=$LASTEXITCODE"
                } -ArgumentList $guestDir, $guestFile, $mode | ForEach-Object {
                    if ("$_" -notmatch '^__EXITCODE__=') { Write-Log "  $_" }
                }
                $exists = Invoke-Command -Session $session -ScriptBlock { param($p) Test-Path -LiteralPath $p } -ArgumentList $guestFile
                if (-not $exists) { Write-Warning "A VM não gerou $fileName."; continue }
                $local = Join-Path $OutDir $fileName
                Copy-Item -FromSession $session -LiteralPath $guestFile -Destination $local -Force
                $copied += $local
                Write-Log ("Copiado: {0} ({1:N0} KB)" -f $local, ((Get-Item -LiteralPath $local).Length / 1KB))
            }
        }
        finally {
            if ($session) { Remove-PSSession $session -ErrorAction SilentlyContinue }
        }
    }
    finally {
        # ---------- 5. restauração ----------
        if ($KeepChanges) {
            Write-Host "Alterações mantidas (-KeepChanges). Para voltar: Restore-VMCheckpoint -VMName '$Name' -Name '$checkpointName' -Confirm:`$false"
        }
        else {
            Write-Host "Restaurando o checkpoint '$checkpointName'..."
            Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
            Restore-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false
            Remove-VMCheckpoint -VMName $Name -Name $checkpointName -Confirm:$false
            Write-Host "VM restaurada (estado: $((Get-VM -Name $Name).State))."
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    if ($logLines.Count -gt 0) {
        $log = Join-Path $OutDir "vm-fingerprint-$stamp.log"
        [System.IO.File]::WriteAllLines($log, $logLines, (New-Object System.Text.UTF8Encoding($true)))
    }
}

if ($copied.Count -eq 0) { Write-Warning 'Nenhuma impressão digital copiada.'; exit 2 }
Write-Host ''
Write-Host 'Impressões digitais:'
$copied | ForEach-Object { Write-Host "  $_" }
exit 0
