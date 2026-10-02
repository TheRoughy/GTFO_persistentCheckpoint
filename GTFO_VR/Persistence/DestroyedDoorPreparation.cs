using System.Buffers.Binary;
using GTFO.PersistentCheckpoint.Core;
using Il2CppInterop.Runtime;
using LevelGeneration;
using SNetwork;

namespace GTFO.PersistentCheckpoint.Persistence;

internal static class DestroyedDoorPreparation
{
    internal static int Prepare(CheckpointSnapshot snapshot)
    {
        if (!SNet.IsMaster)
            return 0;

        // Match the saved recall packet to each generated door's actual packet
        // identity. Never guess the packet index, struct size or enum byte layout.
        var packets = new Dictionary<(ushort Key, byte Index), byte[]>();
        foreach (var pass in snapshot.Passes)
        foreach (var bytes in pass)
        {
            if (bytes.Length >= 3)
                packets[(BinaryPrimitives.ReadUInt16LittleEndian(bytes), bytes[2])] = bytes;
        }

        var targets = new List<(LG_Door_Sync Sync, pDoorState State, string Name)>();
        // Unity 2019's generated bindings lack the includeInactive overload.
        // Resources also finds prefabs; only initialized level doors have a gate.
        foreach (var item in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<LG_WeakDoor>()))
        {
            var door = item.TryCast<LG_WeakDoor>();
            if (door == null)
                continue;
            try
            {
                if (door.Gate == null)
                    continue;
                var sync = door.m_syncComp?.TryCast<LG_Door_Sync>();
                if (sync == null || sync.m_stateReplicator == null)
                    continue;

                var recallPacket = sync.m_stateReplicator.m_statePacket_Recall;
                if (!packets.TryGetValue((recallPacket.Replicator.Key, recallPacket.Index), out var bytes))
                    continue;

                var marshaller = SNet_Packet<pDoorState>.s_marshaller;
                if (marshaller == null || bytes.Length != marshaller.SizeWithIDs)
                {
                    Log.Warning($"Skipping door preparation for {door.name}: saved door packet size does not match the current game.");
                    continue;
                }

                var state = DoorStatePacketDecoder.Decode(bytes, marshaller.Size, marshaller.m_marshallingType);
                if (state.status == eDoorStatus.Destroyed
                    && sync.GetCurrentSyncState().status != eDoorStatus.Destroyed)
                    targets.Add((sync, state, door.name));
            }
            catch (Exception exception)
            {
                Log.Warning($"Skipping door preparation for {door.name}: {exception}");
            }
        }

        var prepared = 0;
        foreach (var target in targets)
        {
            try
            {
                // A normal master state transition broadcasts to unmodded clients
                // and invokes the game's destruction callback on every recipient.
                // Do not replay unrelated checkpoint packets or synthesize damage.
                target.Sync.m_stateReplicator.OnStateChange_Master(target.State);
                prepared++;
                Log.Info($"Broadcast pre-recall destruction for {target.Name}.");
            }
            catch (Exception exception)
            {
                Log.Warning($"Pre-recall destruction failed for {target.Name}; host recall repair remains available: {exception}");
            }
        }

        return prepared;
    }
}
