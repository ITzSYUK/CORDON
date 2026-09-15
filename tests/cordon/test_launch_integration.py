"""End-to-end launch against a real (but harmless) executable.

The engine executable is replaced with a copy of ``/usr/bin/true`` (or ``/bin/true``), so the
whole path is exercised for real: overlay preparation, command line building, ``Popen``, log
writing, exit code handling and playtime accounting.
"""

from __future__ import annotations

import os
import shutil

import pytest

from cordon import cli
from cordon.core import launch, util
from cordon.core.models import Profile

TRUE_BINARY = shutil.which("true") or "/bin/true"


def _engine_as_true(fake_install) -> str:
    target = os.path.join(fake_install.game, "bin", "xr_3da")
    shutil.copy2(TRUE_BINARY, target)
    os.chmod(target, 0o755)
    return target


@pytest.fixture()
def true_engine(fake_install):
    if not os.path.isfile(TRUE_BINARY):  # pragma: no cover - non POSIX host
        pytest.skip("нет /bin/true")
    return _engine_as_true(fake_install)


def test_run_profile_starts_a_real_process(true_engine, fake_install, fake_profile):
    outcome = launch.run_profile(fake_profile, fake_install.store)
    assert outcome.returncode == 0
    assert not outcome.crashed
    assert os.path.isfile(outcome.session_log)
    log = util.read_text(outcome.session_log)
    assert "запуск профиля" in log
    assert "-fsltx" in log
    # the profile is marked as played even for a zero length session
    assert fake_profile.last_played_at is not None
    assert outcome.duration >= 0.0


def test_session_captures_output_and_unmounts(fake_install, fake_profile):
    """A tiny shell script instead of the engine, so stdout can be checked."""
    script = os.path.join(fake_install.game, "bin", "xr_3da")
    util.write_text_atomic(
        script,
        "#!/bin/sh\n"
        'echo "X-Ray 1.6 OpenXRay"\n'
        'echo "! cannot find texture: bug"\n'
        "exit 3\n",
    )
    os.chmod(script, 0o755)

    session = launch.start_session(fake_profile, fake_install.store)
    outcome = launch.finish_session(session, fake_install.store)
    assert outcome.returncode == 3
    assert outcome.crashed
    lines = session.lines()
    assert any("OpenXRay" in line for line in lines)
    assert any(line.startswith("!") for line in lines)
    assert os.path.isfile(session.log_path)
    assert "cannot find texture" in util.read_text(session.log_path)
    assert outcome.diagnostics is not None


def test_overlay_is_rebuilt_before_the_first_launch(true_engine, fake_install, fake_profile):
    workspace = launch.finish_session(
        launch.start_session(fake_profile, fake_install.store), fake_install.store
    )
    assert workspace.returncode == 0
    assert os.path.isfile(os.path.join(fake_install.store.profiles_root, "profile-p001", "fsgame.ltx"))


def test_launch_records_arguments_in_the_session_log(true_engine, fake_install, fake_profile):
    fake_profile.engine_flags = ["-nosplash"]
    fake_profile.launch_arguments = "-nointro"
    outcome = launch.run_profile(fake_profile, fake_install.store)
    log = util.read_text(outcome.session_log)
    assert "-nosplash -nointro" in log


def test_cli_detach_returns_immediately(true_engine, fake_install, monkeypatch, capsys, tmp_path):
    monkeypatch.setenv("CORDON_CONFIG_DIR", str(tmp_path / "cfg"))
    monkeypatch.setenv("CORDON_DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("CORDON_CACHE_DIR", str(tmp_path / "cache"))
    assert cli.main(["new", "Detach", "--game", fake_install.game]) == cli.EXIT_OK
    capsys.readouterr()

    assert cli.main(["launch", "Detach", "--detach"]) == cli.EXIT_OK
    output = capsys.readouterr().out
    assert "Игра запущена (pid" in output
    assert "не будет ждать" in output


def test_cli_launch_reports_the_exit_code(true_engine, fake_install, monkeypatch, capsys, tmp_path):
    monkeypatch.setenv("CORDON_CONFIG_DIR", str(tmp_path / "cfg"))
    monkeypatch.setenv("CORDON_DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("CORDON_CACHE_DIR", str(tmp_path / "cache"))
    assert cli.main(["new", "True", "--game", fake_install.game]) == cli.EXIT_OK
    capsys.readouterr()

    assert cli.main(["launch", "True"]) == cli.EXIT_OK
    assert "кодом 0" in capsys.readouterr().out


def test_cli_launch_blocks_on_broken_profile(fake_install, monkeypatch, capsys, tmp_path):
    monkeypatch.setenv("CORDON_CONFIG_DIR", str(tmp_path / "cfg"))
    monkeypatch.setenv("CORDON_DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("CORDON_CACHE_DIR", str(tmp_path / "cache"))
    assert cli.main(["new", "Пустой"]) == cli.EXIT_OK
    capsys.readouterr()

    assert cli.main(["launch", "Пустой"]) == cli.EXIT_BLOCKED
    assert "заблокирован" in capsys.readouterr().err


def test_unmount_is_safe_without_a_mount(fake_install, fake_profile):
    workspace = launch.prepare_launch(fake_profile, fake_install.store)[1]
    workspace.unmount()  # must not raise: nothing is mounted


def test_standalone_profile_launches_from_the_game_directory(true_engine, fake_install, tmp_path):
    build = str(tmp_path / "anomaly")
    shutil.copytree(fake_install.game, build)
    profile = Profile(id="standalone", name="Anomaly", kind="standalone", game_path=build,
                      executable_relative="bin/xr_3da")
    outcome = launch.run_profile(profile, fake_install.store)
    assert outcome.returncode == 0
    assert outcome.plan.cwd == util.norm(build)
