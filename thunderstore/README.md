# Persistent Checkpoint

Quit for the night and continue your expedition another day.

Persistent Checkpoint saves GTFO's checkpoints between game sessions. Reach a normal checkpoint or create one wherever you need it, then return later without starting the expedition over.

**Host-only:** only the host needs this mod. Other players do not need it installed.

## Usage

### Saving

Normal expedition checkpoints are saved automatically.

To create your own checkpoint, open the **Loadout** page during an expedition and hold **CREATE CHECKPOINT**. Each expedition has its own save; creating a checkpoint replaces that expedition's previous one.

![Create checkpoint button](https://i.imgur.com/fPcCssp.png)

You can then retire to the lobby or close the game. Your checkpoint is kept.

### Resuming

1. Host the same expedition again, using the same host as before.
2. Leave **RESUME CHECKPOINT** checked on the loadout page. It appears automatically when a compatible save is available.
3. Drop into the expedition normally. After everyone enters the level, a Warden message announces restoration. Wait for the restore and player synchronization to finish before continuing.

![Resume checkpoint checkbox](https://i.imgur.com/KuZUk3v.png)

Returning players resume with their saved position, health, ammunition and inventory. Players who were not present at the checkpoint use the game's normal reconstruction defaults.

To start over, uncheck **RESUME CHECKPOINT** before dropping. **Starting fresh deletes that expedition's save.** Completing the expedition also clears it; failing, retiring or quitting does not.

## Dependencies

- Required: [BepInExPack GTFO](https://thunderstore.io/c/gtfo/p/BepInEx/BepInExPack_GTFO/) (`BepInEx-BepInExPack_GTFO-3.2.3`). Installed automatically by your mod manager.
- Optional: [LobbyExpansion](https://thunderstore.io/c/gtfo/p/Cactus/LobbyExpansion/) for more than four players. Expanded lobbies require LobbyExpansion on every player's installation; Persistent Checkpoint itself is still host-only.

## Compatibility and testing

Supports vanilla and modded rundowns, both with and without LobbyExpansion.

**Extensively tested in live sessions with 5–8 players, primarily in Samdown.**

When resuming, keep the same rundown and compatible mod setup. A checkpoint made with expanded player slots needs a lobby with at least that slot capacity. Game or rundown updates can invalidate existing saves.

## Known issues

- This uses GTFO's native checkpoint system. Custom mods may have additional state that it cannot preserve, and normal checkpoint limitations still apply.
- Locker/container interaction inconsistencies have occasionally been reported after restoring a checkpoint.
- If a player fails to synchronize, the host receives a message asking that player to leave and late-join again. For bug reports, include the host's `BepInEx/LogOutput.log` and your rundown/mod list.

## Source

[Source code on GitHub](https://github.com/TheRoughy/GTFO_persistentCheckpoint).
