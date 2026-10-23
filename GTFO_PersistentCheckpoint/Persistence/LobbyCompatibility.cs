using BepInEx.Unity.IL2CPP;
using SNetwork;

namespace GTFO.PersistentCheckpoint.Persistence;

internal static class LobbyCompatibility
{
    internal const string PluginGuid = "GTFO.LobbyExpansion";

    internal static bool IsPresent => IL2CPPChainloader.Instance.Plugins.ContainsKey(PluginGuid);

    internal static string Version => IL2CPPChainloader.Instance.Plugins.TryGetValue(PluginGuid, out var info)
        ? info.Instance?.GetType().Assembly.GetName().Version?.ToString() ?? "unknown"
        : string.Empty;

    internal static string Description => IsPresent ? $"present ({Version})" : "not present";

    internal static int CurrentSlotCapacity
    {
        get
        {
            try
            {
                return SNet.Slots?.CharacterSlots?.Length ?? 4;
            }
            catch
            {
                return 4;
            }
        }
    }

    internal static bool CanRepresent(CheckpointSnapshot snapshot, out string reason)
    {
        var capacity = CurrentSlotCapacity;
        if (capacity < snapshot.RequiredSlotCapacity)
        {
            reason = $"checkpoint needs {snapshot.RequiredSlotCapacity} character slots, current lobby has {capacity}";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
