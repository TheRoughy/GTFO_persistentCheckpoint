using BepInEx.Logging;

namespace GTFO.PersistentCheckpoint.Core;

internal static class Log
{
    private static ManualLogSource? _source;

    internal static void Setup(ManualLogSource source) => _source = source;
    internal static void Debug(string message) => _source?.LogDebug(message);
    internal static void Info(string message) => _source?.LogInfo(message);
    internal static void Warning(string message) => _source?.LogWarning(message);
    internal static void Error(string message) => _source?.LogError(message);
    internal static void Error(Exception exception) => _source?.LogError(exception);
}
