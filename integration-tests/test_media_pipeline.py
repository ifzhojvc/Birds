import io
import os
import subprocess
import time
from pathlib import Path
from urllib.parse import urlparse
from uuid import uuid4

import numpy as np
import pytest
import requests
import soundfile as sf
from PIL import Image

from scripts.local_blockchain import Anvil


ROOT = Path(__file__).resolve().parents[1]
API = os.environ.get("BIRDLAB_TEST_API", "")
RPC = os.environ.get("BIRDLAB_TEST_ANVIL", "")
pytestmark = pytest.mark.skipif(not API or not RPC, reason="Requires a dedicated disposable Compose stack.")


def request(session, method, path, status=200, **kwargs):
    response = session.request(method, API.rstrip("/") + path, timeout=120, **kwargs)
    assert response.status_code == status, f"{method} {path}: HTTP {response.status_code}, expected {status}"
    return response.json() if response.content else None


def terminal(session, task):
    deadline = time.monotonic() + 300
    while time.monotonic() < deadline:
        value = request(session, "GET", task["statusUrl"].removeprefix("/api"))
        if value["status"] in {"Completed", "Failed"}:
            return value
        time.sleep(.3)
    pytest.fail("The real RabbitMQ analysis did not reach a terminal state.")


def remaining(session):
    return request(session, "GET", "/subscriptions/usage")["remaining"]


@pytest.fixture(scope="module")
def free_account():
    endpoint, rpc = urlparse(API), urlparse(RPC)
    assert endpoint.hostname in {"127.0.0.1", "localhost"} and endpoint.port not in {None, 5100}
    assert rpc.hostname in {"127.0.0.1", "localhost"} and rpc.port not in {None, 8545}
    session = requests.Session()
    account = request(session, "POST", "/auth/register", 201, json={
        "email": f"media-qa-{uuid4().hex}@example.test",
        "password": "Bird!9Aa" + uuid4().hex,
        "useBearerToken": True,
    })
    session.headers["Authorization"] = "Bearer " + account["accessToken"]
    yield session
    session.close()


def paid_session(plan, payer_index):
    endpoint, rpc = urlparse(API), urlparse(RPC)
    assert endpoint.hostname in {"127.0.0.1", "localhost"} and endpoint.port not in {None, 5100}
    assert rpc.hostname in {"127.0.0.1", "localhost"} and rpc.port not in {None, 8545}
    session = requests.Session()
    account = request(session, "POST", "/auth/register", 201, json={
        "email": f"payment-qa-{uuid4().hex}@example.test",
        "password": "Bird!9Aa" + uuid4().hex,
        "useBearerToken": True,
    })
    session.headers["Authorization"] = "Bearer " + account["accessToken"]
    network = request(session, "GET", "/subscriptions/network")
    assert network["chainId"] == 31337 and network["testMode"] is True
    assert network["rpcUrl"].rstrip("/") == RPC.rstrip("/")
    assert request(session, "GET", "/subscriptions/readiness")["ready"] is True
    chain = Anvil(RPC)
    chain.require_local()
    accounts = chain.rpc("eth_accounts")
    owner, payer = accounts[0], accounts[payer_index]
    nonce = request(session, "POST", "/wallets/nonce", json={"walletAddress": payer})
    signature = chain.rpc("personal_sign", ["0x" + nonce["message"].encode().hex(), payer])
    request(session, "POST", "/wallets/verify", json={"nonce": nonce["nonce"], "message": nonce["message"], "signature": signature})
    order = request(session, "POST", "/subscriptions/orders", 201, json={"planId": plan, "walletAddress": payer})
    chain.call(owner, network["tokenAddress"], "mint(address,uint256)", payer, int(order["amount"]))
    chain.call(payer, network["tokenAddress"], "approve(address,uint256)", network["paymentContractAddress"], int(order["amount"]))
    transaction = chain.call(payer, network["paymentContractAddress"], "purchaseSubscription(bytes32,uint256)", order["orderId"], plan)
    chain.rpc("anvil_mine", [hex(network["confirmations"])])
    payment = request(session, "POST", "/subscriptions/payments/verify", json={"orderId": order["id"], "transactionHash": transaction})
    assert payment["status"] == "Confirmed"
    period = request(session, "GET", "/subscriptions/me")
    assert period["planId"] == plan
    replay = request(session, "POST", "/subscriptions/payments/verify", json={"orderId": order["id"], "transactionHash": transaction})
    assert replay["status"] == "Confirmed"
    assert request(session, "GET", "/subscriptions/me")["endDate"] == period["endDate"]
    yield session
    session.close()


@pytest.fixture(scope="module")
def paid_account():
    yield from paid_session(1, 3)


@pytest.fixture(scope="module")
def premium_account():
    yield from paid_session(2, 4)


def black_image():
    buffer = io.BytesIO()
    Image.new("RGB", (256, 256), "black").save(buffer, "PNG")
    return buffer.getvalue()


def test_free_rejected_photo_releases_quota_and_stays_out_of_history(free_account):
    before = remaining(free_account)
    accepted = request(free_account, "POST", "/analysis/image", 202, files={"file": ("empty.png", black_image(), "image/png")})
    status = terminal(free_account, accepted)
    assert status["status"] == "Failed" and "птах" in status["errorMessage"]
    assert status.get("result") is None
    assert remaining(free_account) == before
    assert request(free_account, "GET", "/analyses")["total"] == 0
    assert terminal(free_account, accepted)["status"] == "Failed"
    assert remaining(free_account) == before


def test_real_bird_completes_top_three_through_broker_and_sql(paid_account):
    before = remaining(paid_account)
    image = ROOT / "sample_images/01_Cardinalis_cardinalis.jpg"
    accepted = request(paid_account, "POST", "/analysis/image", 202, files={"file": (image.name, image.read_bytes(), "image/jpeg")})
    status = terminal(paid_account, accepted)
    assert status["status"] == "Completed"
    assert len(status["result"]["predictions"]) == 3
    assert remaining(paid_account) == before - 1
    assert request(paid_account, "GET", f"/analyses/{accepted['historyId']}")["id"] == accepted["historyId"]
    request(paid_account, "GET", "/statistics/summary")
    request(paid_account, "GET", "/statistics/species")


def test_batch_uses_the_same_content_gate(premium_account):
    before = remaining(premium_account)
    image = ROOT / "sample_images/01_Cardinalis_cardinalis.jpg"
    accepted = request(premium_account, "POST", "/analysis/batch", 202, files=[
        ("files", (image.name, image.read_bytes(), "image/jpeg")),
        ("files", ("empty.png", black_image(), "image/png")),
    ])
    assert len(accepted) == 2
    statuses = [terminal(premium_account, item["task"]) for item in accepted]
    assert [value["status"] for value in statuses] == ["Completed", "Failed"]
    assert statuses[1].get("result") is None
    assert remaining(premium_account) == before - 1


def test_robin_presence_does_not_force_an_unsupported_species(paid_account):
    before = remaining(paid_account)
    audio = ROOT / "sample_audio/robin.wav"
    accepted = request(paid_account, "POST", "/analysis/audio", 202, files={"file": (audio.name, audio.read_bytes(), "audio/wav")})
    status = terminal(paid_account, accepted)
    if status["status"] == "Failed":
        assert status["errorMessage"] == "Звуки птаха виявлено, але визначити вид із достатньою впевненістю не вдалося."
        assert status.get("result") is None
        assert remaining(paid_account) == before
    else:
        assert len(status["result"]["predictions"]) == 3
        assert status["result"]["predictions"][0]["speciesName"] != "background"
        assert remaining(paid_account) == before - 1


@pytest.mark.parametrize("kind,name,data", [
    ("image", "broken.jpg", b"invalid jpeg"),
    ("audio", "broken.wav", b"invalid wave"),
])
def test_corrupted_files_fail_without_consuming_pro_quota(paid_account, kind, name, data):
    before = remaining(paid_account)
    accepted = request(paid_account, "POST", f"/analysis/{kind}", 202, files={"file": (name, data)})
    status = terminal(paid_account, accepted)
    assert status["status"] == "Failed" and status.get("result") is None
    assert remaining(paid_account) == before


def test_silence_and_white_noise_do_not_produce_species(paid_account):
    before = remaining(paid_account)
    for samples in (np.zeros(32000, dtype=np.float32), np.random.default_rng(7).normal(0, .1, 32000).astype(np.float32)):
        buffer = io.BytesIO()
        sf.write(buffer, samples, 16000, format="WAV")
        accepted = request(paid_account, "POST", "/analysis/audio", 202, files={"file": ("nonbird.wav", buffer.getvalue(), "audio/wav")})
        status = terminal(paid_account, accepted)
        assert status["status"] == "Failed" and status.get("result") is None
        assert remaining(paid_account) == before


@pytest.mark.skipif(not os.environ.get("BIRDLAB_TEST_ANVIL_CONTAINER"), reason="Requires an owned disposable Anvil container.")
def test_anvil_outage_and_restart_preserve_pro_and_old_payments(paid_account, premium_account):
    container = os.environ["BIRDLAB_TEST_ANVIL_CONTAINER"]
    project = os.environ["BIRDLAB_TEST_COMPOSE_PROJECT"]
    assert project.startswith(("birdlab-task-", "birds-subscription-check-"))
    label = subprocess.check_output(["docker", "inspect", "--format", '{{index .Config.Labels "com.docker.compose.project"}}', container], text=True).strip()
    assert label == project
    periods = [request(session, "GET", "/subscriptions/me") for session in (paid_account, premium_account)]
    payments = [request(session, "GET", "/subscriptions/payments") for session in (paid_account, premium_account)]
    assert all(len(rows) == 1 and rows[0]["status"] == "Confirmed" for rows in payments)
    subprocess.run(["docker", "stop", "--timeout", "90", container], check=True, capture_output=True)
    try:
        assert request(paid_account, "GET", "/subscriptions/readiness")["ready"] is False
        request(paid_account, "GET", "/health")
        image = ROOT / "sample_images/01_Cardinalis_cardinalis.jpg"
        accepted = request(paid_account, "POST", "/analysis/image", 202, files={"file": (image.name, image.read_bytes(), "image/jpeg")})
        assert terminal(paid_account, accepted)["status"] == "Completed"
        for session, period in zip((paid_account, premium_account), periods):
            assert request(session, "GET", "/subscriptions/me")["endDate"] == period["endDate"]
    finally:
        subprocess.run(["docker", "start", container], check=True, capture_output=True)
    deadline = time.monotonic() + 120
    while not request(paid_account, "GET", "/subscriptions/readiness")["ready"]:
        assert time.monotonic() < deadline, "Payments did not recover after loading the saved Anvil state."
        time.sleep(.5)
    for session, period, rows in zip((paid_account, premium_account), periods, payments):
        payment = rows[0]
        checked = request(session, "POST", "/subscriptions/payments/verify", json={"orderId": payment["paymentOrderId"], "transactionHash": payment["transactionHash"]})
        assert checked["status"] == "Confirmed"
        assert request(session, "GET", "/subscriptions/me")["endDate"] == period["endDate"]
