"""`review` печатает id ревью-интента сразу после создания.

Упал линк или запуск — интент уже есть, и без напечатанного id повторный
`review` создал бы второй. curl подменяется заглушкой на PATH: create → 201,
links → 500.

Запуск: python3 -m pytest skills/orchestrator/tests -q
"""

from __future__ import annotations

import stat
import subprocess
from pathlib import Path


BIN_DIR = Path(__file__).resolve().parents[1] / "bin"
SCRIPT_PATH = BIN_DIR / "throne-orchestrator"

FAKE_CURL = r'''#!/usr/bin/env python3
"""curl-заглушка: GET интентов, POST create → 201, POST links → 500."""
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
    status, body = 200, {"id": "child", "tags": [{"name": "t"}],
                         "text": "## Ветка\nfeat/x\n\n## Definition of Done\n- a\n"}
elif method == "GET" and path == "api/v1/settings/terminal":
    status, body = 200, {"default_vendor": "claude"}
elif method == "POST" and path == "api/v1/intents":
    status, body = 201, {"id": "rev-1"}
elif method == "POST" and path == "api/v1/intents/rev-1/links":
    status, body = 500, {"error": "boom"}
else:
    status, body = 404, {"error": "unexpected " + method + " " + path}
with open(out, "w") as handle:
    json.dump(body, handle, ensure_ascii=False)
sys.stdout.write(str(status))
'''


class TestReviewPrintsIdBeforeLink:
    def test_review_id_survives_a_failed_link(self, tmp_path):
        bin_dir = tmp_path / "bin"
        bin_dir.mkdir()
        curl = bin_dir / "curl"
        curl.write_text(FAKE_CURL)
        curl.chmod(curl.stat().st_mode | stat.S_IXUSR)
        log = tmp_path / "calls.log"
        env = {
            "PATH": str(bin_dir) + ":/usr/bin:/bin:/usr/local/bin",
            "THRONE_INTENT_ID": "orch",
            "THRONE_API_BASE": "http://fake",
            "FAKE_CURL_LOG": str(log),
        }
        result = subprocess.run(
            [str(SCRIPT_PATH), "review", "--intent", "child"],
            capture_output=True, text=True, env=env, timeout=20,
        )
        assert result.returncode != 0
        # id создан и напечатан до того, как линк упал — повторный `review` не нужен.
        assert "rev-1" in result.stdout.split()
        calls = log.read_text().splitlines()
        assert "POST /api/v1/intents/rev-1/links" in calls
        assert not any("preview" in c for c in calls)
