using System.Runtime.InteropServices;

namespace Debloater.Core.Platform;

/// <summary>Whether the PC is joined to an Active Directory domain (domain GPOs override local policies).</summary>
public static partial class DomainInfo
{
    private const int NetSetupDomainName = 3;

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetGetJoinInformation(string? server, out nint nameBuffer, out int bufferType);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(nint buffer);

    private static readonly Lazy<bool> s_joined = new(Read);

    public static bool IsDomainJoined => s_joined.Value;

    private static bool Read()
    {
        try
        {
            if (NetGetJoinInformation(null, out var buffer, out var type) != 0) return false;
            NetApiBufferFree(buffer);
            return type == NetSetupDomainName;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
