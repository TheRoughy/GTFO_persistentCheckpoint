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

internal readonly record struct CapturedPlayer(
    ulong Lookup,
    int CharacterIndex,
    bool IsBot,
    bool HasResources = false,
    float HealthRel = 0f,
    float Infection = 0f,
    float StandardAmmo = 0f,
    float SpecialAmmo = 0f,
    float ClassAmmo = 0f,
    float ResourcePackAmmo = 0f,
    float ConsumableAmmo = 0f);

internal readonly record struct SnapshotLoadResult(CheckpointSnapshot? Snapshot, string? Error)
{
    internal bool Success => Snapshot != null;
}
