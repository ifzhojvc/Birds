import os
from pathlib import Path
import shutil
import subprocess

import pytest

ROOT = Path(__file__).resolve().parents[1]
POWERSHELL = shutil.which("powershell.exe")
pytestmark = pytest.mark.skipif(os.name != "nt" or not POWERSHELL, reason="Windows launcher integration checks require Windows PowerShell")


def literal(value):
    return "'" + str(value).replace("'", "''") + "'"


@pytest.fixture
def portable_stack(tmp_path):
    root = tmp_path / "Portable Bird Lab"
    scripts = root / "scripts"
    scripts.mkdir(parents=True)
    for name in ("Start-BirdLabStack.ps1", "Start-BirdLabRuntime.ps1", "BirdLabStackTools.psm1", "compose.runtime.yaml"):
        shutil.copyfile(ROOT / "scripts" / name, scripts / name)
    for name in ("compose.yaml", "compose.override.yaml"):
        shutil.copyfile(ROOT / name, root / name)
    (root / ".env").write_text("", encoding="utf-8")
    tools = tmp_path / "fake tools"
    tools.mkdir()
    log = tmp_path / "docker-arguments.txt"
    (tools / "docker.cmd").write_text('@echo off\n>>"' + str(log) + '" echo %*\nexit /b 0\n', encoding="ascii")
    return root, tools, log


def invoke(root, tools, arguments):
    command = "$ErrorActionPreference='Stop'; $env:PATH=" + literal(str(tools) + os.pathsep) + "+$env:PATH; & " + literal(root / "scripts" / "Start-BirdLabStack.ps1") + " -DockerMode Docker " + arguments
    return subprocess.run([POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", command], capture_output=True, text=True, timeout=30)


def test_clean_clone_check_only_needs_no_private_launcher_or_runtime(portable_stack):
    root, tools, log = portable_stack
    result = invoke(root, tools, "-CheckOnly")
    assert result.returncode == 0, result.stdout + result.stderr
    calls = log.read_text().splitlines()
    assert len(calls) == 1 and calls[0].endswith("config --quiet")
    assert str(root / ".env") in calls[0]
    assert str(root / "compose.yaml") in calls[0]
    assert not (root / "data").exists()


def test_generic_build_launches_only_explicit_compose_workflow(portable_stack):
    root, tools, log = portable_stack
    result = invoke(root, tools, "-Build")
    assert result.returncode == 0, result.stdout + result.stderr
    calls = log.read_text().splitlines()
    assert len(calls) == 4
    assert calls[0].endswith("config --quiet")
    assert calls[1].endswith("up -d --no-recreate --build")
    assert calls[2].endswith("wait blockchain-bind")
    assert calls[3].endswith("ps -a")
    assert not (root / "data").exists()


def test_transition_uses_tracked_runtime_compose_without_private_override(portable_stack):
    root, tools, log = portable_stack
    runtime = root / "data" / "runtime"
    runtime.mkdir(parents=True)
    (runtime / ".env.transition").write_text("", encoding="utf-8")
    result = invoke(root, tools, "-CheckOnly")
    assert result.returncode == 0, result.stdout + result.stderr
    call = log.read_text()
    assert str(runtime / ".env.transition") in call
    assert str(root / "scripts" / "compose.runtime.yaml") in call
    assert not (runtime / "compose.transition.yaml").exists()


def test_transition_refuses_implicit_image_replacement(portable_stack):
    root, tools, log = portable_stack
    runtime = root / "data" / "runtime"
    runtime.mkdir(parents=True)
    (runtime / ".env.transition").write_text("", encoding="utf-8")
    result = invoke(root, tools, "-Build")
    assert result.returncode != 0
    assert "Build transition images explicitly" in result.stderr
    assert not log.exists()


def test_missing_local_secrets_fail_before_docker_is_invoked(portable_stack):
    root, tools, log = portable_stack
    (root / ".env").unlink()
    result = invoke(root, tools, "-CheckOnly")
    assert result.returncode != 0
    assert "Create local .env" in result.stderr
    assert not log.exists()


def test_runtime_never_starts_docker_desktop_storage_distribution(portable_stack):
    root, tools, log = portable_stack
    command = "& " + literal(root / "scripts" / "Start-BirdLabRuntime.ps1") + " -Distribution docker-desktop -CheckOnly"
    result = subprocess.run([POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", command], capture_output=True, text=True, timeout=30)
    assert result.returncode != 0
    assert "not Docker Desktop storage" in result.stderr
    assert not (root / "data").exists()