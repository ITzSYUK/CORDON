"""Discord Rich Presence protocol and a headless smoke test of the Qt front end."""

from __future__ import annotations

import json
import os
import struct

import pytest

from cordon.core import discord


# ---------------------------------------------------------------------------- presence
def test_encode_decode_roundtrip():
    payload = {"cmd": "SET_ACTIVITY", "args": {"pid": 42}, "nonce": "n1"}
    raw = discord.encode(discord.OP_FRAME, payload)
    opcode, decoded = discord.decode(raw)
    assert opcode == discord.OP_FRAME
    assert decoded == payload
    assert raw[:8] == struct.pack("<II", discord.OP_FRAME, len(raw) - 8)


def test_decode_garbage_is_safe():
    assert discord.decode(b"") == (0, {})
    assert discord.decode(b"\x01\x00\x00\x00\x05\x00\x00\x00nope!")[0] == discord.OP_FRAME


def test_presence_without_discord_socket_is_silent(monkeypatch):
    monkeypatch.setattr(discord, "find_socket", lambda: None)
    presence = discord.DiscordPresence(client_id="123")
    assert presence.connect() is False
    assert presence.set_activity(details="тест") is False
    assert presence.clear() is False
    assert presence.ping() is False
    presence.close()  # must not raise
    assert discord.announce_launch("123", "Профиль") is None


def test_socket_search_honours_runtime_dir(monkeypatch, tmp_path):
    socket_path = tmp_path / "discord-ipc-0"
    socket_path.write_text("")
    monkeypatch.setenv("XDG_RUNTIME_DIR", str(tmp_path))
    monkeypatch.setattr(discord, "socket_dirs", lambda: [str(tmp_path)])
    assert discord.find_socket() == str(socket_path)
    assert discord.available() is True


def test_presence_writes_frames_to_a_fake_socket(monkeypatch, tmp_path):
    """Talk to a real UNIX socket server to exercise the write path."""
    import socket as socket_mod
    import threading

    path = str(tmp_path / "discord-ipc-0")
    server = socket_mod.socket(socket_mod.AF_UNIX, socket_mod.SOCK_STREAM)
    server.bind(path)
    server.listen(1)
    received: list[bytes] = []

    def accept() -> None:
        connection, _ = server.accept()
        with connection:
            data = connection.recv(4096)
            received.append(data)
            connection.recv(4096)

    thread = threading.Thread(target=accept, daemon=True)
    thread.start()
    monkeypatch.setattr(discord, "find_socket", lambda: path)

    presence = discord.DiscordPresence(client_id="123")
    assert presence.set_activity(details="Играет: Тест", state="CoP", start_timestamp=1) is True
    thread.join(timeout=5)
    server.close()
    presence.close()

    assert received
    opcode, payload = discord.decode(received[0])
    assert opcode in (discord.OP_HANDSHAKE, discord.OP_FRAME)
    if opcode == discord.OP_FRAME:
        assert payload["args"]["activity"]["details"].startswith("Играет")


def test_activity_payload_is_truncated_to_discord_limits(monkeypatch):
    captured: list[dict] = []

    class FakeSocket:
        def connect(self, _path: str) -> None:
            pass

        def sendall(self, raw: bytes) -> None:
            captured.append(discord.decode(raw)[1])

        def settimeout(self, _value) -> None:
            pass

        def close(self) -> None:
            pass

    presence = discord.DiscordPresence(client_id="1")
    monkeypatch.setattr(discord, "find_socket", lambda: "/tmp/fake")
    monkeypatch.setattr(discord.socket, "socket", lambda *_a, **_k: FakeSocket())
    assert presence.set_activity(details="д" * 300, state="с" * 300) is True
    details = captured[-1]["args"]["activity"]["details"]
    assert len(details) == 128


# ---------------------------------------------------------------------------- GUI



@pytest.fixture()
def qt_app(monkeypatch, tmp_path):
    monkeypatch.setenv("QT_QPA_PLATFORM", "offscreen")
    monkeypatch.setenv("QT_LOGGING_RULES", "qt.qpa.*=false")
    libs = os.environ.get("CORDON_TEST_LIBS", "")
    if libs:
        monkeypatch.setenv("LD_LIBRARY_PATH", libs)
    PySide6 = pytest.importorskip("PySide6.QtWidgets", exc_type=ImportError)
    app = PySide6.QApplication.instance() or PySide6.QApplication([])
    return app


def test_gui_window_smoke(qt_app, fake_install, fake_profile, monkeypatch, tmp_path):
    monkeypatch.setenv("XDG_RUNTIME_DIR", str(tmp_path))
    service = __import__("cordon.core.service", fromlist=["CordonService"]).CordonService(fake_install.store)
    service.load()
    service.settings.profiles = [fake_profile]
    service.settings.selected_profile_id = fake_profile.id
    service.save()

    try:
        from cordon.gui.main_window import MainWindow
        from cordon.gui.theme import PDA, stylesheet
    except ImportError as exc:  # missing Qt libraries (libGL & co) on this machine
        pytest.skip(f"PySide6 недоступен: {exc}")

    window = MainWindow(service, profile_id=fake_profile.id)
    assert window.profile_list.count() == 1
    assert window.profile_list.current_profile_id() == fake_profile.id
    assert window.mod_table.rowCount() == 2
    assert window.status_strip.headline.text() == fake_profile.name
    assert stylesheet(PDA)

    # toggling a mod through the table updates the profile and is persisted
    window._mod_toggled("m2", False)
    assert fake_profile.mod_by_id("m2").enabled is False

    # reordering keeps every mod exactly once
    window._mods_reordered(["m2", "m1"])
    assert [mod.id for mod in fake_profile.mods] == ["m2", "m1"]

    # the report pane keeps the plain text for copying
    window.report_pane.set_lines([("ok", "Строка"), ("error", "Проблема")])
    assert window.report_pane.text() == "Строка\nПроблема"

    window.close()


def test_gui_theme_switch(qt_app, fake_install, fake_profile, tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_RUNTIME_DIR", str(tmp_path))
    service = __import__("cordon.core.service", fromlist=["CordonService"]).CordonService(fake_install.store)
    service.load()
    service.settings.profiles = [fake_profile]
    service.settings.theme = "classic"
    service.save()

    try:
        from cordon.gui.main_window import MainWindow
    except ImportError as exc:
        pytest.skip(f"PySide6 недоступен: {exc}")

    window = MainWindow(service)
    assert window._palette.name == "classic"
    window._apply_theme()
    window.close()
