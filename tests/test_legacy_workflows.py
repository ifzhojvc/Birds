from dataclasses import replace
from types import SimpleNamespace
from unittest.mock import Mock

import pytest
import streamlit as st

from birdlab.domain import Prediction, validate_predictions
from birdlab.errors import ModelError, StorageError
from birdlab.files import save_upload, sha256
from birdlab.service import AnalysisService, sample_manifest
from test_ui import app_test


@pytest.mark.parametrize(("page", "kind", "name"), [
    ("Аналіз фото", "image", "bird.png"), ("Аналіз аудіо", "audio", "bird.wav"),
])
def test_streamlit_submit_preview_and_persist(app_test, monkeypatch, tmp_path, png_bytes, wav_bytes, result, page, kind, name):
    payload = png_bytes if kind == "image" else wav_bytes
    key = "photo" if kind == "image" else "audio"
    upload = SimpleNamespace(name=name, getvalue=lambda: payload)
    monkeypatch.setattr(st, "file_uploader", lambda *args, **kwargs: upload if kwargs["key"] == f"{key}_file" else [])
    monkeypatch.setattr(f"birdlab.service.classify_{kind}", Mock(return_value=result))
    monkeypatch.setattr("birdlab.service.get_audio_model", lambda *_: SimpleNamespace(sample_rate=8000))
    app_test.run()
    app_test.sidebar.radio[0].set_value(page).run()
    assert not app_test.exception
    app_test.button(key=f"analyze_{key}").click().run()
    assert not app_test.exception
    assert any("збережено у SQLite" in element.value for element in app_test.success)
    assert any("TOP-3" in element.value for element in app_test.markdown)
    assert (tmp_path / "out.csv").is_file()
    app_test.button(key=f"history_{kind}_result").click().run()
    assert app_test.sidebar.radio[0].value == "Історія аналізів"
    assert app_test.checkbox[0].label.startswith("Підтверджую видалення")


@pytest.mark.parametrize(("page", "key"), [("Аналіз фото", "photo"), ("Аналіз аудіо", "audio")])
def test_streamlit_corrupt_upload_never_creates_an_analysis(app_test, monkeypatch, tmp_path, page, key):
    upload = SimpleNamespace(name="corrupt.png" if key == "photo" else "corrupt.wav", getvalue=lambda: b"broken")
    monkeypatch.setattr(st, "file_uploader", lambda *args, **kwargs: upload if kwargs["key"] == f"{key}_file" else [])
    app_test.run()
    app_test.sidebar.radio[0].set_value(page).run()
    assert len(app_test.error) == 1
    assert not list((tmp_path / "uploads").iterdir())


def test_batch_checks_manifest_paths_and_preserves_verified_evaluation(repository, monkeypatch, tmp_path, result):
    sample = sample_manifest()[0]
    rows = [sample, {**sample, "file_name": "../README.md"}, {**sample, "sha256": "bad"}]
    monkeypatch.setattr("birdlab.service.sample_manifest", lambda: rows)
    monkeypatch.setattr("birdlab.service.classify_image", Mock(return_value=result))
    progress = Mock()
    service = AnalysisService(repository, export_path=tmp_path / "bird.csv")
    saved, traversal, tampered = service.batch_samples(progress)
    assert saved["status"] == "Збережено"
    assert "шлях" in traversal["status"] and traversal["id"] is None
    assert "SHA-256" in tampered["status"] and tampered["id"] is None
    assert progress.call_args.args == (1.0, sample["file_name"])
    assert repository.evaluations()[0]["expected_label"] == sample["model_label"]


def test_streamlit_reports_quality_and_compares_stored_audio(app_test, repository, add_record, monkeypatch, png_bytes, result):
    sample = sample_manifest()[0]
    add_record(evaluation={"label_source": sample["label_source"], "expected_label": result.predictions[0].label,
                           "expected_scientific": "Cardinalis cardinalis", "file_hash": sha256(png_bytes)})
    add_record(kind="audio")
    app_test.run()
    app_test.sidebar.radio[0].set_value("Порівняння видів").run()
    assert not app_test.exception
    assert app_test.selectbox(key="compare_audio").value["analysis_type"] == "audio"
    assert any("той самий" in element.value for element in app_test.info)
    app_test.sidebar.radio[0].set_value("Статистика").run()
    assert next(metric.value for metric in app_test.metric if metric.label == "Top-1 accuracy") == "100.00%"


def test_sqlite_delete_rolls_back_file_quarantine_on_second_rename_failure(repository, add_record, monkeypatch):
    first, second = add_record(), add_record()
    first_path = repository.media_path(repository.get(first))
    second_path = repository.media_path(repository.get(second))
    rename = type(first_path).rename

    def rename_or_fail(path, target):
        if path == second_path:
            raise PermissionError("test locked media")
        return rename(path, target)

    monkeypatch.setattr(type(first_path), "rename", rename_or_fail)
    with pytest.raises(StorageError, match="Не вдалося видалити"):
        repository.delete_many([first, second])
    assert first_path.is_file() and second_path.is_file()
    assert {row["id"] for row in repository.all()} == {first, second}


def test_background_analysis_records_no_species(repository, result, wav_bytes):
    background = replace(result, predictions=(Prediction("background", .8), *result.predictions[1:]))
    path = save_upload(wav_bytes, "song.wav", repository.upload_dir)
    analysis_id = repository.save("audio", "song.wav", path, sha256(wav_bytes), background)
    analysis = repository.get(analysis_id)
    assert analysis["scientific_name"] is None
    assert "фоновий звук" in analysis["description"]


def test_malformed_model_score_is_not_coerced_into_result():
    with pytest.raises(ModelError, match="формат"):
        validate_predictions([{"label": "bird", "score": "invalid"}, {"label": "b", "score": .15}, {"label": "c", "score": .05}])
