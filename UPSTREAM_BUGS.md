# DataField42 — bugs found while porting to Battlefield Vietnam

These three bugs were found while porting DataField42 to Battlefield Vietnam. All of them are in code
that is **shared with the Battlefield 1942 version**, so they affect upstream too — the port didn't
introduce them. They're ordered by severity. Fixes are included for each.

Repo: <https://github.com/Ahrkylien/BF1942-DataField42>

---

## 1. `ChecksumRepository` self-deadlock — CRITICAL (ships in v2.1.0)

**Where:** `ChecksumRepository.add_record` → `save_records` (Python server). Introduced by commit
`a7c0faa` (2024-06-22, *"add smart ChecksumRepositoryManager"*).

**What happens:** the server hangs **forever** the first time it hashes a new file. From the client's
side, a download stalls: the log prints `download : [...]` and then nothing is ever sent (120 s+ of
silence). The checksum watchdog thread dies too.

**Cause:** `add_record` acquires `self.lock` (a plain, non-reentrant `threading.Lock`) and then, while
still holding it, calls `save_records`, which tries to acquire **the same lock**. A non-reentrant lock
re-acquired by the same thread deadlocks immediately.

**Repro:** point a client at a *fresh* server (empty `ChecksumRepository.json`) and request any file.
The server computes the file's checksum, calls `add_record`, and hangs on the first one.

**Fix:** make the lock reentrant:

```python
# ChecksumRepository
self.lock = threading.RLock()   # was threading.Lock()
```

(Alternatively, release `self.lock` before calling `save_records`, or refactor so the persist step
doesn't re-take the lock.)

**Why it likely went unnoticed:** the Python server is optional — most clients sync from the central
database instead — and the deadlock only fires on the *first new* checksum a given server ever computes.

*Diagnosed with `faulthandler.dump_traceback_later` (py-spy couldn't attach under Python 3.14), by
driving `download_files` in-process with a fake connection.*

---

## 2. Checksum watchdog `NameError` from module construction order

**Where:** module initialization of the Python server — `checksum_repository_manager` is constructed
**before** `dataField42_server`.

**What happens:** the checksum pre-warm watchdog thread starts as soon as the manager is constructed and
immediately reads the `dataField42_server` global — which doesn't exist yet — so it raises `NameError`
and dies. Checksum pre-warming never runs; checksums end up computed on demand during the first client
sync instead of in advance. On some setups this is deterministic (it was, on a Debian 12 / Python 3.12
box).

**Cause:** ordering. The watchdog depends on `dataField42_server`, but that global is defined after the
manager that starts the watchdog.

**Fix:** construct `dataField42_server` **before** `checksum_repository_manager` (and before anything
that starts the watchdog).

**Related note (not a crash):** the watchdog is a non-daemon `while True:` loop with a 1 s sleep, so
merely *importing* the server module never lets the interpreter exit on its own — worth knowing if you
ever want to import it for testing (use `os._exit`).

---

## 3. `Bf1942QueryResult` hard-indexes `ticket_ratio` — drops valid servers from the browser

**Where:** `Bf1942QueryResult` parsing (client).

**What happens:** a live server that doesn't send `ticket_ratio` in its GameSpy `\status\` reply is
dropped from DataField42's **own** server browser. It was found on a real BF1942 CTF server (SiMPLE):
CTF servers omit the field, the parser throws `KeyNotFoundException`, and the server silently vanishes
from the list.

**Cause:** the field is read unconditionally, but it isn't part of every server's reply.

**Fix:** treat `ticket_ratio` as optional — default it (e.g. to 0 / empty) when the key is absent,
rather than indexing it directly. Only the fields every server actually sends should be required.

---

## Minor: `get_checksum` reads whole files into memory

`get_checksum` does `file.read()` on the entire file, so peak RAM scales with the largest file it
hashes (Battlefield archives can be hundreds of MB). Hashing in fixed-size chunks removes the spike:

```python
h = ...  # crc32c
with open(path, "rb") as f:
    for block in iter(lambda: f.read(1 << 20), b""):
        h.update(block)
```

Not a correctness bug — just a scalability footgun on a server that hashes large archives.
