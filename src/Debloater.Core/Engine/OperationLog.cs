using System.Text;

namespace Debloater.Core.Engine;

/// <summary>
/// Log of an apply/revert session, as text (UTF-8), one file per session:
/// %ProgramData%\Debloater\logs\yyyy-MM-dd_HHmmss.log. Write failures never interrupt the operation.
/// </summary>
public sealed class OperationLog : IDisposable
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Debloater", "logs");

    private readonly StreamWriter? _writer;
    private readonly object _lock = new();

    public OperationLog(string? directory = null, DateTime? now = null)
    {
        directory ??= DefaultDirectory;
        var stamp = (now ?? DateTime.Now).ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        FilePath = Path.Combine(directory, stamp + ".log");
        try
        {
            Directory.CreateDirectory(directory);
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    public string FilePath { get; }

    /// <summary>Why the log could not be written (the text is still available via <see cref="Lines"/>).</summary>
    public string? Error { get; }

    public List<string> Lines { get; } = [];

    public event Action<string>? LineWritten;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (_lock)
        {
            Lines.Add(line);
            try { _writer?.WriteLine(line); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        LineWritten?.Invoke(line);
    }

    public void Dispose() => _writer?.Dispose();
}
