# GTFO Persistent Checkpoint

A host-side BepInEx IL2CPP plugin that preserves GTFO's latest checkpoint across game sessions.

## Behavior

- The host's latest native checkpoint is saved separately for each rundown expedition.
- When a compatible save exists, the loadout lobby shows a checked `RESUME CHECKPOINT` control beside the Drop button.
- Unchecking it and starting the expedition deletes the old save and starts fresh.
- Completing an expedition deletes its save. Failing, aborting, or quitting keeps it.
- Other players do not need this plugin; restored checkpoints are distributed through GTFO's normal checkpoint recall.

Checkpoint files are stored in:

```text
BepInEx/config/GTFO_PersistentCheckpoint/checkpoints/
```

Saves are tied to the GTFO build revision and expedition checksum. Incompatible or corrupt saves are not recalled.

## LobbyExpansion

`GTFO.LobbyExpansion` is an optional soft dependency. The plugin detects the current runtime character-slot capacity without referencing LobbyExpansion directly.

- Four-slot saves work in vanilla or expanded lobbies.
- Saves that captured expanded character slots require a lobby with at least that many slots.
- The resume control is created after LobbyExpansion's loadout patch and does not use its player-pillar UI.

## Build

Set `GTFO_PATH` to the GTFO installation and build `GTFO_VR/GTFO_PersistentCheckpoint.csproj` for Release. The output assembly is `GTFO_PersistentCheckpoint.dll`. Pass `-p:DeployPlugin=false` to build without copying it to the game's plugin directory.
