namespace GTFO.PersistentCheckpoint.Persistence;

internal sealed class CheckpointSnapshot
{
    internal int BuildRevision { get; init; }
    internal string ExpeditionKey { get; init; } = string.Empty;
    internal string RundownKey { get; init; } = string.Empty;
    internal int Tier { get; init; }
    internal int ExpeditionIndex { get; init; }
    internal int HostSeed { get; init; }
    internal int SessionSeed { get; init; }
    internal ulong LevelChecksum { get; init; }
    internal float ProgressionTime { get; init; }
    internal ulong RecallCount { get; init; }
    internal long SavedAtUtcTicks { get; init; }
    internal int RequiredSlotCapacity { get; init; } = 4;
    internal bool LobbyExpansionPresent { get; init; }
    internal string LobbyExpansionVersion { get; init; } = string.Empty;
    internal List<CapturedPlayer> Players { get; init; } = new();
    internal List<List<byte[]>> Passes { get; init; } = new();
}

internal readonly record struct CapturedPlayer(ulong Lookup, int CharacterIndex, bool IsBot);

internal readonly record struct SnapshotLoadResult(CheckpointSnapshot? Snapshot, string? Error)
{
    internal bool Success => Snapshot != null;
}
