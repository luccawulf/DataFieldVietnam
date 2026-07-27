# DataField Vietnam — how it works

DataField Vietnam is an automatic map/mod downloader for **Battlefield Vietnam**. When you join a
server running content you don't have, it fetches exactly the files you're missing and then launches
the game — no more "MAP NOT FOUND". It's a port of [DataField42](https://github.com/Ahrkylien/BF1942-DataField42)
(by Arklyiën) from Battlefield 1942 to BFV.

## The two ways it runs

**1. As a launcher.** You open DataField Vietnam, pick a server from its browser, and click Join. It
syncs your files to match that server's exact map/mod versions, then starts the game already connected.
This is the reliable path — nothing is missing by the time the game loads.

**2. Triggered by the game ("MAP NOT FOUND" mode).** The installer patches `BfVietnam.exe` so that the
moment the game would show its "map not found" error, it launches DataField Vietnam instead, hands it
the server + map + mod, downloads what's missing, and relaunches the game into the server. This is the
in-game convenience path, and it's the part that required reverse-engineering the game.

## The pieces

| Component | What it is | Where it runs |
|---|---|---|
| **`DataFieldVietnam.exe`** | The client — a WPF (.NET) app: server browser, sync UI, settings | Player's PC, in the game folder |
| **`DataField42Server.py`** | The file server — hands out client files over TCP :28901 | A server box (yours) |
| **`DataFieldVietnam_updater.exe`** | A tiny bootstrap that swaps the client during a self-update | Downloaded on demand |
| **The `BfVietnam.exe` patch** | The in-game hook that launches the client on "map not found" | Applied to the game exe at install |

## How a sync works

The client and server speak a small TCP protocol on port 28901. Every message is a 4-byte
little-endian length followed by UTF-8 text; file bodies are sent raw.

1. **Handshake.** The client connects and exchanges versions. If the server it's joining doesn't run
   DataField Vietnam, the client falls back to the **central database** (a shared box that serves the
   same protocol).
2. **Request.** The client sends `download <map> <mod> <ip> <port> <keyHash>`.
3. **Offer.** The server replies with a file list — one line per file: `mod "path" crc32c size mtime`.
4. **Decision.** The client compares each offered file against what it already has (by CRC32C) and
   replies `yes`/`no` per file. Files it already has correct are never transferred.
5. **Transfer.** The server sends the `yes` files; the client verifies each checksum and moves them
   into the game directory. A local cache means nothing is downloaded twice.

### Why the server resolves the mod from the map

A Battlefield Vietnam client **cannot know what mod a server runs**. Its in-game browser parses the
map, game type, and port out of the server's query reply — but never the mod id (`game_id`). So a
player sitting in base BFVietnam who joins a modded server honestly reports "mod: BFVietnam", which is
useless.

The fix is server-side: the **map** is the one thing we do know (the server is running it), so the
server finds whichever installed mod actually ships that map and serves that instead, then the client
adopts the correction from the offer. Deliberately conservative — an ordinary missing-map sync, and a
stock map served under a custom mod, both still resolve correctly.

## The in-game hook (the reverse-engineered part)

The installer runs `DataFieldVietnam.exe install`, which patches `BfVietnam.exe`. Two small hooks are
written into unused space in the exe (the `.tls` tail, made executable):

- **A capture hook** on the game's `sendto` path stashes the server's IP and port (they only exist as
  raw bytes in the network layer, not as a string anywhere).
- **The main hook** sits where the game gives up looking for a level and would raise MAP_NOT_FOUND. It
  reads the level name and expected hash off the connection object, the mod from the game's mod-path
  list, and the server address from the stash; formats them into a command line; tells the server it's
  leaving (so the CD key is freed for the rejoin); then `ShellExecuteA`-launches DataFieldVietnam.exe
  and exits the game. If the launch fails it falls through to the game's normal error — nothing is lost.

Once DataField Vietnam has synced, it relaunches the game with `+joinServer` and you're in.

## Self-update

The client keeps itself current. On startup (and before each sync) it asks the central database its
version; if the client is behind, it downloads the updater bootstrap, which downloads the new client,
swaps it in place, and relaunches. The version rule: updates fire on a full version comparison, while
server-compatibility only checks Major.Minor — so bumping the third/fourth number pushes an update
without breaking sync. See `DataField42 Server/` and `SERVER_SETUP.md` §8 for the release workflow,
and `SIGNING.md` for the release signing that update executables must carry — a client refuses to run
an update it cannot verify against the pinned key, so an unsigned build does not install at all.

## The installer

Built from `DataField42 Installer/DataField42.iss` with Inno Setup. It finds/validates your Battlefield
Vietnam folder (checks for `BfVietnam.exe`), copies the client in, runs `install` to apply the game
patch, and makes shortcuts. Uninstalling removes the client but leaves the game patch — harmless, the
game just reverts to its normal "map not found" popup.

## Where things live on a player's PC

```
<Battlefield Vietnam>/
    BfVietnam.exe                 (patched with the hook)
    DataFieldVietnam.exe          (the client)
    DataFieldVietnam/             (client data: Settings.ini, cache, tmp, Logs)
```

## Repo layout

- `DataField42/` — the WPF client
- `DataField42.Core/` — shared logic (protocol, models, services); the namespace is still `DataField42`
- `DataField42 Updater/` — the self-update bootstrap
- `DataField42 Server/` — the Python file server
- `DataField42 Installer/` — the Inno Setup script
- `Client Patches/bfv/` — the hand-written asm hooks and the assembler that generates the patch table
- `DataField42.Core.Tests/` — tests
- `SERVER_SETUP.md` — how to stand up a server box; `SIGNING.md` — release signing and key custody;
  `UPSTREAM_BUGS.md` — bugs found in shared code
