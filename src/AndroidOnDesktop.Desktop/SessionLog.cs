using System.IO;
using System.Text;

namespace AndroidOnDesktop.Desktop;

internal sealed class SessionLog : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private bool _disposed;

    internal SessionLog(string directory)
    {
        FilePath = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        try
        {
            Directory.CreateDirectory(directory);
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LastWriteError = ex.Message;
        }
    }

    internal string FilePath { get; }
    internal string? LastWriteError { get; private set; }

    internal void Write(string text)
    {
        lock (_gate)
        {
            if (_disposed || _writer is null) return;
            try { _writer.WriteLine($"[{DateTimeOffset.Now:O}] {text}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                LastWriteError = ex.Message;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastWriteError = ex.Message;
            }
            _writer = null;
        }
    }
}
