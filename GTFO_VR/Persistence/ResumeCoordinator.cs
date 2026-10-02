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
    private static bool _pendingRecallDelayStarted;
    private static float _pendingRecallNotBefore;
    private static bool _pendingDoorPreparationStarted;
    private static float _pendingDoorPreparationNotBefore;
    private static readonly Queue<PlayerResync> PlayerResyncQueue = new();
    private static PlayerResync? _activePlayerResync;
    private static CheckpointSnapshot? _playerResyncSnapshot;
    private static float _activePlayerResyncDeadline;
    private static float _nextPlayerResyncTime;
    private static bool _activePlayerResyncSawNativeSync;
    private static bool _activePlayerResyncTimedOut;
    private static CheckpointSnapshot? _bridgeSnapshot;
    private static float _bridgeNotBefore;
    private static float _bridgeDeadline;

    private const float PendingRecallDelaySeconds = 3f;
    private const float DoorPreparationDelaySeconds = 1f;
    private const float InitialPlayerResyncDelaySeconds = 1.5f;
    private const float PlayerResyncDelaySeconds = 0.25f;
    private const float PlayerResyncTimeoutSeconds = 20f;
    private const float BridgeStabilizationSeconds = 2f;
    private const float BridgeReadyTimeoutSeconds = 20f;

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
            ResetPendingRecallDelay();
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
        if (_suppressCheckpointSave || _pendingDoorPreparationStarted || !SNet.IsMaster)
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
                players.Add(CapturePlayerState(player));
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
        if (snapshot == null || !SNet.IsMaster)
        {
            ResetPendingRecallDelay();
            return false;
        }
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel)
        {
            if (_pendingDoorPreparationStarted)
            {
                PendingRecall = null;
                ResetPendingRecallDelay();
                Log.Warning("Cancelled pending checkpoint recall after leaving InLevel during door preparation.");
            }
            return false;
        }
        if (SNet.Capture.IsCapturing || SNet.Capture.IsRecalling || CheckpointManager.Current == null)
            return false;

        if (!_pendingRecallDelayStarted)
        {
            if (!GameStateManager.IsEveryoneInState(eGameStateName.InLevel, includeMaster: true, skipPlayersInLobbyGameState: true))
                return false;

            _pendingRecallDelayStarted = true;
            _pendingRecallNotBefore = Time.realtimeSinceStartup + PendingRecallDelaySeconds;
            ShowHostWarning("TEMPORAL INSTABILITY DETECTED\nRESTORING TIMELINE...\nEL PSY KONGROO");
            Log.Info(
                $"All players reached InLevel; persistent checkpoint recall will begin in " +
                $"{PendingRecallDelaySeconds:F1} seconds.");
            return false;
        }

        if (Time.realtimeSinceStartup < _pendingRecallNotBefore)
            return false;

        if (!_pendingDoorPreparationStarted)
        {
            try
            {
                // Validate before any destructive preparation, not just before recall.
                ValidateGeneratedLevel(snapshot);
                // A destruction event can trigger other mods' checkpoint captures.
                // Keep those transient captures from replacing the persistent save.
                _pendingDoorPreparationStarted = true;
                var prepared = DestroyedDoorPreparation.Prepare(snapshot);
                _pendingDoorPreparationNotBefore = Time.realtimeSinceStartup
                    + (prepared > 0 ? DoorPreparationDelaySeconds : 0f);
                Log.Info($"Prepared {prepared} destroyed door(s) before persistent checkpoint recall; callback settling delay: {(prepared > 0 ? DoorPreparationDelaySeconds : 0f):F1} seconds.");
                if (prepared > 0)
                    return false;
            }
            catch (Exception exception)
            {
                PendingRecall = null;
                ResetPendingRecallDelay();
                Log.Error($"Persistent checkpoint door preparation failed: {exception}");
                return false;
            }
        }

        if (Time.realtimeSinceStartup < _pendingDoorPreparationNotBefore)
            return false;

        PendingRecall = null;
        ResetPendingRecallDelay();
        RestoreAndRecall(snapshot);
        return true;
    }

    internal static void OnPlayerEvent(SNet_Player player, SNet_PlayerEvent playerEvent)
    {
        if (playerEvent != SNet_PlayerEvent.PlayerIsSynced || _activePlayerResync == null)
            return;
        if (player == null || player.Lookup != _activePlayerResync.Lookup)
            return;

        _activePlayerResyncSawNativeSync = true;
        Log.Info(
            $"GTFO reported PlayerIsSynced for checkpoint hard-sync target " +
            $"{player.NickName} ({player.Lookup}); {GetPlayerSyncSummary(player)}.");
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
                FinishActivePlayerResync();
            }
            else if (IsPlayerPlayingWithAgent(player)
                     && (_activePlayerResyncSawNativeSync
                         || player.SyncProcessState == eSyncProcessState.ProcessDone))
            {
                Log.Info(
                    $"Checkpoint hard-sync completed for {player.NickName} ({player.Lookup}); " +
                    $"{GetPlayerSyncSummary(player)}.");
                FinishActivePlayerResync();
            }
            else if (!_activePlayerResyncTimedOut && Time.realtimeSinceStartup >= _activePlayerResyncDeadline)
            {
                if (IsPlayerPlayingWithAgent(player))
                {
                    Log.Warning(
                        $"Checkpoint hard-sync completion signals were stale or missed for " +
                        $"{player.NickName} ({player.Lookup}); accepting Playing with a valid agent " +
                        $"after {PlayerResyncTimeoutSeconds:F0} seconds. {GetPlayerSyncSummary(player)}.");
                    FinishActivePlayerResync();
                    return;
                }

                Log.Error(
                    $"Checkpoint hard-sync timed out for {player.NickName} ({player.Lookup}). " +
                    $"Remaining queued players: {PlayerResyncQueue.Count}. {GetPlayerSyncSummary(player)}. " +
                    "The canonical checkpoint will not be created; that player should leave and rejoin.");
                ShowHostWarning($"CHECKPOINT SYNC FAILED: {player.NickName}\nLEAVE AND REJOIN");
                _activePlayerResyncTimedOut = true;
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
                _activePlayerResync = pending;
                _activePlayerResyncDeadline = Time.realtimeSinceStartup + PlayerResyncTimeoutSeconds;
                _activePlayerResyncSawNativeSync = false;
                _activePlayerResyncTimedOut = false;
                StartPlayerHardSync(player);
                Log.Info(
                    $"Started checkpoint hard-sync for {player.NickName} ({player.Lookup}); " +
                    $"{GetPlayerSyncSummary(player)}.");
            }
            catch (Exception exception)
            {
                Log.Error($"Failed to start checkpoint hard-sync for {player.NickName} ({player.Lookup}): {exception}");
                ShowHostWarning($"CHECKPOINT SYNC FAILED: {player.NickName}\nLEAVE AND REJOIN");
                ClearPlayerResyncs();
            }
            return;
        }

        if (_playerResyncSnapshot != null)
        {
            var snapshot = _playerResyncSnapshot;
            ClearPlayerResyncs();
            BeginBridgeRecall(snapshot);
        }
    }

    internal static void UpdateBridgeRecall()
    {
        var snapshot = _bridgeSnapshot;
        if (snapshot == null)
            return;

        if (!SNet.IsMaster || GameStateManager.CurrentStateName != eGameStateName.InLevel)
        {
            ClearBridgeRecall();
            return;
        }
        if (Time.realtimeSinceStartup < _bridgeNotBefore
            || SNet.Capture.IsCapturing
            || SNet.Capture.IsRecalling)
            return;

        var unreadyPlayers = GetUnreadyConnectedPlayers();
        if (unreadyPlayers.Count > 0 && Time.realtimeSinceStartup < _bridgeDeadline)
            return;

        if (unreadyPlayers.Count > 0)
        {
            var names = string.Join(", ", unreadyPlayers.Select(player => player.NickName));
            Log.Error(
                $"Timed out waiting for checkpoint bridge players after {BridgeReadyTimeoutSeconds:F0} seconds: {names}. " +
                "The canonical checkpoint recall was cancelled to avoid splitting the session again.");
            ShowHostWarning($"CHECKPOINT SYNC INCOMPLETE: {names}\nLEAVE AND REJOIN");
            ClearBridgeRecall();
            return;
        }

        try
        {
            ApplySavedPlayerResources(snapshot);
            LogCurrentPlayerResources("Checkpoint bridge resources");

            // The sequential DropIn recalls have now rebuilt every connected client
            // against the host's restored world. Capture that healthy current-session
            // state, then let GTFO distribute and recall it as a normal checkpoint.
            // This is a transient bridge checkpoint: do not replace the persistent
            // checkpoint the player actually reached during play.
            var wasSuppressingCheckpointSave = _suppressCheckpointSave;
            try
            {
                _suppressCheckpointSave = true;
                SNet.Capture.CaptureGameState(eBufferType.Checkpoint);
            }
            finally
            {
                _suppressCheckpointSave = wasSuppressingCheckpointSave;
            }

            var canonical = SNet.Capture.m_buffers[(int)eBufferType.Checkpoint];
            if (canonical == null || !canonical.isValid)
                throw new InvalidOperationException("canonical checkpoint capture was invalid");

            CheckpointManager.ReloadCheckpoint();
            if (!SNet.Sync.SessionCommand(eSessionCommandType.ForceStartPlaying))
                Log.Warning("GTFO rejected ForceStartPlaying before canonical checkpoint recall; continuing with recall.");
            StartCanonicalAllPlayerRecall();
            Log.Info(
                $"Captured and recalled canonical checkpoint for {snapshot.ExpeditionKey} " +
                "with GTFO's explicit all-player checkpoint protocol.");
        }
        catch (Exception exception)
        {
            Log.Error($"Canonical checkpoint bridge failed: {exception}");
            ShowHostWarning("CHECKPOINT SYNC FAILED\nLEAVE AND REJOIN");
        }
        finally
        {
            ClearBridgeRecall();
        }
    }

    internal static void ClearOnSuccess()
    {
        if (!SNet.IsMaster || string.IsNullOrEmpty(ActiveExpeditionKey))
            return;

        PendingRecall = null;
        ResetPendingRecallDelay();
        ActiveSnapshot = null;
        ResumeSelected = false;
        ClearPlayerResyncs();
        ClearBridgeRecall();
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
            ValidateGeneratedLevel(snapshot);

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
            QueuePlayerResyncs(connectedHumans, snapshot);
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

    private static void ValidateGeneratedLevel(CheckpointSnapshot snapshot)
    {
        var currentChecksum = (ulong)SNet.LocalPlayer.Session.levelChecksum;
        if (currentChecksum != snapshot.LevelChecksum)
            throw new InvalidOperationException($"Generated level checksum {currentChecksum} does not match checkpoint {snapshot.LevelChecksum}.");
        if (!LobbyCompatibility.CanRepresent(snapshot, out var capacityReason))
            throw new InvalidOperationException(capacityReason);
        if (snapshot.Passes.Count != SNet_CaptureBuffer.PassCount)
            throw new InvalidOperationException($"Checkpoint has {snapshot.Passes.Count} capture passes, game expects {SNet_CaptureBuffer.PassCount}.");
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

    private static void QueuePlayerResyncs(IEnumerable<PlayerResync> players, CheckpointSnapshot snapshot)
    {
        ClearPlayerResyncs();
        _playerResyncSnapshot = snapshot;
        foreach (var player in players)
            PlayerResyncQueue.Enqueue(player);

        if (PlayerResyncQueue.Count == 0)
        {
            var bridgeSnapshot = _playerResyncSnapshot;
            ClearPlayerResyncs();
            BeginBridgeRecall(bridgeSnapshot!);
            return;
        }

        _nextPlayerResyncTime = Time.realtimeSinceStartup + InitialPlayerResyncDelaySeconds;
        Log.Info(
            $"Queued {PlayerResyncQueue.Count} connected human player(s) for fresh DropIn hard-sync; " +
            $"the first sync will begin in {InitialPlayerResyncDelaySeconds:F1} seconds.");
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

    private static void StartCanonicalAllPlayerRecall()
    {
        // StartRecallCheckpointWithAllSyncedPlayers is inert in the current game
        // build while the session is already InLevel. Perform the operations from
        // GTFO's implementation explicitly so the host and every synced client
        // receive and recall the same freshly captured buffer.
        SNet.Capture.InitCheckpointRecall();

        var remotePlayerCount = 0;
        foreach (var player in SNet.Slots.PlayersSynchedWithGame)
        {
            if (player == null || player.IsMaster)
                continue;

            SNet.Sync.SendPlayerData_Session_SyncAllTo(player);
            remotePlayerCount++;
        }

        SNet.Capture.SendBuffer(eBufferType.Checkpoint);
        SNet.Capture.SendBufferCommand(
            eBufferType.Checkpoint,
            eBufferOperationType.RecallGameState,
            null,
            0);
        SNet.Capture.RecallGameState(eBufferType.Checkpoint);
        Log.Info($"Explicit canonical checkpoint recall sent to {remotePlayerCount} synced remote player(s).");
    }

    private static void BeginBridgeRecall(CheckpointSnapshot snapshot)
    {
        _bridgeSnapshot = snapshot;
        _bridgeNotBefore = Time.realtimeSinceStartup + BridgeStabilizationSeconds;
        _bridgeDeadline = Time.realtimeSinceStartup + BridgeReadyTimeoutSeconds;
        Log.Info(
            $"All checkpoint hard-syncs completed; canonical all-player recall will begin after " +
            $"at least {BridgeStabilizationSeconds:F1} seconds once current players are capture-ready.");
    }

    private static List<SNet_Player> GetUnreadyConnectedPlayers()
    {
        var unready = new List<SNet_Player>();
        foreach (var player in SNet.Slots.SlottedPlayers)
        {
            if (player == null || (!player.IsBot && !player.IsInSessionHub))
                continue;

            try
            {
                var playing = player.IsBot || player.Session.mode == eReplicationMode.Playing;
                if (!playing
                    || player.IsOutOfSync
                    || !player.HasPlayerAgent
                    || !PlayerBackpackManager.TryGetBackpack(player, out _))
                    unready.Add(player);
            }
            catch
            {
                unready.Add(player);
            }
        }

        return unready;
    }

    private static void ApplySavedPlayerResources(CheckpointSnapshot snapshot)
    {
        foreach (var saved in snapshot.Players)
        {
            if (!saved.HasResources)
                continue;

            var player = FindSlottedPlayer(saved.Lookup);
            if (player == null || !player.HasPlayerAgent
                || !PlayerBackpackManager.TryGetBackpack(player, out var backpack))
            {
                Log.Warning($"Could not apply saved resources for player {saved.Lookup}: current agent or backpack unavailable.");
                continue;
            }

            try
            {
                var agent = player.PlayerAgent.TryCast<PlayerAgent>()
                    ?? throw new InvalidOperationException("player agent could not be cast to PlayerAgent");
                agent.Damage.Health = Mathf.Clamp01(saved.HealthRel) * agent.Damage.HealthMax;
                agent.Damage.Infection = Mathf.Clamp01(saved.Infection);

                var ammo = CreateAmmoDataForCapture(player, backpack, saved);
                backpack.AmmoStorage.SetStorageData(ref ammo);
                Log.Info(
                    $"Applied saved resources for {player.NickName} ({player.Lookup}): " +
                    $"health={saved.HealthRel:P0}, infection={saved.Infection:P0}, {GetPlayerResourceSummary(player)}.");
            }
            catch (Exception exception)
            {
                Log.Warning($"Could not apply saved resources for {player.NickName} ({player.Lookup}): {exception.Message}");
            }
        }
    }

    private static pAmmoStorageData CreateAmmoDataForCapture(
        SNet_Player player,
        PlayerBackpack backpack,
        CapturedPlayer saved)
    {
        var standard = saved.StandardAmmo;
        var special = saved.SpecialAmmo;

        // Host-local and bot captures add loaded magazines to AmmoInPack.
        // Remote human captures already treat the host's value as aggregate ammo.
        if (backpack.IsLocal || (SNet.IsMaster && player.IsBot))
        {
            standard -= backpack.AmmoStorage.GetClipAmmoFromSlot(InventorySlot.GearStandard)
                * backpack.AmmoStorage.StandardAmmo.CostOfBullet;
            special -= backpack.AmmoStorage.GetClipAmmoFromSlot(InventorySlot.GearSpecial)
                * backpack.AmmoStorage.SpecialAmmo.CostOfBullet;
        }

        var data = default(pAmmoStorageData);
        data.PlayerLookup = player.Lookup;
        data.standardAmmo.Set(Mathf.Max(0f, standard), 500f);
        data.specialAmmo.Set(Mathf.Max(0f, special), 500f);
        data.classAmmo.Set(Mathf.Max(0f, saved.ClassAmmo), 500f);
        data.resourcePackAmmo.Set(Mathf.Max(0f, saved.ResourcePackAmmo), 500f);
        data.consumableAmmo.Set(Mathf.Max(0f, saved.ConsumableAmmo), 500f);
        return data;
    }

    private static CapturedPlayer CapturePlayerState(SNet_Player player)
    {
        try
        {
            if (!player.HasPlayerAgent || !PlayerBackpackManager.TryGetBackpack(player, out var backpack))
                return new CapturedPlayer(player.Lookup, player.CharacterIndex, player.IsBot);

            var agent = player.PlayerAgent.TryCast<PlayerAgent>()
                ?? throw new InvalidOperationException("player agent could not be cast to PlayerAgent");
            var ammo = backpack.AmmoStorage.GetStorageDataForCapture();
            return new CapturedPlayer(
                player.Lookup,
                player.CharacterIndex,
                player.IsBot,
                true,
                agent.Damage.GetHealthRel(),
                agent.Damage.Infection,
                ammo.standardAmmo.Get(500f),
                ammo.specialAmmo.Get(500f),
                ammo.classAmmo.Get(500f),
                ammo.resourcePackAmmo.Get(500f),
                ammo.consumableAmmo.Get(500f));
        }
        catch (Exception exception)
        {
            Log.Warning($"Could not capture resources for {player.NickName} ({player.Lookup}): {exception.Message}");
            return new CapturedPlayer(player.Lookup, player.CharacterIndex, player.IsBot);
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

    private static string GetPlayerSyncSummary(SNet_Player player)
    {
        try
        {
            return
                $"mode={player.Session.mode}, processState={player.SyncProcessState}, " +
                $"hasAgent={player.HasPlayerAgent}, agent key={GetPlayerAgentKey(player)}, " +
                $"native PlayerIsSynced={_activePlayerResyncSawNativeSync}";
        }
        catch (Exception exception)
        {
            return $"sync state unavailable ({exception.Message}), native PlayerIsSynced={_activePlayerResyncSawNativeSync}";
        }
    }

    private static bool IsPlayerPlayingWithAgent(SNet_Player player)
    {
        try
        {
            return player.Session.mode == eReplicationMode.Playing && player.HasPlayerAgent;
        }
        catch
        {
            return false;
        }
    }

    private static void LogCurrentPlayerResources(string label)
    {
        foreach (var player in SNet.Slots.SlottedPlayers)
        {
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
            var playerLayer = GuiManager.PlayerLayer;
            if (playerLayer == null || playerLayer.m_wardenIntel == null)
            {
                Log.Warning("Could not display checkpoint warning: the player Warden HUD is unavailable.");
                return;
            }

            // The sub-objective APIs only enqueue their message. At the start of a
            // level that queue may not be serviced yet, and checkpoint recall resets
            // it in WardenObjectiveManager.OnPrepareForRecall. ShowWardenIntel drives
            // the HUD directly, so the warning is visible during the pre-recall delay.
            playerLayer.ShowWardenIntel(message, delay: 0f, duration: 8f);
        }
        catch (Exception exception)
        {
            Log.Warning($"Could not display checkpoint sync warning: {exception.Message}");
        }
    }

    private static void ClearBridgeRecall()
    {
        _bridgeSnapshot = null;
        _bridgeNotBefore = 0f;
        _bridgeDeadline = 0f;
    }

    private static void ClearPlayerResyncs()
    {
        PlayerResyncQueue.Clear();
        _activePlayerResync = null;
        _playerResyncSnapshot = null;
        _activePlayerResyncDeadline = 0f;
        _nextPlayerResyncTime = 0f;
        _activePlayerResyncSawNativeSync = false;
        _activePlayerResyncTimedOut = false;
    }

    private static void FinishActivePlayerResync()
    {
        _activePlayerResync = null;
        _activePlayerResyncDeadline = 0f;
        _activePlayerResyncSawNativeSync = false;
        _activePlayerResyncTimedOut = false;
        _nextPlayerResyncTime = Time.realtimeSinceStartup + PlayerResyncDelaySeconds;
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
        ResetPendingRecallDelay();
        ActiveExpeditionKey = string.Empty;
        ResumeSelected = false;
        LastSelectionError = null;
        _hasFreshPackage = false;
        _requiredSlotCapacity = 4;
        ClearPlayerResyncs();
        ClearBridgeRecall();
    }

    private static void ResetPendingRecallDelay()
    {
        _pendingRecallDelayStarted = false;
        _pendingRecallNotBefore = 0f;
        _pendingDoorPreparationStarted = false;
        _pendingDoorPreparationNotBefore = 0f;
    }

    private sealed record PlayerResync(ulong Lookup, string DisplayName);
}
