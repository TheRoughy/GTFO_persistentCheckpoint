using GTFO.PersistentCheckpoint.Core;
using HarmonyLib;
using LevelGeneration;
using SNetwork;

namespace GTFO.PersistentCheckpoint.Patches;

[HarmonyPatch(typeof(LG_WeakDoor), "OnSyncDoorStateChange")]
internal static class DestroyedDoorRecallPatch
{
    private static void Postfix(LG_WeakDoor __instance, pDoorState __0, bool __1)
    {
        if (!SNet.IsMaster || !__1 || __0.status != eDoorStatus.Destroyed)
            return;

        try
        {
            // Keep this a derived-state repair: the traversal setter would otherwise
            // mark the replicated door metadata as opened via the gate's factory.
            if (__instance.Gate != null)
                __instance.Gate.HasBeenOpenedDuringPlay = true;

            // The non-recall Destroyed branch stops any door animation and opens
            // the gate. The recall branch would incorrectly mark Destroyed closed.
            var animation = __instance.m_animComp?.TryCast<LG_WeakDoor_Anim>();
            if (animation != null)
                animation.OnDoorState(__0, false);
            if (__instance.Gate != null)
                __instance.Gate.IsTraversable = true;
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to repair recalled destroyed-door navigation for {__instance.name}: {exception}");
        }

        try
        {
            // Recall skips OnSyncDoorGotDestroyed. SnapToExploded alone leaves the
            // simple blade (including its collider) active and the AI gate closed.
            // Reconstruct the derived state, without broadcasting a fake transition
            // or calling TriggerExplosionEffect (noise, debris forces and events).
            var destruction = __instance.m_destructionComp?.TryCast<LG_WeakDoor_Destruction>();
            if (destruction == null || destruction.m_destructionController == null)
            {
                Log.Warning($"Cannot repair destroyed-door geometry for {__instance.name}: unsupported destruction component.");
                return;
            }

            destruction.EnableSkinnedDoor();
            destruction.m_destructionController.SnapToExploded();
            destruction.m_isDestroyed = true;

            var culler = __instance.m_doorBladeCuller;
            if (culler != null)
            {
                culler.SwitchToDestroyedRenderLists();
                culler.ForceUpdate();
            }

            Log.Info($"Repaired recalled destroyed-door geometry for {__instance.name} on host; traversable: {__instance.Gate != null && __instance.Gate.IsTraversable}.");
        }
        catch (Exception exception)
        {
            // A custom door prefab must not abort the remainder of checkpoint recall.
            Log.Error($"Failed to repair recalled destroyed-door geometry for {__instance.name}: {exception}");
        }
    }
}
