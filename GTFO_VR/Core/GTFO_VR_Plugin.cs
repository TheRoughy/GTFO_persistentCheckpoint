using BepInEx;
using BepInEx.Unity.IL2CPP;
using GTFO.PersistentCheckpoint.Persistence;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace GTFO.PersistentCheckpoint.Core;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency("GTFO.LobbyExpansion", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class PersistentCheckpointPlugin : BasePlugin
{
    public const string Guid = "com.Roughy.GTFO.PersistentCheckpoint";
    public const string Name = "GTFO Persistent Checkpoint";
    public const string Version = "1.0.0";

    private Harmony? _harmony;

    public override void Load()
    {
        Core.Log.Setup(base.Log);
        CheckpointStore.Initialize();

        ClassInjector.RegisterTypeInIl2Cpp<PersistentCheckpointController>();
        var host = new GameObject("GTFO_PersistentCheckpoint");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<PersistentCheckpointController>();

        _harmony = new Harmony(Guid);
        _harmony.PatchAll();
        Core.Log.Info($"Loaded {Name} {Version}. LobbyExpansion: {LobbyCompatibility.Description}");
    }

    public override bool Unload()
    {
        CheckpointStore.Flush(TimeSpan.FromSeconds(3));
        _harmony?.UnpatchSelf();
        return true;
    }
}
