# GTFO Persistent Checkpoint

A host-side BepInEx IL2CPP plugin that preserves GTFO's latest checkpoint across game sessions.

Quit for the night and continue the same expedition later, or create your own checkpoint wherever you need one. Only the host needs this mod; other players can join without it.

## Installation

Install with a Thunderstore mod manager. The required [BepInEx pack for GTFO](https://thunderstore.io/c/gtfo/p/BepInEx/BepInExPack_GTFO/) is declared as a dependency and will be installed automatically.

For manual installation, install that BepInEx pack first, then copy `TheRoughy.PersistentCheckpoint.dll` from the ZIP into `BepInEx/plugins/TheRoughy-PersistentCheckpoint/` under the GTFO game directory. Create that folder if needed. When installed under the `TheRoughy` team, a Thunderstore mod manager creates the same author/package folder automatically. Restart the game after installing the plugin.

Do not run this alongside another mod that replaces or restores persistent checkpoint buffers, such as SaveAnywhere. That combination has not been tested.

## How to use

1. Host an expedition and reach a normal checkpoint, or open the loadout page during play and hold `CREATE CHECKPOINT`. This replaces that expedition's previous saved checkpoint.
2. You can now retire to the lobby or quit the game. Your latest checkpoint is retained.
3. Host the same expedition again. `RESUME CHECKPOINT` will be checked on the loadout page when a compatible save is available.
4. Drop normally. After everyone enters the level, a Warden message announces restoration. Wait for restoration and player synchronization to finish before continuing; there is a final checkpoint recall as part of this process.

To start fresh, uncheck `RESUME CHECKPOINT` before dropping. **Starting fresh deletes that expedition's saved checkpoint.** Finishing an expedition also deletes its save; failing or retiring does not.

## Behavior

- The host's latest native checkpoint is saved separately for each rundown expedition. Filenames hash the expedition identifier, rundown title and expedition name to distinguish custom rundowns that reuse numeric IDs. Rundown titles use their underlying literal text or localization ID, not the current display language (with the internal rundown block name as a fallback when no title exists).
- Expeditions marked `SkipLobby`, including the Tutorial, do not save or resume persistent checkpoints.
- When a compatible save exists, the loadout lobby shows a checked `RESUME CHECKPOINT` control beside the Drop button.
- Unchecking it and starting the expedition deletes the previous save and starts fresh.
- During an expedition, the host sees a `CREATE CHECKPOINT` button in the same position on the loadout page. Hold it to create a native checkpoint and replace that mission's persistent save. It is unavailable while a persistent resume is in progress, and creation is refused while players are still synchronizing.
- Completing an expedition deletes its save. Failing, aborting, or quitting keeps it.
- Other players do not need this plugin. The host restores the saved checkpoint locally, then sends each connected human a fresh vanilla DropIn hard-sync. This avoids replaying replication identities from the previous session on clients.
- Before the initial restore, weak doors saved as destroyed receive a normal replicated destruction transition. A one-second settling delay lets native destruction callbacks finish before recall. The host-side door repair remains a fallback; these preparation events do not replace the saved checkpoint.
- Returning human players are matched to their saved health, infection, ammunition, inventory, and position by their Steam lookup ID. A player who was not present in the saved checkpoint receives GTFO's normal reconstruction defaults.
- Connected humans are synchronized one at a time. After 20 seconds, a missing completion signal is tolerated if that player is already playing with an agent. Otherwise, an on-screen warning asks the affected player to leave and late-join again; the final checkpoint recall is withheld while that sync remains incomplete.

Checkpoint files are stored in:

```text
BepInEx/config/GTFO_PersistentCheckpoint/checkpoints/
```

Saves are tied to the GTFO build revision and expedition checksum. Incompatible or corrupt saves are not recalled.

This name-based identity is not a full content fingerprint. Mod updates retaining the same names still rely on GTFO's checksum checks.

Keep a backup of important saves before updating the game or a custom rundown. You can copy checkpoint files into a subdirectory of `checkpoints`; backup subdirectories are not loaded or deleted by the mod. Resume using the same Steam account as the original host.

## LobbyExpansion

`GTFO.LobbyExpansion` is an optional soft dependency. The plugin detects the current runtime character-slot capacity without referencing LobbyExpansion directly.

- Four-slot saves work in vanilla or expanded lobbies.
- Saves that captured expanded character slots require a lobby with at least that many slots.
- The resume control is created after LobbyExpansion's loadout patch and does not use its player-pillar UI.

## Limitations and troubleshooting

- This uses GTFO's checkpoint system, not a complete save of every possible game or mod state. Custom objective scripts and other mods may have state that the native capture does not preserve.
- Locker/container interaction inconsistencies have been reported after restoration and remain under investigation.
- Manual checkpoint creation is refused while players are still synchronizing. Prefer creating checkpoints while everyone is alive, stationary and outside interactions.
- A checkpoint from another game build, an incompatible mission definition or an insufficiently sized lobby will not be resumed.
- If restoration reports a player-sync failure, have the named player leave and late-join again. Report persistent issues with the host's `BepInEx/LogOutput.log`, the mission/mod list and the affected players' logs when available.

## Build

Set `GTFO_PATH` to the GTFO installation and build `GTFO_PersistentCheckpoint/GTFO_PersistentCheckpoint.csproj` for Release. The output assembly is `TheRoughy.PersistentCheckpoint.dll`. By default, it is copied into `BepInEx/plugins/TheRoughy-PersistentCheckpoint/`. Pass `-p:DeployPlugin=false` to build without deploying to the game.

## Create a Thunderstore release

Requires Python 3.9+ and the .NET SDK used to build the plugin. From the repository root:

```text
python create_release.py
```

This builds Release with deployment disabled, validates the package metadata and creates `dist/PersistentCheckpoint-<version>.zip`. Windows and WSL are supported; WSL invokes Windows .NET through `cmd.exe`. It does not upload anything or modify game saves.

The plugin's `Version` constant in `GTFO_PersistentCheckpoint/Core/PersistentCheckpointPlugin.cs` supplies the manifest and archive version. Before each release, update that constant, keep the assembly versions in `GTFO_PersistentCheckpoint/Properties/AssemblyInfo.cs` consistent and add a matching `## <version>` entry to `CHANGELOG.md`. Package name, description, website and explicit dependencies live in `thunderstore/package.json`. No website or public source repository is required.

Useful options:

```text
python create_release.py --no-build --keep
python create_release.py --gtfo-path "C:\Program Files (x86)\Steam\steamapps\common\GTFO"
python create_release.py --no-build --dll path/to/TheRoughy.PersistentCheckpoint.dll
python create_release.py --icon path/to/icon.png --output-dir dist
```

`--no-build` packages an existing DLL, so make sure it is up to date. `--keep` also leaves the generated package files in a unique directory under `dist`. Only explicitly selected files enter the ZIP: `manifest.json`, the dedicated `thunderstore/README.md` (packaged as `README.md`), `CHANGELOG.md`, a 256x256 `icon.png`, `TheRoughy.PersistentCheckpoint.dll` at the ZIP root, and any supplied screenshots. This root README is development documentation and is never included in the release. Edit `thunderstore/README.md` to change the public Thunderstore description. The mod manager supplies the `TheRoughy-PersistentCheckpoint` installation folder; it is not duplicated inside the ZIP. See the [mod manager's packaging rules](https://github.com/ebkr/r2modmanPlus/wiki/Structuring-your-Thunderstore-package).

The release icon is read from `thunderstore/images/icon.png` and must be a valid 256x256 PNG. `--icon` selects a different file. Packaging fails if the selected icon is missing or invalid.

Place optional screenshots in `thunderstore/images/create-checkpoint.png` and `thunderstore/images/resume-checkpoint.png`. They are bundled when present. Embed screenshots directly in `thunderstore/README.md` using normal Markdown image links with public, absolute image URLs. The packager copies that README byte-for-byte; it does not insert images or expand placeholders.

Run packaging tests with:

```text
python -m unittest discover -s tests -p test_release.py
```

Package format reference: [Thunderstore's packaging documentation](https://wiki.thunderstore.io/mods/creating-a-package).
