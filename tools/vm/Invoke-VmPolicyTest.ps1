<#
.SYNOPSIS
    Testa a edição de políticas ADMX ("policy show/set/revert") de verdade, dentro da VM de teste,
    contra o C:\Windows\PolicyDefinitions da própria VM, e restaura a VM.

.DESCRIPTION
    1. dotnet publish da CLI (Release, win-x64, autocontida) numa pasta temporária, compactada em zip.
    2. Liga/retoma a VM, espera o PowerShell Direct e confere que a sessão é ELEVADA.
    3. Cria o checkpoint "antes-policytest-<data-hora>".
    4. Copia o pacote para C:\DebloaterPolicyTest\<data-hora> e roda a matriz de testes na VM:
       para cada política (ver $Matrix), com o registro dos valores/chaves tocados exportado
       ("reg export" da chave existente mais próxima de cada chave da política) antes e depois de
       cada passo:
         a. (opcional) grava valores "originais" (Preseed), que fazem parte do estado inicial;
         b. policy show (estado inicial);
         c. configurações inválidas (Invalid): devem falhar sem tocar no registro nem criar backup;
         d. Não configurada a partir do estado inicial (se já era Não configurada: nada muda, sem backup);
         e. policy set (Set): o show deve ler o estado e os valores pedidos; os valores do registro
            (Expect.Reg) são conferidos de forma independente;
         f. o mesmo set de novo: "nada foi alterado", backup idêntico, registro idêntico;
         g. policy backups lista a política;
         h. Não configurada: estado lido Não configurada, os valores de Expect.Reg ausentes (NcReg
            confere o que deve ficar); de novo: nada muda;
         i. Set2 (padrão: Set) e os passos extras (Extra), cada um conferido como em e.;
         j. policy revert: backup apagado e o export do registro IDÊNTICO (byte a byte) ao inicial;
            o show volta ao estado inicial; um segundo revert falha ("Nenhum backup").
       Depois, a conferência cruzada com o próprio Windows (Cross): aplica algumas políticas com
       mapeamento na Policy CSP, lê a ponte WMI do MDM (root\cimv2\mdm\dmmap, como SYSTEM numa tarefa
       agendada), o PolicyManager e o agente do Windows Update (Microsoft.Update.AutoUpdate), roda
       gpupdate /force (os valores gravados direto no registro devem continuar lá), reverte tudo e
       compara o registro. Confere também que o Registry.pol da GPO local não foi tocado.
    5. Traz policytest-<vm>-<data-hora>.json e .md (e a pasta com saídas e exports) para -ReportDir.
    6. Restaura o checkpoint e o apaga (mesmo se algo falhar no meio), a menos que -KeepChanges.

    Nada roda no host além do dotnet publish e dos cmdlets do Hyper-V: "policy set/revert" NUNCA
    rodam no PC de verdade.

.PARAMETER Name
    Nome da VM. Padrão: DebloaterTest.

.PARAMETER CredentialPath
    Credencial da conta local da VM (Export-Clixml). Padrão: tools/vm/.vmcred.xml.

.PARAMETER Only
    Roda só os casos cujo nome de política contém um destes textos (ex.: -Only AllowTelemetry,Spotlight).

.PARAMETER NoCross
    Não roda a conferência cruzada com o Windows (MDM/PolicyManager/Windows Update/gpupdate).

.PARAMETER ReportDir
    Onde gravar o relatório. Padrão: tests/vm-reports (ignorado pelo git).

.PARAMETER KeepChanges
    Não restaura o checkpoint (mantém a VM como o teste deixou e o checkpoint para voltar à mão).

.PARAMETER PublishDir
    Usa uma publicação já feita (pasta com Debloater.Cli.exe) em vez de rodar dotnet publish.

.PARAMETER BootTimeoutMinutes
    Quanto esperar o PowerShell Direct responder. Padrão: 10.

.EXAMPLE
    .\tools\vm\Invoke-VmPolicyTest.ps1

.EXAMPLE
    .\tools\vm\Invoke-VmPolicyTest.ps1 -Only AllowTelemetry -NoCross
#>
[CmdletBinding()]
param(
    [string]$Name = 'DebloaterTest',
    [string]$CredentialPath,
    [string[]]$Only = @(),
    [switch]$NoCross,
    [string]$ReportDir,
    [switch]$KeepChanges,
    [string]$PublishDir,
    [ValidateRange(1, 120)][int]$BootTimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DebloaterVm.psm1') -Force

# ======================================================================================
# Matriz de testes. Valores do registro: "HIVE\chave\nome=tipo:dado" (dword, qword, sz,
# expand_sz, multi_sz com itens separados por "|") ou "...=absent".
# Expect: Scope (HKLM/HKCU), State (Enabled/Disabled/NotConfigured), Values (linhas "id = valor"
# que o "policy show" deve listar) e Reg (valores conferidos direto no registro).
# ======================================================================================
$P = 'HKLM\Software\Policies\Microsoft\Windows'
$U = 'HKCU\Software\Policies\Microsoft\Windows'
$Matrix = @(
    @{
        Policy = 'DataCollection.admx:AllowTelemetry'
        Covers = 'enum por valor, #índice e nome exibido; classe Both com --scope machine e --scope user; política só com elemento'
        Hives = @('HKLM', 'HKCU')
        Invalid = @(
            @('--state', 'enabled', '--scope', 'machine', '--value', 'AllowTelemetry=7'),
            @('--state', 'enabled', '--scope', 'machine', '--value', 'NaoExiste=1'),
            @('--state', 'enabled', '--scope', 'machine', '--value', 'AllowTelemetry=#9')
        )
        NcArgs = @('--scope', 'machine')
        Set = @('--state', 'enabled', '--scope', 'machine', '--value', 'AllowTelemetry=0')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('AllowTelemetry = 0'); Reg = @("$P\DataCollection\AllowTelemetry=dword:0") }
        Set2 = @('--state', 'enabled', '--scope', 'machine', '--value', 'AllowTelemetry=#2')
        Expect2 = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('AllowTelemetry = 3'); Reg = @("$P\DataCollection\AllowTelemetry=dword:3") }
        Extra = @(
            @{ Args = @('--state', 'enabled', '--scope', 'user', '--value', 'AllowTelemetry=Enviar dados de diagnóstico necessários')
               Expect = @{ Scope = 'HKCU'; State = 'Enabled'; Values = @('AllowTelemetry = 1'); Reg = @("$U\DataCollection\AllowTelemetry=dword:1", "$P\DataCollection\AllowTelemetry=dword:3") } }
        )
    },
    @{
        Policy = 'DataCollection.admx:AllowTelemetry'
        Label = 'AllowTelemetry (Desabilitada, com valor original)'
        Covers = 'Desabilitada numa política só com elemento (apaga o valor = Não configurada); restaura valor original pré-existente'
        Preseed = @("$P\DataCollection\AllowTelemetry=dword:3")
        NcArgs = @('--scope', 'machine')
        Set = @('--state', 'disabled', '--scope', 'machine')
        Expect = @{ Scope = 'HKLM'; State = 'NotConfigured'; Reg = @("$P\DataCollection\AllowTelemetry=absent") }
        Set2 = @('--state', 'enabled', '--scope', 'machine', '--value', 'AllowTelemetry=1')
        Expect2 = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('AllowTelemetry = 1'); Reg = @("$P\DataCollection\AllowTelemetry=dword:1") }
    },
    @{
        Policy = 'WindowsCopilot.admx:TurnOffWindowsCopilot'
        Covers = 'classe User (HKCU); enabledValue/disabledValue; --scope machine recusado'
        Invalid = @(, @('--state', 'enabled', '--scope', 'machine'))
        Set = @('--state', 'enabled')
        Expect = @{ Scope = 'HKCU'; State = 'Enabled'; Reg = @("$U\WindowsCopilot\TurnOffWindowsCopilot=dword:1") }
        Set2 = @('--state', 'disabled')
        Expect2 = @{ Scope = 'HKCU'; State = 'Disabled'; Reg = @("$U\WindowsCopilot\TurnOffWindowsCopilot=dword:0") }
    },
    @{
        Policy = 'Search.admx:AllowCortana'
        Covers = 'enabledValue/disabledValue; Não configurada a partir de Desabilitada'
        Set = @('--state', 'disabled')
        Expect = @{ Scope = 'HKLM'; State = 'Disabled'; Reg = @("$P\Windows Search\AllowCortana=dword:0") }
        Set2 = @('--state', 'enabled')
        Expect2 = @{ Scope = 'HKLM'; State = 'Enabled'; Reg = @("$P\Windows Search\AllowCortana=dword:1") }
    },
    @{
        Policy = 'Search.admx:DoNotUseWebResults'
        Covers = 'ConnectedSearchUseWeb: enabledValue 0 / disabledValue 1 (invertidos)'
        Set = @('--state', 'enabled')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Reg = @("$P\Windows Search\ConnectedSearchUseWeb=dword:0") }
        Set2 = @('--state', 'disabled')
        Expect2 = @{ Scope = 'HKLM'; State = 'Disabled'; Reg = @("$P\Windows Search\ConnectedSearchUseWeb=dword:1") }
    },
    @{
        Policy = 'Search.admx:DisableWebSearch'
        Covers = 'sem enabledValue/disabledValue (Habilitada = REG_DWORD 1; Desabilitada apaga)'
        Set = @('--state', 'enabled')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Reg = @("$P\Windows Search\DisableWebSearch=dword:1") }
        Set2 = @('--state', 'disabled')
        Expect2 = @{ Scope = 'HKLM'; State = 'NotConfigured'; Reg = @("$P\Windows Search\DisableWebSearch=absent") }
    },
    @{
        Policy = 'CloudContent.admx:DisableWindowsConsumerFeatures'
        Covers = 'valor original pré-existente (Desabilitada) restaurado; Habilitada/Desabilitada'
        Preseed = @("$P\CloudContent\DisableWindowsConsumerFeatures=dword:0")
        Set = @('--state', 'enabled')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Reg = @("$P\CloudContent\DisableWindowsConsumerFeatures=dword:1") }
    },
    @{
        Policy = 'CloudContent.admx:ConfigureWindowsSpotlight'
        Covers = 'classe User; boolean com trueValue/falseValue; disabledValue 2; Não configurada a partir de Desabilitada'
        Set = @('--state', 'enabled', '--value', 'ConfigureWindowsSpotlight_Checkbox=true')
        Expect = @{ Scope = 'HKCU'; State = 'Enabled'; Values = @('ConfigureWindowsSpotlight_Checkbox = true')
                    Reg = @("$U\CloudContent\ConfigureWindowsSpotlight=dword:1", "$U\CloudContent\IncludeEnterpriseSpotlight=dword:1") }
        Set2 = @('--state', 'enabled', '--value', 'ConfigureWindowsSpotlight_Checkbox=false')
        Expect2 = @{ Scope = 'HKCU'; State = 'Enabled'; Values = @('ConfigureWindowsSpotlight_Checkbox = false')
                     Reg = @("$U\CloudContent\IncludeEnterpriseSpotlight=dword:0") }
        Extra = @(
            @{ Args = @('--state', 'disabled')
               Expect = @{ Scope = 'HKCU'; State = 'Disabled'; Reg = @("$U\CloudContent\ConfigureWindowsSpotlight=dword:2", "$U\CloudContent\IncludeEnterpriseSpotlight=absent") } },
            @{ Args = @('--state', 'notconfigured')
               Expect = @{ Scope = 'HKCU'; State = 'NotConfigured'; Reg = @("$U\CloudContent\ConfigureWindowsSpotlight=absent") } }
        )
    },
    @{
        Policy = 'WindowsUpdate.admx:DeferQualityUpdates'
        Covers = 'decimal com min/max (fora do intervalo recusado; limites 0 e 30); text com maxLength; risco alto exige --max-risk'
        HighRisk = $true
        Invalid = @(
            @('--state', 'enabled', '--value', 'DeferQualityUpdatesPeriodId=31', '--max-risk', 'high'),
            @('--state', 'enabled', '--value', 'DeferQualityUpdatesPeriodId=-1', '--max-risk', 'high'),
            @('--state', 'enabled', '--value', 'PauseQualityUpdatesStartId=2026-10-18X', '--max-risk', 'high'),
            @('--state', 'enabled', '--value', 'DeferQualityUpdatesPeriodId=7')
        )
        Set = @('--state', 'enabled', '--value', 'DeferQualityUpdatesPeriodId=30', '--value', 'PauseQualityUpdatesStartId=2026-10-18')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('DeferQualityUpdatesPeriodId = 30', 'PauseQualityUpdatesStartId = 2026-10-18')
                    Reg = @("$P\WindowsUpdate\DeferQualityUpdates=dword:1", "$P\WindowsUpdate\DeferQualityUpdatesPeriodInDays=dword:30", "$P\WindowsUpdate\PauseQualityUpdatesStartTime=sz:2026-10-18") }
        Set2 = @('--state', 'enabled', '--value', 'DeferQualityUpdatesPeriodId=0')
        Expect2 = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('DeferQualityUpdatesPeriodId = 0')
                     Reg = @("$P\WindowsUpdate\DeferQualityUpdatesPeriodInDays=dword:0", "$P\WindowsUpdate\PauseQualityUpdatesStartTime=absent") }
    },
    @{
        Policy = 'WindowsUpdate.admx:AutoUpdateCfg'
        Covers = 'enabledValue 0/disabledValue 1; enums obrigatórios; boolean sem trueValue (padrão 1/0); padrões da apresentação; Desabilitada apaga elementos'
        HighRisk = $true
        Set = @('--state', 'enabled', '--value', 'AutoUpdateMode=2', '--value', 'AutoUpdateAutomaticMaintenanceEnabled=true', '--value', 'AutoUpdateSchTime=Automático')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('AutoUpdateMode = 2', 'AutoUpdateAutomaticMaintenanceEnabled = true', 'AutoUpdateSchTime = 24', 'AutoUpdateSchDay = 0', 'AutoUpdateSchEveryWeek = true')
                    Reg = @("$P\WindowsUpdate\AU\NoAutoUpdate=dword:0", "$P\WindowsUpdate\AU\AUOptions=dword:2", "$P\WindowsUpdate\AU\AutomaticMaintenanceEnabled=dword:1",
                            "$P\WindowsUpdate\AU\ScheduledInstallDay=dword:0", "$P\WindowsUpdate\AU\ScheduledInstallTime=dword:24", "$P\WindowsUpdate\AU\ScheduledInstallEveryWeek=dword:1",
                            "$P\WindowsUpdate\AU\AllowMUUpdateService=dword:0") }
        Set2 = @('--state', 'disabled')
        Expect2 = @{ Scope = 'HKLM'; State = 'Disabled'; Reg = @("$P\WindowsUpdate\AU\NoAutoUpdate=dword:1", "$P\WindowsUpdate\AU\AUOptions=absent", "$P\WindowsUpdate\AU\ScheduledInstallTime=absent") }
    },
    @{
        Policy = 'WindowsUpdate.admx:ActiveHours'
        Covers = 'enum pelo nome exibido ("08:00") e pelo valor'
        HighRisk = $true
        Set = @('--state', 'enabled', '--value', 'ActiveHoursStartTime=08:00', '--value', 'ActiveHoursEndTime=18:00')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('ActiveHoursStartTime = 8', 'ActiveHoursEndTime = 18')
                    Reg = @("$P\WindowsUpdate\SetActiveHours=dword:1", "$P\WindowsUpdate\ActiveHoursStart=dword:8", "$P\WindowsUpdate\ActiveHoursEnd=dword:18") }
        Set2 = @('--state', 'enabled', '--value', 'ActiveHoursStartTime=7', '--value', 'ActiveHoursEndTime=#23')
        Expect2 = @{ Scope = 'HKLM'; State = 'Enabled'; Reg = @("$P\WindowsUpdate\ActiveHoursStart=dword:7", "$P\WindowsUpdate\ActiveHoursEnd=dword:23") }
    },
    @{
        Policy = 'ErrorReporting.admx:PCH_ShowUI'
        Covers = 'enabledList/disabledList em subchave (chave criada e apagada na reversão)'
        Set = @('--state', 'enabled')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Reg = @('HKLM\Software\Policies\Microsoft\PCHealth\ErrorReporting\ShowUI=dword:1', 'HKLM\Software\Policies\Microsoft\PCHealth\ErrorReporting\DW\DWAllowHeadless=dword:0') }
        Set2 = @('--state', 'disabled')
        Expect2 = @{ Scope = 'HKLM'; State = 'Disabled'; Reg = @('HKLM\Software\Policies\Microsoft\PCHealth\ErrorReporting\ShowUI=dword:0', 'HKLM\Software\Policies\Microsoft\PCHealth\ErrorReporting\DW\DWAllowHeadless=dword:1') }
    },
    @{
        Policy = 'Globalization.admx:ImplicitDataCollectionOff_1'
        Covers = 'classe User; só enabledList/disabledList (sem valueName)'
        Set = @('--state', 'enabled')
        Expect = @{ Scope = 'HKCU'; State = 'Enabled'; Reg = @('HKCU\SOFTWARE\Policies\Microsoft\InputPersonalization\RestrictImplicitTextCollection=dword:1', 'HKCU\SOFTWARE\Policies\Microsoft\InputPersonalization\RestrictImplicitInkCollection=dword:1') }
        Set2 = @('--state', 'disabled')
        Expect2 = @{ Scope = 'HKCU'; State = 'Disabled'; Reg = @('HKCU\SOFTWARE\Policies\Microsoft\InputPersonalization\RestrictImplicitTextCollection=dword:0', 'HKCU\SOFTWARE\Policies\Microsoft\InputPersonalization\RestrictImplicitInkCollection=dword:0') }
    },
    @{
        Policy = 'WindowsMediaPlayer.admx:ConfigureHTTPProxySettings'
        Covers = 'enum com valueList (com <delete/>; trocar de item apaga a valueList do anterior); text; decimal min 1; boolean trueValue/falseValue; chaves pai criadas'
        Invalid = @(, @('--state', 'enabled', '--value', 'Proxytype=2', '--value', 'ProxyPort=0'))
        Set = @('--state', 'enabled', '--value', 'Proxytype=Personalizado', '--value', 'ProxyAddress=proxy.local', '--value', 'ProxyPort=8080', '--value', 'BypassProxyLocal=true')
        Expect = @{ Scope = 'HKCU'; State = 'Enabled'; Values = @('Proxytype = 2', 'ProxyAddress = proxy.local', 'ProxyPort = 8080', 'BypassProxyLocal = true')
                    Reg = @('HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\ProxyPolicy=dword:1', 'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\ProxyType=dword:2',
                            'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\UseProxy=dword:1', 'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\AutodetectProxy=absent',
                            'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\ProxyAddress=sz:proxy.local', 'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\ProxyPort=dword:8080',
                            'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\BypassProxyLocal=dword:1') }
        Set2 = @('--state', 'enabled', '--value', 'Proxytype=3')
        Expect2 = @{ Scope = 'HKCU'; State = 'Enabled'; Values = @('Proxytype = 3', 'ProxyPort = 80')
                     Reg = @('HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\ProxyType=dword:3', 'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\AutodetectProxy=dword:1',
                             'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\UseProxy=absent', 'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\Protocols\HTTP\ProxyAddress=absent') }
    },
    @{
        Policy = 'WindowsMediaPlayer.admx:NetworkBuffering'
        Covers = 'enum com valueList sem <delete/> (troca de item reescreve o mesmo valor); decimal max 60'
        Invalid = @(, @('--state', 'enabled', '--value', 'BufferingTime=2', '--value', 'NetworkBuffering=61'))
        Set = @('--state', 'enabled', '--value', 'BufferingTime=2', '--value', 'NetworkBuffering=60')
        Expect = @{ Scope = 'HKCU'; State = 'Enabled'; Values = @('BufferingTime = 2', 'NetworkBuffering = 60')
                    Reg = @('HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\NetworkBufferingPolicy=dword:1', 'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\BufferingType=dword:2',
                            'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\UseDefaultBuffering=dword:0', 'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\NetworkBuffering=dword:60') }
        Set2 = @('--state', 'enabled', '--value', 'BufferingTime=Padrão')
        Expect2 = @{ Scope = 'HKCU'; State = 'Enabled'; Values = @('BufferingTime = 1')
                     Reg = @('HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\UseDefaultBuffering=dword:1', 'HKCU\Software\Policies\Microsoft\WindowsMediaPlayer\NetworkBuffering=dword:3') }
    },
    @{
        Policy = 'EventViewer.admx:EventViewer_RedirectionProgram'
        Covers = 'text expandable (REG_EXPAND_SZ sem expandir); padrão da apresentação; política só com elemento'
        Set = @('--state', 'enabled', '--value', 'EventViewer_RedirectionProgram=%SystemRoot%\System32\notepad.exe')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('EventViewer_RedirectionProgram = %SystemRoot%\System32\notepad.exe')
                    Reg = @('HKLM\Software\Policies\Microsoft\EventViewer\MicrosoftRedirectionProgram=expand_sz:%SystemRoot%\System32\notepad.exe') }
        Set2 = @('--state', 'enabled')
        Expect2 = @{ Scope = 'HKLM'; State = 'Enabled'; Reg = @('HKLM\Software\Policies\Microsoft\EventViewer\MicrosoftRedirectionProgram=expand_sz:%SystemRoot%\PCHealth\HelpCtr\Binaries\HelpCtr.exe') }
    },
    @{
        Policy = 'AppPrivacy.admx:LetAppsAccessCamera'
        Covers = 'multiText (REG_MULTI_SZ, várias linhas); enum; multiText vazio não grava'
        Set = @('--state', 'enabled', '--value', 'LetAppsAccessCamera_Enum=2', '--value', 'LetAppsAccessCamera_ForceAllowTheseApps_List=Microsoft.WindowsCamera_8wekyb3d8bbwe', '--value', 'LetAppsAccessCamera_ForceAllowTheseApps_List=Microsoft.SkypeApp_kzf8qxf38zg5c')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('LetAppsAccessCamera_Enum = 2', 'LetAppsAccessCamera_ForceAllowTheseApps_List = Microsoft.WindowsCamera_8wekyb3d8bbwe | Microsoft.SkypeApp_kzf8qxf38zg5c')
                    Reg = @("$P\AppPrivacy\LetAppsAccessCamera=dword:2", "$P\AppPrivacy\LetAppsAccessCamera_ForceAllowTheseApps=multi_sz:Microsoft.WindowsCamera_8wekyb3d8bbwe|Microsoft.SkypeApp_kzf8qxf38zg5c",
                            "$P\AppPrivacy\LetAppsAccessCamera_ForceDenyTheseApps=absent") }
        Set2 = @('--state', 'enabled', '--value', 'LetAppsAccessCamera_Enum=Forçar Negação')
        Expect2 = @{ Scope = 'HKLM'; State = 'Enabled'; Reg = @("$P\AppPrivacy\LetAppsAccessCamera=dword:2", "$P\AppPrivacy\LetAppsAccessCamera_ForceAllowTheseApps=absent") }
    },
    @{
        Policy = 'ControlPanel.admx:DisallowCpls'
        Covers = 'classe User; list com valuePrefix vazio (1, 2...) em subchave; lista não aditiva encolhe; chave da lista apagada em Não configurada'
        Set = @('--state', 'enabled', '--value', 'DisallowCplsList=Microsoft.Mouse', '--value', 'DisallowCplsList=Microsoft.Sound')
        Expect = @{ Scope = 'HKCU'; State = 'Enabled'; Values = @('DisallowCplsList = Microsoft.Mouse | Microsoft.Sound')
                    Reg = @('HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowCpl=dword:1',
                            'HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowCpl\1=sz:Microsoft.Mouse',
                            'HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowCpl\2=sz:Microsoft.Sound') }
        Set2 = @('--state', 'enabled', '--value', 'DisallowCplsList=Microsoft.Sound')
        Expect2 = @{ Scope = 'HKCU'; State = 'Enabled'; Values = @('DisallowCplsList = Microsoft.Sound')
                     Reg = @('HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowCpl\1=sz:Microsoft.Sound',
                             'HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowCpl\2=absent') }
    },
    @{
        Policy = 'ErrorReporting.admx:WerConsentCustomize_2'
        Covers = 'list com explicitValue + additive na própria chave da política, que também guarda DefaultConsent (de WerDefaultConsent_2)'
        Preseed = @("$P\Windows Error Reporting\Consent\DefaultConsent=dword:1")
        Invalid = @(, @('--state', 'enabled', '--value', 'WerConsentCustomize=semigual'))
        Set = @('--state', 'enabled', '--value', 'WerConsentCustomize=notepad.exe=4')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('WerConsentCustomize = notepad.exe=4')
                    Reg = @("$P\Windows Error Reporting\Consent\notepad.exe=sz:4") }
        NcReg = @("$P\Windows Error Reporting\Consent\DefaultConsent=dword:1")
        Keep = @("$P\Windows Error Reporting\Consent\DefaultConsent=dword:1")
    },
    @{
        Policy = 'ErrorReporting.admx:WerExlusion_2'
        Covers = 'list aditiva sem prefixo (nome = dado) mantém itens existentes'
        Preseed = @("$P\Windows Error Reporting\ExcludedApplications\existente.exe=sz:existente.exe")
        Set = @('--state', 'enabled', '--value', 'WerExlusionList=novo.exe')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'
                    Reg = @("$P\Windows Error Reporting\ExcludedApplications\novo.exe=sz:novo.exe", "$P\Windows Error Reporting\ExcludedApplications\existente.exe=sz:existente.exe") }
        # Depois de Não configurada (que apaga todos os itens da lista), o mesmo set só tem o item novo.
        Expect2 = @{ Scope = 'HKLM'; State = 'Enabled'
                     Reg = @("$P\Windows Error Reporting\ExcludedApplications\novo.exe=sz:novo.exe", "$P\Windows Error Reporting\ExcludedApplications\existente.exe=absent") }
    },
    @{
        Policy = 'StartMenu.admx:StartPinAppsWhenInstalled'
        Covers = 'classe Both com --scope user; list aditiva com valuePrefix vazio (numeração continua depois do maior índice)'
        Hives = @('HKCU')
        Preseed = @("$U\Explorer\StartPinAppsWhenInstalled\1=sz:Existente!App")
        NcArgs = @('--scope', 'user')
        Set = @('--state', 'enabled', '--scope', 'user', '--value', 'StartPinAppsWhenInstalled_Name=Microsoft.WindowsCalculator_8wekyb3d8bbwe!App')
        Expect = @{ Scope = 'HKCU'; State = 'Enabled'
                    Reg = @("$U\Explorer\StartPinAppsWhenInstalled=dword:1", "$U\Explorer\StartPinAppsWhenInstalled\1=sz:Existente!App", "$U\Explorer\StartPinAppsWhenInstalled\2=sz:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") }
        Expect2 = @{ Scope = 'HKCU'; State = 'Enabled'
                     Reg = @("$U\Explorer\StartPinAppsWhenInstalled\1=sz:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "$U\Explorer\StartPinAppsWhenInstalled\2=absent") }
    },
    @{
        Policy = 'AppxPackageManager.admx:RemoveDefaultMicrosoftStorePackages'
        Covers = 'elementos com chave própria (subchaves por pacote); boolean trueValue/falseValue; padrão da apresentação (BingNews = true)'
        Set = @('--state', 'enabled', '--value', 'Clipchamp=true')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('Clipchamp = true', 'BingNews = true', 'Photos = false')
                    Reg = @("$P\Appx\RemoveDefaultMicrosoftStorePackages\Enabled=dword:1",
                            "$P\Appx\RemoveDefaultMicrosoftStorePackages\Clipchamp.Clipchamp_yxz26nhyzhsrt\RemovePackage=dword:1",
                            "$P\Appx\RemoveDefaultMicrosoftStorePackages\Microsoft.BingNews_8wekyb3d8bbwe\RemovePackage=dword:1",
                            "$P\Appx\RemoveDefaultMicrosoftStorePackages\Microsoft.Windows.Photos_8wekyb3d8bbwe\RemovePackage=dword:0") }
        Set2 = @('--state', 'disabled')
        Expect2 = @{ Scope = 'HKLM'; State = 'Disabled'; Reg = @("$P\Appx\RemoveDefaultMicrosoftStorePackages\Enabled=dword:0", "$P\Appx\RemoveDefaultMicrosoftStorePackages\Clipchamp.Clipchamp_yxz26nhyzhsrt\RemovePackage=absent") }
    },
    @{
        Policy = 'ControlPanelDisplay.admx:CPL_Personalization_ScreenSaverTimeOut'
        Covers = 'classe User; decimal com storeAsText (REG_SZ); máximo 599940'
        Invalid = @(, @('--state', 'enabled', '--value', 'ScreenSaverTimeOutFreqSpin=599941'))
        Set = @('--state', 'enabled', '--value', 'ScreenSaverTimeOutFreqSpin=600')
        Expect = @{ Scope = 'HKCU'; State = 'Enabled'; Values = @('ScreenSaverTimeOutFreqSpin = 600'); Reg = @("$U\Control Panel\Desktop\ScreenSaveTimeOut=sz:600") }
        Set2 = @('--state', 'enabled', '--value', 'ScreenSaverTimeOutFreqSpin=599940')
        Expect2 = @{ Scope = 'HKCU'; State = 'Enabled'; Reg = @("$U\Control Panel\Desktop\ScreenSaveTimeOut=sz:599940") }
    },
    @{
        Policy = 'WindowsCopilot.admx:DisableAIDataAnalysis'
        Covers = 'classe Both: --scope user e --scope machine no mesmo backup; revert restaura os dois'
        Hives = @('HKLM', 'HKCU')
        NcArgs = @('--scope', 'user')
        Set = @('--state', 'enabled', '--scope', 'user')
        Expect = @{ Scope = 'HKCU'; State = 'Enabled'; Reg = @("$U\WindowsAI\DisableAIDataAnalysis=dword:1", "$P\WindowsAI\DisableAIDataAnalysis=absent") }
        Extra = @(
            @{ Args = @('--state', 'disabled', '--scope', 'machine')
               Expect = @{ Scope = 'HKLM'; State = 'Disabled'; Reg = @("$P\WindowsAI\DisableAIDataAnalysis=dword:0", "$U\WindowsAI\DisableAIDataAnalysis=dword:1") } }
        )
    },
    @{
        Policy = 'Search.admx:AllowCloudSearch'
        Covers = 'valueName da política igual ao do elemento enum; Desabilitada sem disabledValue (apaga = Não configurada)'
        Set = @('--state', 'enabled', '--value', 'AllowCloudSearch_Dropdown=0')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('AllowCloudSearch_Dropdown = 0'); Reg = @("$P\Windows Search\AllowCloudSearch=dword:0") }
        Set2 = @('--state', 'disabled')
        Expect2 = @{ Scope = 'HKLM'; State = 'NotConfigured'; Reg = @("$P\Windows Search\AllowCloudSearch=absent") }
    },
    @{
        Policy = 'Search.admx:ConfigureSearchOnTaskbarMode'
        Covers = 'enum só com elemento (debloat: ocultar a pesquisa da barra de tarefas), nome exibido com acento'
        Set = @('--state', 'enabled', '--value', 'ConfigureSearchOnTaskbarMode_Dropdown=Ocultar')
        Expect = @{ Scope = 'HKLM'; State = 'Enabled'; Values = @('ConfigureSearchOnTaskbarMode_Dropdown = 0'); Reg = @("$P\Windows Search\SearchOnTaskbarMode=dword:0") }
        Set2 = @('--state', 'enabled', '--value', 'ConfigureSearchOnTaskbarMode_Dropdown=Somente ícone de pesquisa')
        Expect2 = @{ Scope = 'HKLM'; State = 'Enabled'; Reg = @("$P\Windows Search\SearchOnTaskbarMode=dword:1") }
    }
)

# Conferência cruzada com o Windows: políticas com Policy CSP; a ponte WMI do MDM e o PolicyManager
# são lidos antes e depois. Expected = valor esperado na CSP se o Windows refletir a política de GP.
$Cross = @{
    Sets = @(
        @{ Policy = 'DataCollection.admx:AllowTelemetry'; Args = @('--state', 'enabled', '--scope', 'machine', '--value', 'AllowTelemetry=0'); Reg = "$P\DataCollection\AllowTelemetry=dword:0" },
        @{ Policy = 'Search.admx:AllowCortana'; Args = @('--state', 'disabled'); Reg = "$P\Windows Search\AllowCortana=dword:0" },
        @{ Policy = 'Search.admx:DoNotUseWebResults'; Args = @('--state', 'enabled'); Reg = "$P\Windows Search\ConnectedSearchUseWeb=dword:0" },
        @{ Policy = 'Search.admx:AllowCloudSearch'; Args = @('--state', 'enabled', '--value', 'AllowCloudSearch_Dropdown=0'); Reg = "$P\Windows Search\AllowCloudSearch=dword:0" },
        @{ Policy = 'CloudContent.admx:DisableWindowsConsumerFeatures'; Args = @('--state', 'enabled'); Reg = "$P\CloudContent\DisableWindowsConsumerFeatures=dword:1" },
        @{ Policy = 'WindowsUpdate.admx:AutoUpdateCfg'; Args = @('--state', 'enabled', '--value', 'AutoUpdateMode=2', '--max-risk', 'high'); Reg = "$P\WindowsUpdate\AU\AUOptions=dword:2" },
        @{ Policy = 'WindowsCopilot.admx:TurnOffWindowsCopilot'; Args = @('--state', 'enabled'); Reg = "$U\WindowsCopilot\TurnOffWindowsCopilot=dword:1" }
    )
    Mdm = @(
        @{ Class = 'MDM_Policy_Result01_System02'; Property = 'AllowTelemetry'; Expected = '0' },
        @{ Class = 'MDM_Policy_Result01_Experience02'; Property = 'AllowCortana'; Expected = '0' },
        @{ Class = 'MDM_Policy_Result01_Experience02'; Property = 'AllowWindowsConsumerFeatures'; Expected = '0' },
        @{ Class = 'MDM_Policy_Result01_Search02'; Property = 'DoNotUseWebResults'; Expected = '0' },
        @{ Class = 'MDM_Policy_Result01_Search02'; Property = 'AllowCloudSearch'; Expected = '0' },
        @{ Class = 'MDM_Policy_Result01_Update02'; Property = 'AllowAutoUpdate'; Expected = '0' }
    )
    PolicyManager = @(
        'HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\System\AllowTelemetry',
        'HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Experience\AllowCortana',
        'HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Experience\AllowWindowsConsumerFeatures',
        'HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Search\DoNotUseWebResults',
        'HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Search\AllowCloudSearch',
        'HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Update\AllowAutoUpdate'
    )
}

if ($Only.Count -gt 0) {
    $Matrix = @($Matrix | Where-Object { $c = $_; @($Only | Where-Object { $c.Policy -like "*$_*" -or ($c.Label -and $c.Label -like "*$_*") }).Count -gt 0 })
    if ($Matrix.Count -eq 0) { throw "Nenhum caso corresponde a -Only $($Only -join ', ')." }
}

# ======================================================================================
# Código que roda DENTRO da VM (sessão PowerShell Direct elevada).
# ======================================================================================
$guestScript = {
    param([string]$GuestDir, [string]$CasesJson, [string]$CrossJson, [bool]$RunCross)
    $ErrorActionPreference = 'Continue'
    try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
    $exe = Join-Path $GuestDir 'app\Debloater.Cli.exe'
    $out = Join-Path $GuestDir 'out'
    $null = New-Item -ItemType Directory -Path $out -Force
    $backupDir = Join-Path $env:ProgramData 'Debloater\backups\policies'
    $cases = ConvertFrom-Json $CasesJson
    $cases = @($cases)
    $cross = $CrossJson | ConvertFrom-Json

    function Invoke-Cli([string[]]$CliArgs) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $lines = @(& $exe @CliArgs 2>&1 | ForEach-Object { "$_" })
        [pscustomobject]@{ Command = 'Debloater.Cli ' + (($CliArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '); Exit = $LASTEXITCODE; Output = ($lines -join "`n"); Ms = $sw.ElapsedMilliseconds }
    }

    function Split-RegPath([string]$Path) {
        $hive = $Path.Substring(0, 4).ToUpperInvariant()
        $rest = $Path.Substring(5)
        $i = $rest.LastIndexOf('\')
        [pscustomobject]@{ Hive = $hive; Key = $rest.Substring(0, $i); Name = $rest.Substring($i + 1) }
    }
    function Open-Base([string]$Hive) {
        $h = if ($Hive -eq 'HKLM') { [Microsoft.Win32.RegistryHive]::LocalMachine } else { [Microsoft.Win32.RegistryHive]::CurrentUser }
        [Microsoft.Win32.RegistryKey]::OpenBaseKey($h, [Microsoft.Win32.RegistryView]::Registry64)
    }
    function Test-RegKey([string]$Hive, [string]$Key) {
        $b = Open-Base $Hive
        try { $k = $b.OpenSubKey($Key); if ($k) { $k.Dispose(); $true } else { $false } } finally { $b.Dispose() }
    }
    function Get-RegText([string]$Path) {
        $p = Split-RegPath $Path
        $b = Open-Base $p.Hive
        try {
            $k = $b.OpenSubKey($p.Key)
            if (-not $k) { return 'absent' }
            try {
                $v = $k.GetValue($p.Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ($null -eq $v) { return 'absent' }
                switch ($k.GetValueKind($p.Name)) {
                    'DWord' { return 'dword:' + [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$v), 0) }
                    'QWord' { return 'qword:' + [BitConverter]::ToUInt64([BitConverter]::GetBytes([long]$v), 0) }
                    'String' { return "sz:$v" }
                    'ExpandString' { return "expand_sz:$v" }
                    'MultiString' { return 'multi_sz:' + (@($v) -join '|') }
                    'Binary' { return 'binary:' + [BitConverter]::ToString([byte[]]$v) }
                    default { return "$($k.GetValueKind($p.Name)):$v" }
                }
            }
            finally { $k.Dispose() }
        }
        finally { $b.Dispose() }
    }
    function Set-RegText([string]$Assignment) {
        $eq = $Assignment.IndexOf('=')
        $p = Split-RegPath $Assignment.Substring(0, $eq)
        $spec = $Assignment.Substring($eq + 1)
        $c = $spec.IndexOf(':')
        $kind = $spec.Substring(0, $c); $data = $spec.Substring($c + 1)
        $b = Open-Base $p.Hive
        try {
            $k = $b.CreateSubKey($p.Key, $true)
            try {
                switch ($kind) {
                    'dword' { $k.SetValue($p.Name, [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]$data), 0), 'DWord') }
                    'qword' { $k.SetValue($p.Name, [long]$data, 'QWord') }
                    'sz' { $k.SetValue($p.Name, $data, 'String') }
                    'expand_sz' { $k.SetValue($p.Name, $data, 'ExpandString') }
                    'multi_sz' { $k.SetValue($p.Name, [string[]]($data -split '\|'), 'MultiString') }
                    default { throw "tipo desconhecido no preseed: $kind" }
                }
            }
            finally { $k.Dispose() }
        }
        finally { $b.Dispose() }
    }
    function Test-RegAssertion([string]$Assertion) {
        $eq = $Assertion.IndexOf('=')
        $path = $Assertion.Substring(0, $eq); $want = $Assertion.Substring($eq + 1)
        $got = Get-RegText $path
        [pscustomobject]@{ Ok = ($got -ceq $want); Text = "$path = $got" + $(if ($got -cne $want) { " (esperado $want)" } else { '' }) }
    }

    # Exporta (reg export) a chave existente mais próxima de cada chave tocada; o hash do conjunto
    # compara estados byte a byte.
    function Get-ExportRoots([string[]]$Keys, [string[]]$Hives) {
        $roots = New-Object System.Collections.Generic.List[string]
        foreach ($h in $Hives) {
            foreach ($key in $Keys) {
                $k = $key.Trim('\')
                while ($k.Contains('\') -and -not (Test-RegKey $h $k)) { $k = $k.Substring(0, $k.LastIndexOf('\')) }
                $roots.Add("$h\$k")
            }
        }
        $sorted = @($roots | Sort-Object { $_.Length } -Unique)
        $kept = New-Object System.Collections.Generic.List[string]
        foreach ($r in $sorted) {
            if (@($kept | Where-Object { $r -ieq $_ -or $r.StartsWith("$_\", [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) { $kept.Add($r) }
        }
        return , @($kept)
    }
    function Save-Snapshot([string[]]$Roots, [string]$File) {
        $ms = New-Object System.IO.MemoryStream
        foreach ($r in $Roots) {
            $tmp = [System.IO.Path]::GetTempFileName()
            $null = & reg.exe export $r $tmp /y 2>&1
            $bytes = if ($LASTEXITCODE -eq 0) { [System.IO.File]::ReadAllBytes($tmp) } else { [System.Text.Encoding]::Unicode.GetBytes("[$r] (ausente)`r`n") }
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            $ms.Write($bytes, 0, $bytes.Length)
        }
        [System.IO.File]::WriteAllBytes($File, $ms.ToArray())
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($ms.ToArray())).Replace('-', '') } finally { $sha.Dispose() }
    }
    function Get-FileSha([string]$Path) {
        if (-not (Test-Path -LiteralPath $Path)) { return $null }
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
    function Get-BackupPath([string]$Policy) {
        $n = $Policy.Replace(':', '__')
        foreach ($c in [System.IO.Path]::GetInvalidFileNameChars()) { $n = $n.Replace([string]$c, '_') }
        Join-Path $backupDir "$n.json"
    }
    function ConvertTo-State([string]$Text) {
        if ($Text -match '^Habilitada') { 'Enabled' }
        elseif ($Text -match '^Desabilitada') { 'Disabled' }
        elseif ($Text -match '^N.o configurada') { 'NotConfigured' }
        else { 'Unknown' }
    }
    function Read-Show([string]$Output) {
        $res = @{}
        $cur = $null
        foreach ($l in ($Output -split "`n")) {
            if ($l -match '^\s+Atual (HKLM|HKCU)\s*:\s*(.+?)\s*$') {
                $cur = @{ State = (ConvertTo-State $Matches[2]); Values = New-Object System.Collections.Generic.List[string] }
                $res[$Matches[1]] = $cur
            }
            elseif ($cur -and $l -match '^\s{6}(\S.*?) = (.*?)\s*$') { $cur.Values.Add("$($Matches[1]) = $($Matches[2])") }
            else { $cur = $null }
        }
        $res
    }

    # Chaves que uma política toca, a partir do export do catálogo ADMX.
    & $exe admx export (Join-Path $out 'admx.json') | Out-Null
    Add-Type -AssemblyName System.Web.Extensions
    $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $ser.MaxJsonLength = [int]::MaxValue
    $catalog = $ser.DeserializeObject([System.IO.File]::ReadAllText((Join-Path $out 'admx.json')))
    $byName = @{}
    foreach ($p in $catalog['policies']) { $byName["$($p['file']):$($p['name'])".ToLowerInvariant()] = $p }
    function Get-PolicyKeys([string]$Policy) {
        $p = $byName[$Policy.ToLowerInvariant()]
        if (-not $p) { return @() }
        $keys = New-Object System.Collections.Generic.List[string]
        $keys.Add($p['key'])
        foreach ($l in @('enabledList', 'disabledList')) { if ($p[$l]) { foreach ($i in $p[$l]) { $keys.Add($i['key']) } } }
        foreach ($e in @($p['elements'])) {
            if (-not $e) { continue }
            $keys.Add($e['key'])
            foreach ($l in @('trueList', 'falseList')) { if ($e[$l]) { foreach ($i in $e[$l]) { $keys.Add($i['key']) } } }
            if ($e['items']) { foreach ($it in $e['items']) { if ($it['valueList']) { foreach ($i in $it['valueList']) { $keys.Add($i['key']) } } } }
        }
        return @($keys | Sort-Object -Unique)
    }
    function Get-ClassHives([string]$Policy) {
        $p = $byName[$Policy.ToLowerInvariant()]
        switch ("$($p['class'])") { 'user' { @('HKCU') } 'machine' { @('HKLM') } default { @('HKLM', 'HKCU') } }
    }

    $gpPol = @{}
    foreach ($f in @('C:\Windows\System32\GroupPolicy\Machine\Registry.pol', 'C:\Windows\System32\GroupPolicy\User\Registry.pol')) { $gpPol[$f] = Get-FileSha $f }

    $results = New-Object System.Collections.Generic.List[object]
    $caseNo = 0
    foreach ($c in $cases) {
        $caseNo++
        $label = if ($c.Label) { $c.Label } else { $c.Policy }
        $dir = Join-Path $out ('caso-{0:D2}' -f $caseNo)
        $null = New-Item -ItemType Directory -Path $dir -Force
        $steps = New-Object System.Collections.Generic.List[object]
        $failures = New-Object System.Collections.Generic.List[string]
        $ctx = @{ Step = 0 }
        $riskArgs = if ($c.HighRisk) { @('--max-risk', 'high') } else { @() }
        $ncArgs = @('--state', 'notconfigured') + @($c.NcArgs | Where-Object { $_ }) + $riskArgs
        $hives = if ($c.Hives) { @($c.Hives) } else { Get-ClassHives $c.Policy }
        $bpath = Get-BackupPath $c.Policy
        Write-Output "[$caseNo/$($cases.Count)] $label"

        function Add-Step([string]$Title, $Run, [string[]]$Checks, [bool]$Ok) {
            $ctx.Step++
            $snapFile = Join-Path $dir ('{0:D2}-registro.reg' -f $ctx.Step)
            $hash = Save-Snapshot $roots $snapFile
            if ($Run) { [System.IO.File]::WriteAllText((Join-Path $dir ('{0:D2}-saida.txt' -f $ctx.Step)), "> $($Run.Command)`r`n(exit $($Run.Exit))`r`n$($Run.Output)", (New-Object System.Text.UTF8Encoding($true))) }
            $steps.Add([pscustomobject]@{ N = $ctx.Step; Title = $Title; Command = $(if ($Run) { $Run.Command } else { $null }); Exit = $(if ($Run) { $Run.Exit } else { $null })
                    Ms = $(if ($Run) { $Run.Ms } else { $null }); Ok = $Ok; Checks = @($Checks); Snapshot = $hash; Output = $(if ($Run) { $Run.Output } else { $null }) })
            if (-not $Ok) { foreach ($ch in $Checks) { if ($ch -like 'FALHA*') { $failures.Add("$Title`: $ch") } } }
            return $hash
        }
        function Check([System.Collections.Generic.List[string]]$List, [bool]$Cond, [string]$Text) {
            $List.Add($(if ($Cond) { "ok: $Text" } else { "FALHA: $Text" }))
            return $Cond
        }
        function Test-Expect($Expect, $Show, [System.Collections.Generic.List[string]]$List) {
            $ok = $true
            $sc = $Show[$Expect.Scope]
            if (-not $sc) { return (Check $List $false "show não listou o escopo $($Expect.Scope)") }
            $ok = (Check $List ($sc.State -eq $Expect.State) "estado lido em $($Expect.Scope): $($sc.State) (esperado $($Expect.State))") -and $ok
            foreach ($v in @($Expect.Values | Where-Object { $_ })) { $ok = (Check $List ($sc.Values -contains $v) "show lista '$v' (lido: $($sc.Values -join '; '))") -and $ok }
            foreach ($r in @($Expect.Reg | Where-Object { $_ })) { $t = Test-RegAssertion $r; $ok = (Check $List $t.Ok "registro: $($t.Text)") -and $ok }
            return $ok
        }
        function Invoke-SetStep([string]$Title, [string[]]$SetArgs, $Expect, [string]$ExpectBackup = 'exists') {
            $chk = New-Object System.Collections.Generic.List[string]
            $r = Invoke-Cli (@('policy', 'set', $c.Policy) + $SetArgs + $riskArgs)
            $ok = Check $chk ($r.Exit -eq 0) "policy set saiu com $($r.Exit)"
            $show = Invoke-Cli @('policy', 'show', $c.Policy)
            $chk.Add("show: " + (($show.Output -split "`n" | Where-Object { $_ -match '^\s+Atual|^\s{6}\S' }) -join ' / '))
            $ok = (Test-Expect $Expect (Read-Show $show.Output) $chk) -and $ok
            if ($ExpectBackup -eq 'exists') { $ok = (Check $chk (Test-Path -LiteralPath $bpath) 'backup gravado') -and $ok }
            $h = Add-Step $Title $r @($chk) $ok
            return [pscustomobject]@{ Hash = $h; Ok = $ok; Run = $r }
        }

        try {
            foreach ($s in @($c.Preseed | Where-Object { $_ })) { Set-RegText $s }
            $keys = Get-PolicyKeys $c.Policy
            if ($keys.Count -eq 0) { throw "política $($c.Policy) não existe no ADMX desta VM." }
            $roots = Get-ExportRoots $keys $hives
            if (Test-Path -LiteralPath $bpath) { throw "já existe backup de $($c.Policy) antes do teste." }

            # inicial
            $show0 = Invoke-Cli @('policy', 'show', $c.Policy)
            $initial = Read-Show $show0.Output
            $chk = New-Object System.Collections.Generic.List[string]
            $null = Check $chk ($show0.Exit -eq 0) "policy show saiu com $($show0.Exit)"
            foreach ($k in $initial.Keys) { $chk.Add("inicial $k`: $($initial[$k].State) $($initial[$k].Values -join '; ')") }
            $chk.Add('raízes exportadas: ' + ($roots -join '; '))
            $hash0 = Add-Step 'show (antes)' $show0 @($chk) ($show0.Exit -eq 0)

            # inválidas
            foreach ($inv in @($c.Invalid)) {
                if ($null -eq $inv) { continue }
                $chk = New-Object System.Collections.Generic.List[string]
                $r = Invoke-Cli (@('policy', 'set', $c.Policy) + @($inv))
                $ok = Check $chk ($r.Exit -ne 0) "configuração inválida recusada (exit $($r.Exit))"
                $msg = ($r.Output -split "`n" | Where-Object { $_ -match '^\s*x |acima do permitido|n.o pode ser gravada' }) -join ' / '
                $chk.Add("mensagem: $msg")
                $ok = (Check $chk (-not (Test-Path -LiteralPath $bpath)) 'nenhum backup criado') -and $ok
                $h = Add-Step "inválida: $($inv -join ' ')" $r @($chk) $ok
                if ($h -ne $hash0) { $failures.Add("inválida: $($inv -join ' '): o registro mudou"); $steps[$steps.Count - 1].Ok = $false; $steps[$steps.Count - 1].Checks += 'FALHA: o registro mudou' }
            }

            # Não configurada a partir do estado inicial
            $initState = if ($initial[$c.Expect.Scope]) { $initial[$c.Expect.Scope].State } else { 'NotConfigured' }
            if ($initState -eq 'NotConfigured' -and -not $c.Preseed) {
                $chk = New-Object System.Collections.Generic.List[string]
                $r = Invoke-Cli (@('policy', 'set', $c.Policy) + $ncArgs)
                $ok = Check $chk ($r.Exit -eq 0 -and $r.Output -match 'nada foi alterado') "Não configurada -> Não configurada não muda nada (exit $($r.Exit))"
                $ok = (Check $chk (-not (Test-Path -LiteralPath $bpath)) 'nenhum backup criado') -and $ok
                $h = Add-Step 'notconfigured (a partir de Não configurada)' $r @($chk) $ok
                if ($h -ne $hash0) { $failures.Add('notconfigured inicial: o registro mudou'); $steps[$steps.Count - 1].Ok = $false }
            }

            # set
            $set1 = Invoke-SetStep "set $($c.Set -join ' ')" @($c.Set) $c.Expect
            $b1 = Get-FileSha $bpath

            # re-aplicar
            $chk = New-Object System.Collections.Generic.List[string]
            $r = Invoke-Cli (@('policy', 'set', $c.Policy) + @($c.Set) + $riskArgs)
            $ok = Check $chk ($r.Exit -eq 0 -and $r.Output -match 'nada foi alterado') "re-aplicar não muda nada (exit $($r.Exit))"
            $ok = (Check $chk ((Get-FileSha $bpath) -eq $b1) 'backup idêntico') -and $ok
            $h = Add-Step 're-aplicar o mesmo set' $r @($chk) $ok
            if ($h -ne $set1.Hash) { $failures.Add('re-aplicar: o registro mudou'); $steps[$steps.Count - 1].Ok = $false }

            # backups
            $chk = New-Object System.Collections.Generic.List[string]
            $r = Invoke-Cli @('policy', 'backups')
            $ok = Check $chk ($r.Exit -eq 0 -and $r.Output -match [regex]::Escape($c.Policy)) 'policy backups lista a política'
            $null = Add-Step 'policy backups' $r @($chk) $ok

            # Não configurada
            $chk = New-Object System.Collections.Generic.List[string]
            $r = Invoke-Cli (@('policy', 'set', $c.Policy) + $ncArgs)
            $ok = Check $chk ($r.Exit -eq 0) "notconfigured saiu com $($r.Exit)"
            $sh = Read-Show (Invoke-Cli @('policy', 'show', $c.Policy)).Output
            $sc = $sh[$c.Expect.Scope]
            $ok = (Check $chk ($sc -and $sc.State -eq 'NotConfigured') "estado lido em $($c.Expect.Scope): $($sc.State)") -and $ok
            $keep = @($c.Keep | Where-Object { $_ } | ForEach-Object { $_.Substring(0, $_.IndexOf('=')) })
            foreach ($a in @($c.Expect.Reg | Where-Object { $_ })) {
                $path = $a.Substring(0, $a.IndexOf('='))
                if ($keep -contains $path) { continue }
                $t = Test-RegAssertion "$path=absent"; $ok = (Check $chk $t.Ok "registro: $($t.Text)") -and $ok
            }
            foreach ($a in @($c.NcReg | Where-Object { $_ })) { $t = Test-RegAssertion $a; $ok = (Check $chk $t.Ok "registro: $($t.Text)") -and $ok }
            $hNc = Add-Step 'notconfigured' $r @($chk) $ok

            $chk = New-Object System.Collections.Generic.List[string]
            $r = Invoke-Cli (@('policy', 'set', $c.Policy) + $ncArgs)
            $ok = Check $chk ($r.Exit -eq 0 -and $r.Output -match 'nada foi alterado') 'notconfigured de novo não muda nada'
            $h = Add-Step 'notconfigured de novo' $r @($chk) $ok
            if ($h -ne $hNc) { $failures.Add('notconfigured de novo: o registro mudou'); $steps[$steps.Count - 1].Ok = $false }

            # set 2 e extras
            $set2 = if ($c.Set2) { @($c.Set2) } else { @($c.Set) }
            $exp2 = if ($c.Expect2) { $c.Expect2 } else { $c.Expect }
            $null = Invoke-SetStep "set $($set2 -join ' ')" $set2 $exp2
            foreach ($x in @($c.Extra | Where-Object { $_ })) { $null = Invoke-SetStep "set $($x.Args -join ' ')" @($x.Args) $x.Expect }
            foreach ($a in @($c.Keep | Where-Object { $_ })) {
                $t = Test-RegAssertion $a
                if (-not $t.Ok) { $failures.Add("valor que não pertence à política foi alterado: $($t.Text)") }
            }

            # reverter
            $chk = New-Object System.Collections.Generic.List[string]
            $r = Invoke-Cli @('policy', 'revert', $c.Policy)
            $ok = Check $chk ($r.Exit -eq 0) "revert saiu com $($r.Exit)"
            $ok = (Check $chk (-not (Test-Path -LiteralPath $bpath)) 'backup apagado') -and $ok
            $sh = Read-Show (Invoke-Cli @('policy', 'show', $c.Policy)).Output
            foreach ($k in $initial.Keys) {
                $same = $sh[$k] -and $sh[$k].State -eq $initial[$k].State -and (($sh[$k].Values -join ';') -eq ($initial[$k].Values -join ';'))
                $ok = (Check $chk $same "show $k igual ao inicial ($($sh[$k].State) $($sh[$k].Values -join '; '))") -and $ok
            }
            $hFinal = Add-Step 'revert' $r @($chk) $ok
            if ($hFinal -ne $hash0) {
                $failures.Add('revert: o registro NÃO ficou idêntico ao inicial (compare 01-registro.reg com o último export)')
                $steps[$steps.Count - 1].Ok = $false
                $steps[$steps.Count - 1].Checks += 'FALHA: export diferente do inicial'
            }
            else { $steps[$steps.Count - 1].Checks += 'ok: export do registro idêntico ao inicial (SHA-256)' }

            $chk = New-Object System.Collections.Generic.List[string]
            $r = Invoke-Cli @('policy', 'revert', $c.Policy)
            $ok = Check $chk ($r.Exit -ne 0 -and $r.Output -match 'Nenhum backup') "segundo revert recusado (exit $($r.Exit))"
            $h = Add-Step 'revert de novo (sem backup)' $r @($chk) $ok
            if ($h -ne $hash0) { $failures.Add('segundo revert mudou o registro'); $steps[$steps.Count - 1].Ok = $false }
        }
        catch {
            $failures.Add("exceção: $($_.Exception.Message)")
        }
        finally {
            # Deixa a VM limpa para o próximo caso, mesmo se algo falhou.
            if (Test-Path -LiteralPath $bpath) { $null = Invoke-Cli @('policy', 'revert', $c.Policy) }
        }
        $verdict = if ($failures.Count -eq 0) { 'Pass' } else { 'Fail' }
        Write-Output ("    {0} ({1} passo(s))" -f $verdict, $steps.Count)
        foreach ($f in $failures) { Write-Output "    - $f" }
        $results.Add([pscustomobject]@{ Case = $caseNo; Policy = $c.Policy; Label = $label; Covers = $c.Covers; Verdict = $verdict; Failures = $failures.ToArray(); Steps = $steps.ToArray() })
    }

    # ---------- conferência cruzada ----------
    $crossResult = $null
    if ($RunCross) {
        Write-Output 'Conferência cruzada com o Windows (MDM/PolicyManager/Windows Update/gpupdate)...'
        $log = New-Object System.Collections.Generic.List[string]
        $dir = Join-Path $out 'cruzada'
        $null = New-Item -ItemType Directory -Path $dir -Force
        $allKeys = @(); $allHives = @('HKLM', 'HKCU')
        foreach ($s in $cross.Sets) { $allKeys += Get-PolicyKeys $s.Policy }
        $roots = Get-ExportRoots @($allKeys | Sort-Object -Unique) $allHives
        $h0 = Save-Snapshot $roots (Join-Path $dir '00-inicial.reg')

        function Get-MdmAsSystem {
            # A ponte WMI do MDM só responde como SYSTEM: roda numa tarefa agendada.
            $classes = @($cross.Mdm | ForEach-Object { $_.Class } | Sort-Object -Unique)
            $outFile = Join-Path $dir ('mdm-{0}.json' -f [guid]::NewGuid().ToString('N'))
            $ps = @"
`$r = @{}
foreach (`$cl in @('$($classes -join "','")')) {
  try { `$i = Get-CimInstance -Namespace 'root\cimv2\mdm\dmmap' -ClassName `$cl -ErrorAction Stop | Select-Object -First 1
        `$h = @{}; if (`$i) { foreach (`$p in `$i.CimInstanceProperties) { if (`$null -ne `$p.Value) { `$h[`$p.Name] = "`$(`$p.Value)" } } }; `$r[`$cl] = `$h }
  catch { `$r[`$cl] = @{ '__erro' = `$_.Exception.Message } }
}
`$r | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath '$outFile' -Encoding UTF8
"@
            $scriptFile = Join-Path $dir 'mdm-query.ps1'
            Set-Content -LiteralPath $scriptFile -Value $ps -Encoding UTF8
            $taskName = 'DebloaterPolicyTestMdm'
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptFile`""
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
            Start-ScheduledTask -TaskName $taskName
            $deadline = (Get-Date).AddMinutes(3)
            while (-not (Test-Path -LiteralPath $outFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
            Start-Sleep -Seconds 1
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
            if (-not (Test-Path -LiteralPath $outFile)) { return $null }
            return (Get-Content -LiteralPath $outFile -Raw -Encoding UTF8 | ConvertFrom-Json)
        }
        function Get-MdmValue($Snapshot, [string]$Class, [string]$Prop) {
            if (-not $Snapshot) { return '(sem resposta)' }
            $o = $Snapshot.$Class
            if (-not $o) { return '(classe sem instância)' }
            if ($o.__erro) { return "(erro: $($o.__erro))" }
            $v = $o.$Prop
            if ($null -eq $v) { return '(ausente)' } else { return "$v" }
        }
        function Get-WuSettings {
            try { $s = (New-Object -ComObject Microsoft.Update.AutoUpdate).Settings; "NotificationLevel=$($s.NotificationLevel) ReadOnly=$($s.ReadOnly)" } catch { "erro: $($_.Exception.Message)" }
        }

        $mdm0 = Get-MdmAsSystem
        $pm0 = @{}; foreach ($pp in $cross.PolicyManager) { $pm0[$pp] = Get-RegText $pp }
        $wu0 = Get-WuSettings

        $applied = @()
        foreach ($s in $cross.Sets) {
            $r = Invoke-Cli (@('policy', 'set', $s.Policy) + @($s.Args))
            $log.Add("$($r.Command) -> exit $($r.Exit)")
            if ($r.Exit -eq 0) { $applied += $s.Policy }
        }
        Start-Sleep -Seconds 5
        $mdm1 = Get-MdmAsSystem
        $pm1 = @{}; foreach ($pp in $cross.PolicyManager) { $pm1[$pp] = Get-RegText $pp }
        $wu1 = Get-WuSettings

        $gp = (& gpupdate.exe /force /wait:120 2>&1 | ForEach-Object { "$_" }) -join ' '
        $log.Add("gpupdate /force: $gp")
        Start-Sleep -Seconds 5
        $afterGp = @()
        foreach ($s in $cross.Sets) { $t = Test-RegAssertion $s.Reg; $afterGp += [pscustomobject]@{ Policy = $s.Policy; Ok = $t.Ok; Text = $t.Text } }
        $mdm2 = Get-MdmAsSystem
        $wu2 = Get-WuSettings

        $rev = @()
        foreach ($pol in $applied) { $r = Invoke-Cli @('policy', 'revert', $pol); $rev += "$pol -> exit $($r.Exit)" }
        $hEnd = Save-Snapshot $roots (Join-Path $dir '99-final.reg')

        $mdmRows = @()
        foreach ($m in $cross.Mdm) {
            $mdmRows += [pscustomobject]@{ Csp = "$($m.Class).$($m.Property)"; Expected = $m.Expected
                Before = (Get-MdmValue $mdm0 $m.Class $m.Property); After = (Get-MdmValue $mdm1 $m.Class $m.Property); AfterGpupdate = (Get-MdmValue $mdm2 $m.Class $m.Property) }
        }
        $pmRows = @()
        foreach ($pp in $cross.PolicyManager) { $pmRows += [pscustomobject]@{ Path = $pp; Before = $pm0[$pp]; After = $pm1[$pp] } }
        $crossResult = [pscustomobject]@{
            Log = $log.ToArray(); Mdm = $mdmRows; PolicyManager = $pmRows
            WindowsUpdate = [pscustomobject]@{ Before = $wu0; After = $wu1; AfterGpupdate = $wu2 }
            AfterGpupdate = $afterGp; Revert = $rev; RegistryRestored = ($hEnd -eq $h0)
        }
    }

    $gpPolAfter = @{}
    foreach ($f in $gpPol.Keys) { $gpPolAfter[$f] = Get-FileSha $f }
    $polRows = @()
    foreach ($f in $gpPol.Keys) { $polRows += [pscustomobject]@{ File = $f; Before = $gpPol[$f]; After = $gpPolAfter[$f]; Unchanged = ($gpPol[$f] -eq $gpPolAfter[$f]) } }

    $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $report = [pscustomobject]@{
        Windows = ('{0} {1} {2}.{3}' -f $cv.ProductName, $cv.DisplayVersion, $cv.CurrentBuildNumber, $cv.UBR)
        Edition = $cv.EditionID
        AdmxPolicies = @($catalog['policies']).Count
        Cli = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
        Results = $results.ToArray()
        Cross = $crossResult
        RegistryPol = $polRows
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'relatorio.json') -Encoding UTF8
    Write-Output '__DONE__'
}

# ======================================================================================
# Host
# ======================================================================================
$repo = Get-DebloaterRepoRoot
if (-not $CredentialPath) { $CredentialPath = Get-DebloaterDefaultCredentialPath }
if (-not $ReportDir) { $ReportDir = Join-Path $repo 'tests\vm-reports' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$checkpointName = "antes-policytest-$stamp"
$guestDir = "C:\DebloaterPolicyTest\$stamp"
$null = New-Item -ItemType Directory -Path $ReportDir -Force
$localBase = Join-Path $ReportDir "policytest-$Name-$stamp"
$cred = Import-DebloaterVmCredential -Path $CredentialPath
if (-not (Get-VM -Name $Name -ErrorAction SilentlyContinue)) { throw "VM '$Name' não encontrada. Crie-a com New-TestVM.ps1." }

$work = Join-Path ([System.IO.Path]::GetTempPath()) "debloater-policytest-$stamp"
$null = New-Item -ItemType Directory -Path $work -Force
$zip = Join-Path $work 'debloater.zip'
$log = New-Object System.Collections.Generic.List[string]
try {
    if ($PublishDir) { $publish = (Resolve-Path -LiteralPath $PublishDir).ProviderPath }
    else {
        $publish = Join-Path $work 'publish'
        Write-Host 'Publicando a CLI (Release, win-x64, autocontida)...'
        & (Get-DebloaterDotNet) publish (Join-Path $repo 'src\Debloater.Cli\Debloater.Cli.csproj') -c Release -r win-x64 --self-contained true -o $publish --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (código $LASTEXITCODE)." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'Debloater.Cli.exe'))) { throw "Debloater.Cli.exe não encontrado em '$publish'." }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Fastest, $false)

    $vm = Get-VM -Name $Name
    switch ($vm.State) {
        'Running' { }
        'Paused' { Resume-VM -Name $Name }
        default { Write-Host "Ligando a VM '$Name' (estado: $($vm.State))..."; Start-VM -Name $Name }
    }
    $s0 = Wait-DebloaterVmSession -VMName $Name -Credential $cred -TimeoutMinutes $BootTimeoutMinutes
    try { $info = Get-DebloaterGuestInfo -Session $s0 } finally { Remove-PSSession $s0 }
    Write-Host ("VM: Windows build {0} ({1} {2}), usuário {3}, modelo '{4}'." -f $info.Build, $info.Edition, $info.DisplayVersion, $info.User, $info.Model)
    if (-not $info.IsElevated) { throw 'A sessão PowerShell Direct não está elevada (veja tools\vm\README.md, "UAC e PowerShell Direct").' }
    if ($info.Model -ne 'Virtual Machine') { throw "O convidado não parece uma VM Hyper-V (modelo '$($info.Model)'); nada foi feito." }

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

            $casesJson = ConvertTo-Json -InputObject @($Matrix) -Depth 10 -Compress
            $crossJson = ConvertTo-Json -InputObject $Cross -Depth 10 -Compress
            Write-Host "Rodando a matriz ($($Matrix.Count) caso(s)) na VM..."
            # Erros não fatais dentro da VM são registrados (com a posição) em vez de abortar o teste.
            Invoke-Command -Session $session -ScriptBlock $guestScript -ArgumentList $guestDir, $casesJson, $crossJson, (-not $NoCross) -ErrorAction Continue 2>&1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.ErrorRecord]) {
                    $line = "ERRO na VM: $($_.Exception.Message) $($_.InvocationInfo.PositionMessage)"
                    Write-Host $line -ForegroundColor Yellow
                }
                else { $line = "$_"; Write-Host $line }
                $log.Add($line)
            }

            $guestZip = "$guestDir\resultado.zip"
            Invoke-Command -Session $session -ScriptBlock {
                param($d, $z)
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                Remove-Item -LiteralPath "$d\out\admx.json" -Force -ErrorAction SilentlyContinue
                [System.IO.Compression.ZipFile]::CreateFromDirectory("$d\out", $z)
            } -ArgumentList $guestDir, $guestZip
            Copy-Item -FromSession $session -LiteralPath $guestZip -Destination "$localBase.zip"
            Expand-Archive -LiteralPath "$localBase.zip" -DestinationPath $localBase -Force
            Remove-Item -LiteralPath "$localBase.zip" -Force
        }
        finally {
            if ($session) { Remove-PSSession $session -ErrorAction SilentlyContinue }
        }
    }
    finally {
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
}

# ---------- relatório ----------
$jsonPath = Join-Path $localBase 'relatorio.json'
if (-not (Test-Path -LiteralPath $jsonPath)) { Write-Warning "Sem relatório. Saída: $($log -join "`n")"; exit 2 }
$report = Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
Copy-Item -LiteralPath $jsonPath -Destination "$localBase.json" -Force

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Teste de políticas ADMX na VM ($stamp)")
$md.Add('')
$md.Add("- Windows: $($report.Windows) ($($report.Edition)), $($report.AdmxPolicies) políticas no PolicyDefinitions da VM")
$md.Add("- CLI: $($report.Cli)")
$results = @($report.Results)
$md.Add("- Casos: $($results.Count); Pass: $(@($results | Where-Object Verdict -eq 'Pass').Count); Fail: $(@($results | Where-Object Verdict -ne 'Pass').Count)")
$md.Add('')
$md.Add('| # | Política | Cobre | Passos | Veredito |')
$md.Add('|---|---|---|---|---|')
foreach ($r in $results) { $md.Add("| $($r.Case) | ``$($r.Label)`` | $($r.Covers) | $(@($r.Steps).Count) | $($r.Verdict) |") }
foreach ($r in $results | Where-Object Verdict -ne 'Pass') {
    $md.Add(''); $md.Add("## Falhas: $($r.Label)")
    foreach ($f in $r.Failures) { $md.Add("- $f") }
}
if ($report.Cross) {
    $md.Add(''); $md.Add('## Conferência cruzada com o Windows'); $md.Add('')
    $md.Add('| CSP (ponte WMI do MDM, como SYSTEM) | Esperado se refletir GP | Antes | Depois | Depois do gpupdate |')
    $md.Add('|---|---|---|---|---|')
    foreach ($m in $report.Cross.Mdm) { $md.Add("| $($m.Csp) | $($m.Expected) | $($m.Before) | $($m.After) | $($m.AfterGpupdate) |") }
    $md.Add(''); $md.Add('| PolicyManager | Antes | Depois |'); $md.Add('|---|---|---|')
    foreach ($m in $report.Cross.PolicyManager) { $md.Add("| $($m.Path) | $($m.Before) | $($m.After) |") }
    $md.Add('')
    $md.Add("- Windows Update (Microsoft.Update.AutoUpdate.Settings): antes $($report.Cross.WindowsUpdate.Before); depois $($report.Cross.WindowsUpdate.After); depois do gpupdate $($report.Cross.WindowsUpdate.AfterGpupdate)")
    foreach ($g in $report.Cross.AfterGpupdate) { $md.Add("- Depois do gpupdate /force: $(if ($g.Ok) { 'mantido' } else { 'ALTERADO' }) $($g.Text)") }
    $md.Add("- Registro idêntico ao inicial depois de reverter: $($report.Cross.RegistryRestored)")
}
foreach ($p in $report.RegistryPol) { $md.Add("- Registry.pol $($p.File): $(if ($p.Unchanged) { 'não alterado' } else { 'ALTERADO' }) ($(if ($p.Before) { $p.Before.Substring(0, 12) } else { 'inexistente' }))") }
[System.IO.File]::WriteAllLines("$localBase.md", $md, (New-Object System.Text.UTF8Encoding($true)))

Write-Host ''
Write-Host "Relatório: $localBase.md / .json (saídas e exports em $localBase)"
$fails = @($results | Where-Object Verdict -ne 'Pass')
$crossBad = $report.Cross -and (-not $report.Cross.RegistryRestored -or @($report.Cross.AfterGpupdate | Where-Object { -not $_.Ok }).Count -gt 0)
$polBad = @($report.RegistryPol | Where-Object { -not $_.Unchanged }).Count -gt 0
if ($fails.Count -gt 0 -or $crossBad -or $polBad) {
    Write-Host "$($fails.Count) caso(s) com falha$(if ($crossBad) { '; conferência cruzada com problema' })$(if ($polBad) { '; Registry.pol alterado' })." -ForegroundColor Yellow
    exit 1
}
Write-Host "Todos os $($results.Count) casos passaram." -ForegroundColor Green
exit 0
