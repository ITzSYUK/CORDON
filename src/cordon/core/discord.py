"""Optional Discord Rich Presence (upstream CORDON has the same feature).

Implemented straight against the UNIX socket IPC protocol Discord exposes on Linux
(``$XDG_RUNTIME_DIR/discord-ipc-0``), so no third-party dependency is needed.  Every failure is
swallowed: presence is a nicety and must never prevent a launch.
"""

from __future__ import annotations

import json
import os
import socket
import struct
import time
from dataclasses import dataclass

from . import util

OP_HANDSHAKE = 0
OP_FRAME = 1
OP_CLOSE = 2
OP_PING = 3
OP_PONG = 4

SOCKET_NAMES = tuple(f"discord-ipc-{index}" for index in range(10))
CONNECT_TIMEOUT = 2.0


def socket_dirs() -> list[str]:
    directories: list[str] = []
    for variable in ("XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP"):
        value = os.environ.get(variable, "").strip()
        if value and os.path.isdir(value):
            directories.append(value)
    directories.append("/tmp")
    # Flatpak/Snap variants of Discord keep their own runtime directories.
    for base in (os.path.expanduser("~/.flatpak"), os.path.expanduser("~/.var/app")):
        if os.path.isdir(base):
            directories.append(base)
    return util.unique(directories)


def find_socket() -> str | None:
    for directory in socket_dirs():
        for name in SOCKET_NAMES:
            candidate = os.path.join(directory, name)
            if os.path.exists(candidate):
                return candidate
        try:
            for entry in os.listdir(directory):
                if entry.startswith("discord-ipc-"):
                    candidate = os.path.join(directory, entry)
                    if os.path.exists(candidate):
                        return candidate
        except OSError:
            continue
    return None


def encode(opcode: int, payload: dict) -> bytes:
    body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    return struct.pack("<II", opcode, len(body)) + body


def decode(raw: bytes) -> tuple[int, dict]:
    if len(raw) < 8:
        return 0, {}
    opcode, length = struct.unpack("<II", raw[:8])
    try:
        payload = json.loads(raw[8 : 8 + length].decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        payload = {}
    return opcode, payload if isinstance(payload, dict) else {}


@dataclass
class DiscordPresence:
    client_id: str
    _socket: socket.socket | None = None
    _connected: bool = False

    # ------------------------------------------------------------------ lifecycle
    def connect(self) -> bool:
        if self._connected:
            return True
        path = find_socket()
        if not path:
            return False
        try:
            sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            sock.settimeout(CONNECT_TIMEOUT)
            sock.connect(path)
            sock.sendall(encode(OP_HANDSHAKE, {"v": 1, "client_id": self.client_id}))
            self._socket = sock
            self._connected = True
            return True
        except OSError:
            self._socket = None
            self._connected = False
            return False

    def close(self) -> None:
        if self._socket is not None:
            try:
                self._socket.sendall(encode(OP_CLOSE, {}))
            except OSError:
                pass
            try:
                self._socket.close()
            except OSError:  # pragma: no cover
                pass
        self._socket = None
        self._connected = False

    # ------------------------------------------------------------------ updates
    def set_activity(
        self,
        *,
        details: str,
        state: str = "",
        start_timestamp: int | None = None,
        large_text: str = "CORDON-LINUX",
    ) -> bool:
        if not self.connect():
            return False
        activity: dict = {
            "details": details[:128],
            "assets": {"large_text": large_text[:128]},
        }
        if state:
            activity["state"] = state[:128]
        if start_timestamp:
            activity["timestamps"] = {"start": int(start_timestamp)}
        payload = {"cmd": "SET_ACTIVITY", "args": {"pid": os.getpid(), "activity": activity}, "nonce": util.new_id()}
        try:
            self._socket.sendall(encode(OP_FRAME, payload))  # type: ignore[union-attr]
            return True
        except OSError:
            self._connected = False
            return False

    def clear(self) -> bool:
        if not self.connect():
            return False
        payload = {"cmd": "SET_ACTIVITY", "args": {"pid": os.getpid(), "activity": None}, "nonce": util.new_id()}
        try:
            self._socket.sendall(encode(OP_FRAME, payload))  # type: ignore[union-attr]
            return True
        except OSError:
            self._connected = False
            return False

    def ping(self) -> bool:
        if not self.connect():
            return False
        try:
            self._socket.sendall(encode(OP_PING, {}))  # type: ignore[union-attr]
        except OSError:
            self._connected = False
            return False
        try:
            raw = self._socket.recv(4096)  # type: ignore[union-attr]
        except OSError:
            return False
        opcode, _payload = decode(raw)
        return opcode in (OP_PONG, OP_FRAME)


def available() -> bool:
    return find_socket() is not None


def announce_launch(client_id: str, profile_name: str, *, game_id: str = "") -> DiscordPresence | None:
    """Best effort presence for a running profile; returns ``None`` when Discord is absent."""
    presence = DiscordPresence(client_id=client_id)
    state = {"cop": "Call of Pripyat", "cs": "Clear Sky", "coc": "Call of Chernobyl / Anomaly"}.get(
        game_id, "S.T.A.L.K.E.R."
    )
    if not presence.set_activity(
        details=f"Играет: {profile_name}",
        state=f"{state} · через CORDON-LINUX",
        start_timestamp=int(time.time()),
    ):
        presence.close()
        return None
    return presence
