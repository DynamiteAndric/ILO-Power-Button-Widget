using System.IO;
using System.Text;

namespace IloWidget.Services;

/// <summary>
/// Diagnostic log of every iLO request and response, plus the widget's power decisions.
/// One file per day (<c>ilo-yyyyMMdd.log</c>) in <see cref="Directory"/>; files older than
/// <see cref="KeepDays"/> days are deleted. Logging is off while <see cref="Directory"/> is null.
/// Credentials are never written: no headers are logged and passwords in bodies are masked.
/// </summary>
public static class IloLog
{
    private const int KeepDays = 3;
    private const int MaxBodyChars = 64 * 1024;
    private static readonly object Sync = new();
    private static string? _directory;
    private static string? _lastFile;

    public static string? Directory
    {
        get => _directory;
        set
        {
            _directory = value;
            _lastFile = null;
            if (value is not null)
                Cleanup(value);
        }
    }

    /// <summary>Path of today's log file, or null when logging is off.</summary>
    public static string? CurrentFile => _directory is { } dir ? Path.Combine(dir, $"ilo-{DateTime.Now:yyyyMMdd}.log") : null;

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : message + Environment.NewLine + Describe(e));

    public static void Request(int id, string method, string path, string? body) =>
        Write("REQ  ", $"#{id} {method} {path}" + FormatBody(body));

    public static void Response(int id, int code, TimeSpan elapsed, string? body) =>
        Write("RESP ", $"#{id} HTTP {code} in {elapsed.TotalMilliseconds:0} ms" + FormatBody(body));

    public static void Failure(int id, TimeSpan elapsed, Exception e) =>
        Write("FAIL ", $"#{id} after {elapsed.TotalMilliseconds:0} ms{Environment.NewLine}{Describe(e)}");

    /// <summary>The exception chain with types, messages and HResults; the inner ones explain socket and TLS failures.</summary>
    public static string Describe(Exception e)
    {
        var sb = new StringBuilder();
        var depth = 0;
        for (Exception? x = e; x is not null; x = x.InnerException, depth++)
        {
            sb.Append(' ', 2 + depth * 2)
              .Append(x.GetType().FullName)
              .Append(" (0x").Append(x.HResult.ToString("X8")).Append("): ")
              .AppendLine(x.Message);
            if (x is System.Net.Sockets.SocketException se)
                sb.Append(' ', 4 + depth * 2).Append("SocketError=").AppendLine(se.SocketErrorCode.ToString());
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Masks the value of any "password", "session_key" or "sessionKey" JSON property and of a sessionKey cookie.</summary>
    public static string Redact(string body)
    {
        const System.Text.RegularExpressions.RegexOptions ci = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        body = System.Text.RegularExpressions.Regex.Replace(body, "(\"(?:password|session_key|sessionKey)\"\\s*:\\s*\")(?:[^\"\\\\]|\\\\.)*(\")", "$1***$2", ci);
        return System.Text.RegularExpressions.Regex.Replace(body, "(sessionKey=)[^;\\s\"]+", "$1***", ci);
    }

    private static string FormatBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        body = Redact(body.Trim());
        if (body.Length > MaxBodyChars)
            body = body[..MaxBodyChars] + $" ...[{body.Length - MaxBodyChars} more chars]";
        return Environment.NewLine + "  " + body;
    }

    private static void Write(string level, string message)
    {
        // read the directory once: it can be changed (or cleared) from another thread at any time
        var dir = _directory;
        if (dir is null)
            return;
        var file = Path.Combine(dir, $"ilo-{DateTime.Now:yyyyMMdd}.log");
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        lock (Sync)
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                if (file != _lastFile)
                {
                    // a new day: a widget that runs for weeks must not keep every daily file
                    _lastFile = file;
                    Cleanup(dir);
                }
                Append(file, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // logging must never break the widget
            }
        }
    }

    /// <summary>
    /// Appends with shared access so a second widget instance writing the same daily file does not collide;
    /// a write that still hits an IOException is retried once. Like <c>File.AppendAllText</c> with
    /// <see cref="Encoding.UTF8"/>, a BOM is written only when the file is created.
    /// </summary>
    private static void Append(string file, string line)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(line);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (!System.IO.Directory.Exists(dir))
                return;
            var limit = DateTime.Now.AddDays(-KeepDays);
            foreach (var f in System.IO.Directory.EnumerateFiles(dir, "ilo-*.log"))
            {
                if (File.GetLastWriteTime(f) < limit)
                    File.Delete(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
