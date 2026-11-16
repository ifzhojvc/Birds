import math
import os
import threading
from dataclasses import dataclass, field

import numpy as np

from birdlab.config import MODEL_CACHE
from birdlab.domain import validate_predictions
from birdlab.errors import ContentError, ModelError


IMAGE_PRESENCE_MODEL = "hustvl/yolos-tiny"
IMAGE_PRESENCE_REVISION = "95a90f3c189fbfca3bcfc6d7315b9e84d95dc2de"
AUDIO_PRESENCE_MODEL = "MIT/ast-finetuned-audioset-10-10-0.4593"
AUDIO_PRESENCE_REVISION = "f826b80d28226b62986cc218e5cec390b1096902"
AUDIO_BIRD_LABELS = frozenset({
    "Bird", "Bird vocalization, bird call, bird song", "Chirp, tweet", "Squawk",
    "Pigeon, dove", "Coo", "Crow", "Caw", "Owl", "Hoot", "Turkey", "Gobble",
    "Chicken, rooster", "Cluck", "Crowing, cock-a-doodle-doo", "Duck", "Quack",
    "Goose", "Honk",
})
MESSAGES = {
    "image": (
        "На зображенні не вдалося виявити птаха. Спробуйте завантажити іншу фотографію.",
        "Не вдалося впевнено визначити, чи є на фото птах. Спробуйте чіткіше зображення.",
    ),
    "audio": (
        "На аудіозаписі не вдалося виявити звуків птахів. Завантажте інший запис.",
        "Не вдалося впевнено визначити наявність голосу птаха. Спробуйте якісніший запис.",
    ),
}


@dataclass
class PresenceModel:
    classifier: object
    sample_rate: int | None = None
    lock: threading.Lock = field(default_factory=threading.Lock)


def probability_setting(name: str, default: float) -> float:
    try:
        value = float(os.environ.get(name, default))
        if not math.isfinite(value) or not 0 <= value <= 1:
            raise ValueError
        return value
    except (TypeError, ValueError) as exc:
        raise ModelError(f"{name} має бути числом від 0 до 1.") from exc


def _thresholds(kind: str) -> tuple[float, float]:
    prefix = f"BIRD_{kind.upper()}_PRESENCE"
    accept = probability_setting(prefix + "_ACCEPT", .35 if kind == "image" else .30)
    reject = probability_setting(prefix + "_REJECT", .10 if kind == "image" else .05)
    if reject >= accept:
        raise ModelError(f"{prefix}_REJECT має бути менше {prefix}_ACCEPT.")
    return accept, reject


def _load(kind: str, offline: bool) -> PresenceModel:
    try:
        import torch
        from transformers import AutoFeatureExtractor, AutoImageProcessor, AutoModelForAudioClassification, AutoModelForObjectDetection, pipeline

        torch.set_num_threads(max(1, min(4, os.cpu_count() or 1)))
        model_id, revision = (IMAGE_PRESENCE_MODEL, IMAGE_PRESENCE_REVISION) if kind == "image" else (AUDIO_PRESENCE_MODEL, AUDIO_PRESENCE_REVISION)
        folder = MODEL_CACHE / f"presence-{kind}--{revision}"
        required = ["config.json", "preprocessor_config.json", "model.safetensors"]
        if not all((folder / name).is_file() for name in required):
            if offline:
                raise ModelError("Офлайн-кеш моделі перевірки вмісту відсутній. Запустіть scripts/download_models.py з інтернетом.")
            from huggingface_hub import snapshot_download

            snapshot_download(model_id, revision=revision, local_dir=str(folder), allow_patterns=required)
        requested = os.getenv("BIRD_DEVICE", "auto").lower()
        if requested not in {"auto", "cpu", "cuda"}:
            raise ModelError("BIRD_DEVICE має бути auto, cpu або cuda.")
        if requested == "cuda" and not torch.cuda.is_available():
            raise ModelError("CUDA недоступна. Встановіть BIRD_DEVICE=cpu або auto.")
        device = 0 if requested != "cpu" and torch.cuda.is_available() else -1
        options = {"local_files_only": True, "trust_remote_code": False}
        if kind == "image":
            processor = AutoImageProcessor.from_pretrained(folder, use_fast=False, **options)
            model = AutoModelForObjectDetection.from_pretrained(folder, **options)
            if "bird" not in set(model.config.id2label.values()):
                raise ModelError("Фотомодель перевірки вмісту не містить клас bird.")
            return PresenceModel(pipeline("object-detection", model=model.eval(), image_processor=processor, device=device))
        processor = AutoFeatureExtractor.from_pretrained(folder, **options)
        model = AutoModelForAudioClassification.from_pretrained(folder, **options)
        if model.config.num_labels != 527 or not {"Bird", "Bird vocalization, bird call, bird song"}.issubset(set(model.config.id2label.values())) or processor.sampling_rate != 16000:
            raise ModelError("Аудіомодель перевірки вмісту не відповідає конфігурації AudioSet/16000 Гц.")
        return PresenceModel(pipeline("audio-classification", model=model.eval(), feature_extractor=processor, device=device), 16000)
    except ModelError:
        raise
    except Exception as exc:
        raise ModelError("Не вдалося завантажити модель перевірки вмісту. Перевірте кеш моделей та доступність Hugging Face.") from exc


class PresenceCache:
    def __init__(self):
        self._models = {}
        self._lock = threading.Lock()

    def __call__(self, kind: str, offline: bool) -> PresenceModel:
        with self._lock:
            if kind not in self._models:
                self._models[kind] = _load(kind, offline)
            return self._models[kind]

    def clear(self):
        with self._lock:
            self._models.clear()


get_presence_model = PresenceCache()


def _decision(kind: str, score: float, accept: float, reject: float, evaluations: int) -> dict:
    if score < accept:
        absent = score <= reject
        raise ContentError("bird_not_detected" if absent else "bird_presence_uncertain", MESSAGES[kind][0 if absent else 1])
    model_id, revision = (IMAGE_PRESENCE_MODEL, IMAGE_PRESENCE_REVISION) if kind == "image" else (AUDIO_PRESENCE_MODEL, AUDIO_PRESENCE_REVISION)
    return {"model_id": model_id, "revision": revision, "score": score, "accept_threshold": accept, "reject_threshold": reject, "evaluations": evaluations}


def _image_score(raw) -> float:
    if not isinstance(raw, list) or not raw:
        raise ModelError("Детектор зображень повернув порожню або некоректну відповідь.")
    best = 0.0
    for item in raw:
        try:
            label, score = item["label"], float(item["score"])
            if not isinstance(label, str) or not label or not math.isfinite(score) or not 0 <= score <= 1:
                raise ValueError
        except (KeyError, TypeError, ValueError) as exc:
            raise ModelError("Детектор зображень повернув некоректний результат.") from exc
        if label == "bird":
            best = max(best, score)
    return best


def require_image_bird(image, offline: bool = False) -> dict:
    accept, reject = _thresholds("image")
    bundle = get_presence_model("image", offline)
    try:
        with bundle.lock:
            score = _image_score(bundle.classifier(image, threshold=0.0))
            evaluations = 1
            if score < accept and min(image.size) >= 800:
                width, height = image.size
                crop_width, crop_height = int(width * .65), int(height * .65)
                for left, top in ((0, 0), (width - crop_width, 0), (0, height - crop_height), (width - crop_width, height - crop_height)):
                    crop = image.crop((left, top, left + crop_width, top + crop_height))
                    score = max(score, _image_score(bundle.classifier(crop, threshold=0.0)))
                    evaluations += 1
                    if score >= accept:
                        break
        return _decision("image", score, accept, reject, evaluations)
    except (ContentError, ModelError):
        raise
    except Exception as exc:
        raise ModelError("Не вдалося перевірити наявність птаха на зображенні.") from exc


def _audio_score(bundle, samples, sample_rate: int) -> float:
    with bundle.lock:
        raw = bundle.classifier({"raw": samples, "sampling_rate": sample_rate}, top_k=527, function_to_apply="sigmoid")
    scores = validate_predictions(raw, minimum=527)
    if not {"Bird", "Bird vocalization, bird call, bird song"}.issubset({item["label"] for item in scores}):
        raise ModelError("Аудіодетектор повернув неочікувані класи.")
    return max(item["score"] for item in scores if item["label"] in AUDIO_BIRD_LABELS)


def require_audio_bird(audio, offline: bool = False) -> dict:
    accept, reject = _thresholds("audio")
    bundle = get_presence_model("audio", offline)
    if audio.sample_rate != bundle.sample_rate:
        raise ModelError("Модель перевірки звуків очікує аудіо 16000 Гц.")
    try:
        window, stride = 10 * audio.sample_rate, 8 * audio.sample_rate
        best, evaluations = 0.0, 0
        candidate = None
        for start in range(0, len(audio.samples), stride):
            segment = np.ascontiguousarray(audio.samples[start:start + window], dtype=np.float32)
            if start and len(segment) <= window - stride:
                break
            score = _audio_score(bundle, segment, audio.sample_rate)
            if candidate is None or score > best:
                best, candidate = score, segment
            evaluations += 1
        if not evaluations:
            raise ModelError("Аудіодетектор не отримав відліків аудіо.")
        if reject < best < accept and len(candidate) > audio.sample_rate:
            for offset in np.linspace(0, len(candidate) - audio.sample_rate, 4, dtype=int):
                segment = np.ascontiguousarray(candidate[offset:offset + audio.sample_rate])
                best = max(best, _audio_score(bundle, segment, audio.sample_rate))
                evaluations += 1
                if best >= accept:
                    break
        return _decision("audio", best, accept, reject, evaluations)
    except (ContentError, ModelError):
        raise
    except Exception as exc:
        raise ModelError("Не вдалося перевірити наявність голосу птаха.") from exc
