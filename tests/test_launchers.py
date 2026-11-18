import json
import os
from pathlib import Path
import shutil
import subprocess

import pytest


ROOT = Path(__file__).resolve().parents[1]
NODE = shutil.which("node")
POWERSHELL = shutil.which("powershell.exe") or shutil.which("powershell")
LAN_FILES = (
    "Start-BirdLabLanApi.ps1",
    "Stop-BirdLabLanApi.ps1",
    "BirdLabLanApi.Common.ps1",
    "birdlab-lan-api-proxy.cjs",
)


def ps_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def run_powershell(command):
    if not POWERSHELL:
        pytest.skip("Windows PowerShell is required for the Windows launcher checks")
    result = subprocess.run(
        [POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", command],
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    return result.stdout.strip()


@pytest.fixture
def portable_lan_repo(tmp_path):
    target = tmp_path / "Portable Bird Lab" / "scripts"
    target.mkdir(parents=True)
    for name in LAN_FILES:
        shutil.copyfile(ROOT / "scripts" / name, target / name)
    return target.parent


def test_node_lan_proxy_security_behavior():
    if not NODE:
        pytest.skip("Node.js is required for LAN proxy checks")
    result = subprocess.run(
        [NODE, "--test", str(ROOT / "tests" / "lan_api_proxy.test.cjs")],
        cwd=ROOT,
        capture_output=True,
        text=True,
        timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr


@pytest.mark.parametrize("filename", LAN_FILES[:3])
def test_lan_launchers_parse_in_windows_powershell(filename):
    result = run_powershell(
        "$ErrorActionPreference='Stop'; $tokens=$null; $errors=$null; "
        "[void][Management.Automation.Language.Parser]::ParseFile("
        + ps_literal(ROOT / "scripts" / filename)
        + ",[ref]$tokens,[ref]$errors); "
        "if($errors.Count){throw ($errors|Out-String)}; 'parsed'"
    )
    assert result == "parsed"


def test_public_profile_fails_before_listener_or_firewall_access(portable_lan_repo):
    script = portable_lan_repo / "scripts" / "Start-BirdLabLanApi.ps1"
    result = run_powershell(
        "$ErrorActionPreference='Stop'; "
        "function Get-NetIPAddress { param($AddressFamily,$ErrorAction) "
        "[pscustomobject]@{IPAddress='192.168.8.10';AddressState=4;InterfaceIndex=11} }; "
        "function Get-NetConnectionProfile { param($InterfaceIndex,$ErrorAction) "
        "[pscustomobject]@{NetworkCategory='Public'} }; "
        "function Get-NetFirewallProfile { throw 'Unexpected firewall access' }; "
        "function Start-Process { throw 'Unexpected process creation' }; "
        "$refused=$false; try { & "
        + ps_literal(script)
        + " -TrustedSubnet '192.168.8.0/24' -BindAddress '192.168.8.10' } "
        "catch { if($_.Exception.Message -notlike '*requires explicit -AllowPublicProfile*'){throw}; $refused=$true }; "
        "if(!$refused){throw 'Public listener was not refused'}; 'public-refused'"
    )
    assert result == "public-refused"
    assert not (portable_lan_repo / "data").exists()


def test_ambiguous_interface_requires_explicit_bind(portable_lan_repo):
    script = portable_lan_repo / "scripts" / "Start-BirdLabLanApi.ps1"
    result = run_powershell(
        "$ErrorActionPreference='Stop'; "
        "function Get-NetIPAddress { param($AddressFamily,$ErrorAction) "
        "[pscustomobject]@{IPAddress='10.42.0.10';AddressState=4;InterfaceIndex=11}; "
        "[pscustomobject]@{IPAddress='10.42.0.20';AddressState=4;InterfaceIndex=12} }; "
        "function Get-NetConnectionProfile { throw 'Network profile must not be read for ambiguous interfaces' }; "
        "$refused=$false; try { & "
        + ps_literal(script)
        + " -TrustedSubnet '10.42.0.0/16' } "
        "catch { if($_.Exception.Message -notlike '*missing or ambiguous*'){throw}; $refused=$true }; "
        "if(!$refused){throw 'Ambiguous interface was not refused'}; 'ambiguous-refused'"
    )
    assert result == "ambiguous-refused"


def test_stop_without_state_does_not_target_other_repositories(portable_lan_repo):
    result = run_powershell(
        "$ErrorActionPreference='Stop'; & "
        + ps_literal(portable_lan_repo / "scripts" / "Stop-BirdLabLanApi.ps1")
    )
    assert json.loads(result) == {
        "Status": "NotRunning",
        "Stopped": False,
        "UnrelatedProcessesChanged": False,
    }


def test_pid_identity_checks_executable_command_and_start_time(portable_lan_repo):
    common = portable_lan_repo / "scripts" / "BirdLabLanApi.Common.ps1"
    proxy_path = portable_lan_repo / "scripts" / "birdlab-lan-api-proxy.cjs"
    result = run_powershell(
        "$ErrorActionPreference='Stop'; . "
        + ps_literal(common)
        + "; $state=[pscustomobject]@{ProcessId=123;NodePath='C:\\Program Files\\nodejs\\node.exe';ProxyPath="
        + ps_literal(proxy_path)
        + ";ProcessStartedUtc='2026-01-01T00:00:00.0000000Z';BindAddress='10.42.0.10';TrustedSubnet='10.42.0.0/16';Port=55100;UpstreamPort=5100}; "
        "$command='\"'+$state.NodePath+'\" \"'+$state.ProxyPath+'\" '+((Get-BirdLanArguments $state)-join ' '); "
        "$actual=[pscustomobject]@{ProcessId=123;Name='node.exe';ExecutablePath=$state.NodePath;CommandLine=$command}; "
        "if(!(Test-BirdLanProcessIdentity $actual $state $state.ProcessStartedUtc)){throw 'Exact identity was rejected'}; "
        "$actual.CommandLine=$command+' --extra'; "
        "if(Test-BirdLanProcessIdentity $actual $state $state.ProcessStartedUtc){throw 'Extra CLI was accepted'}; "
        "$actual.CommandLine=$command.Replace('--port 55100','--port 55101'); "
        "if(Test-BirdLanProcessIdentity $actual $state $state.ProcessStartedUtc){throw 'Wrong port was accepted'}; "
        "$actual.CommandLine=$command; $actual.ExecutablePath='C:\\unrelated\\node.exe'; "
        "if(Test-BirdLanProcessIdentity $actual $state $state.ProcessStartedUtc){throw 'Wrong executable was accepted'}; "
        "$actual.ExecutablePath=$state.NodePath; "
        "if(Test-BirdLanProcessIdentity $actual $state '2026-01-02T00:00:00.0000000Z'){throw 'Reused PID start time was accepted'}; "
        "$actual.ProcessId=124; "
        "if(Test-BirdLanProcessIdentity $actual $state $state.ProcessStartedUtc){throw 'Different PID was accepted'}; 'identity-safe'"
    )
    assert result == "identity-safe"


def test_stop_refuses_unrelated_node_with_reused_state(portable_lan_repo):
    if not NODE or not POWERSHELL or os.name != "nt":
        pytest.skip("Windows Node.js and PowerShell are required")
    sentinel = subprocess.Popen(
        [NODE, "-e", "setInterval(()=>{},1000)"],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    try:
        runtime = portable_lan_repo / "data" / "runtime"
        runtime.mkdir(parents=True)
        state = {
            "SchemaVersion": 1,
            "ProcessId": sentinel.pid,
            "ProcessStartedUtc": "",
            "NodePath": str(Path(NODE).resolve()),
            "ProxyPath": str(portable_lan_repo / "scripts" / "birdlab-lan-api-proxy.cjs"),
            "BindAddress": "192.168.8.10",
            "TrustedSubnet": "192.168.8.0/24",
            "Port": 5100,
            "UpstreamPort": 5100,
        }
        state_path = runtime / "lan-api-proxy.state.json"
        state_path.write_text(json.dumps(state), encoding="utf-8")
        result = run_powershell(
            "$ErrorActionPreference='Stop'; $statePath="
            + ps_literal(state_path)
            + "; $state=[IO.File]::ReadAllText($statePath)|ConvertFrom-Json; "
            "$state.ProcessStartedUtc=([Diagnostics.Process]::GetProcessById("
            + str(sentinel.pid)
            + ")).StartTime.ToUniversalTime().ToString('o'); "
            "[IO.File]::WriteAllText($statePath,($state|ConvertTo-Json)); "
            "$refused=$false; try { & "
            + ps_literal(portable_lan_repo / "scripts" / "Stop-BirdLabLanApi.ps1")
            + " } catch { if($_.Exception.Message -notlike '*PID identity changed*'){throw}; $refused=$true }; "
            "if(!$refused){throw 'Unrelated Node was not refused'}; 'unrelated-preserved'"
        )
        assert result == "unrelated-preserved"
        assert sentinel.poll() is None
        assert state_path.exists()
    finally:
        sentinel.terminate()
        sentinel.wait(timeout=5)

@pytest.mark.skipif(os.name != "nt", reason="Windows launcher checks require Windows")
@pytest.mark.parametrize("engine", list(dict.fromkeys(filter(None, [POWERSHELL, shutil.which("pwsh")]))))
def test_persisted_process_timestamp_remains_exact_across_powershell_versions(portable_lan_repo, engine):
    common = portable_lan_repo / "scripts" / "BirdLabLanApi.Common.ps1"
    command = (
        "$ErrorActionPreference='Stop'; . " + ps_literal(common)
        + "; $context=Get-BirdLanContext; [void][IO.Directory]::CreateDirectory($context.RuntimeRoot); "
        "$stamp='2026-01-01T00:00:00.1234567Z'; "
        "$state=[pscustomobject]@{SchemaVersion=1;ProcessId=123;NodePath=$context.NodePath;ProxyPath=$context.ProxyPath;"
        "ProcessStartedUtc=$stamp;BindAddress='10.42.0.10';TrustedSubnet='10.42.0.0/16';Port=55100;UpstreamPort=5100}; "
        "Write-BirdLanState $context $state; $loaded=Read-BirdLanState $context; "
        "if($loaded.ProcessStartedUtc -isnot [string] -or $loaded.ProcessStartedUtc -cne $stamp){throw 'JSON changed the exact process timestamp'}; "
        "$actual=[pscustomobject]@{ProcessId=123;Name='node.exe';ExecutablePath=$context.NodePath;"
        "CommandLine=('\"'+$context.NodePath+'\" \"'+$context.ProxyPath+'\" '+((Get-BirdLanArguments $loaded)-join ' '))}; "
        "if(!(Test-BirdLanProcessIdentity $actual $loaded $stamp)){throw 'Persisted process identity changed'}; 'roundtrip-safe'"
    )
    result = subprocess.run([engine, "-NoProfile", "-NonInteractive", "-Command", command], capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, result.stdout + result.stderr
    assert result.stdout.strip() == "roundtrip-safe"
