# Release signing

DataField Vietnam updates itself by downloading an executable and running it. That is the most
dangerous thing the program does, so the executable has to prove where it came from before it runs.

## Why this exists

The update channel used to download an `.exe` over plain TCP and execute it with no checks at all —
the code said `// TODO: check file size` and did not even do that. Anyone who could answer for the
server could run code as the player, and because the client relaunches itself elevated when it is
installed somewhere only an administrator can write, quite possibly as administrator.

"Anyone who could answer for the server" is a wider set than it sounds. There is no TLS, the central
address is a bare IP, and a game server can redirect the client to sync from an arbitrary host. An
on-path attacker, a compromise of the box, or whoever inherits that IP one day would all do.

So the bytes now carry a signature, and **the key that makes signatures never goes near the server.**
That is the part that matters. Someone who takes over the file server can serve whatever they like —
they simply cannot make the client run it.

## What this does and does not protect

It protects the **update channel**: the client executable and the updater bootstrap.

It does **not** make downloaded maps and mods trustworthy. Those are still verified only against a
checksum the server itself supplies, which proves the bytes arrived intact and nothing about whether
they are legitimate. Signing the game-file manifest is the next step, and until it is done, do not
claim otherwise.

## Creating the key — once, ever

```bash
dotnet run --project DataField42.Sign -c Release -- keygen
```

Writes an encrypted key to `%APPDATA%\DataFieldVietnam\signing\release-key.p8` (AES-256, PBKDF2-SHA256
at 600,000 iterations) and prints the public half to paste into
`ReleaseSignature.PublicKeyBase64` in [DataField42.Core/Services/ReleaseSignature.cs](DataField42.Core/Services/ReleaseSignature.cs).

Then rebuild. Until that constant is filled in, the client refuses **every** update rather than
accepting unverified ones — it fails shut, which is the correct direction to fail.

Three things to get right, because none of them can be undone later:

- **Back the key up offline.** Lose it and you can never ship an update your existing users will
  accept; every one of them would have to reinstall by hand.
- **Leave it where `keygen` put it.** In particular it must never sit in the game folder
  (`D:\Games\EA GAMES\Battlefield Vietnam\`), in the repository, or in `update_files` — those all get
  zipped, shared or copied to the server sooner or later, and one careless archive publishes it. The
  whole guarantee is that nobody else has this file.
- **The passphrase matters.** Anyone with the file *and* the passphrase can push code to every user,
  so do not paste it into a chat, an issue, or a build script.

If a key ever does end up somewhere it should not, and you have not signed a release with it yet,
regenerate rather than move it — at that point it costs nothing. Once clients are pinned to it, the
only way out is shipping a new pinned key to every user by hand.

The public key is not secret — it is compiled into every client and belongs in the repo. Only the
`.p8` is sensitive. `.gitignore` carries `*.p8` and friends as a backstop.

## Shipping a release

```bash
# 1. build
dotnet publish DataField42/DataField42.csproj -c Release
dotnet publish "DataField42 Updater/DataField42 Updater.csproj" -c Release

# 2. sign — version must match the exe's own version, and must be newer than what users have
dotnet run --project DataField42.Sign -c Release -- sign path/to/DataFieldVietnam.exe 2.1.0.2
dotnet run --project DataField42.Sign -c Release -- sign path/to/DataFieldVietnam_updater.exe 2.1.0.2

# 3. check it the way a client will, before anyone else does
dotnet run --project DataField42.Sign -c Release -- verify path/to/DataFieldVietnam.exe

# 4. upload BOTH the .exe and its .sig to update_files/ on the server, then bump
#    dataField42_server_version in DataField42Server.py and restart:
#      systemctl restart datafield-vietnam
```

Upload the `.sig` files. A signed release whose signature is missing from `update_files` is not a
half-working update — clients refuse it outright and log why, which is deliberate: an attacker who
could suppress the signature would otherwise be able to turn verification off.

## How it works

`dfvsign sign` produces a two-line document beside the executable:

```
DFVSIG1 DataFieldVietnam.exe 2.1.0.2 41c1cfbc6b9cad3af7270e6ab97c8f54460c072e3fbefbe79a0ef649e46aaaac
j9mScY0EqJIjdg5tLKi91vl64L5hkLI4ARNwemI+tEd1rj10QW6tB6UPXV+Skj9FN6KKYVox5jbwa1DNtRz/kg==
```

The first line is the manifest — format marker, filename, version, SHA-256 of the file. The second is
an ECDSA P-256 signature over exactly those manifest bytes. The client fetches it with a new
`updateSig <name>` command and checks, in this order:

1. the signature is genuine under the pinned public key — nothing the document claims counts until this passes;
2. the filename matches what was actually requested, so a signed updater cannot be served in place of a signed client;
3. the version is strictly newer than what is installed, so an old signed release cannot be replayed to walk someone back onto a build with a known hole;
4. the SHA-256 of the file on disk matches the manifest.

Anything else — a bad signature, a malformed document, a server that replies `unknown identifier`
(too old to sign) or `signature not available` (nothing to offer) — deletes the download and aborts.

P-256 rather than Ed25519 only because P-256 is in .NET's own library and Ed25519 is not, so there is
no third-party crypto to vendor and keep patched.

## Compatibility

The wire format of existing commands is unchanged, so **old clients keep updating normally** — they
never ask for a signature. They are no worse off than before, and the update they receive is the way
to get them onto a build that does check.

A **new client against an old server** refuses to update and says so. If you run the server, update
the server first.
