using System.Text;

namespace Debloater.Core.Policies;

/// <summary>An entry of a Registry.pol file (PReg format).</summary>
/// <param name="Type">Value type (REG_SZ = 1, REG_EXPAND_SZ = 2, REG_BINARY = 3, REG_DWORD = 4, REG_MULTI_SZ = 7, REG_QWORD = 11).</param>
/// <param name="ValueName">Value name; "**del.X", "**delvals." etc. are gpedit commands.</param>
public sealed record RegistryPolEntry(string Key, string ValueName, uint Type, byte[] Data);

/// <summary>
/// Read-only reader of the PReg format used by gpedit in
/// %SystemRoot%\System32\GroupPolicy\{Machine,User}\Registry.pol: "PReg" header + version 1 and
/// "[key;value;type;size;data]" entries in UTF-16LE. Only used to warn when the local GPO
/// also configures a policy (the program does NOT write this file).
/// </summary>
public static class RegistryPolFile
{
    private const uint Signature = 0x67655250; // "PReg"

    public static string MachinePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "GroupPolicy", "Machine", "Registry.pol");
    public static string UserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "GroupPolicy", "User", "Registry.pol");

    /// <summary>Reads a file; missing, inaccessible or invalid → empty list.</summary>
    public static IReadOnlyList<RegistryPolEntry> TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return []; }
    }

    /// <summary>Reads the contents of a Registry.pol. Throws <see cref="FormatException"/> if the format is invalid.</summary>
    public static IReadOnlyList<RegistryPolEntry> Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) throw new FormatException("Registry.pol too short.");
        if (BitConverter.ToUInt32(bytes[..4]) != Signature) throw new FormatException("Registry.pol without the PReg signature.");
        if (BitConverter.ToUInt32(bytes[4..8]) != 1) throw new FormatException("unsupported Registry.pol version.");

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
            if (size > (uint)(bytes.Length - pos)) throw new FormatException("Registry.pol: invalid data size.");
            var data = bytes.Slice(pos, (int)size).ToArray();
            pos += (int)size;
            ExpectChar(bytes, ref pos, ']');
            entries.Add(new RegistryPolEntry(key, name, type, data));
        }
        return entries;
    }

    /// <summary>Builds the contents of a Registry.pol (used in the reader tests).</summary>
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

    /// <summary>Entries that touch values of the key (includes "**del.Name" and "**delvals.").</summary>
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
            throw new FormatException($"Registry.pol: expected '{c}' at position {pos}.");
        pos += 2;
    }

    private static string ReadString(ReadOnlySpan<byte> b, ref int pos)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (pos + 2 > b.Length) throw new FormatException("Registry.pol: string without terminator.");
            var ch = (char)BitConverter.ToUInt16(b.Slice(pos, 2));
            pos += 2;
            if (ch == '\0') return sb.ToString();
            sb.Append(ch);
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> b, ref int pos)
    {
        if (pos + 4 > b.Length) throw new FormatException("Registry.pol: truncated number.");
        var v = BitConverter.ToUInt32(b.Slice(pos, 4));
        pos += 4;
        return v;
    }
}
