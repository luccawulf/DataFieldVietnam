# DataField Vietnam
A Battlefield Vietnam tool for automatic map/mod downloads.

DataField Vietnam is a port of [DataField42](https://github.com/Ahrkylien/BF1942-DataField42) (by Arklyiën) to Battlefield Vietnam. The original DataField42 targets Battlefield 1942; this fork carries the same system over to BFV — the desktop launcher, the sync-then-join flow, and the in-game "map not found" hook. Credit for the underlying design and code is Arklyiën's.

## Features:
- Download maps and mods seamlessly from the central database or the server you're joining, eliminating the "MAP NOT FOUND" pop-up message.
- Utilize an extra Desktop application with its own game server lobby for a convenient server joining experience while synchronizing files with the selected server.
- Enable fast and easy switching between servers that may have different versions of a mod.
  - Sync game files with DataField Vietnam-compatible servers to match their specific versions, ensuring a smooth transition between servers with varying mod and map versions.
- Store data in a cache for reuse, preventing the need for redownloading and ensuring nothing is removed.
- Maintain functionality even when the central database is down, allowing seamless joining of servers that support DataField Vietnam.

## How it works:
DataField Vietnam operates in two modes:

**Triggered by Battlefield Vietnam ("MAP NOT FOUND" mode)**
During installation, DataField Vietnam patches `BfVietnam.exe` so that when the game would normally show a "MAP NOT FOUND" error, it launches DataField Vietnam instead. DataField Vietnam then attempts to download the missing map or mod from the central database or directly from the server you're joining, and once the download is complete the game continues.

> If DataField Vietnam is uninstalled, the patch to `BfVietnam.exe` remains but is harmless — Battlefield Vietnam simply reverts to showing the "MAP NOT FOUND" message as it normally would.

**Used as a launcher**
DataField Vietnam includes its own server browser. Joining a server through it synchronizes all required files (maps, mods, and their correct versions) before launching Battlefield Vietnam, preventing version mismatches and in-game crashes.

## Limitations:
Joining a server in-game with the wrong version of the mod or map can cause your game to crash or display an error message. To prevent this, it's advisable to connect to the server through DataField Vietnam, provided the server supports it. This precaution is essential because DataField Vietnam isn't used when connecting to a server through the in-game browser if the game files for a particular version are already present.

## DataField Vietnam Server:
The python script for hosting a server is `DataField42Server.py`. It needs to sit in the same folder as the mods folder. Make sure to put client files in the mods folder and not server files.\
It should look like this:\
Some Folder/
- DataField42Server.py
- Mods/
  - BfVietnam/
    - ...
  - OtherMod/
    - ...

For the clients to be able to reach the server you will need to open port 28901 (TCP) in your firewall/router.\
The minimal version of python is 3.10. The only dependency that you need to manually download is google_crc32c:\
pip3 install google-crc32c\
https://pypi.org/project/google-crc32c/

## Credits
- **DataField Vietnam** (Battlefield Vietnam port): LuccaWulf
- **DataField42** (original, Battlefield 1942): Arklyiën — https://github.com/Ahrkylien/BF1942-DataField42
