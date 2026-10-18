using Debloater.Core.Model;
using Debloater.Coverage;

namespace Debloater.Core.Tests;

/// <summary>Extractors and matching of the coverage comparison (tools/coverage).</summary>
public class CoverageTests
{
    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\X", @"HKLM\SOFTWARE\Policies\X")]
    [InlineData(@"HKLM:\SOFTWARE\Policies\X\", @"HKLM\SOFTWARE\Policies\X")]
    [InlineData(@"Registry::HKEY_CURRENT_USER\Software\X", @"HKCU\Software\X")]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Services\Foo", @"HKLM\SYSTEM\CurrentControlSet\Services\Foo")]
    [InlineData(@"HKU\.DEFAULT\Control Panel\Keyboard", @"HKCU\Control Panel\Keyboard")]
    [InlineData(@"HKLM\SOFTWARE\Classes\CLSID\{X}", @"HKCR\CLSID\{X}")]
    public void NormalizeKey_Canonicalizes(string input, string expected) =>
        Assert.Equal(expected, Targets.NormalizeKey(input));

    [Theory]
    [InlineData(@"HKCU:\Software\$env:USERNAME")]
    [InlineData(@"HKLM\SOFTWARE\%VAR%\X")]
    [InlineData(@"SOFTWARE\NoHive")]
    public void NormalizeKey_RejectsVariablesAndMissingHive(string input) =>
        Assert.Null(Targets.NormalizeKey(input));

    [Fact]
    public void ServiceStartValue_BecomesServiceTarget()
    {
        var t = Targets.Registry(@"HKLM\SYSTEM\CurrentControlSet\Services\DiagTrack", "Start");
        Assert.Equal(TargetKind.Service, t!.Kind);
        Assert.Equal("DIAGTRACK", t.Key);
    }

    [Fact]
    public void RegFile_ExtractsValuesAndDeletedKeys()
    {
        const string reg = "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\A]\r\n\"Enabled\"=dword:00000000\r\n@=\"x\"\r\n\r\n[-HKEY_LOCAL_MACHINE\\SOFTWARE\\B]\r\n";
        var targets = Extractors.FromRegFile(reg).ToList();
        Assert.Contains(targets, t => t.Kind == TargetKind.Registry && t.Display == @"HKCU\Software\A\Enabled");
        Assert.Contains(targets, t => t.Kind == TargetKind.Registry && t.Display == @"HKCU\Software\A\(default)");
        Assert.Contains(targets, t => t.Kind == TargetKind.RegistryKey && t.Display == @"HKLM\SOFTWARE\B");
    }

    [Fact]
    public void Script_ExtractsCommonPowerShellAndCmdForms()
    {
        const string script = """
            New-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection" -Name AllowTelemetry -PropertyType DWord -Value 0 -Force
            Set-ItemProperty 'HKCU:\Software\X' 'Y' 1
            reg add "HKLM\SOFTWARE\Z" /v W /t REG_DWORD /d 1 /f
            Get-Service -Name DiagTrack | Set-Service -StartupType Disabled
            sc.exe config WerSvc start= disabled
            Disable-ScheduledTask -TaskPath "\Microsoft\Windows\Autochk\" -TaskName "Proxy"
            schtasks /Change /TN "\Microsoft\Windows\Maps\MapsUpdateTask" /Disable
            Get-AppxPackage -AllUsers Microsoft.BingNews | Remove-AppxPackage
            Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart
            $apps = @('Microsoft.ZuneMusic', "Clipchamp.Clipchamp")
            """;
        var t = Extractors.FromScript(script).Select(x => $"{x.Kind}:{x.Display}").ToList();
        Assert.Contains(@"Registry:HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry", t);
        Assert.Contains(@"Registry:HKCU\Software\X\Y", t);
        Assert.Contains(@"Registry:HKLM\SOFTWARE\Z\W", t);
        Assert.Contains("Service:DiagTrack", t);
        Assert.Contains("Service:WerSvc", t);
        Assert.Contains(@"Task:\Microsoft\Windows\Autochk\Proxy", t);
        Assert.Contains(@"Task:\Microsoft\Windows\Maps\MapsUpdateTask", t);
        Assert.Contains("Appx:Microsoft.BingNews", t);
        Assert.Contains("Feature:SMB1Protocol", t);
        Assert.Contains("Appx:Microsoft.ZuneMusic", t);
        Assert.Contains("Appx:Clipchamp.Clipchamp", t);
    }

    [Fact]
    public void CSharp_PairsRegistryPathWithFollowingValueName()
    {
        const string code = """
            internal const string PATH = @"SOFTWARE\Policies\Foo";
            internal const string NAME = "Bar";
            new RegistryItem(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
            RegHelper.SetValue(RegistryHive.LocalMachine, PATH, NAME, 1, RegistryValueKind.DWord);
            AppxPackageName = ["Microsoft.BingNews"],
            """;
        var consts = Extractors.CsConstants([code]);
        var t = Extractors.FromCSharp(code, consts).Select(x => $"{x.Kind}:{x.Display}").ToList();
        Assert.Contains(@"Registry:HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry", t);
        Assert.Contains(@"Registry:HKLM\SOFTWARE\Policies\Foo\Bar", t);
        Assert.Contains("Appx:Microsoft.BingNews", t);
    }

    [Fact]
    public void Playbook_ReadsEveryActionOfAList()
    {
        const string yml = """
            actions:
              - !registryValue:
                path: 'HKCU\Software\A'
                value: 'One'
                data: '0'
                type: REG_DWORD
              - !registryValue:
                path: 'HKCU\Software\A'
                value: 'Two'
                data: '0'
                type: REG_DWORD
              - !service: {name: 'DiagTrack', operation: change, startup: 4}
            """;
        var t = Parsers.Playbook([("x.yml", yml)]).Select(x => x.Display).ToList();
        Assert.Contains(@"HKCU\Software\A\One", t);
        Assert.Contains(@"HKCU\Software\A\Two", t);
        Assert.Contains("DiagTrack", t);
    }

    [Fact]
    public void CatalogIndex_MatchesPolicyHivesTaskLeafAndAppxPrefix()
    {
        var tweak = new Tweak
        {
            Id = "x.y",
            Actions =
            [
                new ActionSpec { Type = ActionSpec.Registry, Path = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", Name = "AllowClipboardHistory", Kind = "dword", Value = 0 },
                new ActionSpec { Type = ActionSpec.ScheduledTask, Path = @"\Microsoft\Windows\Autochk\Proxy", State = "disabled" },
                new ActionSpec { Type = ActionSpec.Appx, Name = "Microsoft.Xbox*", State = "removed" },
            ],
        };
        var index = new CatalogIndex([tweak]);
        Assert.NotEmpty(index.CoveredBy(Targets.Registry(@"HKCU\Software\Policies\Microsoft\Windows\System", "AllowClipboardHistory")!));
        Assert.NotEmpty(index.CoveredBy(Targets.Task("Proxy")!));
        Assert.NotEmpty(index.CoveredBy(Targets.Appx("Microsoft.XboxApp")!));
        Assert.Empty(index.CoveredBy(Targets.Registry(@"HKLM\SOFTWARE\Other", "AllowClipboardHistory")!));
    }
}
