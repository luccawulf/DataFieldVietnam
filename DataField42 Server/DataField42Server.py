import socket
import socketserver
import os
import sys
import re
import subprocess
import threading
import select
import time
import json
import hashlib
from enum import Enum
from datetime import datetime
import google_crc32c


class Version:
    def __init__(self, version_string: str):
        self.version_numbers = [int(num) for num in version_string.split('.')]
        self.major, self.minor, self.patch = self.version_numbers[:-1]

    def __gt__(self, other):
        return self.version_numbers > other.version_numbers

    def __str__(self):
        return ".".join(map(str, self.version_numbers))


log_lock = threading.Lock()
def log(level, message):
    with log_lock:
        print(f"{datetime.now().isoformat()} [{level}] {message}")


def log_error(message):
    log("Error", message)


def log_warning(message):
    log("Warning", message)


def log_info(message):
    log("Info", message)


def log_debug(message):
    log("Debug", message)


AllowableChars = "0-9a-zA-Z_-"


class Bf1942FileTypes(Enum):
    nonetype = 0
    movie = 1
    music = 2
    modmiscfile = 3
    archive = 4
    level = 5


class IgnoreSyncScenarios(Enum):
    always = 0
    never = 1


class SyncRuleManager:
    def __init__(self, rule_file_path):
        self.rule_file_path = rule_file_path
        self.ignore_file_sync_rules = []
        self.parse_rule_file()

    def parse_rule_file(self):
        try:
            with open(self.rule_file_path, 'r') as file:
                for line in file:
                    self.parse_rule_line(line)
        except IOError:
            pass

    def parse_rule_line(self, line):
        if line.strip().startswith("//"):  # comment
            return

        line_parts = line.split(' ')
        if line_parts[0] == "ignore" and len(line_parts) == 5:
            try:
                file_rule = FileRule(line_parts[1], line_parts[2], line_parts[3], line_parts[4])
                self.ignore_file_sync_rules.append(file_rule)
            except Exception as ex:
                log_warning(f"Can't parse line: {line}, Exception: {ex}")

    def get_ignore_file_sync_scenario(self, file_info) -> IgnoreSyncScenarios:
        for file_rule in self.ignore_file_sync_rules:
            if file_rule.matches(file_info):
                return file_rule.ignore_sync_scenario
        return IgnoreSyncScenarios.never


class FileInfo:
    def __init__(self, file_name: str, file_type: Bf1942FileTypes, mod: str):
        self.file_name = file_name
        self.file_type = file_type
        self.mod = mod

    @property
    def file_name_without_patch_number(self):
        if self.file_type == Bf1942FileTypes.level or self.file_type == Bf1942FileTypes.archive:
            file_name_without_extension = os.path.splitext(self.file_name)[0]
            file_extension = os.path.splitext(self.file_name)[1]
            match = re.match(f"^([{AllowableChars}]+)(_{{1}})([0-9]{{1,3}})$", file_name_without_extension)
            return f"{match.group(1)}{file_extension}" if match else self.file_name
        else:
            return self.file_name


class FileRule:
    def __init__(self, ignore_sync_scenario: str, file_type: str, mod: str, file_name: str):
        self.ignore_sync_scenario = IgnoreSyncScenarios[ignore_sync_scenario.lower()]
        self.file_type = Bf1942FileTypes[file_type.lower()]
        self.mod = mod.lower()
        self.file_name = file_name.lower()

        if (self.file_type == Bf1942FileTypes.level or self.file_type == Bf1942FileTypes.archive) and not self.file_name.endswith(".rfa"):
            self.file_name += ".rfa"
        elif (self.file_type == Bf1942FileTypes.movie or self.file_type == Bf1942FileTypes.music) and not self.file_name.endswith(".bik"):
            self.file_name += ".bik"
        elif self.file_type == Bf1942FileTypes.modmiscfile:
            if self.file_name in ["contentcrc32", "init"]:
                self.file_name += ".con"
            elif self.file_name == "mod":
                self.file_name += ".dll"
            elif self.file_name == "lexiconall":
                self.file_name += ".dat"
            elif self.file_name == "serverinfo":
                self.file_name += ".dds"

    def matches(self, file_info: FileInfo):
        return (self.mod == "*" or self.mod == file_info.mod.lower()) and \
            self.file_type == file_info.file_type and \
            (self.file_name == "*" or self.file_name == file_info.file_name_without_patch_number.lower())


class ChecksumRepository:
    def __init__(self, filename: str):
        self.filename = filename
        self.dirty = False
        self.records = self.load_records()
        # Reentrant: add_record holds this and then calls save_records, which takes it again. With a
        # plain Lock that is a self-deadlock, and it fires the first time any new checksum is
        # recorded -- which on a fresh server is the first file it ever hashes, hanging both the
        # watchdog and any client waiting on a download listing.
        self.lock = threading.RLock()

    @staticmethod
    def key_of(path, size, last_time_modified) -> str:
        # The path belongs in the key. Keyed on size and mtime alone, any two files packed in the same
        # session collide -- which is the normal case for a mod's archives -- and one file's checksum
        # gets served for another. Clients then fail verification on a perfectly good download.
        return f"{os.path.abspath(path)}|{int(size)}|{int(last_time_modified)}"

    def load_records(self):
        try:
            with open(self.filename, 'r') as file:
                loaded = json.load(file)
        except Exception:
            return {}

        # Older files are a list, and their records carry neither a path nor a sha256. They can never
        # be matched under the current key, so drop them and let the digests be recomputed once.
        if not isinstance(loaded, dict):
            return {}
        return {key: record for key, record in loaded.items()
                if isinstance(record, dict) and record.get('sha256')}

    def save_records(self):
        with self.lock:
            if not self.dirty:
                return
            # Written beside the real file and moved into place, so an interrupted write cannot leave
            # a truncated cache that fails to parse on the next start.
            temporary = self.filename + ".tmp"
            with open(temporary, 'w') as file:
                json.dump(self.records, file)
            os.replace(temporary, self.filename)
            self.dirty = False

    def add_record(self, path, checksum, sha256, size, last_time_modified):
        with self.lock:
            self.records[self.key_of(path, size, last_time_modified)] = {
                'path': os.path.abspath(path),
                'checksum': str(checksum),
                'sha256': str(sha256),
                'size': int(size),
                'lastTimeModified': int(last_time_modified),
            }
            self.dirty = True

    def find_record(self, path, size, last_time_modified) -> dict | None:
        with self.lock:
            return self.records.get(self.key_of(path, size, last_time_modified))


class ChecksumRepositoryManager:
    def __init__(self, filename: str):
        self.repository = ChecksumRepository(filename)
        self.lock = threading.Lock()  # Lock for thread safety
        self.start_new_files_watchdog()

    def get_checksums(self, path: str) -> tuple[str, str]:
        """Both digests for a file: the CRC32C the protocol has always carried, and its SHA-256.

        Sent together so a client can use whichever it understands. Old clients read the CRC32C field
        and ignore the extra one; newer clients verify against the SHA-256.
        """
        size = os.path.getsize(path)
        last_time_modified = os.path.getmtime(path)

        with self.lock:
            record = self.repository.find_record(path, size, last_time_modified)
            if record is not None:
                return record['checksum'], record['sha256']

            # Streamed rather than file.read(): the archives here reach hundreds of megabytes and
            # reading one whole means holding all of it in memory per request.
            crc = 0
            sha = hashlib.sha256()
            with open(path, "rb") as file:
                while True:
                    block = file.read(1 << 20)
                    if not block:
                        break
                    crc = google_crc32c.extend(crc, block)
                    sha.update(block)

            checksum = f"{(crc & 0xFFFFFFFF):08X}"
            sha256 = sha.hexdigest()
            self.repository.add_record(path, checksum, sha256, size, last_time_modified)
        return checksum, sha256

    def get_checksum(self, path: str) -> str:
        """The CRC32C alone, for callers that predate the second digest."""
        return self.get_checksums(path)[0]

    def start_new_files_watchdog(self):
        threading.Thread(target=self.new_files_watchdog).start()

    def new_files_watchdog(self):
        while True:
            all_files = [os.path.join(dp, f)
                         for dp, dn, filenames in os.walk(dataField42_server.game_directory) for f in filenames
                         if os.path.splitext(f)[1].lower() in ['.rfa', '.bik', '.dat', '.con', '.dll', '.dds']]
            for file in all_files:
                try:
                    self.get_checksums(file)
                except Exception as e:
                    log_error(f"Failed adding file with new_files_watchdog {file}: {e}")
            # Written once per sweep rather than per file: the cache is rewritten whole, so saving on
            # every addition made the first pass over a full mod library quadratic.
            self.repository.save_records()
            time.sleep(1)


class DataField42Communication:
    def __init__(self, socket: socket.socket, name: str):
        self.socket = socket
        self.name = name

    def receive_bytes(self, length: int, timeout: int | None = None, log: bool = True) -> bytes:
        total_data = b""
        start_time = time.time()
        while len(total_data) < length:
            if timeout is not None:
                ready, _, _ = select.select([self.socket], [], [], timeout)
                if not ready:
                    raise TimeoutError(f"Timeout occurred while waiting to receive {length} bytes")
            else:
                ready, _, _ = select.select([self.socket], [], [])
            data = self.socket.recv(length - len(total_data))
            if not data:
                raise Exception("Socket closed or no more data to receive")
            total_data += data
            # Update timeout based on elapsed time
            elapsed_time = time.time() - start_time
            if timeout is not None:
                timeout -= elapsed_time
            start_time = time.time()
        if log:
            log_debug(f"<< {total_data}")
        return total_data

    def receive_file(self, length: int, timeout: int | None = None) -> bytes:
        total_data = self.receive_bytes(length, timeout, log=False)
        log_debug(f"<< ~file~")
        return total_data

    def receive_data_length(self, timeout: int | None = None) -> int:
        return int.from_bytes(self.receive_bytes(4, timeout), 'little')

    def receive_string(self, timeout: int | None = None) -> str:
        length = self.receive_data_length(timeout)
        return self.receive_bytes(length, timeout).decode('utf-8')

    def receive_int(self, timeout: int | None = None) -> int:
        return int(self.receive_string(timeout))

    def receive_space_separated_string(self, timeout: int | None = None) -> list[str]:
        return self.receive_string(timeout).split()

    def await_acknowledgement(self, timeout: int | None = None):
        if self.receive_string(timeout) != "ok":
            raise Exception("Acknowledge not received")

    def send(self, message: any, await_acknowledgement=True, prepend_with_length=True):
        if not isinstance(message, bytes):
            message = str(message).encode('utf-8')
        if prepend_with_length:
            message = len(message).to_bytes(4, byteorder='little') + message
            log_debug(f">> {message}")
        self.socket.sendall(message)
        if await_acknowledgement:
            self.await_acknowledgement()

    def send_file(self, path: str, chunk_size=8192) -> None:
        log_debug(f">> ~file~ {path}")
        with open(path, "rb") as file:
            while True:
                file_bytes = file.read(chunk_size)
                if not file_bytes:
                    break
                self.send(file_bytes, await_acknowledgement=False, prepend_with_length=False)
        self.await_acknowledgement()

    def send_acknowledgement(self):
        self.send("ok", await_acknowledgement=False)


def update_and_restart_script(new_script_bytes: bytes):
    with open(sys.argv[0], 'wb') as file:
        file.write(new_script_bytes)
    if not restart_systemd_service("DataField42Server"):
        python_path = sys.executable
        args = [f"\"{arg}\"" for arg in [python_path] + sys.argv]
        os.execl(python_path, *args)


def restart_systemd_service(service_name: str) -> bool:
    try:
        subprocess.run(["sudo", "systemctl", "restart", service_name], check=True)
        return True
    except Exception as e:
        log_info(f"Failed to restart SystemD service '{service_name}'. Reason: {e}")
        return False


def smart_path_join(base_dir: str, rel_path: str, is_dir=False) -> str | None:
    current_dir = "." if base_dir == "" else base_dir
    rel_path_parts = rel_path.replace('\\', '/').split('/')
    for i, path_part in enumerate(rel_path_parts):
        entry_to_join = None
        for entry in os.scandir(current_dir):
            if entry.name.lower() == path_part.lower():
                if entry.is_dir() and (i < len(rel_path_parts) - 1 or is_dir):
                    entry_to_join = entry.name
                    break
                elif entry.is_file() and i == len(rel_path_parts) - 1 and not is_dir:
                    entry_to_join = entry.name
                    break
        if entry_to_join is None:
            return None
        current_dir = os.path.join(current_dir, entry_to_join)
    return current_dir


class DataField42TCPHandler(socketserver.BaseRequestHandler):
    def handle(self):
        log_info(f"#### New Connection: {self.client_address[0]} ####")
        request_socket = self.request
        communication = DataField42Communication(request_socket, self.client_address[0])

        arguments = communication.receive_space_separated_string()
        header = arguments.pop(0)

        log_info(f"{header} : {arguments}")

        if header == "handshake" and len(arguments) == 1:
            handshake(communication, *arguments)
        elif header == "download" and len(arguments) >= 5:
            arguments = arguments[:5] + [arguments[5:]]
            download_files(communication, *arguments)
        elif header == "update" and len(arguments) == 1:
            send_update(communication, *arguments)
        elif header == "updateFile" and len(arguments) == 1:
            send_update_file(communication, *arguments)
        elif header == "updateSig" and len(arguments) == 1:
            send_update_signature(communication, *arguments)
        elif header == "news" and len(arguments) == 0:
            send_news(communication)
        elif header == "newsImage" and len(arguments) == 1:
            send_news_image(communication, *arguments)
        else:
            communication.send("unknown identifier")
        log_info("#### Connection Closed ####")


def handshake(communication: DataField42Communication, version: str):
    redirect_server_ip = "null" if dataField42_server.redirect_server_ip == "" else dataField42_server.redirect_server_ip
    communication.send(f"{redirect_server_ip} {dataField42_server_version}", await_acknowledgement=False)


# --- news ------------------------------------------------------------------------------------------
# A short announcement shown to anyone who opens the news page in DataField Vietnam. Write whatever you
# like in news.txt next to this script; it is served verbatim as plain text, so there is no format to
# get wrong and a bad file can never break a client. Only ever sent when a client asks -- nothing is
# pushed, and the client has no background process to push to.
NEWS_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "news.txt")


def send_news(communication: DataField42Communication):
    if not os.path.isfile(NEWS_FILE):
        log_debug(f"News requested but {NEWS_FILE} does not exist; sending nothing.")
        communication.send("", await_acknowledgement=False)
        return
    with open(NEWS_FILE, "r", encoding="utf-8") as file:
        news = file.read()
    log_info(f"Serving news ({len(news)} characters)")
    communication.send(news, await_acknowledgement=False)


# Pictures the news text can refer to, as [img:name.png] on a line of its own. They live in a folder
# beside news.txt and are served from here rather than linked to somewhere on the web: a link would
# have every player's client contact a third-party host the moment they opened the page, handing that
# host their IP address, and news is meant to be something the client reads from us when asked -- not
# a thing that quietly fans out to other servers.
NEWS_IMAGES_DIRECTORY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "news_images")

NEWS_IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg"}

# Anything larger is refused rather than sent. The client draws these at a few hundred pixels wide, so
# a picture past this is a mistake, and a cap keeps one bad file from making the page hang.
NEWS_IMAGE_MAXIMUM_BYTES = 4 * 1024 * 1024


def send_news_image(communication: DataField42Communication, file_name: str):
    """Serve one picture referenced by the news text.

    The name arrives from the client, so it is reduced to a basename and looked up only inside the
    images folder -- the same rule the update files follow. Nothing outside that folder is reachable,
    whatever the client asks for.
    """
    base_name = os.path.basename(file_name)
    if os.path.splitext(base_name)[1].lower() not in NEWS_IMAGE_EXTENSIONS:
        log_warning(f"Refusing newsImage for a name that is not a picture: {file_name!r}")
        communication.send("image not available", await_acknowledgement=False)
        return

    path = None
    if os.path.isdir(NEWS_IMAGES_DIRECTORY):
        path = smart_path_join(NEWS_IMAGES_DIRECTORY, base_name)

    if path is None or not os.path.isfile(path):
        log_error(f"newsImage {base_name!r} requested but it is not in {NEWS_IMAGES_DIRECTORY}")
        communication.send("image not available", await_acknowledgement=False)
        return

    size = os.path.getsize(path)
    if size > NEWS_IMAGE_MAXIMUM_BYTES:
        log_error(f"newsImage {base_name!r} is {size} bytes, over the {NEWS_IMAGE_MAXIMUM_BYTES} limit")
        communication.send("image not available", await_acknowledgement=False)
        return

    log_info(f"Serving news image: {base_name} ({size} bytes)")
    _serve_file_with_size(communication, path)


# --- client auto-update ----------------------------------------------------------------------------
# A client that finds this server reporting a newer version in its handshake asks for these. Put the
# built executables in an "update_files" folder next to this script, and set dataField42_server_version
# (below) to the new client's version. Keep Major.Minor equal to the clients you serve -- the sync path
# rejects a mismatched Major/Minor -- so bump only the third/fourth number to push a client update.
UPDATE_FILES_DIRECTORY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "update_files")

# The small bootstrap the client downloads first and runs; it then pulls the client exe over itself.
UPDATER_FILE_NAME = "DataFieldVietnam_updater.exe"

# "updateFile" names arrive from the client, so only these may be served -- never an arbitrary path.
UPDATE_FILE_ALLOWLIST = {"datafieldvietnam.exe", "datafieldvietnam_updater.exe"}


def _resolve_update_file(name: str) -> str | None:
    # Case-insensitive lookup within the update folder; basename only, so a client cannot escape it.
    if not os.path.isdir(UPDATE_FILES_DIRECTORY):
        return None
    return smart_path_join(UPDATE_FILES_DIRECTORY, os.path.basename(name))


def _serve_file_with_size(communication: DataField42Communication, path: str):
    # Matches the client: ReceiveUlong (size), SendAcknowledgement, ReceiveFile, SendAcknowledgement.
    communication.send(os.path.getsize(path))   # size string, length-prefixed; awaits the client's "ok"
    communication.send_file(path)               # raw bytes; awaits the client's final "ok"


def send_update(communication: DataField42Communication, client_version: str):
    """Serve the updater bootstrap to a client that has decided it is behind."""
    path = _resolve_update_file(UPDATER_FILE_NAME)
    if path is None:
        log_error(f"Client on {client_version} asked to update, but {UPDATER_FILE_NAME} is missing from {UPDATE_FILES_DIRECTORY}")
        communication.send("update not available", await_acknowledgement=False)
        return
    log_info(f"Serving updater to a client on version {client_version}")
    _serve_file_with_size(communication, path)


def send_update_file(communication: DataField42Communication, file_name: str):
    """Serve one allow-listed update executable by name (the updater asks for the client exe)."""
    if os.path.basename(file_name).lower() not in UPDATE_FILE_ALLOWLIST:
        log_warning(f"Refusing updateFile for a name that is not allow-listed: {file_name!r}")
        communication.send("file not available", await_acknowledgement=False)
        return
    path = _resolve_update_file(file_name)
    if path is None:
        log_error(f"updateFile {file_name!r} requested but not present in {UPDATE_FILES_DIRECTORY}")
        communication.send("file not available", await_acknowledgement=False)
        return
    log_info(f"Serving update file: {os.path.basename(path)}")
    _serve_file_with_size(communication, path)


def send_update_signature(communication: DataField42Communication, file_name: str):
    """Serve the release signature for an update executable.

    Clients refuse to run an update they cannot verify, so a missing or unreadable .sig here stops the
    update rather than weakening it. That is deliberate: an attacker who could suppress the signature
    would otherwise be able to downgrade every client to no verification at all.

    Signatures are made off this machine with dfvsign and uploaded alongside the executables. This
    server only hands the file over -- it has no key and cannot produce one, which is exactly why a
    signature is worth anything even if this box is taken over.
    """
    if os.path.basename(file_name).lower() not in UPDATE_FILE_ALLOWLIST:
        log_warning(f"Refusing updateSig for a name that is not allow-listed: {file_name!r}")
        communication.send("signature not available", await_acknowledgement=False)
        return

    path = _resolve_update_file(file_name)
    if path is None or not os.path.isfile(path + ".sig"):
        log_error(
            f"updateSig {file_name!r} requested but {os.path.basename(file_name)}.sig is missing from "
            f"{UPDATE_FILES_DIRECTORY}. Clients will refuse this update until it is signed "
            f"(run: dfvsign sign {os.path.basename(file_name)} <version>)."
        )
        communication.send("signature not available", await_acknowledgement=False)
        return

    with open(path + ".sig", "r", encoding="utf-8") as signature_file:
        signature_document = signature_file.read()

    log_info(f"Serving release signature for {os.path.basename(path)}")
    communication.send(signature_document, await_acknowledgement=False)


# Central database this server registers with. The DataField Vietnam central database is currently
# hosted by LuccaWulf at 188.245.154.232. Set to None to run standalone -- clients can always sync
# straight from the server without any central database.
MASTER_HOST = '188.245.154.232'

# Whether to replace this script with the master's copy when the master reports a newer version.
#
# LEAVE THIS OFF. An earlier version of this comment said accepting a script from our own master was
# safe. It is not, and the reasoning was wrong: it argued about WHO we intend to talk to, when the
# problem is that nothing here can tell who actually answered.
#
# The heartbeat goes out over plain TCP to a bare IP -- no TLS, no certificate, no signature. Anything
# that can answer it (someone on the path, someone who takes over that box, or whoever is assigned
# that address after it is given up) can claim a higher version and hand back a Python script that
# this process then writes over itself and runs. That is arbitrary code execution as whatever user
# runs the service, which per SERVER_SETUP.md is root.
#
# This is the same hole that the client update channel had until release signing was added (see
# SIGNING.md). Until the server script is signed the same way -- offline key, manifest verified before
# the file is written -- there is nothing to check, so the only safe setting is False. Updating by
# hand is one scp, which is what SERVER_SETUP.md tells you to do anyway.
ALLOW_SELF_UPDATE = False

"""
Battlefield Vietnam ships a different set of archives to BF1942: it has effects.rfa and music.rfa,
and has no shaders.rfa or treeMesh.rfa. Names are matched case-insensitively by smart_path_join,
but the relative path is sent to the client verbatim, so it is spelled the way the game does.
"""
ARCHIVES = [
    "Archives/ai.rfa",
    "Archives/aiMeshes.rfa",
    "Archives/animations.rfa",
    "Archives/animations_001.rfa",
    "Archives/effects.rfa",
    "Archives/font.rfa",
    "Archives/menu.rfa",
    "Archives/menu_001.rfa",
    "Archives/music.rfa",
    "Archives/objects.rfa",
    "Archives/objects_001.rfa",
    "Archives/sound.rfa",
    "Archives/sound_001.rfa",
    "Archives/standardMesh.rfa",
    "Archives/standardMesh_001.rfa",
    "Archives/texture.rfa",
    "Archives/texture_001.rfa",
    "Archives/BfVietnam/game.rfa",
]

# LevelCheck.con is the per-mod manifest of archive hashes that the client checks its own .rfa files
# against, so it has to travel with them -- a synced archive set paired with a stale manifest is
# exactly the mismatch the game kicks for.
MOD_MISC_FILES = [
    "contentCrc32.con",
    "init.con",
    "LevelCheck.con",
    "mod.dll",
    "lexiconAll.dat",
    "serverInfo.dds",
    "bfdist.vlu",
]

# Every mod keeps its maps under this fixed subfolder, whatever the mod is called -- the WW2 mod's
# Pacific maps still live in Archives/BfVietnam/Levels.
LEVELS_PATH = "Archives/BfVietnam/Levels"


def get_relevant_mod_names(init_con_path: str) -> list[str]:
    mod_names = []
    with open(init_con_path, 'r') as file:
        for line in file:
            if line.lower().startswith('game.addmodpath'):
                _, mod_path = line.split(' ', 1)
                mod_names.append(mod_path.split("/")[1].strip())
    return mod_names


def get_name_parts(path: str) -> dict[str, str]:
    returner = {"name": "", "patchNumber": None, "extension": ""}
    filename, file_extension = os.path.splitext(path)
    returner["extension"] = file_extension
    filename = os.path.basename(filename)
    last_underscore_pos = filename.rfind("_")
    if last_underscore_pos != -1:
        patch_number = filename[last_underscore_pos + 1:]
        if patch_number.isnumeric():
            returner["patchNumber"] = int(patch_number)
            returner["name"] = filename[0:last_underscore_pos]
        else:
            returner["name"] = filename
    else:
        returner["name"] = filename
    return returner


class DataField42Server:
    def __init__(self, game_directory="", redirect_server_ip=""):
        self.game_directory = game_directory
        self.redirect_server_ip = redirect_server_ip

    def start(self):
        log_info("Starting DataField Vietnam server")
        self.start_heartbeat_and_update_monitor()
        self.start_file_server()

    def start_file_server(self):
        s = socketserver.ThreadingTCPServer(('0.0.0.0', 28901), DataField42TCPHandler, bind_and_activate=False)
        s.allow_reuse_address = True
        s.server_bind()
        s.server_activate()
        s.serve_forever()

    def start_heartbeat_and_update_monitor(self):
        if MASTER_HOST is None:
            log_info("No central database configured: not sending heartbeats.")
            return
        if not ALLOW_SELF_UPDATE:
            log_info(f"Heartbeating to {MASTER_HOST}; self-update is off.")
        threading.Thread(target=self.heartbeat_and_update_monitor_thread).start()

    def heartbeat_and_update_monitor_thread(self):
        connection_to_data_field42_master = ConnectionToDataField42Master()
        while True:
            try:
                master_data_field42_server_version = connection_to_data_field42_master.send_heartbeat()
                if Version(master_data_field42_server_version) > dataField42_server_version:
                    if ALLOW_SELF_UPDATE:
                        connection_to_data_field42_master.update()
                    else:
                        log_info(f"{MASTER_HOST} has {master_data_field42_server_version} "
                                 f"(local {dataField42_server_version}), but self-update is off.")
            except Exception as e:
                log_error(f"Can't send heartbeat to {MASTER_HOST}: {e}")
            time.sleep(60)


class ConnectionToDataField42Master:
    def __init__(self):
        self.socket = None
        self.communication = None

    def connect(self):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.socket.connect((MASTER_HOST, 28901))
        self.communication = DataField42Communication(self.socket, MASTER_HOST)

    def send_heartbeat(self):
        self.connect()
        self.communication.send(f"heartbeatServer {dataField42_server_version}", await_acknowledgement=False)
        version = self.communication.receive_string()
        return version

    def update(self):
        self.connect()
        self.communication.send(f"updateServer {dataField42_server_version}", await_acknowledgement=False)
        file_size = self.communication.receive_int()
        new_script = self.communication.receive_file(file_size)
        self.communication.send_acknowledgement()
        update_and_restart_script(new_script)


def get_mod_chain(game_directory: str, mod_name: str) -> list[str]:
    init_con_path = smart_path_join(game_directory, f"mods/{mod_name}/init.con")
    if init_con_path is None:
        return []
    return get_relevant_mod_names(init_con_path)


def get_mods_shipping_map(game_directory: str, mods_directory: str, map_name: str) -> list[str]:
    """Mods whose own levels folder holds <map_name>.rfa."""
    mods = []
    for entry in os.listdir(mods_directory):
        if not os.path.isdir(os.path.join(mods_directory, entry)):
            continue
        levels_folder = smart_path_join(game_directory, f"mods/{entry}/{LEVELS_PATH}", True)
        if levels_folder is None:
            continue
        for filename in os.listdir(levels_folder):
            file_info = get_name_parts(filename)
            if file_info["name"].lower() == map_name.lower() and file_info["extension"].lower() == ".rfa":
                mods.append(entry)
                break
    return mods


def resolve_mod_name(game_directory: str, mods_directory: str, mod_names: list[str], map_name: str, mod_name: str) -> str:
    """
    Correct the mod the client asked for to the one that actually ships the map.

    A Battlefield Vietnam client cannot know what mod a server runs: its browser parses mapname,
    gametype and hostport out of the query reply but never game_id, so someone sitting in base
    BFVietnam who joins a DiceCity_V server asks us for BFVietnam. That answer is honest and useless
    -- it describes the client, not the server. The map is the discriminator we do have: the server
    is running it, so whichever mod ships it is the mod the client actually needs.

    Deliberately conservative. The request stands unless the map is absent from the requested mod's
    whole chain, so an ordinary missing-map sync is untouched -- including a stock map served under a
    custom mod, where the map lives in the base and the derived mod must still be the answer.
    """
    if map_name == "*":
        return mod_name

    candidates = get_mods_shipping_map(game_directory, mods_directory, map_name)
    if not candidates:
        return mod_name

    candidates_lower = [candidate.lower() for candidate in candidates]

    if mod_name.lower() in mod_names:
        if any(mod.lower() in candidates_lower for mod in get_mod_chain(game_directory, mod_name)):
            return mod_name

    # Several mods can ship the same map name. The one being served is the most derived of them: the
    # one that no other candidate is built on top of.
    resolved = candidates[0]
    for candidate in candidates:
        others = [other for other in candidates if other.lower() != candidate.lower()]
        if not any(candidate.lower() in [mod.lower() for mod in get_mod_chain(game_directory, other)] for other in others):
            resolved = candidate
            break

    log_warning(f"Client asked for mod '{mod_name}', which does not have map '{map_name}'; serving '{resolved}' instead")
    return resolved


def get_files_to_sync(map_name: str, mod_name: str) -> list[list[str]]:
    files = []
    game_directory = dataField42_server.game_directory
    mods_directory = smart_path_join(game_directory, "mods", True)

    if mods_directory is not None:
        mod_names = [item.lower() for item in os.listdir(mods_directory) if os.path.isdir(os.path.join(mods_directory, item))]
        mod_name = resolve_mod_name(game_directory, mods_directory, mod_names, map_name, mod_name)
        if mod_name.lower() in mod_names:
            all_relevant_mod_names = get_relevant_mod_names(smart_path_join(game_directory, f"mods/{mod_name}/init.con"))
            for relevant_mod_name in all_relevant_mod_names:
                mod_folder = smart_path_join(game_directory, f"mods/{relevant_mod_name}", True)
                if mod_folder is None:
                    log_warning(f"Cant find mod: {relevant_mod_name}")
                    return []

                # mod map RFAs:
                levels_folder = smart_path_join(game_directory, f"mods/{relevant_mod_name}/{LEVELS_PATH}", True)
                if levels_folder is not None:
                    for filename in os.listdir(levels_folder):
                        file_info = get_name_parts(filename)
                        if file_info["name"].lower() == map_name.lower() and file_info["extension"].lower() == ".rfa":
                            files.append([relevant_mod_name, f"{LEVELS_PATH}/" + filename, os.path.join(levels_folder, filename), Bf1942FileTypes.level])

                # mod base files:
                for file_path_relative in ARCHIVES:
                    filePath = smart_path_join(mod_folder, file_path_relative)
                    if filePath is not None:
                        files.append([relevant_mod_name, file_path_relative, filePath, Bf1942FileTypes.archive])
                for file_path_relative in MOD_MISC_FILES:
                    filePath = smart_path_join(mod_folder, file_path_relative)
                    if filePath is not None:
                        files.append([relevant_mod_name, file_path_relative, filePath, Bf1942FileTypes.modmiscfile])

                # mod movies:
                movies_folder = smart_path_join(mod_folder, "movies", True)
                if movies_folder is not None:
                    files += [[relevant_mod_name, os.path.relpath(os.path.join(dp, f), mod_folder), os.path.join(dp, f), Bf1942FileTypes.movie]
                              for dp, dn, filenames in os.walk(movies_folder) for f in filenames
                              if os.path.splitext(f)[1].lower() == '.bik']

                # mod music:
                music_folder = smart_path_join(mod_folder, "music", True)
                if music_folder is not None:
                    files += [[relevant_mod_name, os.path.relpath(os.path.join(dp, f), mod_folder), os.path.join(dp, f), Bf1942FileTypes.music]
                              for dp, dn, filenames in os.walk(music_folder) for f in filenames
                              if os.path.splitext(f)[1].lower() == '.bik']
                
                # always use normal slash in path send:
                for file in files:
                    file[1] = file[1].replace('\\', '/')
        else:
            log_warning(f"Cant find mod: {mod_name}")
    else:
        log_error("Can't find mods folder")

    files_after_rules_applied = [file for file in files
                        if sync_rule_manager.get_ignore_file_sync_scenario(FileInfo(os.path.basename(file[1]), file[3], file[0]))
                        == IgnoreSyncScenarios.never]

    return files_after_rules_applied


def download_files(communication: DataField42Communication, map_name: str, mod_name: str, ip: str, port: str, key_hash: str, key_value_pair: dict[str, str] | None = None):
    files = get_files_to_sync(map_name, mod_name)

    # add file sizes and checksums:
    total_size = 0
    for file in files:
        size = os.path.getsize(file[2])
        total_size += size
        file.append(str(size))
        crc32c, sha256 = checksum_repository_manager.get_checksums(file[2])
        file.append(crc32c)
        file.append(sha256)

    files_to_send = []

    file_info_strings = []

    for file in files:
        # mod filePath checksum size lastModified sha256
        #
        # The sha256 is a sixth field appended to the end. Clients built before it read the first five
        # by position and never look further, so adding it here does not disturb them -- which is why
        # it goes on this bulk listing and not on the per-file preamble below, where the client asserts
        # an exact field count and a sixth would be a protocol error.
        file_info_strings.append(f"{file[0]} \"{file[1]}\" {file[5]} {file[4]} {int(os.path.getmtime(file[2]))} {file[6]}")

    communication.send('\n'.join(file_info_strings), await_acknowledgement=False)
    file_info_response_strings = communication.receive_space_separated_string()

    if len(file_info_response_strings) != len(file_info_strings):
        communication.send(f"no 0 0")
        raise Exception(f"file info length response incorrect: {len(file_info_response_strings)} != {len(file_info_strings)}")

    for i, file_info_response_string in enumerate(file_info_response_strings):
        if file_info_response_string == "yes":
            files_to_send.append(files[i])

    total_size = sum(int(file[4]) for file in files_to_send)
    communication.send(f"yes {len(files_to_send)} {total_size}")

    for file in files_to_send:
        with open(file[2], "rb") as f:
            file_bytes = f.read()
        communication.send(f"{file[0]} \"{file[1]}\" {file[5]} {file[4]} {int(os.path.getmtime(file[2]))}")  # mod filePath checksum size lastModified
        communication.send(file_bytes, prepend_with_length=False)

    communication.await_acknowledgement()


# The version reported to clients in the handshake, and the trigger for auto-update: a client whose
# version is lower downloads the exe from update_files/. INVARIANT: this must equal the version of the
# DataFieldVietnam.exe sitting in update_files/, or a client updates, still reads a lower version off the
# new exe, and updates again forever. To push a release: build the client at the new version, drop it
# (and the updater) in update_files/, then set this to match. Keep Major.Minor equal to the clients you
# serve -- the sync path rejects a mismatched Major/Minor -- so bump only the third/fourth number.
dataField42_server_version = Version("2.1.0.4")

# The directory holding the Mods folder to serve, defaulting to the working directory.
#
# Worth passing explicitly on a dedicated server: what the game server runs is a stripped build with
# geometry and sounds removed, and serving those to a client would fail its archive check. The files
# handed out have to be the full client ones, which usually means a separate tree from the live
# server's own game directory.
#
# Built before the checksum manager: constructing that starts a watchdog thread which reads this
# global straight away, so the other order is a race the thread can lose with a NameError, leaving
# checksums to be computed on demand during the first client sync instead of in advance.
dataField42_server = DataField42Server(sys.argv[1] if len(sys.argv) > 1 else "")

checksum_repository_manager = ChecksumRepositoryManager("ChecksumRepository.json")
sync_rule_manager = SyncRuleManager("Synchronization rules.txt")

def check_no_signing_key_is_present():
    """Refuse to start if a release signing key has been copied onto this machine.

    The client only trusts updates because the key that signs them is not here: a compromise of this
    box lets an attacker serve any file, but not sign one. Someone syncing a release folder wholesale
    (scp -r, a backup, a sync tool) would quietly undo that, and nothing else would ever notice.

    So notice loudly, and stop. Being briefly offline is a far smaller problem than serving files from
    a machine that can also sign them.
    """
    if not os.path.isdir(UPDATE_FILES_DIRECTORY):
        return

    key_suffixes = (".p8", ".pem", ".pfx", ".key")
    for directory_path, _, file_names in os.walk(UPDATE_FILES_DIRECTORY):
        for file_name in file_names:
            if file_name.lower().endswith(key_suffixes):
                found = os.path.join(directory_path, file_name)
                log_error("=" * 78)
                log_error(f"REFUSING TO START: what looks like a signing key is on this server: {found}")
                log_error("The release signing key must never leave the machine you build on -- while it")
                log_error("is here, anyone who gets into this box can sign updates as you.")
                log_error("Delete it here, make sure the original is still safe on your build machine,")
                log_error("and upload only the .exe and .sig files.")
                log_error("=" * 78)
                raise SystemExit(1)


# Only bind the socket when run as a script, so the file-gathering can be exercised by importing it.
if __name__ == "__main__":
    check_no_signing_key_is_present()
    log_info(f"Serving client files from: {os.path.abspath(dataField42_server.game_directory or '.')}")
    dataField42_server.start()
