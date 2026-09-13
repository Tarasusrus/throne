"""Свойства сборки payload для `throne-orchestrator run --effort`.

Красный → зелёный: до реализации `build_run_payload`/`validate_effort` в
`_orchestrator.py` этот файл падает на импорте. Property-based тесты гоняют
инвариант через широкий спектр preview-ответов, а не один фиксированный мок.

Запуск: python3 -m pytest skills/orchestrator/tests -q
"""

from __future__ import annotations

import importlib.util
import subprocess
import sys
from pathlib import Path

import pytest
from hypothesis import given, strategies as st

BIN_DIR = Path(__file__).resolve().parents[1] / "bin"
SCRIPT_PATH = BIN_DIR / "throne-orchestrator"

_spec = importlib.util.spec_from_file_location("_orchestrator", BIN_DIR / "_orchestrator.py")
orchestrator = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(orchestrator)

KNOWN_EFFORTS = ("low", "medium", "high", "xhigh")

skill_strategy = st.fixed_dictionaries(
    {
        "skill_id": st.text(min_size=1, max_size=12),
        "selected": st.booleans(),
        "materializable": st.booleans(),
    }
)

preview_strategy = st.fixed_dictionaries(
    {
        "system_prompt": st.text(max_size=200),
        "user_prompt": st.text(max_size=200),
        "selected_part_ids": st.lists(st.text(min_size=1, max_size=10), max_size=5),
        "available_skills_for_mode": st.lists(skill_strategy, max_size=5),
    }
)

# vendor/model — пустая строка воспроизводит «флаг не передан» (см. cmd_run в
# throne-orchestrator: default пустая строка).
vendor_model_strategy = st.one_of(st.just(""), st.text(alphabet=st.characters(min_codepoint=97, max_codepoint=122), min_size=1, max_size=10))

not_effort_strategy = st.text(max_size=15).filter(lambda s: s not in KNOWN_EFFORTS and s != "")


class TestBuildRunPayloadInvariants:
    @given(preview=preview_strategy, vendor=vendor_model_strategy, model=vendor_model_strategy)
    def test_no_effort_flag_leaves_payload_without_effort_key(self, preview, vendor, model):
        payload = orchestrator.build_run_payload(preview, vendor, model, "")
        assert "effort" not in payload

    @given(preview=preview_strategy, effort=st.sampled_from(KNOWN_EFFORTS))
    def test_known_effort_lands_verbatim_in_payload(self, preview, effort):
        payload = orchestrator.build_run_payload(preview, "", "", effort)
        assert payload["effort"] == effort

    @given(preview=preview_strategy, vendor=vendor_model_strategy, model=vendor_model_strategy)
    def test_effort_omission_does_not_perturb_any_other_field(self, preview, vendor, model):
        with_effort = orchestrator.build_run_payload(preview, vendor, model, "high")
        without_effort = orchestrator.build_run_payload(preview, vendor, model, "")
        with_effort.pop("effort")
        assert with_effort == without_effort

    @given(preview=preview_strategy)
    def test_base_fields_always_mirror_preview(self, preview):
        payload = orchestrator.build_run_payload(preview, "", "", "")
        assert payload["mode"] == "work"
        assert payload["system_prompt"] == preview["system_prompt"]
        assert payload["user_prompt"] == preview["user_prompt"]
        assert payload["selected_part_ids"] == preview["selected_part_ids"]
        expected_skills = [
            s["skill_id"]
            for s in preview["available_skills_for_mode"]
            if s["selected"] and s["materializable"]
        ]
        assert payload["selected_skill_ids"] == expected_skills


class TestValidateEffortInvariants:
    @given(effort=st.sampled_from(KNOWN_EFFORTS))
    def test_known_efforts_pass(self, effort):
        orchestrator.validate_effort(effort)  # не должно бросать

    def test_empty_string_passes_as_not_provided(self):
        orchestrator.validate_effort("")  # флаг не задан — валиден

    @given(effort=not_effort_strategy)
    def test_unknown_effort_is_rejected(self, effort):
        with pytest.raises(ValueError):
            orchestrator.validate_effort(effort)


class TestCliOrderingRejectsBeforeHttp:
    """Неизвестный --effort должен отбиваться раньше первого сетевого вызова.

    own_tag()/assert_own_tag() — первый шаг, который требует THRONE_INTENT_ID и
    делает HTTP GET. Если валидация эффорта стоит раньше него, при незаданном
    THRONE_INTENT_ID и битом --effort в stderr будет жалоба на effort, а не на
    отсутствующий INTENT_ID/сетевую ошибку.
    """

    def test_bad_effort_fails_before_intent_lookup(self, tmp_path):
        env = {"PATH": "/usr/bin:/bin:/usr/local/bin"}
        result = subprocess.run(
            [str(SCRIPT_PATH), "run", "--intent", "does-not-matter", "--effort", "bogus"],
            capture_output=True,
            text=True,
            env=env,
            timeout=10,
        )
        assert result.returncode == 64
        assert "effort" in result.stderr
        assert "THRONE_INTENT_ID" not in result.stderr
