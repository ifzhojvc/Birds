import asyncio
import runpy
import subprocess
import sys
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock

import numpy as np
import pytest
from fastapi.testclient import TestClient
from imageio_ffmpeg import get_ffmpeg_exe

from ai_api.app import BodyLimit, app
from ai_api.audio import decode_audio
from birdlab import models
from birdlab.config import ROOT
from birdlab.errors import InputError, ModelError
from birdlab.files import AudioData


def test_uvicorn_entrypoint_exports_configured_application():
    entrypoint = runpy.run_path(str(ROOT / "ai-server/main.py"))
    assert entrypoint["app"] is app
    assert {route.path for route in app.routes} >= {"/health", "/predict/image", "/predict/audio"}


def test_body_limit_passes_lifespan_messages_through():
    application = AsyncMock()
    scope, receive, send = {"type": "lifespan"}, AsyncMock(), AsyncMock()
    asyncio.run(BodyLimit(application)(scope, receive, send))
    application.assert_awaited_once_with(scope, receive, send)


def test_oversized_multipart_rejected_before_classification(monkeypatch):
    monkeypatch.setattr("ai_api.app.MAX_AUDIO_BYTES", 1)
    classifier = Mock()
    monkeypatch.setattr("ai_api.app.classify_audio", classifier)
    with TestClient(app) as client:
        response = client.post("/predict/audio", files={"file": ("big.wav", bytes(1024 * 1024 + 1))})
    assert response.status_code == 413
    assert response.json()["code"] == "request_error"
    classifier.assert_not_called()


def test_unexpected_failure_hides_model_exception_details(monkeypatch, png_bytes):
    monkeypatch.setattr("ai_api.app.classify_image", Mock(side_effect=RuntimeError("internal filesystem path")))
    with TestClient(app, raise_server_exceptions=False) as client:
        response = client.post("/predict/image", files={"file": ("bird.png", png_bytes)})
    assert response.status_code == 500
    assert response.json()["code"] == "internal_error"
    assert "internal filesystem path" not in response.text


def test_audio_endpoint_preserves_rank_and_preprocessing_metadata(monkeypatch, wav_bytes, result):
    classifier = Mock(return_value=result)
    monkeypatch.setattr("ai_api.app.classify_audio", classifier)
    with TestClient(app) as client:
        response = client.post("/predict/audio", files={"file": ("song.WAV", wav_bytes)})
    assert response.status_code == 200
    assert response.json()["analysis_type"] == "audio"
    assert [p["confidence"] for p in response.json()["predictions"]] == [.8, .15, .05]
    audio = classifier.call_args.args[0]
    assert (audio.sample_rate, audio.original_rate, audio.original_channels) == (16000, 22050, 2)
    assert audio.samples.shape == (16000,)


@pytest.mark.parametrize(("suffix", "format_name"), [("m4a", "ipod"), ("aac", "adts"), ("ogg", "ogg")])
def test_mobile_audio_formats_decode_to_16khz_mono(tmp_path, wav_bytes, suffix, format_name):
    converted = tmp_path / f"sample.{suffix}"
    subprocess.run(
        [get_ffmpeg_exe(), "-v", "error", "-f", "wav", "-i", "pipe:0",
         "-f", format_name, str(converted)],
        input=wav_bytes, capture_output=True, check=True, timeout=15,
    )
    audio = decode_audio(converted.read_bytes(), converted.name)
    assert (audio.sample_rate, audio.original_channels) == (16000, 1)
    assert audio.duration == pytest.approx(1, abs=.12)
    assert np.max(np.abs(audio.samples)) > .05


@pytest.mark.parametrize(("name", "error"), [("corrupt.aac", "декодувати"), ("bird.bin", "Підтримуються")])
def test_decoder_rejects_bad_content_and_extension(name, error):
    with pytest.raises(InputError, match=error):
        decode_audio(b"not an audio stream", name)


@pytest.fixture
def model_dependencies(monkeypatch, tmp_path):
    model = Mock(config=SimpleNamespace(num_labels=200), classifier=SimpleNamespace(out_features=200))
    processor = Mock(sampling_rate=16000)
    model_load, processor_load = Mock(return_value=model), Mock(return_value=processor)
    pipeline = Mock()
    loaders = SimpleNamespace(
        AutoFeatureExtractor=SimpleNamespace(from_pretrained=processor_load),
        AutoImageProcessor=SimpleNamespace(from_pretrained=processor_load),
        AutoModelForAudioClassification=SimpleNamespace(from_pretrained=model_load),
        AutoModelForImageClassification=SimpleNamespace(from_pretrained=model_load),
        pipeline=pipeline,
    )
    cuda = Mock(return_value=False)
    download = Mock()
    monkeypatch.setitem(sys.modules, "transformers", loaders)
    monkeypatch.setitem(sys.modules, "torch", SimpleNamespace(set_num_threads=Mock(), cuda=SimpleNamespace(is_available=cuda)))
    monkeypatch.setitem(sys.modules, "huggingface_hub", SimpleNamespace(snapshot_download=download))
    monkeypatch.setattr(models, "MODEL_CACHE", tmp_path)
    monkeypatch.setenv("BIRD_DEVICE", "cpu")
    for kind, revision in [("image", models.IMAGE_REVISION), ("audio", models.AUDIO_REVISION)]:
        folder = tmp_path / f"{kind}--{revision}"
        folder.mkdir()
        for name in ["config.json", "preprocessor_config.json", "model.safetensors"]:
            (folder / name).touch()
    return SimpleNamespace(model=model, processor=processor, pipeline=pipeline, cuda=cuda,
                           load=model_load, download=download, root=tmp_path)


@pytest.mark.parametrize(("kind", "labels", "device", "rate"), [
    ("image", 200, "cpu", None), ("audio", 22, "auto", 16000),
])
def test_model_loader_uses_pinned_local_weights(model_dependencies, monkeypatch, kind, labels, device, rate):
    deps = model_dependencies
    deps.model.config.num_labels = labels
    deps.cuda.return_value = True
    monkeypatch.setenv("BIRD_DEVICE", device)
    bundle = models._load(kind, offline=True)
    assert bundle.sample_rate == rate
    assert bundle.classifier is deps.pipeline.return_value
    assert deps.pipeline.call_args.args == (f"{kind}-classification",)
    assert deps.pipeline.call_args.kwargs["device"] == (-1 if device == "cpu" else 0)
    revision = models.IMAGE_REVISION if kind == "image" else models.AUDIO_REVISION
    deps.load.assert_called_once_with(deps.root / f"{kind}--{revision}", local_files_only=True, trust_remote_code=False)
    deps.download.assert_not_called()


@pytest.mark.parametrize(("device", "expected"), [("tpu", "BIRD_DEVICE"), ("cuda", "CUDA недоступна")])
def test_model_loader_validates_device_before_opening_weights(model_dependencies, monkeypatch, device, expected):
    monkeypatch.setenv("BIRD_DEVICE", device)
    with pytest.raises(ModelError, match=expected):
        models._load("image", offline=True)
    model_dependencies.load.assert_not_called()


@pytest.mark.parametrize(("kind", "labels", "outputs"), [("image", 1000, 200), ("image", 200, 1000), ("audio", 21, 21)])
def test_model_loader_rejects_incompatible_class_heads(model_dependencies, kind, labels, outputs):
    model_dependencies.model.config.num_labels = labels
    model_dependencies.model.classifier.out_features = outputs
    with pytest.raises(ModelError, match="конфігурації|набір класів"):
        models._load(kind, offline=True)
    model_dependencies.pipeline.assert_not_called()


def test_online_cache_miss_downloads_only_pinned_files_and_reports_failure(model_dependencies):
    deps = model_dependencies
    folder = deps.root / f"audio--{models.AUDIO_REVISION}"
    (folder / "model.safetensors").unlink()
    deps.download.side_effect = OSError("test cache error")
    with pytest.raises(ModelError, match="першого завантаження") as failure:
        models._load("audio", offline=False)
    assert failure.value.__cause__ is deps.download.side_effect
    deps.download.assert_called_once_with(
        models.AUDIO_MODEL, revision=models.AUDIO_REVISION, local_dir=str(folder),
        allow_patterns=["config.json", "preprocessor_config.json", "model.safetensors", "README.md"],
    )


@pytest.mark.parametrize("kind", ["image", "audio"])
def test_classifier_wraps_dependency_exception(monkeypatch, kind):
    classifier = Mock(side_effect=RuntimeError("dependency failure"))
    monkeypatch.setattr(models, f"get_{kind}_model", lambda offline: models.ModelBundle(classifier, 16000))
    media = Mock() if kind == "image" else AudioData(np.ones(4000), 16000, .25, 16000, 1)
    with pytest.raises(ModelError, match="Не вдалося проаналізувати") as failure:
        getattr(models, f"classify_{kind}")(media, True)
    assert failure.value.__cause__ is classifier.side_effect
