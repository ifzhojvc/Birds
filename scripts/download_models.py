import sys
import time
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from birdlab.config import AUDIO_MODEL, AUDIO_REVISION, IMAGE_MODEL, IMAGE_REVISION, MODEL_CACHE, ROOT
from birdlab.presence import AUDIO_PRESENCE_MODEL, AUDIO_PRESENCE_REVISION, IMAGE_PRESENCE_MODEL, IMAGE_PRESENCE_REVISION


def main() -> None:
    from huggingface_hub import snapshot_download
    for kind, model_id, revision in (("image", IMAGE_MODEL, IMAGE_REVISION), ("audio", AUDIO_MODEL, AUDIO_REVISION), ("presence-image", IMAGE_PRESENCE_MODEL, IMAGE_PRESENCE_REVISION), ("presence-audio", AUDIO_PRESENCE_MODEL, AUDIO_PRESENCE_REVISION)):
        print(f"Завантаження {model_id} @ {revision}", flush=True)
        for attempt in range(4):
            try:
                snapshot_download(model_id, revision=revision, local_dir=str(MODEL_CACHE / f"{kind}--{revision}"), allow_patterns=["config.json", "preprocessor_config.json", "model.safetensors"], max_workers=2)
                break
            except Exception as exc:
                if attempt == 3:
                    raise
                print("Перервано завантаження, повторна спроба з докачуванням:", type(exc).__name__, flush=True)
                time.sleep(2 * (attempt + 1))
    from birdlab.models import get_audio_model, get_image_model

    for loader in (get_image_model, get_audio_model):
        bundle = loader(True)
        print(type(bundle.classifier.model).__name__, "пристрій", bundle.classifier.device, "частота", bundle.sample_rate, flush=True)
    from birdlab.presence import get_presence_model

    for kind in ("image", "audio"):
        bundle = get_presence_model(kind, True)
        print(type(bundle.classifier.model).__name__, "пристрій", bundle.classifier.device, "частота", bundle.sample_rate, flush=True)


if __name__ == "__main__":
    main()
