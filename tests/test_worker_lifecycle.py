import asyncio
import hashlib
import json
import runpy
import time
from unittest.mock import AsyncMock, Mock

import pytest

from birdlab.config import ROOT
from birdlab.errors import InputError
from bird_worker import inference, runtime
from bird_worker.contracts import AnalysisRequest, encode, event
from bird_worker.storage import Ledger
from test_worker import delivery, request


@pytest.mark.parametrize("confirmed", [False, None])
def test_publish_requires_confirmation(confirmed):
    payload = event(request(), "processing", 2)
    exchange = Mock(publish=AsyncMock(return_value=confirmed))
    with pytest.raises(RuntimeError, match="не підтвердив"):
        asyncio.run(runtime.publish(exchange, encode(payload), retry=1, routing_key="requests.1"))
    message = exchange.publish.call_args.args[0]
    assert message.message_id == payload["eventId"]
    assert message.correlation_id == payload["correlationId"]
    assert message.headers == {"x-retry": 1}
    assert exchange.publish.call_args.kwargs == {
        "routing_key": "requests.1", "mandatory": True, "timeout": 10
    }


@pytest.mark.parametrize(("user", "password"), [("guest", "test-only"), ("bird-test", "")])
def test_connection_rejects_missing_password_and_guest(monkeypatch, user, password):
    monkeypatch.setenv("RABBITMQ_USERNAME", user)
    monkeypatch.setenv("RABBITMQ_PASSWORD", password)
    connect = AsyncMock()
    monkeypatch.setattr(runtime.aio_pika, "connect_robust", connect)
    with pytest.raises(ValueError, match="RABBITMQ_USERNAME"):
        asyncio.run(runtime.connect())
    connect.assert_not_awaited()


def test_connection_propagates_settings_to_robust_broker_client(monkeypatch):
    for key, value in {
        "USERNAME": "bird-test", "PASSWORD": "test-only", "HOST": "rabbit.test",
        "PORT": "5678", "VHOST": "qa",
    }.items():
        monkeypatch.setenv(f"RABBITMQ_{key}", value)
    connect = AsyncMock()
    monkeypatch.setattr(runtime.aio_pika, "connect_robust", connect)
    assert asyncio.run(runtime.connect()) is connect.return_value
    connect.assert_awaited_once_with(
        host="rabbit.test", port=5678, login="bird-test", password="test-only",
        virtualhost="qa", timeout=10, reconnect_interval=5, heartbeat=20,
    )


@pytest.mark.parametrize("signal_fallback", [False, True])
def test_worker_reconnects_then_nacks_unconfirmed_delivery(monkeypatch, tmp_path, signal_fallback):
    monkeypatch.setattr(runtime, "STATE", tmp_path)
    work = request()
    message = delivery(work.model_dump_json().encode())
    message.channel.is_closed = False
    message.processed = False
    message.nack = AsyncMock()
    shutdown = {}
    exchange = Mock(publish=AsyncMock(return_value=False))
    queue = Mock(bind=AsyncMock())

    class Deliveries:
        async def __aenter__(self):
            return self

        async def __aexit__(self, *_):
            return False

        async def __anext__(self):
            if not message.nack.await_count:
                return message
            shutdown[runtime.signal.SIGTERM]()
            await asyncio.Future()

    queue.iterator.return_value = Deliveries()
    channel = Mock(
        set_qos=AsyncMock(), declare_exchange=AsyncMock(return_value=exchange),
        declare_queue=AsyncMock(return_value=queue),
    )

    class Connection:
        def __init__(self):
            self.connected = asyncio.Event()
            self.connected.set()
            self.channel = AsyncMock(return_value=channel)
            self.closed = False

        async def __aenter__(self):
            return self

        async def __aexit__(self, *_):
            self.closed = True

    connection = Connection()
    connect = AsyncMock(side_effect=[OSError("test outage"), connection])
    monkeypatch.setattr(runtime, "connect", connect)
    sleep, wait_for = asyncio.sleep, asyncio.wait_for

    async def scenario():
        def register(sig, callback):
            if signal_fallback:
                raise NotImplementedError()
            shutdown[sig] = callback

        monkeypatch.setattr(asyncio.get_running_loop(), "add_signal_handler", register)
        monkeypatch.setattr(runtime.signal, "signal", lambda sig, callback: shutdown.update({sig: callback}))
        monkeypatch.setattr(runtime.asyncio, "sleep", lambda _: sleep(0))
        monkeypatch.setattr(runtime.asyncio, "wait_for", lambda task, timeout: wait_for(task, .01))
        await wait_for(runtime.run(), 3)

    asyncio.run(scenario())
    assert connect.await_count == 2
    assert connection.closed
    queue.iterator.assert_called_once_with(no_ack=False)
    channel.set_qos.assert_awaited_once_with(prefetch_count=1)
    message.ack.assert_not_awaited()
    message.nack.assert_awaited_once_with(requeue=True)
    assert not (tmp_path / "heartbeat").exists()
    ledger = Ledger(tmp_path)
    try:
        assert ledger.get(work) == (1, None)
    finally:
        ledger.close()


@pytest.mark.parametrize("attempts", [0, runtime.MAX_ATTEMPTS])
def test_unexpected_inference_and_exhausted_deliveries_are_terminal(tmp_path, attempts):
    job = request()
    ledger = Ledger(tmp_path)
    ledger.get(job)
    for _ in range(attempts):
        ledger.begin_attempt(job)
    exchange = Mock(publish=AsyncMock(return_value=True))
    model = Mock(side_effect=ValueError("internal model details"))
    message = delivery(job.model_dump_json().encode())
    try:
        asyncio.run(runtime.Handler(ledger, exchange, exchange, tmp_path, True, model).deliver(message))
        result = json.loads(ledger.get(job)[1])
        assert result["status"] == "failed"
        assert result["causationId"] == str(job.eventId)
        assert "internal model details" not in result["errorMessage"]
        assert model.call_count == (0 if attempts else 1)
        message.reject.assert_awaited_once_with(requeue=False)
        message.ack.assert_not_awaited()
    finally:
        ledger.close()


@pytest.mark.parametrize("kind", ["image", "audio"])
def test_worker_preprocesses_media_and_preserves_top_three(monkeypatch, tmp_path, png_bytes, wav_bytes, result, kind):
    content, suffix = (png_bytes, "png") if kind == "image" else (wav_bytes, "wav")
    job = request(analysisType=kind, fileSha256=hashlib.sha256(content).hexdigest())
    (tmp_path / f"{job.fileId.hex}.{suffix}").write_bytes(content)
    model = Mock(return_value=result)
    monkeypatch.setattr(inference, f"classify_{kind}", model)
    response = inference.infer(job, tmp_path, True)
    media, offline = model.call_args.args
    assert offline is True
    assert (media.size if kind == "image" else media.sample_rate) == ((40, 30) if kind == "image" else 16000)
    assert response["modelRevision"] == result.revision
    assert [(item["species"], item["confidence"], item["rank"]) for item in response["predictions"]] == [
        (p.label, p.score, rank) for rank, p in enumerate(result.predictions, 1)
    ]


def test_oversized_request_and_empty_media_are_rejected(tmp_path):
    with pytest.raises(ValueError, match="64 КБ"):
        AnalysisRequest.read(b" " * 65537)
    job = request()
    (tmp_path / f"{job.fileId.hex}.jpg").touch()
    with pytest.raises(InputError, match="розмір"):
        inference.read_media(tmp_path, job)


@pytest.mark.parametrize(("heartbeat", "code"), [(None, 1), ("invalid", 1), ("old", 1), ("now", 0)])
def test_worker_health_checks_freshness(monkeypatch, tmp_path, heartbeat, code):
    monkeypatch.setattr(runtime, "STATE", tmp_path)
    monkeypatch.setattr("sys.argv", ["worker.py", "--health"])
    if heartbeat:
        value = str(time.time() - 90) if heartbeat == "old" else str(time.time()) if heartbeat == "now" else heartbeat
        (tmp_path / "heartbeat").write_text(value)
    with pytest.raises(SystemExit) as error:
        runpy.run_path(str(ROOT / "ai-server/worker.py"), run_name="__main__")
    assert error.value.code == code


def test_worker_entrypoint_runs_runtime(monkeypatch):
    monkeypatch.setattr("sys.argv", ["worker.py"])
    run = AsyncMock()
    monkeypatch.setattr(runtime, "run", run)
    runpy.run_path(str(ROOT / "ai-server/worker.py"), run_name="__main__")
    run.assert_awaited_once()


@pytest.mark.parametrize("retention_confirmed", [True, False])
def test_dlq_replay_confirms_before_ack_and_preserves_unrecognized_messages(monkeypatch, capsys, retention_confirmed):
    valid = delivery(encode(event(request(), "completed", 1)))
    corrupt = delivery(b"{invalid")
    unknown = delivery(b'{"schemaVersion":2,"status":"completed"}')
    for message in (corrupt, unknown):
        message.headers = {"x-delivery-count": 8, "x-retry": 2}
        message.content_type = "application/json"
        message.message_id = "retained-id"
        message.correlation_id = "retained-correlation"
    live = Mock(publish=AsyncMock(return_value=True))
    dead = Mock(publish=AsyncMock(return_value=retention_confirmed))
    queue = Mock(declare=AsyncMock(return_value=Mock(message_count=4)),
                 get=AsyncMock(side_effect=[valid, unknown, corrupt, None]))
    channel = Mock(set_qos=AsyncMock(), get_exchange=AsyncMock(side_effect=[live, dead]),
                   get_queue=AsyncMock(return_value=queue))

    class Connection:
        async def __aenter__(self):
            return self

        async def __aexit__(self, *_):
            return False

    connection = Connection()
    connection.channel = AsyncMock(return_value=channel)
    monkeypatch.setattr(runtime, "connect", AsyncMock(return_value=connection))
    path = str(ROOT / "ai-server/replay_results.py")
    if retention_confirmed:
        runpy.run_path(path, run_name="__main__")
        assert json.loads(capsys.readouterr().out) == {
            "results_replayed_with_confirms": 1, "other_messages_retained": 2,
        }
        corrupt.ack.assert_awaited_once()
        unknown.ack.assert_awaited_once()
    else:
        with pytest.raises(RuntimeError, match="DLQ"):
            runpy.run_path(path, run_name="__main__")
        unknown.ack.assert_not_awaited()
        corrupt.ack.assert_not_awaited()
    valid.ack.assert_awaited_once()
    assert live.publish.call_args.args[0].body == valid.body
    assert dead.publish.call_args.args[0].headers == {"x-retry": 2}
