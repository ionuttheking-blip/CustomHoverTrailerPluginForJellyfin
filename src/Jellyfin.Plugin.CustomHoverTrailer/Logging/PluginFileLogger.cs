using System.Reflection;
using System.Text;

namespace Jellyfin.Plugin.CustomHoverTrailer.Logging;

/// <summary>
/// Dedicated file logger for the CustomHoverTrailer plugin DLL.
/// Writes timestamped diagnostic entries to CustomHoverTrailer.log inside the plugin folder.
/// </summary>
public static class PluginFileLogger
{
    private static readonly Lock FileLock = new();

    /// <summary>
    /// Gets the full path to the dedicated DLL log file.
    /// Defaults to the folder where Jellyfin.Plugin.CustomHoverTrailer.dll is located.
    /// </summary>
    public static string LogFilePath
    {
        get
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            var dir = !string.IsNullOrWhiteSpace(assemblyLocation)
                ? Path.GetDirectoryName(assemblyLocation)
                : @"C:\ProgramData\Jellyfin\ServerData\plugins\CustomHoverTrailer";

            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = @"C:\ProgramData\Jellyfin\ServerData\plugins\CustomHoverTrailer";
            }

            return Path.Combine(dir, "CustomHoverTrailer.log");
        }
    }

    /// <summary>
    /// Writes an informational message to CustomHoverTrailer.log.
    /// </summary>
    /// <param name="message">The log message.</param>
    public static void Info(string message) => WriteEntry("INFO", message);

    /// <summary>
    /// Writes a warning message to CustomHoverTrailer.log.
    /// </summary>
    /// <param name="message">The warning message.</param>
    public static void Warn(string message) => WriteEntry("WARN", message);

    /// <summary>
    /// Writes an error message and optional exception details to CustomHoverTrailer.log.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="ex">Optional exception.</param>
    public static void Error(string message, Exception? ex = null)
    {
        var fullMessage = ex is null
            ? message
            : $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        WriteEntry("ERROR", fullMessage);
    }

    /// <summary>
    /// Reads the last N lines from CustomHoverTrailer.log.
    /// </summary>
    /// <param name="maxLines">Maximum number of lines to return.</param>
    /// <returns>Log file contents.</returns>
    public static string ReadRecentLogs(int maxLines = 250)
    {
        lock (FileLock)
        {
            try
            {
                var path = LogFilePath;
                if (!File.Exists(path))
                {
                    return $"Log file does not exist yet at: {path}";
                }

                var lines = File.ReadAllLines(path, Encoding.UTF8);
                var recent = lines.Skip(Math.Max(0, lines.Length - maxLines));
                return $"=== Log Path: {path} ===\n" + string.Join(Environment.NewLine, recent);
            }
            catch (Exception ex)
            {
                return $"Failed reading log file: {ex.Message}";
            }
        }
    }

    private static void WriteEntry(string level, string message)
    {
        lock (FileLock)
        {
            try
            {
                var path = LogFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Rotate if log exceeds 5 MB
                if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                {
                    var backupPath = path + ".old";
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }

                    File.Move(path, backupPath);
                }

                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch
            {
                // Never throw from diagnostic logger
            }
        }
    }
}
