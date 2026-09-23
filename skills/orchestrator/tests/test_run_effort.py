"""Политика запуска `throne-orchestrator run/review`: пары модель×effort.

Инварианты: effort исполнителя и ревьюера — только low/medium; ревью не идёт на
сильнейшей модели вендора; без флагов в payload уходят дефолты политики, а не
пустота, на которую сервер ответил бы своим opus + high. Запрещённая пара
отбивается до первого HTTP-вызова.

Запуск: python3 -m pytest skills/orchestrator/tests -q
"""

from __future__ import annotations

import importlib.util
import json
import stat
import subprocess
from pathlib import Path

import pytest
from hypothesis import given, strategies as st

BIN_DIR = Path(__file__).resolve().parents[1] / "bin"
SCRIPT_PATH = BIN_DIR / "throne-orchestrator"

_spec = importlib.util.spec_from_file_location("_orchestrator", BIN_DIR / "_orchestrator.py")
orchestrator = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(orchestrator)

# Захардкожено намеренно, а не взято из модуля: тест обязан покраснеть, если
# кто-то впишет high в ALLOWED_EFFORTS или сменит сильнейшую модель на слабую.
FORBIDDEN_EFFORTS = ("high", "xhigh")
STRONGEST = {"claude": "opus", "codex": "gpt-5.6-sol"}
SERVER_DEFAULT = {"claude": ("opus", "high"), "codex": ("gpt-5.6-sol", "high")}
MODES = ("work", "verify")

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

policed_vendor = st.sampled_from(sorted(STRONGEST))
# Пустая строка воспроизводит «флаг не передан» (default в cmd_run/cmd_review).
model_flag = st.sampled_from(["", "opus", "sonnet", "haiku", "gpt-5.6-sol", "gpt-5.6-terra"])
effort_flag = st.sampled_from(["", "low", "medium", "high", "xhigh"])
unknown_effort = st.text(max_size=15).filter(
    lambda s: s not in ("low", "medium", "high", "xhigh") and s != ""
)


def _payload_or_refusal(preview, vendor, model, effort, mode):
    try:
        return orchestrator.build_run_payload(preview, vendor, model, effort, mode)
    except ValueError:
        return None


class TestPolicyInvariants:
    @given(preview=preview_strategy, vendor=policed_vendor, model=model_flag,
           effort=effort_flag, mode=st.sampled_from(MODES))
    def test_no_payload_ever_carries_forbidden_pair(self, preview, vendor, model, effort, mode):
        payload = _payload_or_refusal(preview, vendor, model, effort, mode)
        if payload is None:
            return
        assert payload["effort"] not in FORBIDDEN_EFFORTS
        assert payload["model"]
        if mode == "verify":
            assert payload["model"] != STRONGEST[vendor]

    @given(preview=preview_strategy, vendor=policed_vendor, mode=st.sampled_from(MODES))
    def test_no_flags_never_fall_to_server_default(self, preview, vendor, mode):
        payload = orchestrator.build_run_payload(preview, vendor, "", "", mode)
        assert payload["vendor"] == vendor
        assert payload["effort"] == "medium"
        assert (payload["model"], payload["effort"]) != SERVER_DEFAULT[vendor]
        assert payload["model"] != STRONGEST[vendor]

    @given(preview=preview_strategy, vendor=policed_vendor,
           effort=st.sampled_from(FORBIDDEN_EFFORTS), mode=st.sampled_from(MODES))
    def test_high_effort_is_refused_in_every_role(self, preview, vendor, effort, mode):
        with pytest.raises(ValueError):
            orchestrator.build_run_payload(preview, vendor, "", effort, mode)

    @given(preview=preview_strategy, vendor=policed_vendor, effort=st.sampled_from(["", "low", "medium"]))
    def test_review_on_strongest_model_is_refused(self, preview, vendor, effort):
        with pytest.raises(ValueError):
            orchestrator.build_run_payload(preview, vendor, STRONGEST[vendor], effort, "verify")

    @given(preview=preview_strategy, effort=st.sampled_from(["low", "medium"]))
    def test_task_may_run_opus_under_the_ceiling(self, preview, effort):
        payload = orchestrator.build_run_payload(preview, "claude", "opus", effort, "work")
        assert (payload["model"], payload["effort"]) == ("opus", effort)

    @given(preview=preview_strategy)
    def test_opencode_is_not_policed(self, preview):
        payload = orchestrator.build_run_payload(preview, "opencode", "", "", "work")
        assert "model" not in payload and "effort" not in payload

    @given(preview=preview_strategy, vendor=st.sampled_from(["gemini", "x"]))
    def test_vendor_outside_policy_is_refused(self, preview, vendor):
        with pytest.raises(ValueError):
            orchestrator.build_run_payload(preview, vendor, "", "", "work")

    @given(effort=unknown_effort)
    def test_unknown_effort_is_refused(self, effort):
        with pytest.raises(ValueError):
            orchestrator.check_launch("work", "", "", effort)

    def test_review_without_vendor_refuses_any_strongest(self):
        for model in STRONGEST.values():
            with pytest.raises(ValueError):
                orchestrator.check_launch("verify", "", model, "")


class TestBaseFields:
    @given(preview=preview_strategy)
    def test_base_fields_always_mirror_preview(self, preview):
        payload = orchestrator.build_run_payload(preview, "claude", "", "")
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


def _cli(args):
    return subprocess.run(
        [str(SCRIPT_PATH), *args], capture_output=True, text=True,
        env={"PATH": "/usr/bin:/bin:/usr/local/bin"}, timeout=10,
    )


class TestCliRefusesBeforeHttp:
    """Без THRONE_INTENT_ID первый HTTP-шаг упал бы с жалобой на него — значит,
    отказ по политике должен прийти раньше и сказать про effort/модель."""

    def test_bad_effort_fails_before_intent_lookup(self):
        result = _cli(["run", "--intent", "x", "--effort", "bogus"])
        assert result.returncode == 64
        assert "effort" in result.stderr
        assert "THRONE_INTENT_ID" not in result.stderr

    @pytest.mark.parametrize("effort", FORBIDDEN_EFFORTS)
    def test_task_high_effort_points_to_decomposition(self, effort):
        result = _cli(["run", "--intent", "x", "--effort", effort])
        assert result.returncode == 64
        assert "декомпозиции" in result.stderr
        assert "THRONE_INTENT_ID" not in result.stderr

    @pytest.mark.parametrize("args", [["--effort", "high"], ["--model", "opus"],
                                      ["--vendor", "codex", "--model", "gpt-5.6-sol"]])
    def test_review_forbidden_pair_fails_before_intent_lookup(self, args):
        result = _cli(["review", "--intent", "x", *args])
        assert result.returncode == 64
        assert "ревью" in result.stderr
        assert "THRONE_INTENT_ID" not in result.stderr


FAKE_CURL = r'''#!/usr/bin/env python3
"""curl-заглушка: свой тег, дефолтный вендор из настроек, preview; тело run — в файл."""
import json, os, sys
args = sys.argv[1:]
out = args[args.index("-o") + 1]
url = [a for a in args if a.startswith("http")][0]
path = url.split("://", 1)[1].split("/", 1)[1]
method = "POST" if "-X" in args else "GET"
with open(os.environ["FAKE_CURL_LOG"], "a") as log:
    log.write(method + " /" + path + "\n")
if method == "GET" and path == "api/v1/intents/orch":
    status, body = 200, {"id": "orch", "tags": [{"name": "t"}]}
elif method == "GET" and path == "api/v1/intents/child":
    status, body = 200, {"id": "child", "tags": [{"name": "t"}]}
elif method == "GET" and path == "api/v1/settings/terminal":
    status, body = 200, {"default_vendor": os.environ["FAKE_DEFAULT_VENDOR"]}
elif method == "POST" and path == "api/v1/intents/child/terminal/preview":
    sys.stdin.read()
    status, body = 200, {"system_prompt": "s", "user_prompt": "u", "selected_part_ids": [],
                         "available_skills_for_mode": []}
elif method == "POST" and path == "api/v1/intents/child/terminal/run":
    with open(os.environ["FAKE_RUN_BODY"], "w") as handle:
        handle.write(sys.stdin.read())
    status, body = 200, {"session_state": "started", "session_name": "throne-child"}
else:
    status, body = 404, {"error": "unexpected " + method + " " + path}
with open(out, "w") as handle:
    json.dump(body, handle, ensure_ascii=False)
sys.stdout.write(str(status))
'''


def _fake_env(tmp_path, default_vendor):
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    curl = bin_dir / "curl"
    curl.write_text(FAKE_CURL)
    curl.chmod(curl.stat().st_mode | stat.S_IXUSR)
    return {
        "PATH": str(bin_dir) + ":/usr/bin:/bin:/usr/local/bin",
        "THRONE_INTENT_ID": "orch",
        "THRONE_API_BASE": "http://fake",
        "FAKE_DEFAULT_VENDOR": default_vendor,
        "FAKE_RUN_BODY": str(tmp_path / "run.json"),
        "FAKE_CURL_LOG": str(tmp_path / "calls.log"),
    }


class TestCliSendsPolicyDefaults:
    """Проверяется тело, реально ушедшее в /terminal/run, а не возврат хелпера."""

    @pytest.mark.parametrize("vendor,model", [("claude", "sonnet"), ("codex", "gpt-5.6-terra")])
    def test_run_without_flags_sends_policy_defaults_for_server_vendor(self, tmp_path, vendor, model):
        env = _fake_env(tmp_path, vendor)
        run_body = tmp_path / "run.json"
        result = subprocess.run(
            [str(SCRIPT_PATH), "run", "--intent", "child"],
            capture_output=True, text=True, env=env, timeout=20,
        )
        assert result.returncode == 0, result.stderr
        sent = json.loads(run_body.read_text())
        assert (sent["vendor"], sent["model"], sent["effort"]) == (vendor, model, "medium")

    def test_review_with_unpoliced_default_vendor_creates_nothing(self, tmp_path):
        env = _fake_env(tmp_path, "gemini")
        result = subprocess.run(
            [str(SCRIPT_PATH), "review", "--intent", "child"],
            capture_output=True, text=True, env=env, timeout=20,
        )
        assert result.returncode == 64
        assert "политике" in result.stderr
        calls = (tmp_path / "calls.log").read_text().splitlines()
        assert calls == ["GET /api/v1/settings/terminal"]
