"""``fuse-overlayfs`` plumbing: mount detection, command construction and unmounting.

These tests guard two bugs that were found by running the launcher for real:

* ``is_mounted`` used to treat *any* path below *any* mount point as mounted, and since ``/`` is a
  mount point that meant "everything is mounted" - the overlay was never actually mounted;
* the unmount attempt list was built with wrong operator precedence, so the launcher tried to
  execute ``-uz`` as a program instead of running ``fusermount3 -uz <point>``.
"""

from __future__ import annotations

import os
import subprocess

import pytest

from cordon.core import layers, mounts, util


def test_is_mounted_only_for_real_mount_points(fake_install, tmp_path):
    plain = tmp_path / "plain"
    plain.mkdir()
    assert mounts.is_mounted(str(plain)) is False
    assert mounts.is_mounted(str(fake_install.game)) is False
    assert mounts.is_mounted(str(tmp_path / "does-not-exist")) is False
    # /proc is always a mount point on Linux
    assert mounts.is_mounted("/proc") is True
    assert mounts.is_mounted("/proc/sys/fs/binfmt_misc") in (True, False)  # depends on the kernel


def test_mount_for_plan_orders_layers_by_priority(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    overlay = mounts.mount_for_plan(plan, "/tmp/cordon-preview")
    assert overlay.mount_point == "/tmp/cordon-preview/gamedata"
    assert overlay.upper_dir == "/tmp/cordon-preview/.cordon-overlay/upper"
    assert overlay.work_dir == "/tmp/cordon-preview/.cordon-overlay/work"
    assert overlay.lower_dirs, "должен быть хотя бы один слой gamedata"
    # the highest priority layer must come first in lowerdir
    priorities = [priority for priority, _path in mounts.layer_gamedata_dirs(plan)]
    assert priorities == sorted(priorities, reverse=True)
    assert overlay.lower_dirs[0] == fake_install.mods["b"] + "/gamedata"


def test_mount_command_uses_posix_paths(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    overlay = mounts.mount_for_plan(plan, "/tmp/cordon-preview")
    command = overlay.command(executable="/usr/bin/fuse-overlayfs")
    assert command[0] == "/usr/bin/fuse-overlayfs"
    assert command[1:3] == ["-f", "-o"]
    options = command[3]
    assert options.startswith("lowerdir=")
    assert ":".join(overlay.lower_dirs) in options
    assert f"upperdir={overlay.upper_dir}" in options
    assert f"workdir={overlay.work_dir}" in options
    assert command[4] == overlay.mount_point
    assert "\\" not in " ".join(command)
    assert "нижний слой" in overlay.describe()


def test_mount_prepares_directories(fake_install, fake_profile, tmp_path):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    overlay = mounts.mount_for_plan(plan, str(tmp_path / "profile"))
    overlay.prepare_dirs()
    assert os.path.isdir(overlay.mount_point)
    assert os.path.isdir(overlay.upper_dir)
    assert os.path.isdir(overlay.work_dir)


def test_mount_without_layers_raises(fake_install, tmp_path):
    from cordon.core.errors import OverlayError

    plan = layers.build_plan(fake_install.profile(), engine_data_path="")
    overlay = mounts.OverlayMount(
        mount_point=str(tmp_path / "gamedata"),
        upper_dir=str(tmp_path / "u"),
        work_dir=str(tmp_path / "w"),
        lower_dirs=[],
    )
    if not mounts.detect_tools().find("fuse-overlayfs"):
        with pytest.raises(Exception) as excinfo:
            overlay.mount()
        assert "fuse-overlayfs" in str(excinfo.value)
        return
    with pytest.raises(OverlayError):
        overlay.mount()


def test_unmount_runs_a_single_command(monkeypatch, tmp_path):
    overlay = mounts.OverlayMount(
        mount_point=str(tmp_path / "gamedata"),
        upper_dir=str(tmp_path / "u"),
        work_dir=str(tmp_path / "w"),
        lower_dirs=[],
    )
    state = {"mounted": True}
    monkeypatch.setattr(mounts, "is_mounted", lambda _path: state["mounted"])
    monkeypatch.setattr(mounts.shutil, "which", lambda name: f"/usr/bin/{name}")
    commands: list[list[str]] = []

    def fake_run(command, **_kwargs):
        commands.append(list(command))
        state["mounted"] = False  # the first attempt succeeds
        return subprocess.CompletedProcess(command, 0, "", "")

    monkeypatch.setattr(mounts.subprocess, "run", fake_run)
    overlay.unmount()
    assert commands == [["/usr/bin/fusermount3", "-uz", overlay.mount_point]]


def test_unmount_falls_back_to_umount(monkeypatch, tmp_path):
    overlay = mounts.OverlayMount(
        mount_point=str(tmp_path / "gamedata"),
        upper_dir=str(tmp_path / "u"),
        work_dir=str(tmp_path / "w"),
        lower_dirs=[],
    )
    state = {"mounted": True, "calls": 0}
    monkeypatch.setattr(mounts, "is_mounted", lambda _path: state["mounted"])
    monkeypatch.setattr(mounts.shutil, "which", lambda name: f"/usr/bin/{name}" if name == "fusermount3" else None)
    commands: list[list[str]] = []

    def fake_run(command, **_kwargs):
        commands.append(list(command))
        state["calls"] += 1
        if state["calls"] >= 2:
            state["mounted"] = False
        return subprocess.CompletedProcess(command, 0 if state["calls"] >= 2 else 1, "", "busy")

    monkeypatch.setattr(mounts.subprocess, "run", fake_run)
    overlay.unmount()
    assert commands[0][1:] == ["-uz", overlay.mount_point]
    assert commands[1][:2] == ["umount", "-l"]


def test_unmount_reports_a_stubborn_mount(monkeypatch, tmp_path):
    from cordon.core.errors import OverlayError

    overlay = mounts.OverlayMount(
        mount_point=str(tmp_path / "gamedata"),
        upper_dir=str(tmp_path / "u"),
        work_dir=str(tmp_path / "w"),
        lower_dirs=[],
    )
    monkeypatch.setattr(mounts, "is_mounted", lambda _path: True)
    monkeypatch.setattr(mounts.shutil, "which", lambda _name: None)

    def fake_run(command, **_kwargs):
        return subprocess.CompletedProcess(command, 1, "", "device is busy")

    monkeypatch.setattr(mounts.subprocess, "run", fake_run)
    with pytest.raises(OverlayError) as excinfo:
        overlay.unmount()
    assert "device is busy" in str(excinfo.value)


def test_unmount_ignores_missing_tools(monkeypatch, tmp_path):
    overlay = mounts.OverlayMount(
        mount_point=str(tmp_path / "gamedata"),
        upper_dir=str(tmp_path / "u"),
        work_dir=str(tmp_path / "w"),
        lower_dirs=[],
    )
    state = {"mounted": True}
    monkeypatch.setattr(mounts, "is_mounted", lambda _path: state["mounted"])
    monkeypatch.setattr(mounts.shutil, "which", lambda _name: "/usr/bin/fusermount3")

    def fake_run(command, **_kwargs):
        raise FileNotFoundError(2, "No such file or directory", command[0])

    monkeypatch.setattr(mounts.subprocess, "run", fake_run)
    with pytest.raises(Exception):
        overlay.unmount()
    state["mounted"] = False
    overlay.unmount()  # nothing is mounted any more → silent no-op


def test_fuse_backend_fails_before_destroying_the_previous_build(fake_install, fake_profile):
    """A missing fuse-overlayfs must not leave the profile half-rebuilt."""
    from cordon.core.errors import ToolMissingError
    from cordon.core.overlay import ProfileWorkspace

    if mounts.detect_tools().find("fuse-overlayfs"):  # pragma: no cover - host dependant
        pytest.skip("fuse-overlayfs установлен")

    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    workspace = ProfileWorkspace(fake_install.store, fake_profile)
    workspace.prepare(plan, engine_executable=os.path.join(fake_install.game, "bin", "xr_3da"))
    assert os.path.isdir(workspace.game_data)
    assert os.path.isfile(workspace.fsgame_path)

    fake_profile.backend = "fuse-overlayfs"
    with pytest.raises(ToolMissingError):
        workspace.prepare(plan, engine_executable=os.path.join(fake_install.game, "bin", "xr_3da"), force=True)
    # nothing was deleted on the way
    assert os.path.isdir(workspace.game_data)
    assert os.path.isfile(workspace.fsgame_path)
    assert os.path.isfile(os.path.join(workspace.root, "build-manifest.json"))


def test_require_tool_hint(fake_install):
    from cordon.core.errors import ToolMissingError

    if mounts.detect_tools().find("fuse-overlayfs"):  # pragma: no cover - host dependant
        pytest.skip("fuse-overlayfs установлен")
    with pytest.raises(ToolMissingError) as excinfo:
        mounts.require_tool("fuse-overlayfs")
    assert "fuse" in str(excinfo.value).lower()


def test_proc_mounts_parsing_is_sane():
    table = mounts.mounts()
    assert "/" in table
    assert table["/"].startswith("/")


def test_paths_written_to_the_engine_are_posix(fake_install, fake_profile):
    plan = layers.build_plan(fake_profile, engine_data_path=fake_install.engine_data)
    plan.index()
    overlay = mounts.mount_for_plan(plan, "/tmp/profile")
    for directory in overlay.lower_dirs:
        assert "\\" not in directory
    # game-relative paths are normalised to the slash form, the engine gets backslashes
    assert util.to_posix("gamedata\\configs\\system.ltx") == "gamedata/configs/system.ltx"
    assert util.to_engine("gamedata/configs/system.ltx") == "gamedata\\configs\\system.ltx"
    with pytest.raises(Exception):
        util.to_posix("../../etc/passwd")
