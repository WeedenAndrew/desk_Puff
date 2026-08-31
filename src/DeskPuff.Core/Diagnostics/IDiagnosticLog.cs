using System.Globalization;
using System.Text;

namespace DeskPuff.Core.Diagnostics;

public interface IDiagnosticLog
{
    void Write(string message);

    void WriteException(string context, Exception exception);
}

public sealed class FileDiagnosticLog : IDiagnosticLog, IDisposable
{
    private readonly Lock writeLock = new();
    private readonly StreamWriter writer;
    private bool disposed;

    public FileDiagnosticLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("The diagnostic log path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        FileStream stream = new(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public static FileDiagnosticLog CreateBesideExecutable()
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new FileDiagnosticLog(Path.Combine(
            AppContext.BaseDirectory,
            $"desk_Puff-{timestamp}.log"));
    }

    public void Write(string message)
    {
        string timestamp = DateTimeOffset.Now.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
            CultureInfo.InvariantCulture);
        string oneLine = message.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        lock (writeLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            writer.WriteLine($"{timestamp} {oneLine}");
        }
    }

    public void WriteException(string context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(
            $"EXCEPTION context=\"{context}\" type={exception.GetType().FullName} " +
            $"message=\"{exception.Message}\"");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lock (writeLock)
        {
            if (disposed)
            {
                return;
            }

            writer.Dispose();
            disposed = true;
        }
    }
}

public sealed class NullDiagnosticLog : IDiagnosticLog
{
    private NullDiagnosticLog()
    {
    }

    public static NullDiagnosticLog Instance { get; } = new();

    public void Write(string message)
    {
    }

    public void WriteException(string context, Exception exception)
    {
    }
}
