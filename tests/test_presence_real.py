import hashlib
import io
import json
import os
import time

import numpy as np
import pytest
import requests
import soundfile as sf
from PIL import Image, ImageEnhance, ImageFilter

from birdlab.config import ROOT, SAMPLE_DIR
from birdlab.errors import ContentError
from birdlab.files import AudioData, load_audio, load_image
from birdlab.presence import require_audio_bird, require_image_bird
from birdlab.service import sample_manifest


pytestmark = [pytest.mark.integration, pytest.mark.skipif(os.environ.get("RUN_REAL_MODELS") != "1", reason="RUN_REAL_MODELS=1 required")]
SAMPLES = json.loads((ROOT / "tests" / "media_samples.json").read_text(encoding="utf-8"))


def cached_sample(sample):
    directory = ROOT / ".cache" / "validation-media"
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / sample["name"]
    if not path.exists():
        response = requests.get(sample["url"], headers={"User-Agent": "AIBirdLab/1.0 educational media validation"}, timeout=90)
        response.raise_for_status()
        path.write_bytes(response.content)
    assert hashlib.sha256(path.read_bytes()).hexdigest() == sample["sha256"], f"Sample changed: {sample['source']}"
    return path


def audio_fixture(path, seconds=10):
    with sf.SoundFile(path) as source:
        samples = source.read(min(len(source), source.samplerate * seconds), dtype="float32", always_2d=True)
        rate = source.samplerate
    stream = io.BytesIO()
    sf.write(stream, samples, rate, format="WAV")
    return load_audio(stream.getvalue(), "fixture.wav")


def test_real_presence_image_false_positives_and_false_negatives():
    positive = sample_manifest()
    assert len(positive) == 15
    false_negatives, false_positives, uncertain = [], [], []
    for row in positive:
        path = SAMPLE_DIR / row["file_name"]
        content = path.read_bytes()
        assert hashlib.sha256(content).hexdigest() == row["sha256"]
        started = time.perf_counter()
        try:
            result = require_image_bird(load_image(content, path.name), True)
            print(f"bird-image {path.name}: score={result['score']:.5f} seconds={time.perf_counter() - started:.3f}")
        except ContentError as exc:
            (uncertain if exc.code == "bird_presence_uncertain" else false_negatives).append(path.name)
    for sample in SAMPLES:
        if sample["kind"] != "image":
            continue
        path = cached_sample(sample)
        try:
            result = require_image_bird(load_image(path.read_bytes(), path.name), True)
            false_positives.append((path.name, result["score"]))
        except ContentError as exc:
            assert exc.code in {"bird_not_detected", "bird_presence_uncertain"}
    print(f"image presence: FP={len(false_positives)}/4 FN={len(false_negatives)}/15 uncertain-positive={len(uncertain)}/15")
    assert not false_positives and not false_negatives and not uncertain


@pytest.mark.parametrize("variant", ["blurred", "dim", "two_birds", "small_bird"])
def test_real_bird_photograph_variants(variant):
    source = load_image((SAMPLE_DIR / "01_Cardinalis_cardinalis.jpg").read_bytes(), "bird.jpg")
    if variant == "blurred":
        image = source.filter(ImageFilter.GaussianBlur(2))
    elif variant == "dim":
        image = ImageEnhance.Brightness(source).enhance(.25)
    elif variant == "two_birds":
        image = Image.new("RGB", (source.width * 2, source.height))
        image.paste(source, (0, 0))
        image.paste(source, (source.width, 0))
    else:
        image = source.resize((160, 160))
        background = Image.new("RGB", (1000, 1000), (70, 100, 60))
        background.paste(image, (620, 620))
        image = background
    result = require_image_bird(image, True)
    assert result["score"] >= result["accept_threshold"]


@pytest.mark.parametrize("variant", ["black", "white", "noise"])
def test_real_blank_and_noise_images_are_not_classified(variant):
    image = Image.fromarray(np.random.default_rng(7).integers(0, 256, (600, 800, 3), dtype=np.uint8)) if variant == "noise" else Image.new("RGB", (800, 600), variant)
    with pytest.raises(ContentError):
        require_image_bird(image, True)


def test_real_presence_audio_false_positives_and_robin_false_negative():
    robin = ROOT / "sample_audio" / "robin.wav"
    result = require_audio_bird(audio_fixture(robin), True)
    assert result["score"] >= result["accept_threshold"]
    print(f"robin presence: score={result['score']:.5f}")
    false_positives, uncertain_negative = [], []
    for sample in SAMPLES:
        if sample["kind"] != "audio":
            continue
        path = cached_sample(sample)
        try:
            result = require_audio_bird(audio_fixture(path), True)
            false_positives.append((path.name, result["score"]))
        except ContentError as exc:
            if exc.code == "bird_presence_uncertain":
                uncertain_negative.append(path.name)
    print(f"audio presence: FP={len(false_positives)}/3 FN=0/1 uncertain-negative={len(uncertain_negative)}/3")
    assert not false_positives


@pytest.mark.parametrize("variant", ["quiet", "short", "noise", "several", "speech"])
def test_real_robin_recording_variants(variant):
    robin = audio_fixture(ROOT / "sample_audio" / "robin.wav")
    samples = robin.samples.copy()
    if variant == "quiet":
        samples *= .05
    elif variant == "short":
        start = max(0, int(np.argmax(np.abs(samples))) - 4000)
        samples = samples[start:start + 8000]
    elif variant == "noise":
        rms = np.sqrt(np.mean(samples ** 2))
        samples += np.random.default_rng(7).normal(0, rms * .2, samples.shape).astype(np.float32)
    elif variant == "several":
        samples += .7 * np.roll(samples, 6000)
    else:
        speech = audio_fixture(cached_sample(next(sample for sample in SAMPLES if sample["name"] == "speech.ogg")))
        background = np.resize(speech.samples, samples.shape)
        background *= .3 * np.max(np.abs(samples)) / max(np.max(np.abs(background)), 1e-8)
        samples += background
    media = AudioData(samples, 16000, len(samples) / 16000, 16000, 1)
    result = require_audio_bird(media, True)
    print(f"bird-audio {variant}: score={result['score']:.5f}")
    assert result["score"] >= result["accept_threshold"]


def test_real_white_noise_is_not_accepted_as_bird_song():
    samples = np.random.default_rng(7).normal(0, .05, 160000).astype(np.float32)
    with pytest.raises(ContentError):
        require_audio_bird(AudioData(samples, 16000, 10, 16000, 1), True)
