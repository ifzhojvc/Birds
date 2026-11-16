import asyncio
import hashlib
import io
import json
import sys
from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock
from uuid import uuid4

import numpy as np
import pytest
import soundfile as sf
from fastapi.testclient import TestClient
from PIL import Image

from ai_api.app import app
from birdlab import models, presence
from birdlab.errors import ContentError, ModelError
from birdlab.files import AudioData
from bird_worker.contracts import AnalysisRequest
from bird_worker.runtime import Handler
from bird_worker.storage import Ledger


def audio_data(seconds=1, sample_rate=16000):
    samples = .01 * np.sin(2 * np.pi * 1000 * np.arange(int(seconds * sample_rate)) / sample_rate)
    return AudioData(samples.astype(np.float32), sample_rate, seconds, sample_rate, 1)


def audio_scores(bird=.001, other="Speech"):
    labels = ["Bird", "Bird vocalization, bird call, bird song", other] + [f"class_{i}" for i in range(524)]
    return [{"label": label, "score": bird if index < 2 else .95 if index == 2 else .001} for index, label in enumerate(labels)]


def detector(monkeypatch, result, rate=None):
    classifier = Mock(return_value=result)
    monkeypatch.setattr(presence, "get_presence_model", Mock(return_value=presence.PresenceModel(classifier, rate)))
    return classifier


@pytest.mark.parametrize("score", [.35, .6, .99])
def test_image_bird_accepts_multiple_objects_and_boundary(monkeypatch, score):
    classifier = detector(monkeypatch, [{"label": "person", "score": .98}, {"label": "bird", "score": .1}, {"label": "bird", "score": score}])
    result = presence.require_image_bird(Image.new("RGB", (100, 80)), True)
    assert result["score"] == score
    assert result["model_id"] == presence.IMAGE_PRESENCE_MODEL
    assert result["evaluations"] == 1
    classifier.assert_called_once()
    assert classifier.call_args.kwargs == {"threshold": 0.0}


@pytest.mark.parametrize("label", ["cat", "dog", "person", "car", "tree", "chair"])
def test_nonbird_objects_rejected_before_species_model(monkeypatch, label):
    detector(monkeypatch, [{"label": label, "score": .99}])
    species = Mock()
    monkeypatch.setattr(models, "get_image_model", species)
    with pytest.raises(ContentError, match="не вдалося виявити птаха") as failure:
        models.classify_image(Image.new("RGB", (50, 50)))
    assert failure.value.code == "bird_not_detected"
    species.assert_not_called()


@pytest.mark.parametrize("score,code", [(.1, "bird_not_detected"), (.1001, "bird_presence_uncertain"), (.3499, "bird_presence_uncertain")])
def test_image_absent_and_uncertain_are_distinct(monkeypatch, score, code):
    detector(monkeypatch, [{"label": "bird", "score": score}])
    with pytest.raises(ContentError) as failure:
        presence.require_image_bird(Image.new("RGB", (100, 100)))
    assert failure.value.code == code


def test_small_bird_on_complex_background_can_be_found_in_overlapping_crop(monkeypatch):
    classifier = detector(monkeypatch, [])
    classifier.side_effect = [[{"label": "bird", "score": .03}], [{"label": "tree", "score": .9}], [{"label": "bird", "score": .7}]]
    result = presence.require_image_bird(Image.new("RGB", (1200, 1000)))
    assert result["score"] == .7 and result["evaluations"] == 3
    assert classifier.call_args.args[0].size == (780, 650)


def test_dark_large_image_checks_all_crops_before_rejection(monkeypatch):
    classifier = detector(monkeypatch, [{"label": "bird", "score": .01}])
    with pytest.raises(ContentError) as failure:
        presence.require_image_bird(Image.new("RGB", (800, 800), "black"))
    assert failure.value.code == "bird_not_detected"
    assert classifier.call_count == 5


@pytest.mark.parametrize("raw", [None, [], [{"label": "bird"}], [{"label": None, "score": .9}], [{"label": "bird", "score": float("nan")}], [{"label": "bird", "score": 1.1}]])
def test_invalid_detector_response_fails_closed(monkeypatch, raw):
    detector(monkeypatch, raw)
    with pytest.raises(ModelError):
        presence.require_image_bird(Image.new("RGB", (100, 100)))


@pytest.mark.parametrize("kind", ["image", "audio"])
def test_presence_inference_failure_is_transient_not_fake_result(monkeypatch, kind):
    classifier = detector(monkeypatch, None, 16000)
    classifier.side_effect = RuntimeError("internal details")
    media = Image.new("RGB", (100, 100)) if kind == "image" else audio_data()
    with pytest.raises(ModelError, match="Не вдалося перевірити") as failure:
        getattr(presence, f"require_{kind}_bird")(media)
    assert failure.value.__cause__ is classifier.side_effect


@pytest.mark.parametrize("seconds", [.25, 1, 2.7])
def test_audio_short_quiet_bird_with_louder_speech_is_accepted(monkeypatch, seconds):
    classifier = detector(monkeypatch, audio_scores(.65), 16000)
    result = presence.require_audio_bird(audio_data(seconds), True)
    assert result["score"] == .65 and result["evaluations"] == 1
    assert classifier.call_args.kwargs == {"top_k": 527, "function_to_apply": "sigmoid"}
    assert len(classifier.call_args.args[0]["raw"]) == int(seconds * 16000)


@pytest.mark.parametrize("other", ["Speech", "Music", "Bark", "Wind", "Vehicle", "White noise"])
def test_audio_nonbird_sounds_are_terminal_rejections(monkeypatch, other):
    detector(monkeypatch, audio_scores(.001, other), 16000)
    species = Mock()
    monkeypatch.setattr(models, "get_audio_model", species)
    with pytest.raises(ContentError, match="звуків птахів") as failure:
        models.classify_audio(audio_data())
    assert failure.value.code == "bird_not_detected"
    species.assert_not_called()


@pytest.mark.parametrize("score,code", [(.05, "bird_not_detected"), (.051, "bird_presence_uncertain"), (.299, "bird_presence_uncertain")])
def test_audio_uncertainty_band(monkeypatch, score, code):
    detector(monkeypatch, audio_scores(score), 16000)
    with pytest.raises(ContentError) as failure:
        presence.require_audio_bird(audio_data())
    assert failure.value.code == code


@pytest.mark.parametrize("seconds,lengths", [(10, [160000]), (18, [160000, 160000]), (20, [160000, 160000, 64000])])
def test_audio_windows_overlap_and_preserve_brief_bird_among_noise(monkeypatch, seconds, lengths):
    classifier = detector(monkeypatch, None, 16000)
    classifier.side_effect = [audio_scores(.8 if index == 0 else .001) for index in range(len(lengths))]
    result = presence.require_audio_bird(audio_data(seconds))
    assert result["score"] == .8 and result["evaluations"] == len(lengths)
    assert [len(call.args[0]["raw"]) for call in classifier.call_args_list] == lengths


def test_uncertain_mixed_audio_rechecks_short_windows_before_rejecting(monkeypatch):
    classifier = detector(monkeypatch, None, 16000)
    classifier.side_effect = [audio_scores(.1), audio_scores(.2), audio_scores(.6)]
    result = presence.require_audio_bird(audio_data(18))
    assert result["score"] == .6 and result["evaluations"] == 3
    assert [len(call.args[0]["raw"]) for call in classifier.call_args_list] == [160000, 160000, 16000]


def test_ambiguous_short_windows_keep_uncertain_result(monkeypatch):
    classifier = detector(monkeypatch, audio_scores(.2), 16000)
    with pytest.raises(ContentError) as failure:
        presence.require_audio_bird(audio_data(3))
    assert failure.value.code == "bird_presence_uncertain"
    assert classifier.call_count == 5


def test_audio_wrong_rate_empty_or_wrong_class_response_fails_closed(monkeypatch):
    classifier = detector(monkeypatch, audio_scores(.9), 16000)
    with pytest.raises(ModelError, match="16000"):
        presence.require_audio_bird(audio_data(sample_rate=8000))
    classifier.assert_not_called()
    with pytest.raises(ModelError, match="відліків"):
        presence.require_audio_bird(audio_data(0))
    classifier.return_value = [{"label": f"unknown_{i}", "score": .5} for i in range(527)]
    with pytest.raises(ModelError, match="неочікувані класи"):
        presence.require_audio_bird(audio_data())
    classifier.return_value = []
    with pytest.raises(ModelError, match="неповну"):
        presence.require_audio_bird(audio_data())


@pytest.mark.parametrize("value", ["nan", "inf", "-0.1", "1.01", "not-a-number"])
def test_invalid_threshold_configuration_stops_before_models(monkeypatch, value):
    monkeypatch.setenv("BIRD_IMAGE_PRESENCE_ACCEPT", value)
    loader = Mock()
    monkeypatch.setattr(presence, "get_presence_model", loader)
    with pytest.raises(ModelError, match="BIRD_IMAGE_PRESENCE_ACCEPT"):
        presence.require_image_bird(Image.new("RGB", (20, 20)))
    loader.assert_not_called()


def test_threshold_order_and_override(monkeypatch):
    monkeypatch.setenv("BIRD_IMAGE_PRESENCE_ACCEPT", ".2")
    monkeypatch.setenv("BIRD_IMAGE_PRESENCE_REJECT", ".2")
    with pytest.raises(ModelError, match="менше"):
        presence.require_image_bird(Image.new("RGB", (20, 20)))
    monkeypatch.setenv("BIRD_IMAGE_PRESENCE_REJECT", ".1")
    detector(monkeypatch, [{"label": "bird", "score": .2}])
    assert presence.require_image_bird(Image.new("RGB", (20, 20)))["accept_threshold"] == .2


@pytest.fixture
def loader_dependencies(monkeypatch, tmp_path):
    model = Mock(config=SimpleNamespace(num_labels=527, id2label={0: "bird", 1: "Bird", 2: "Bird vocalization, bird call, bird song"}))
    processor = Mock(sampling_rate=16000)
    model_load, processor_load, pipeline = Mock(return_value=model), Mock(return_value=processor), Mock()
    dependencies = SimpleNamespace(
        AutoFeatureExtractor=SimpleNamespace(from_pretrained=processor_load),
        AutoImageProcessor=SimpleNamespace(from_pretrained=processor_load),
        AutoModelForAudioClassification=SimpleNamespace(from_pretrained=model_load),
        AutoModelForObjectDetection=SimpleNamespace(from_pretrained=model_load), pipeline=pipeline,
    )
    cuda, download = Mock(return_value=False), Mock()
    monkeypatch.setitem(sys.modules, "transformers", dependencies)
    monkeypatch.setitem(sys.modules, "torch", SimpleNamespace(set_num_threads=Mock(), cuda=SimpleNamespace(is_available=cuda)))
    monkeypatch.setitem(sys.modules, "huggingface_hub", SimpleNamespace(snapshot_download=download))
    monkeypatch.setattr(presence, "MODEL_CACHE", tmp_path)
    monkeypatch.setenv("BIRD_DEVICE", "cpu")
    for kind, revision in [("image", presence.IMAGE_PRESENCE_REVISION), ("audio", presence.AUDIO_PRESENCE_REVISION)]:
        folder = tmp_path / f"presence-{kind}--{revision}"
        folder.mkdir()
        for name in ["config.json", "preprocessor_config.json", "model.safetensors"]:
            (folder / name).touch()
    return SimpleNamespace(model=model, processor=processor, load=model_load, processor_load=processor_load, pipeline=pipeline, cuda=cuda, download=download, root=tmp_path)


@pytest.mark.parametrize("kind,task,device,rate", [("image", "object-detection", "cpu", None), ("audio", "audio-classification", "auto", 16000)])
def test_presence_loader_uses_pinned_safe_local_models(loader_dependencies, monkeypatch, kind, task, device, rate):
    deps = loader_dependencies
    monkeypatch.setenv("BIRD_DEVICE", device)
    deps.cuda.return_value = True
    result = presence._load(kind, True)
    assert result.classifier is deps.pipeline.return_value and result.sample_rate == rate
    assert deps.pipeline.call_args.args == (task,)
    assert deps.pipeline.call_args.kwargs["device"] == (-1 if device == "cpu" else 0)
    assert deps.load.call_args.kwargs == {"local_files_only": True, "trust_remote_code": False}
    assert "presence-" + kind in str(deps.load.call_args.args[0])
    deps.download.assert_not_called()


@pytest.mark.parametrize("kind", ["image", "audio"])
def test_cold_offline_presence_cache_cannot_bypass_gate(loader_dependencies, kind):
    deps = loader_dependencies
    revision = presence.IMAGE_PRESENCE_REVISION if kind == "image" else presence.AUDIO_PRESENCE_REVISION
    (deps.root / f"presence-{kind}--{revision}" / "model.safetensors").unlink()
    with pytest.raises(ModelError, match="Офлайн-кеш"):
        presence._load(kind, True)
    deps.download.assert_not_called()
    deps.load.assert_not_called()


def test_cache_miss_downloads_only_pinned_weights_and_configs(loader_dependencies):
    deps = loader_dependencies
    folder = deps.root / f"presence-image--{presence.IMAGE_PRESENCE_REVISION}"
    (folder / "model.safetensors").unlink()
    presence._load("image", False)
    deps.download.assert_called_once_with(presence.IMAGE_PRESENCE_MODEL, revision=presence.IMAGE_PRESENCE_REVISION, local_dir=str(folder), allow_patterns=["config.json", "preprocessor_config.json", "model.safetensors"])


def test_presence_loader_wraps_download_failure(loader_dependencies):
    deps = loader_dependencies
    (deps.root / f"presence-audio--{presence.AUDIO_PRESENCE_REVISION}" / "config.json").unlink()
    deps.download.side_effect = OSError("internal storage details")
    with pytest.raises(ModelError, match="Не вдалося завантажити") as failure:
        presence._load("audio", False)
    assert failure.value.__cause__ is deps.download.side_effect
    assert "internal storage details" not in str(failure.value)


@pytest.mark.parametrize("device", ["tpu", "cuda"])
def test_presence_loader_rejects_invalid_or_missing_device(loader_dependencies, monkeypatch, device):
    monkeypatch.setenv("BIRD_DEVICE", device)
    with pytest.raises(ModelError, match="BIRD_DEVICE|CUDA"):
        presence._load("image", True)
    loader_dependencies.load.assert_not_called()


@pytest.mark.parametrize("kind,problem", [("image", "labels"), ("audio", "count"), ("audio", "labels"), ("audio", "rate")])
def test_presence_loader_rejects_incompatible_models(loader_dependencies, kind, problem):
    deps = loader_dependencies
    if problem == "labels":
        deps.model.config.id2label = {0: "not a bird"}
    elif problem == "count":
        deps.model.config.num_labels = 22
    else:
        deps.processor.sampling_rate = 22050
    with pytest.raises(ModelError, match="клас bird|AudioSet"):
        presence._load(kind, True)
    deps.pipeline.assert_not_called()


def test_presence_cache_shares_loaded_model_across_threads_and_offline_toggle(monkeypatch):
    loader = Mock(return_value=presence.PresenceModel(Mock()))
    monkeypatch.setattr(presence, "_load", loader)
    cache = presence.PresenceCache()
    with ThreadPoolExecutor(max_workers=4) as pool:
        models_loaded = list(pool.map(lambda offline: cache("image", offline), [False, True, False, True]))
    assert all(model is models_loaded[0] for model in models_loaded)
    assert loader.call_count == 1
    cache.clear()
    cache("image", True)
    assert loader.call_count == 2


@pytest.mark.parametrize("label,first,second", [("background", .9, .05), ("Great_egret", .4, .2), ("Great_egret", .52, .5)])
def test_present_bird_does_not_force_uncertain_audio_species(monkeypatch, label, first, second):
    detector(monkeypatch, audio_scores(.9), 16000)
    raw = [{"label": label, "score": first}, {"label": "Little_egret", "score": second}] + [{"label": f"species_{i}", "score": .001} for i in range(20)]
    classifier = Mock(return_value=raw)
    monkeypatch.setattr(models, "get_audio_model", lambda offline: models.ModelBundle(classifier, 16000))
    with pytest.raises(ContentError, match="Звуки птаха виявлено") as failure:
        models.classify_audio(audio_data())
    assert failure.value.code == "bird_species_uncertain"
    classifier.assert_called_once()


def test_success_preserves_species_top_three_and_presence_provenance(monkeypatch):
    detector(monkeypatch, audio_scores(.8), 16000)
    raw = [{"label": "Great_egret", "score": .8}, {"label": "Little_egret", "score": .15}] + [{"label": f"species_{i}", "score": .0025} for i in range(20)]
    monkeypatch.setattr(models, "get_audio_model", lambda offline: models.ModelBundle(Mock(return_value=raw), 16000))
    result = models.classify_audio(audio_data())
    assert len(result.predictions) == 3 and result.predictions[0].label == "Great_egret"
    assert result.metadata["presence"]["score"] == .8
    assert result.metadata["presence"]["revision"] == presence.AUDIO_PRESENCE_REVISION


@pytest.mark.parametrize("kind,score,code", [("image", .01, "bird_not_detected"), ("image", .2, "bird_presence_uncertain"), ("audio", .001, "bird_not_detected"), ("audio", .1, "bird_presence_uncertain")])
def test_direct_api_returns_structured_content_rejection_without_predictions(monkeypatch, png_bytes, kind, score, code):
    if kind == "image":
        detector(monkeypatch, [{"label": "bird", "score": score}])
        content, name = png_bytes, "example.png"
    else:
        detector(monkeypatch, audio_scores(score), 16000)
        stream = io.BytesIO()
        sf.write(stream, audio_data().samples, 16000, format="WAV")
        content, name = stream.getvalue(), "example.wav"
    species = Mock()
    monkeypatch.setattr(models, f"get_{kind}_model", species)
    with TestClient(app) as client:
        response = client.post(f"/predict/{kind}", files={"file": (name, content)})
    assert response.status_code == 422 and response.json()["code"] == code
    assert "predictions" not in response.json()
    species.assert_not_called()


def test_worker_rejection_is_terminal_and_redelivery_reuses_failed_result(monkeypatch, tmp_path, png_bytes):
    detector(monkeypatch, [{"label": "cat", "score": .99}])
    species = Mock()
    monkeypatch.setattr(models, "get_image_model", species)
    job = AnalysisRequest.read(json.dumps({"schemaVersion": 1, "eventId": str(uuid4()), "analysisId": str(uuid4()), "analysisType": "image", "fileId": str(uuid4()), "fileSha256": hashlib.sha256(png_bytes).hexdigest(), "createdAt": "2026-11-16T00:00:00Z", "correlationId": str(uuid4())}).encode())
    (tmp_path / f"{job.fileId.hex}.png").write_bytes(png_bytes)
    ledger = Ledger(tmp_path / "ledger")
    exchange, retry = Mock(publish=AsyncMock(return_value=True)), Mock(publish=AsyncMock())
    handler = Handler(ledger, exchange, retry, tmp_path, True)
    messages = [Mock(body=job.model_dump_json().encode(), ack=AsyncMock(), reject=AsyncMock()) for _ in range(2)]
    try:
        for message in messages:
            asyncio.run(handler.deliver(message))
            message.reject.assert_awaited_once_with(requeue=False)
        attempts, final = ledger.get(job)
        payload = json.loads(final)
        assert attempts == 1 and payload["status"] == "failed"
        assert "не вдалося виявити птаха" in payload["errorMessage"]
        assert "predictions" not in payload
        retry.publish.assert_not_awaited()
        species.assert_not_called()
        assert exchange.publish.call_args_list[-1].args[0].body == final
    finally:
        ledger.close()
