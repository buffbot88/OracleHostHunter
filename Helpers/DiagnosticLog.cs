using System.Text;
using System.Text.RegularExpressions;

namespace OracleHost.Helpers;

/// <summary>
/// Small file logger for troubleshooting the desktop app. Log entries are
/// redacted before they are written so exceptions cannot accidentally persist
/// private keys, bearer tokens, or refresh tokens.
/// </summary>
public static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static readonly Regex PemBlock = new(
        "-----BEGIN [^-]+-----.*?-----END [^-]+-----",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex SecretAssignment = new(
        "(?<name>(?:access[_-]?token|refresh[_-]?token|security[_-]?token|authorization|private[_-]?key|client[_-]?secret|password|secret|credential|signature|token))\\s*[:=]\\s*[^\\s,;]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Bearer = new(
        "Bearer\\s+[A-Za-z0-9._~+\\-/]+=*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OracleHost", "logs");

    public static string LogPath { get; } = Path.Combine(
        LogDirectory, $"oraclehost-{DateTime.Now:yyyyMMdd}.log");

    public static void StartSession()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            CleanupOldLogs();
            Info("Startup", $"OracleHost starting. Log file: {LogPath}");
        }
        catch
        {
            // Diagnostics must never prevent the application from starting.
        }
    }

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Warn(string area, string message) => Write("WARN", area, message);

    public static void Error(string area, string message) => Write("ERROR", area, message);

    public static string SafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "[none]";
        try { return Path.GetFileName(path); }
        catch { return "[invalid path]"; }
    }

    public static string RedactIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "[none]";
        var trimmed = value.Trim();
        return trimmed.Length <= 18 ? trimmed : $"{trimmed[..12]}…{trimmed[^6..]}";
    }

    public static void Exception(string area, string operation, Exception ex)
    {
        Write("ERROR", area,
            $"{operation} failed: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}\n" +
            FormatInnerExceptions(ex));
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var redacted = PemBlock.Replace(value, "[REDACTED PEM BLOCK]");
        redacted = Bearer.Replace(redacted, "Bearer [REDACTED]");
        redacted = SecretAssignment.Replace(redacted, m => $"{m.Groups["name"].Value}=[REDACTED]");
        return redacted;
    }

    private static string FormatInnerExceptions(Exception ex)
    {
        var builder = new StringBuilder();
        var inner = ex.InnerException;
        while (inner != null)
        {
            builder.AppendLine($"Inner exception: {inner.GetType().FullName}: {Redact(inner.Message)}");
            inner = inner.InnerException;
        }
        return builder.ToString();
    }

    private static void CleanupOldLogs()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(LogDirectory, "oraclehost-*.log"))
            {
                var info = new FileInfo(path);
                if (info.Length > 5 * 1024 * 1024 || info.LastWriteTime < DateTime.Now.AddDays(-14))
                    info.Delete();
            }
        }
        catch
        {
            // Best effort only; logging must remain non-fatal.
        }
    }

    private static void Write(string level, string area, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{area}] {Redact(message)}";
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging is best effort and must not mask the original failure.
        }
    }
}
