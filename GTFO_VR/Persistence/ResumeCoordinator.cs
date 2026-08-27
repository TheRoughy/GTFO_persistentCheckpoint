using System.Reflection;
using CellMenu;
using GameData;
using GTFO.PersistentCheckpoint.Core;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using SNetwork;
using UnityEngine;

namespace GTFO.PersistentCheckpoint.Persistence;

internal static class ResumeCoordinator
{
    private static MethodInfo? _setActiveExpedition;
    private static MethodInfo? _startSinglePlayerRecall;
    private static pActiveExpedition _freshPackage = null!;
    private static bool _hasFreshPackage;
    private static bool _applyingPackage;
    private static bool _suppressCheckpointSave;
    private static int _requiredSlotCapacity = 4;
    private static readonly Queue<PlayerResync> PlayerResyncQueue = new();
    private static PlayerResync? _activePlayerResync;
    private static float _activePlayerResyncDeadline;
    private static float _nextPlayerResyncTime;

    private const float PlayerResyncDelaySeconds = 0.25f;
    private const float PlayerResyncTimeoutSeconds = 45f;

    internal static CheckpointSnapshot? ActiveSnapshot { get; private set; }
    internal static CheckpointSnapshot? PendingRecall { get; private set; }
    internal static string ActiveExpeditionKey { get; private set; } = string.Empty;
    internal static bool ResumeSelected { get; private set; }
    internal static string? LastSelectionError { get; private set; }

    internal static bool HasResumeChoice => ActiveSnapshot != null && !string.IsNullOrEmpty(ActiveExpeditionKey);
    internal static bool ShouldShowControl => HasResumeChoice || LastSelectionError != null;

    internal static void OnSelectingExpedition(ref pActiveExpedition package, ExpeditionInTierData expedition)
    {
        if (_applyingPackage || !SNet.IsMaster || expedition == null)
            return;

        ResetSelection();
        var key = GetExpeditionKey(package);
        ActiveExpeditionKey = key;
        _freshPackage = ClonePackage(package);
        _hasFreshPackage = true;

        var load = CheckpointStore.TryLoad(key);
        if (!load.Success)
        {
            LastSelectionError = load.Error;
            if (load.Error != null)
                Log.Warning($"Checkpoint for {key} is unavailable: {load.Error}");
            return;
        }

        var snapshot = load.Snapshot!;
        if (!ValidateBeforeGeneration(snapshot, package, expedition, out var reason))
        {
            LastSelectionError = reason;
            Log.Warning($"Checkpoint for {key} is incompatible: {reason}");
            return;
        }

        ActiveSnapshot = snapshot;
        ResumeSelected = true;
        ApplySeeds(ref package, snapshot);
        Log.Info($"Selected persistent checkpoint for {key}; session seed {snapshot.SessionSeed}.");
    }

    internal static void ToggleResume()
    {
        if (!SNet.IsMaster || ActiveSnapshot == null || !_hasFreshPackage)
            return;

        var requestedState = !ResumeSelected;
        var package = requestedState ? RundownManager.GetActiveExpeditionData() : _freshPackage;
        if (requestedState)
            ApplySeeds(ref package, ActiveSnapshot);

        if (!ApplyActivePackage(package))
            return;

        ResumeSelected = requestedState;
        Log.Info($"Resume checkpoint for {ActiveExpeditionKey}: {(requestedState ? "enabled" : "disabled")}.");
    }

    internal static void OnStartLevelResult(bool started)
    {
        if (!started || !SNet.IsMaster || string.IsNullOrEmpty(ActiveExpeditionKey))
            return;

        if (ResumeSelected && ActiveSnapshot != null)
        {
            PendingRecall = ActiveSnapshot;
            Log.Info($"Expedition launch will recall checkpoint for {ActiveExpeditionKey}.");
            return;
        }

        CheckpointStore.EnqueueDelete(ActiveExpeditionKey);
        ActiveSnapshot = null;
        Log.Info($"Fresh expedition selected; deleted the old checkpoint for {ActiveExpeditionKey}.");
    }

    internal static void CaptureCheckpoint()
    {
        if (_suppressCheckpointSave || !SNet.IsMaster)
            return;

        try
        {
            var package = RundownManager.GetActiveExpeditionData();
            var key = GetExpeditionKey(package);
            var buffer = SNet.Capture.m_buffers[(int)eBufferType.Checkpoint];
            if (buffer == null || !buffer.isValid)
                return;

            var passes = new List<List<byte[]>>(SNet_CaptureBuffer.PassCount);
            for (var passIndex = 0; passIndex < SNet_CaptureBuffer.PassCount; passIndex++)
            {
                var sourcePass = buffer.m_passes[passIndex];
                var pass = new List<byte[]>(sourcePass.Count);
                for (var packetIndex = 0; packetIndex < sourcePass.Count; packetIndex++)
                    pass.Add(CopyBytes(sourcePass[packetIndex]));
                passes.Add(pass);
            }

            var players = new List<CapturedPlayer>();
            var requiredCapacity = _requiredSlotCapacity;
            foreach (var player in SNet.Slots.SlottedPlayers)
            {
                if (player == null)
                    continue;
                players.Add(new CapturedPlayer(player.Lookup, player.CharacterIndex, player.IsBot));
                requiredCapacity = Math.Max(requiredCapacity, player.CharacterIndex + 1);
            }

            var snapshot = new CheckpointSnapshot
            {
                BuildRevision = CellBuildData.GetRevision(),
                ExpeditionKey = key,
                RundownKey = package.rundownKey.data,
                Tier = (int)package.tier,
                ExpeditionIndex = package.expeditionIndex,
                HostSeed = package.hostIDSeed,
                SessionSeed = package.sessionSeed,
                LevelChecksum = buffer.data.levelChecksum,
                ProgressionTime = buffer.data.progressionTime,
                RecallCount = buffer.data.recallCount,
                SavedAtUtcTicks = DateTime.UtcNow.Ticks,
                RequiredSlotCapacity = requiredCapacity,
                LobbyExpansionPresent = LobbyCompatibility.IsPresent,
                LobbyExpansionVersion = LobbyCompatibility.Version,
                Players = players,
                Passes = passes
            };

            ActiveExpeditionKey = key;
            ActiveSnapshot = snapshot;
            ResumeSelected = true;
            CheckpointStore.EnqueueSave(snapshot);
        }
        catch (Exception exception)
        {
            Log.Error(exception);
        }
    }

    internal static bool TryRecallPending()
    {
        var snapshot = PendingRecall;
        if (snapshot == null || !SNet.IsMaster || GameStateManager.CurrentStateName != eGameStateName.InLevel)
            return false;
        if (SNet.Capture.IsCapturing || SNet.Capture.IsRecalling || CheckpointManager.Current == null)
            return false;
        if (!GameStateManager.IsEveryoneInState(eGameStateName.InLevel, includeMaster: true, skipPlayersInLobbyGameState: true))
            return false;

        PendingRecall = null;
        RestoreAndRecall(snapshot);
        return true;
    }

    internal static void UpdatePlayerResync()
    {
        if (!SNet.IsMaster || GameStateManager.CurrentStateName != eGameStateName.InLevel)
        {
            ClearPlayerResyncs();
            return;
        }

        if (_activePlayerResync != null)
        {
            var active = _activePlayerResync;
            var player = FindSlottedPlayer(active.Lookup);
            if (player == null || !player.IsInSessionHub)
            {
                Log.Warning($"Skipping checkpoint hard-sync for {active.DisplayName} ({active.Lookup}): player disconnected.");
                _activePlayerResync = null;
                _nextPlayerResyncTime = Time.realtimeSinceStartup + PlayerResyncDelaySeconds;
            }
            else if (player.SyncProcessState == eSyncProcessState.ProcessDone
                     && player.Session.mode == eReplicationMode.Playing
                     && player.HasPlayerAgent)
            {
                Log.Info(
                    $"Checkpoint hard-sync completed for {player.NickName} ({player.Lookup}); " +
                    $"agent key {GetPlayerAgentKey(player)}.");
                _activePlayerResync = null;
                _nextPlayerResyncTime = Time.realtimeSinceStartup + PlayerResyncDelaySeconds;
            }
            else if (Time.realtimeSinceStartup >= _activePlayerResyncDeadline)
            {
                var message =
                    $"Checkpoint hard-sync timed out for {player.NickName} ({player.Lookup}). " +
                    "That player should leave and rejoin.";
                Log.Error(message);
                ShowHostWarning($"CHECKPOINT SYNC FAILED: {player.NickName}\nLEAVE AND REJOIN");
                ClearPlayerResyncs();
                return;
            }
        }

        if (_activePlayerResync != null || Time.realtimeSinceStartup < _nextPlayerResyncTime)
            return;

        while (PlayerResyncQueue.Count > 0)
        {
            var pending = PlayerResyncQueue.Dequeue();
            var player = FindSlottedPlayer(pending.Lookup);
            if (player == null || !player.IsInSessionHub)
            {
                Log.Warning($"Skipping checkpoint hard-sync for {pending.DisplayName} ({pending.Lookup}): player disconnected.");
                continue;
            }

            try
            {
                StartPlayerHardSync(player);
                _activePlayerResync = pending;
                _activePlayerResyncDeadline = Time.realtimeSinceStartup + PlayerResyncTimeoutSeconds;
                Log.Info(
                    $"Started checkpoint hard-sync for {player.NickName} ({player.Lookup}); " +
                    $"agent key {GetPlayerAgentKey(player)}.");
            }
            catch (Exception exception)
            {
                Log.Error($"Failed to start checkpoint hard-sync for {player.NickName} ({player.Lookup}): {exception}");
                ShowHostWarning($"CHECKPOINT SYNC FAILED: {player.NickName}\nLEAVE AND REJOIN");
                ClearPlayerResyncs();
            }
            return;
        }
    }

    internal static void ClearOnSuccess()
    {
        if (!SNet.IsMaster || string.IsNullOrEmpty(ActiveExpeditionKey))
            return;

        PendingRecall = null;
        ActiveSnapshot = null;
        ResumeSelected = false;
        ClearPlayerResyncs();
        CheckpointStore.EnqueueDelete(ActiveExpeditionKey);
        Log.Info($"Expedition succeeded; deleted checkpoint for {ActiveExpeditionKey}.");
    }

    internal static string GetButtonText()
    {
        if (ActiveSnapshot == null)
            return $"<color=#FF8050>CHECKPOINT UNAVAILABLE</color>  <size=55%>{LastSelectionError}</size>";

        var saved = new DateTime(ActiveSnapshot.SavedAtUtcTicks, DateTimeKind.Utc).ToLocalTime();
        return $"[{(ResumeSelected ? "X" : " ")}] RESUME CHECKPOINT  <size=65%>{saved:g}</size>";
    }

    internal static void ObserveCurrentSlots()
    {
        if (!SNet.HasLocalPlayer)
            return;

        try
        {
            foreach (var player in SNet.Slots.SlottedPlayers)
            {
                if (player != null)
                    _requiredSlotCapacity = Math.Max(_requiredSlotCapacity, player.CharacterIndex + 1);
            }
        }
        catch
        {
            // Slot managers are briefly unavailable while sessions reset.
        }
    }

    private static void RestoreAndRecall(CheckpointSnapshot snapshot)
    {
        SNet_CaptureBuffer? buffer = null;
        try
        {
            var currentChecksum = (ulong)SNet.LocalPlayer.Session.levelChecksum;
            if (currentChecksum != snapshot.LevelChecksum)
                throw new InvalidOperationException($"Generated level checksum {currentChecksum} does not match checkpoint {snapshot.LevelChecksum}.");
            if (!LobbyCompatibility.CanRepresent(snapshot, out var capacityReason))
                throw new InvalidOperationException(capacityReason);
            if (snapshot.Passes.Count != SNet_CaptureBuffer.PassCount)
                throw new InvalidOperationException($"Checkpoint has {snapshot.Passes.Count} capture passes, game expects {SNet_CaptureBuffer.PassCount}.");

            var connectedHumans = CaptureConnectedHumans(snapshot);

            _suppressCheckpointSave = true;
            SNet.Capture.CaptureGameState(eBufferType.Checkpoint);
            buffer = SNet.Capture.m_buffers[(int)eBufferType.Checkpoint];
            var freshBufferId = buffer.data.bufferID;

            for (var passIndex = 0; passIndex < snapshot.Passes.Count; passIndex++)
            {
                var targetPass = buffer.m_passes[passIndex];
                targetPass.Clear();
                foreach (var packet in snapshot.Passes[passIndex])
                    targetPass.Add(new Il2CppStructArray<byte>(packet));
            }

            var data = buffer.data;
            data.sendingPlayerLookup = SNet.LocalPlayer.Lookup;
            data.levelChecksum = snapshot.LevelChecksum;
            data.bufferID = freshBufferId;
            data.progressionTime = snapshot.ProgressionTime;
            data.recallCount = snapshot.RecallCount;
            buffer.data = data;
            buffer.isValid = true;

            CheckpointManager.ReloadCheckpoint();
            SNet.Capture.InitCheckpointRecall();
            SNet.Capture.RecallGameState(eBufferType.Checkpoint);

            if (Math.Abs(Clock.ExpeditionProgressionTime - snapshot.ProgressionTime) > 0.01f)
                throw new InvalidOperationException(
                    $"Checkpoint recall returned without restoring progression time " +
                    $"{snapshot.ProgressionTime:F2} (current {Clock.ExpeditionProgressionTime:F2}).");

            Log.Info(
                $"Recalled persistent checkpoint for {snapshot.ExpeditionKey} at " +
                $"{Clock.ExpeditionProgressionTime:F2} seconds.");
            LogPlayerResources("Restored checkpoint resources", connectedHumans);
            QueuePlayerResyncs(connectedHumans);
        }
        catch (Exception exception)
        {
            buffer?.Clear();
            Log.Error($"Persistent checkpoint recall failed: {exception}");
        }
        finally
        {
            _suppressCheckpointSave = false;
        }
    }

    private static List<PlayerResync> CaptureConnectedHumans(CheckpointSnapshot snapshot)
    {
        var savedHumans = snapshot.Players
            .Where(player => !player.IsBot)
            .Select(player => player.Lookup)
            .ToHashSet();
        var connected = new List<PlayerResync>();

        foreach (var player in SNet.Slots.SlottedPlayers)
        {
            if (player == null || player.IsMaster || player.IsBot || !player.IsInSessionHub)
                continue;

            connected.Add(new PlayerResync(player.Lookup, player.NickName));
            Log.Info(
                $"Checkpoint player mapping: {player.NickName} ({player.Lookup}), " +
                $"character {player.CharacterIndex}, saved state {(savedHumans.Contains(player.Lookup) ? "found" : "not found; vanilla defaults will apply")}, " +
                $"fresh agent key {GetPlayerAgentKey(player)}.");
        }

        return connected;
    }

    private static void QueuePlayerResyncs(IEnumerable<PlayerResync> players)
    {
        ClearPlayerResyncs();
        foreach (var player in players)
            PlayerResyncQueue.Enqueue(player);

        if (PlayerResyncQueue.Count == 0)
            return;

        // Start on this same Unity update, before a connected client's fresh-lobby
        // resource packets can overwrite the state restored on the host.
        _nextPlayerResyncTime = Time.realtimeSinceStartup;
        Log.Info($"Queued {PlayerResyncQueue.Count} connected human player(s) for fresh DropIn hard-sync.");
    }

    private static void StartPlayerHardSync(SNet_Player player)
    {
        _startSinglePlayerRecall ??= typeof(SNet_SyncManager)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "StartRecall"
                    && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(SNet_Player);
            })
            ?? throw new MissingMethodException(typeof(SNet_SyncManager).FullName, "StartRecall(SNet_Player)");

        Log.Info($"DropIn capture resources for {player.NickName} ({player.Lookup}): {GetPlayerResourceSummary(player)}.");
        player.SyncProcessState = eSyncProcessState.InProcess_HardSync;
        try
        {
            _startSinglePlayerRecall.Invoke(SNet.Sync, new object?[] { player });
        }
        catch
        {
            player.SyncProcessState = eSyncProcessState.ProcessDone;
            throw;
        }
    }

    private static SNet_Player? FindSlottedPlayer(ulong lookup)
    {
        foreach (var player in SNet.Slots.SlottedPlayers)
        {
            if (player != null && player.Lookup == lookup)
                return player;
        }
        return null;
    }

    private static string GetPlayerAgentKey(SNet_Player player)
    {
        try
        {
            if (player.HasPlayerAgent)
                return player.PlayerAgent.GetReplicator().Key.ToString();
        }
        catch
        {
            // Agent ownership changes briefly during recall.
        }
        return "none";
    }

    private static void LogPlayerResources(string label, IEnumerable<PlayerResync> players)
    {
        foreach (var savedPlayer in players)
        {
            var player = FindSlottedPlayer(savedPlayer.Lookup);
            if (player != null)
                Log.Info($"{label} for {player.NickName} ({player.Lookup}): {GetPlayerResourceSummary(player)}.");
        }
    }

    private static string GetPlayerResourceSummary(SNet_Player player)
    {
        try
        {
            if (!PlayerBackpackManager.TryGetBackpack(player, out var backpack))
                return "backpack unavailable";

            var ammo = backpack.AmmoStorage.GetStorageDataForCapture();
            return
                $"standard={ammo.standardAmmo.Get(500f):F1}, " +
                $"special={ammo.specialAmmo.Get(500f):F1}, " +
                $"tool={ammo.classAmmo.Get(500f):F1}, " +
                $"resourcePack={ammo.resourcePackAmmo.Get(500f):F1}, " +
                $"consumable={ammo.consumableAmmo.Get(500f):F1}";
        }
        catch (Exception exception)
        {
            return $"unavailable ({exception.Message})";
        }
    }

    private static void ShowHostWarning(string message)
    {
        try
        {
            if (GuiManager.PlayerLayer != null && GuiManager.PlayerLayer.m_wardenIntel != null)
                GuiManager.PlayerLayer.m_wardenIntel.ShowPrioritySubObjectiveMessage("", message, isAdditional: true);
        }
        catch (Exception exception)
        {
            Log.Warning($"Could not display checkpoint sync warning: {exception.Message}");
        }
    }

    private static void ClearPlayerResyncs()
    {
        PlayerResyncQueue.Clear();
        _activePlayerResync = null;
        _activePlayerResyncDeadline = 0f;
        _nextPlayerResyncTime = 0f;
    }

    private static bool ValidateBeforeGeneration(CheckpointSnapshot snapshot, pActiveExpedition package, ExpeditionInTierData expedition, out string reason)
    {
        if (snapshot.BuildRevision != CellBuildData.GetRevision())
        {
            reason = $"saved build {snapshot.BuildRevision}, current build {CellBuildData.GetRevision()}";
            return false;
        }
        if (snapshot.HostSeed != package.hostIDSeed)
        {
            reason = "checkpoint belongs to a different host";
            return false;
        }
        if (!LobbyCompatibility.CanRepresent(snapshot, out reason))
            return false;

        expedition.GetSeeds(out var originalHostSeed, out var originalSessionSeed);
        try
        {
            expedition.SetSeeds(snapshot.HostSeed, snapshot.SessionSeed);
            var expectedChecksum = (ulong)expedition.GetChecksum();
            if (expectedChecksum != snapshot.LevelChecksum)
            {
                reason = $"saved level checksum {snapshot.LevelChecksum}, current data produces {expectedChecksum}";
                return false;
            }
        }
        finally
        {
            expedition.SetSeeds(originalHostSeed, originalSessionSeed);
        }

        reason = string.Empty;
        return true;
    }

    private static void ApplySeeds(ref pActiveExpedition package, CheckpointSnapshot snapshot)
    {
        package.hostIDSeed = snapshot.HostSeed;
        package.sessionSeed = snapshot.SessionSeed;
    }

    private static pActiveExpedition ClonePackage(pActiveExpedition source)
    {
        return new pActiveExpedition
        {
            PlayerData = source.PlayerData,
            rundownType = source.rundownType,
            rundownKey = source.rundownKey,
            tier = source.tier,
            expeditionIndex = source.expeditionIndex,
            hostIDSeed = source.hostIDSeed,
            sessionSeed = source.sessionSeed,
            sessionGUID = source.sessionGUID
        };
    }

    private static bool ApplyActivePackage(pActiveExpedition package)
    {
        try
        {
            _setActiveExpedition ??= typeof(RundownManager)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "SetActiveExpedition")
                .Where(method =>
                {
                    var candidateParameters = method.GetParameters();
                    return candidateParameters.Length is 2 or 3
                        && candidateParameters[0].ParameterType == typeof(pActiveExpedition)
                        && candidateParameters[1].ParameterType == typeof(ExpeditionInTierData)
                        && (candidateParameters.Length == 2 || candidateParameters[2].ParameterType == typeof(bool));
                })
                .OrderByDescending(method => method.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new MissingMethodException(
                    typeof(RundownManager).FullName,
                    "SetActiveExpedition(pActiveExpedition, ExpeditionInTierData[, bool])");

            var parameters = _setActiveExpedition.GetParameters();
            var arguments = parameters.Length == 3
                ? new object?[] { package, RundownManager.ActiveExpedition, true }
                : new object?[] { package, RundownManager.ActiveExpedition };

            _applyingPackage = true;
            _setActiveExpedition.Invoke(RundownManager.Current, arguments);
            return true;
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to switch expedition seed package: {exception}");
            return false;
        }
        finally
        {
            _applyingPackage = false;
        }
    }

    private static string GetExpeditionKey(pActiveExpedition package) =>
        $"{package.rundownKey.data}_{package.tier}_{package.expeditionIndex}";

    private static byte[] CopyBytes(Il2CppStructArray<byte> source)
    {
        var copy = new byte[source.Length];
        for (var i = 0; i < source.Length; i++)
            copy[i] = source[i];
        return copy;
    }

    private static void ResetSelection()
    {
        ActiveSnapshot = null;
        PendingRecall = null;
        ActiveExpeditionKey = string.Empty;
        ResumeSelected = false;
        LastSelectionError = null;
        _hasFreshPackage = false;
        _requiredSlotCapacity = 4;
        ClearPlayerResyncs();
    }

    private sealed record PlayerResync(ulong Lookup, string DisplayName);
}
