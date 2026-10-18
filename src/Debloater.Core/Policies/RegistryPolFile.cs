using System.Text;

namespace Debloater.Core.Policies;

/// <summary>Uma entrada de um arquivo Registry.pol (formato PReg).</summary>
/// <param name="Type">Tipo do valor (REG_SZ = 1, REG_EXPAND_SZ = 2, REG_BINARY = 3, REG_DWORD = 4, REG_MULTI_SZ = 7, REG_QWORD = 11).</param>
/// <param name="ValueName">Nome do valor; "**del.X", "**delvals." etc. são comandos do gpedit.</param>
public sealed record RegistryPolEntry(string Key, string ValueName, uint Type, byte[] Data);

/// <summary>
/// Leitor (somente leitura) do formato PReg usado pelo gpedit em
/// %SystemRoot%\System32\GroupPolicy\{Machine,User}\Registry.pol: cabeçalho "PReg" + versão 1 e
/// entradas "[chave;valor;tipo;tamanho;dados]" em UTF-16LE. Usado só para avisar quando a GPO local
/// também configura uma política (o programa NÃO grava este arquivo).
/// </summary>
public static class RegistryPolFile
{
    private const uint Signature = 0x67655250; // "PReg"

    public static string MachinePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "GroupPolicy", "Machine", "Registry.pol");
    public static string UserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "GroupPolicy", "User", "Registry.pol");

    /// <summary>Lê um arquivo; ausente, sem acesso ou inválido → lista vazia.</summary>
    public static IReadOnlyList<RegistryPolEntry> TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return []; }
    }

    /// <summary>Lê o conteúdo de um Registry.pol. Lança <see cref="FormatException"/> se o formato for inválido.</summary>
    public static IReadOnlyList<RegistryPolEntry> Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) throw new FormatException("Registry.pol curto demais.");
        if (BitConverter.ToUInt32(bytes[..4]) != Signature) throw new FormatException("Registry.pol sem a assinatura PReg.");
        if (BitConverter.ToUInt32(bytes[4..8]) != 1) throw new FormatException("versão de Registry.pol não suportada.");

        var entries = new List<RegistryPolEntry>();
        int pos = 8;
        while (pos < bytes.Length)
        {
            ExpectChar(bytes, ref pos, '[');
            var key = ReadString(bytes, ref pos);
            ExpectChar(bytes, ref pos, ';');
            var name = ReadString(bytes, ref pos);
            ExpectChar(bytes, ref pos, ';');
            var type = ReadUInt32(bytes, ref pos);
            ExpectChar(bytes, ref pos, ';');
            var size = ReadUInt32(bytes, ref pos);
            ExpectChar(bytes, ref pos, ';');
            if (size > (uint)(bytes.Length - pos)) throw new FormatException("Registry.pol: tamanho de dados inválido.");
            var data = bytes.Slice(pos, (int)size).ToArray();
            pos += (int)size;
            ExpectChar(bytes, ref pos, ']');
            entries.Add(new RegistryPolEntry(key, name, type, data));
        }
        return entries;
    }

    /// <summary>Gera o conteúdo de um Registry.pol (usado nos testes do leitor).</summary>
    public static byte[] Build(IEnumerable<RegistryPolEntry> entries)
    {
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(Signature));
        ms.Write(BitConverter.GetBytes(1u));
        void Char(char c) => ms.Write(BitConverter.GetBytes((ushort)c));
        void Str(string s) { ms.Write(Encoding.Unicode.GetBytes(s)); Char('\0'); }
        foreach (var e in entries)
        {
            Char('['); Str(e.Key); Char(';'); Str(e.ValueName); Char(';');
            ms.Write(BitConverter.GetBytes(e.Type)); Char(';');
            ms.Write(BitConverter.GetBytes((uint)e.Data.Length)); Char(';');
            ms.Write(e.Data); Char(']');
        }
        return ms.ToArray();
    }

    /// <summary>Entradas que mexem em valores da chave (inclui "**del.Nome" e "**delvals.").</summary>
    public static bool Touches(RegistryPolEntry e, string key, string? valueName)
    {
        if (!e.Key.Trim('\\').Equals(key.Trim('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        if (valueName is null) return true;
        var n = e.ValueName;
        if (n.StartsWith("**del.", StringComparison.OrdinalIgnoreCase)) n = n[6..];
        if (n.StartsWith("**delvals", StringComparison.OrdinalIgnoreCase)) return true;
        return n.Equals(valueName, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExpectChar(ReadOnlySpan<byte> b, ref int pos, char c)
    {
        if (pos + 2 > b.Length || BitConverter.ToUInt16(b.Slice(pos, 2)) != c)
            throw new FormatException($"Registry.pol: esperado '{c}' na posição {pos}.");
        pos += 2;
    }

    private static string ReadString(ReadOnlySpan<byte> b, ref int pos)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (pos + 2 > b.Length) throw new FormatException("Registry.pol: texto sem terminador.");
            var ch = (char)BitConverter.ToUInt16(b.Slice(pos, 2));
            pos += 2;
            if (ch == '\0') return sb.ToString();
            sb.Append(ch);
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> b, ref int pos)
    {
        if (pos + 4 > b.Length) throw new FormatException("Registry.pol: número truncado.");
        var v = BitConverter.ToUInt32(b.Slice(pos, 4));
        pos += 4;
        return v;
    }
}
