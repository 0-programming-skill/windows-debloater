using Debloater.Core.Admx;
using Debloater.Core.Policies;
using Microsoft.Win32;

namespace Debloater.Core.Tests;

/// <summary>
/// Regras do esquema ADMX que geram escrita no registro (fixture Writer.admx), contra um registro em memória.
/// </summary>
public class PolicyWriterTests
{
    internal static AdmxCatalog Catalog { get; } =
        AdmxParser.ParseDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Admx"), ["en-US"]);

    internal static AdmxPolicy P(string name) => Assert.Single(Catalog.Policies, p => p.File == "Writer.admx" && p.Name == name);

    private const string All = @"Software\Policies\Test\W\All";
    private const PolicyScope M = PolicyScope.Machine;

    private static PolicySetting S(string name, PolicyTargetState state) => new() { Policy = "Writer.admx:" + name, State = state };

    private static string? V(MemoryPolicyRegistry r, string key, string name, PolicyScope s = M) =>
        r.Read(s, key, name) is { } v ? PolicyValues.Describe(v) : null;

    private static PolicyWritePlan Run(MemoryPolicyRegistry r, AdmxPolicy p, PolicySetting s)
    {
        var plan = PolicyWriter.Plan(p, s, r);
        Assert.True(plan.IsValid, string.Join(" ", plan.Errors));
        r.Execute(plan);
        // Idempotente: depois de executar, o mesmo pedido não tem mais nada a fazer.
        Assert.False(PolicyWriter.Plan(p, s, r).HasEffect, "o plano deveria estar vazio depois de executado");
        return plan;
    }

    private static List<string> Errors(AdmxPolicy p, PolicySetting s) => PolicyWriter.Plan(p, s, new MemoryPolicyRegistry()).Errors;

    // ---------------- parser: apresentação ----------------

    [Fact]
    public void Parser_ReadsPresentationDefaultsOrderAndLimits()
    {
        var p = P("AllElements");
        var byId = p.Elements.ToDictionary(e => e.Id);
        Assert.Equal(("decimalTextBox", "7", "Count:"), (byId["Count"].PresentationType, byId["Count"].DefaultValue, byId["Count"].Label));
        Assert.Equal(("textBox", "srv"), (byId["Server"].PresentationType, byId["Server"].DefaultValue));
        Assert.Equal(("comboBox", "%SystemRoot%"), (byId["Path"].PresentationType, byId["Path"].DefaultValue));
        Assert.Equal(["%TEMP%", @"C:\x"], byId["Path"].Suggestions);
        Assert.Equal("true", byId["Check"].DefaultValue);
        Assert.Null(byId["CheckLists"].DefaultValue);
        Assert.Equal("#1", byId["Choice"].DefaultValue);
        Assert.Equal(("longDecimalTextBox", "10"), (byId["Big"].PresentationType, byId["Big"].DefaultValue));
        Assert.Equal((3, 8), (byId["Lines"].MaxStrings!.Value, byId["Lines"].MaxLength!.Value));
        Assert.Equal(@"Software\Policies\Test\W\All\Net", byId["Server"].Key);
        Assert.Equal("", byId["Numbered"].ValuePrefix);
        Assert.Null(byId["Plain"].ValuePrefix);

        Assert.Equal(new AdmxPresentationControl("text", null, "Configure everything below."), p.Presentation![0]);
        Assert.Equal(15, p.Presentation.Count);
    }

    // ---------------- valueName ----------------

    [Fact]
    public void Implicit_EnabledWritesDword1_DisabledAndNotConfiguredDelete()
    {
        var p = P("Implicit");
        var r = new MemoryPolicyRegistry();
        const string k = @"Software\Policies\Test\W\Implicit";

        // Desabilitar algo que não existe não tem efeito (sem disabledValue o valor é apagado).
        Assert.False(PolicyWriter.Plan(p, S("Implicit", PolicyTargetState.Disabled), r).HasEffect);

        Run(r, p, S("Implicit", PolicyTargetState.Enabled));
        Assert.Equal("1 (REG_DWORD)", V(r, k, "Flag"));
        Assert.Equal(AdmxPolicyState.Enabled, PolicyWriter.ReadCurrent(p, M, r).State);

        var plan = Run(r, p, S("Implicit", PolicyTargetState.Disabled));
        var c = Assert.Single(plan.Effective);
        Assert.True(c.IsDelete);
        Assert.Null(V(r, k, "Flag"));

        Run(r, p, S("Implicit", PolicyTargetState.Enabled));
        Run(r, p, S("Implicit", PolicyTargetState.NotConfigured));
        Assert.Empty(r.Snapshot());
    }

    [Fact]
    public void Mixed_EnabledDisabledListsAndKinds()
    {
        var p = P("Mixed");
        var r = new MemoryPolicyRegistry();
        const string k = @"Software\Policies\Test\W\Mixed";
        var u = PolicyScope.User;

        Run(r, p, S("Mixed", PolicyTargetState.Enabled));
        Assert.Equal("\"on\" (REG_SZ)", V(r, k, "Mode", u));
        Assert.Equal("1 (REG_DWORD)", V(r, k, "A", u));
        Assert.Equal("5000000000 (REG_QWORD)", V(r, k + @"\Sub", "B", u));
        Assert.Empty(r.Snapshot(M));

        Run(r, p, S("Mixed", PolicyTargetState.Disabled));
        Assert.Equal("0 (REG_DWORD)", V(r, k, "Mode", u));
        Assert.Equal("0 (REG_DWORD)", V(r, k, "A", u));
        Assert.Null(V(r, k + @"\Sub", "B", u));
        Assert.Equal(AdmxPolicyState.Disabled, PolicyWriter.ReadCurrent(p, u, r).State);

        Run(r, p, S("Mixed", PolicyTargetState.NotConfigured));
        Assert.Empty(r.Snapshot());
    }

    [Fact]
    public void ClassMismatch_IsAnError_AndBothAcceptsEitherScope()
    {
        var s = S("Mixed", PolicyTargetState.Enabled);
        s.Scope = PolicyScope.Machine;
        Assert.Contains(Errors(P("Mixed"), s), e => e.Contains("classe User"));

        var r = new MemoryPolicyRegistry();
        var both = S("AllElements", PolicyTargetState.Disabled);
        both.Scope = PolicyScope.User;
        Run(r, P("AllElements"), both);
        Assert.Equal("0 (REG_DWORD)", V(r, All, "Enabled", PolicyScope.User));
        Assert.Empty(r.Snapshot(M));
    }

    // ---------------- elementos ----------------

    [Fact]
    public void AllElements_EnabledWithPresentationDefaults()
    {
        var r = new MemoryPolicyRegistry();
        Run(r, P("AllElements"), S("AllElements", PolicyTargetState.Enabled));

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\ENABLED"] = "1 (REG_DWORD)",
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\COUNT"] = "7 (REG_DWORD)",
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\BIG"] = "10 (REG_QWORD)",
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\CHECK"] = "1 (REG_DWORD)",
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\CHOICE"] = "2 (REG_DWORD)",
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\EXTRA\BOOST"] = "\"yes\" (REG_SZ)",
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\NET\SERVER"] = "\"srv\" (REG_SZ)",
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\PATH"] = "\"%SystemRoot%\" (REG_EXPAND_SZ)",
        };
        Assert.Equal(expected, r.Snapshot());
    }

    [Fact]
    public void AllElements_ExplicitValuesForEveryType()
    {
        var r = new MemoryPolicyRegistry();
        var s = S("AllElements", PolicyTargetState.Enabled)
            .Set("Count", "50").Set("AsText", "42").Set("Big", "18446744073709551615")
            .Set("Check", "false").Set("CheckLists", "sim")
            .Set("Choice", "Low").Set("Named", "HIGH")
            .Set("Server", "0123456789").Set("Path", @"%TEMP%\x")
            .Set("Lines", "a", "", "b\nc")
            .Set("Plain", "x", "y", "X")
            .Set("Numbered", "%TEMP%", "two")
            .Set("Prefixed", "p1")
            .Set("Pairs", "n1=v1", "n2 = v=2");
        var plan = Run(r, P("AllElements"), s);
        Assert.Contains(plan.Notes, n => n.Contains("repetidos"));

        Assert.Equal("50 (REG_DWORD)", V(r, All, "Count"));
        Assert.Equal("\"42\" (REG_SZ)", V(r, All, "AsText"));
        Assert.Equal("18446744073709551615 (REG_QWORD)", V(r, All, "Big"));
        Assert.Equal("0 (REG_DWORD)", V(r, All, "Check"));
        Assert.Equal("1 (REG_DWORD)", V(r, All + @"\Bool", "T"));
        Assert.Equal("0 (REG_DWORD)", V(r, All, "Choice"));
        Assert.Null(V(r, All + @"\Extra", "Boost"));
        Assert.Equal("\"high\" (REG_SZ)", V(r, All, "Named"));
        Assert.Equal("\"0123456789\" (REG_SZ)", V(r, All + @"\Net", "Server"));
        Assert.Equal("\"%TEMP%\\x\" (REG_EXPAND_SZ)", V(r, All, "Path"));
        Assert.Equal("[\"a\", \"b\", \"c\"] (REG_MULTI_SZ)", V(r, All, "Lines"));
        Assert.Equal("\"x\" (REG_SZ)", V(r, All + @"\Plain", "x"));
        Assert.Equal("\"y\" (REG_SZ)", V(r, All + @"\Plain", "y"));
        Assert.Equal(2, r.ValueNames(M, All + @"\Plain").Count);
        Assert.Equal("\"%TEMP%\" (REG_EXPAND_SZ)", V(r, All + @"\Numbered", "1"));
        Assert.Equal("\"two\" (REG_EXPAND_SZ)", V(r, All + @"\Numbered", "2"));
        Assert.Equal("\"p1\" (REG_SZ)", V(r, All + @"\Prefixed", "Item1"));
        Assert.Equal("\"v1\" (REG_SZ)", V(r, All + @"\Pairs", "n1"));
        Assert.Equal("\"v=2\" (REG_SZ)", V(r, All + @"\Pairs", "n2"));
    }

    [Fact]
    public void Booleans_CheckedAndUncheckedLists()
    {
        var r = new MemoryPolicyRegistry();
        var p = P("AllElements");
        Run(r, p, S("AllElements", PolicyTargetState.Enabled).Set("CheckLists", "true"));
        Assert.Equal("1 (REG_DWORD)", V(r, All + @"\Bool", "T"));
        // Desmarcado: falseList apaga o mesmo valor.
        Run(r, p, S("AllElements", PolicyTargetState.Enabled).Set("CheckLists", "false"));
        Assert.Null(V(r, All + @"\Bool", "T"));
        Assert.Contains(Errors(p, S("AllElements", PolicyTargetState.Enabled).Set("Check", "talvez")), e => e.Contains("booleano"));
    }

    [Theory]
    [InlineData("Choice", "2", "2")]
    [InlineData("Choice", "#0", "0")]
    [InlineData("Choice", "high", "2")]
    [InlineData("Named", "low", "low")]
    [InlineData("Named", "#1", "high")]
    [InlineData("Named", "High", "high")]
    public void Enum_AcceptsTokenIndexValueOrDisplayName(string element, string input, string written)
    {
        var r = new MemoryPolicyRegistry();
        Run(r, P("AllElements"), S("AllElements", PolicyTargetState.Enabled).Set(element, input));
        Assert.Contains($"{written}", V(r, All, element));
    }

    [Fact]
    public void Enum_SwitchingItemsRemovesTheOldValueList()
    {
        var r = new MemoryPolicyRegistry();
        var p = P("AllElements");
        Run(r, p, S("AllElements", PolicyTargetState.Enabled).Set("Choice", "2"));
        Assert.NotNull(V(r, All + @"\Extra", "Boost"));
        var plan = Run(r, p, S("AllElements", PolicyTargetState.Enabled).Set("Choice", "0"));
        Assert.Contains(plan.Effective, c => c.Name == "Boost" && c.IsDelete);
        Assert.Null(V(r, All + @"\Extra", "Boost"));
    }

    [Fact]
    public void Enum_DeleteItemRemovesTheValue()
    {
        var r = new MemoryPolicyRegistry();
        var p = P("ElementsOnly");
        Run(r, p, S("ElementsOnly", PolicyTargetState.Enabled).Set("Mode", "1").Set("Level", "3"));
        Assert.Equal("1 (REG_DWORD)", V(r, @"Software\Policies\Test\W\Only", "Mode"));
        Run(r, p, S("ElementsOnly", PolicyTargetState.Enabled).Set("Mode", "#1").Set("Level", "3"));
        Assert.Null(V(r, @"Software\Policies\Test\W\Only", "Mode"));
        Assert.Equal("#1", PolicyWriter.ReadCurrent(p, M, r).Setting.Values["Mode"][0]);
    }

    [Theory]
    [InlineData("Count", "0", "fora do intervalo")]
    [InlineData("Count", "51", "fora do intervalo")]
    [InlineData("Count", "-1", "não é um número")]
    [InlineData("Count", "abc", "não é um número")]
    [InlineData("Count", "", "obrigatório")]
    [InlineData("Server", "01234567890", "máximo é 10")]
    [InlineData("Server", "", "obrigatório")]
    [InlineData("Choice", "Medium", "não existe")]
    [InlineData("Choice", "#9", "não existe")]
    [InlineData("Pairs", "semigual", "nome=valor")]
    [InlineData("Nope", "1", "não existe nesta política")]
    public void Validation_Errors(string element, string value, string fragment)
    {
        var errors = Errors(P("AllElements"), S("AllElements", PolicyTargetState.Enabled).Set(element, value));
        Assert.Contains(errors, e => e.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validation_MultiTextLimits_DecimalRangeEdges_AndNoWriteWhenInvalid()
    {
        var p = P("AllElements");
        Assert.Contains(Errors(p, S("AllElements", PolicyTargetState.Enabled).Set("Lines", "1", "2", "3", "4")), e => e.Contains("máximo é 3"));
        Assert.Contains(Errors(p, S("AllElements", PolicyTargetState.Enabled).Set("Lines", "123456789")), e => e.Contains("máximo é 8"));
        Assert.Empty(Errors(p, S("AllElements", PolicyTargetState.Enabled).Set("Count", "1")));
        Assert.Empty(Errors(p, S("AllElements", PolicyTargetState.Enabled).Set("Count", " 50 ")));
        Assert.Contains(Errors(p, S("AllElements", PolicyTargetState.Enabled).Set("Pairs", "a=1", "A=2")), e => e.Contains("repetido"));
        // Decimal sem limites explícitos usa 0..9999 (padrão do esquema).
        Assert.Contains(Errors(P("ElementsOnly"), S("ElementsOnly", PolicyTargetState.Enabled).Set("Level", "4")), e => e.Contains("0..3"));

        var invalid = PolicyWriter.Plan(p, S("AllElements", PolicyTargetState.Enabled).Set("Count", "999"), new MemoryPolicyRegistry());
        Assert.False(invalid.IsValid);
        Assert.Empty(invalid.Changes);
    }

    // ---------------- listas ----------------

    [Fact]
    public void List_NonAdditive_ReplacesAllValuesOfTheKey()
    {
        var r = new MemoryPolicyRegistry();
        r.Write(M, All + @"\Plain", "old", PolicyValues.Sz("old"));
        r.Write(M, All + @"\Numbered", "7", PolicyValues.Sz("seven"));
        r.Write(M, All + @"\Numbered", "Foreign", PolicyValues.Sz("kept"));   // não é numérico: não pertence à lista (prefixo "")
        Run(r, P("AllElements"), S("AllElements", PolicyTargetState.Enabled).Set("Plain", "new").Set("Numbered", "one"));
        Assert.Equal(["new"], r.ValueNames(M, All + @"\Plain"));
        Assert.Null(V(r, All + @"\Numbered", "7"));
        Assert.Equal("\"one\" (REG_EXPAND_SZ)", V(r, All + @"\Numbered", "1"));
        Assert.Equal("\"kept\" (REG_SZ)", V(r, All + @"\Numbered", "Foreign"));
    }

    [Fact]
    public void List_Additive_KeepsExistingItemsAndContinuesNumbering()
    {
        var r = new MemoryPolicyRegistry();
        r.Write(M, All + @"\Prefixed", "Item1", PolicyValues.Sz("a"));
        r.Write(M, All + @"\Prefixed", "Item3", PolicyValues.Sz("c"));
        r.Write(M, All + @"\Prefixed", "Other", PolicyValues.Sz("foreign"));
        r.Write(M, All + @"\Pairs", "keep", PolicyValues.Sz("1"));
        Run(r, P("AllElements"), S("AllElements", PolicyTargetState.Enabled).Set("Prefixed", "c", "d").Set("Pairs", "new=2"));

        Assert.Equal("\"a\" (REG_SZ)", V(r, All + @"\Prefixed", "Item1"));
        Assert.Equal("\"c\" (REG_SZ)", V(r, All + @"\Prefixed", "Item3"));
        Assert.Equal("\"d\" (REG_SZ)", V(r, All + @"\Prefixed", "Item4"));   // "c" já existia: não repete
        Assert.Equal("\"foreign\" (REG_SZ)", V(r, All + @"\Prefixed", "Other"));
        Assert.Equal("\"1\" (REG_SZ)", V(r, All + @"\Pairs", "keep"));
        Assert.Equal("\"2\" (REG_SZ)", V(r, All + @"\Pairs", "new"));

        var current = PolicyWriter.ReadCurrent(P("AllElements"), M, r).Setting;
        Assert.Equal(["a", "c", "d"], current.Values["Prefixed"]);
        Assert.Equal(["keep=1", "new=2"], current.Values["Pairs"].Order(StringComparer.Ordinal));
    }

    // ---------------- desabilitada / não configurada ----------------

    private static MemoryPolicyRegistry Configured(out AdmxPolicy p)
    {
        p = P("AllElements");
        var r = new MemoryPolicyRegistry();
        r.Write(M, All, "Unrelated", PolicyValues.Dword(5));                 // valor de outro dono, na mesma chave
        r.Write(M, All + @"\Prefixed", "NotMine", PolicyValues.Sz("x"));     // não casa com o prefixo "Item"
        Run(r, p, S("AllElements", PolicyTargetState.Enabled)
            .Set("Choice", "2").Set("CheckLists", "true").Set("AsText", "1").Set("Named", "low").Set("Lines", "l")
            .Set("Plain", "a").Set("Numbered", "b").Set("Prefixed", "c").Set("Pairs", "d=e"));
        return r;
    }

    [Fact]
    public void Disabled_WritesDisabledValueAndDeletesEveryElementValue()
    {
        var r = Configured(out var p);
        var plan = Run(r, p, S("AllElements", PolicyTargetState.Disabled));
        Assert.Empty(plan.KeysToDelete);

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\ENABLED"] = "0 (REG_DWORD)",
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\UNRELATED"] = "5 (REG_DWORD)",
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\PREFIXED\NOTMINE"] = "\"x\" (REG_SZ)",
        };
        Assert.Equal(expected, r.Snapshot());
        Assert.Equal(AdmxPolicyState.Disabled, PolicyWriter.ReadCurrent(p, M, r).State);
        // Chaves das listas continuam existindo (gpedit usa **delvals., que não apaga a chave).
        Assert.True(r.KeyExists(M, All + @"\Plain"));
    }

    [Fact]
    public void NotConfigured_RemovesExactlyWhatThePolicyOwns_AndEmptyListKeys()
    {
        var r = Configured(out var p);
        var plan = Run(r, p, S("AllElements", PolicyTargetState.NotConfigured));

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\UNRELATED"] = "5 (REG_DWORD)",
            [@"HKLM\SOFTWARE\POLICIES\TEST\W\ALL\PREFIXED\NOTMINE"] = "\"x\" (REG_SZ)",
        };
        Assert.Equal(expected, r.Snapshot());
        Assert.Equal(AdmxPolicyState.NotConfigured, PolicyWriter.ReadCurrent(p, M, r).State);

        // Chaves de lista vazias são apagadas; a que tem valor de outro dono fica.
        Assert.Equal([All + @"\Plain", All + @"\Numbered", All + @"\Pairs"], plan.KeysToDelete);
        Assert.False(r.KeyExists(M, All + @"\Plain"));
        Assert.True(r.KeyExists(M, All + @"\Prefixed"));
        Assert.True(r.KeyExists(M, All));
    }

    [Fact]
    public void ElementsOnly_DisabledWritesNothing_AndSaysSo()
    {
        var r = new MemoryPolicyRegistry();
        var p = P("ElementsOnly");
        Run(r, p, S("ElementsOnly", PolicyTargetState.Enabled).Set("Level", "2").Set("Mode", "1"));
        var plan = Run(r, p, S("ElementsOnly", PolicyTargetState.Disabled));
        Assert.Contains(plan.Notes, n => n.Contains("não grava nada"));
        Assert.Empty(r.Snapshot());
    }

    [Fact]
    public void ValuesAreIgnoredWhenNotEnabled_WithNote()
    {
        var plan = PolicyWriter.Plan(P("Implicit"), S("Implicit", PolicyTargetState.NotConfigured).Set("x", "1"), new MemoryPolicyRegistry());
        Assert.True(plan.IsValid);
        Assert.Contains(plan.Notes, n => n.Contains("ignorados"));
    }

    // ---------------- leitura / prévia ----------------

    [Fact]
    public void ReadCurrent_RoundTripsEveryElement()
    {
        var r = new MemoryPolicyRegistry();
        var p = P("AllElements");
        var s = S("AllElements", PolicyTargetState.Enabled)
            .Set("Count", "12").Set("AsText", "3").Set("Big", "99").Set("Check", "false").Set("CheckLists", "true")
            .Set("Choice", "2").Set("Named", "high").Set("Server", "abc").Set("Path", "%P%").Set("Lines", "x", "y")
            .Set("Plain", "p").Set("Numbered", "n1", "n2").Set("Prefixed", "q").Set("Pairs", "k=v");
        Run(r, p, s);

        var (state, read) = PolicyWriter.ReadCurrent(p, M, r);
        Assert.Equal(AdmxPolicyState.Enabled, state);
        Assert.Equal(PolicyScope.Machine, read.Scope);   // classe Both: o escopo é registrado
        foreach (var (id, values) in s.Values)
            Assert.True(values.SequenceEqual(read.Values[id]), $"{id}: {string.Join("|", values)} != {string.Join("|", read.Values[id])}");
        // Reaplicar o que foi lido não muda nada.
        Assert.False(PolicyWriter.Plan(p, read, r).HasEffect);
    }

    [Fact]
    public void Describe_ShowsAddsDeletesAndCurrentValues()
    {
        var r = new MemoryPolicyRegistry();
        r.Write(M, @"Software\Policies\Test\W\Implicit", "Flag", PolicyValues.Dword(0));
        var lines = PolicyWriter.Plan(P("Implicit"), S("Implicit", PolicyTargetState.Enabled), r).Describe().ToList();
        Assert.Equal([@"+ HKLM\Software\Policies\Test\W\Implicit\Flag = 1 (REG_DWORD) (atual: 0 (REG_DWORD))"], lines);
        var del = PolicyWriter.Plan(P("Implicit"), S("Implicit", PolicyTargetState.NotConfigured), r).Describe().Single();
        Assert.StartsWith(@"- HKLM\Software\Policies\Test\W\Implicit\Flag (apagar", del);
    }

    [Fact]
    public void Values_KindsMatchTheSchema()
    {
        Assert.Equal(RegistryValueKind.DWord, PolicyValues.FromAdmx(new AdmxValue(AdmxValueKind.Decimal, "4294967295"))!.Value.Kind);
        Assert.Equal(-1, PolicyValues.FromAdmx(new AdmxValue(AdmxValueKind.Decimal, "4294967295"))!.Value.Data);
        Assert.Throws<FormatException>(() => PolicyValues.FromAdmx(new AdmxValue(AdmxValueKind.Decimal, "4294967296")));
        Assert.Equal(RegistryValueKind.QWord, PolicyValues.FromAdmx(new AdmxValue(AdmxValueKind.LongDecimal, "1"))!.Value.Kind);
        Assert.Null(PolicyValues.FromAdmx(new AdmxValue(AdmxValueKind.Delete, null)));

        foreach (var v in new[] { PolicyValues.Dword(uint.MaxValue), PolicyValues.Qword(ulong.MaxValue), PolicyValues.Sz("a"), PolicyValues.ExpandSz("%x%"), PolicyValues.MultiSz(["a", "b"]) })
        {
            var (kind, data, multi) = PolicyValues.ToBackup(v);
            Assert.True(PolicyValues.Equal(v, PolicyValues.FromBackup(kind, data, multi)), kind);
        }
    }

    [Theory]
    [InlineData("enabled", PolicyTargetState.Enabled)]
    [InlineData("Habilitada", PolicyTargetState.Enabled)]
    [InlineData("disabled", PolicyTargetState.Disabled)]
    [InlineData("notconfigured", PolicyTargetState.NotConfigured)]
    [InlineData("not-configured", PolicyTargetState.NotConfigured)]
    [InlineData("não configurada", PolicyTargetState.NotConfigured)]
    public void ParseState(string text, PolicyTargetState expected)
    {
        Assert.True(PolicySetting.TryParseState(text, out var s));
        Assert.Equal(expected, s);
    }
}
