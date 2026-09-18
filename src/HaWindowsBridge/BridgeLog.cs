using System.Text;

namespace HAWindowsBridge;

internal static class BridgeLog
{
    private static readonly object Sync = new();
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HAWindowsBridge", "bridge.log");

    public static void Write(string operation, Exception exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1024 * 1024)
                    File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath,
                    $"{DateTimeOffset.Now:O} | {operation} | {exception.GetType().Name}: {exception.Message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
