using CellMenu;
using GameData;
using GTFO.PersistentCheckpoint.Core;
using GTFO.PersistentCheckpoint.Persistence;
using HarmonyLib;
using SNetwork;

namespace GTFO.PersistentCheckpoint.Patches;

[HarmonyPatch(typeof(RundownManager), "SetActiveExpedition")]
internal static class SetActiveExpeditionPatch
{
    private static void Prefix(ref pActiveExpedition __0, ExpeditionInTierData __1)
    {
        ResumeCoordinator.OnSelectingExpedition(ref __0, __1);
    }
}

[HarmonyPatch(typeof(SNet_Capture), nameof(SNet_Capture.CaptureGameState))]
internal static class CaptureCheckpointPatch
{
    private static void Postfix(eBufferType __0)
    {
        if (__0 == eBufferType.Checkpoint)
            ResumeCoordinator.CaptureCheckpoint();
    }
}

[HarmonyPatch(typeof(GS_Lobby), nameof(GS_Lobby.TryStartLevelTrigger))]
internal static class StartLevelPatch
{
    private static void Postfix(bool __result)
    {
        ResumeCoordinator.OnStartLevelResult(__result);
    }
}

[HarmonyPatch(typeof(GS_ExpeditionSuccess), nameof(GS_ExpeditionSuccess.Enter))]
internal static class ExpeditionSuccessPatch
{
    private static void Prefix()
    {
        ResumeCoordinator.ClearOnSuccess();
    }
}

[HarmonyPatch(typeof(SNet_GlobalManager), "OnPlayerEvent")]
internal static class PlayerSyncEventPatch
{
    private static void Postfix(SNet_Player __0, SNet_PlayerEvent __1)
    {
        ResumeCoordinator.OnPlayerEvent(__0, __1);
    }
}

[HarmonyPatch(typeof(CM_PageLoadout), nameof(CM_PageLoadout.Setup))]
[HarmonyAfter(LobbyCompatibility.PluginGuid)]
internal static class LoadoutUiPatch
{
    private static void Postfix(CM_PageLoadout __instance)
    {
        PersistentCheckpointController.QueueLoadoutUi(__instance);
    }
}
