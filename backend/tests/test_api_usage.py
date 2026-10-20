"""Offline transport/audit tests. No credentials or provider calls."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

import httpx


class ApiUsageTests(unittest.TestCase):
    def test_attempts_payload_usage_errors_and_summary(self):
        root = Path(__file__).resolve().parents[2]
        spec = importlib.util.spec_from_file_location("usage_backend", root / "backend/app/main.py")
        backend = importlib.util.module_from_spec(spec)
        with patch("openai.OpenAI", return_value=Mock()), patch("lancedb.connect"), patch("httpx.HTTPTransport"), patch("httpx.Client"):
            spec.loader.exec_module(backend)
        with tempfile.TemporaryDirectory() as folder, patch.dict(
                os.environ, {"LAVENDER_API_LOG_DIRECTORY": folder}):
            replies = iter([429, 200])

            def reply(request):
                status = next(replies)
                return httpx.Response(status, json={"usage": {"prompt_tokens": 8, "total_tokens": 8}}
                                      if status == 200 else {"error": "rate limit"})

            with httpx.Client(transport=backend.TrackingTransport(httpx.MockTransport(reply)), trust_env=False) as client:
                with backend.track_api(trigger="semantic_search", tool="lavender_semantic_search"):
                    for _ in range(2):
                        client.post("https://example.test/v1/embeddings",
                                    headers={"Authorization": "Bearer secret-fixture"},
                                    json={"model": "fixture", "input": "exact query"})
            events = [json.loads(line) for path in Path(folder).glob("*.jsonl")
                      for line in path.read_text().splitlines()]
            self.assertEqual(len(events), 4)
            self.assertEqual(events[0]["data"]["request_id"], events[1]["data"]["request_id"])
            self.assertNotEqual(events[0]["data"]["request_id"], events[2]["data"]["request_id"])
            self.assertEqual(json.loads(events[0]["data"]["request_body"])["input"], "exact query")
            self.assertEqual(events[3]["data"]["usage"]["total_tokens"], 8)
            self.assertNotIn("secret-fixture", json.dumps(events))
            spec = importlib.util.spec_from_file_location("usage_summary", root / "tools/api_usage_summary.py")
            summary = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(summary)
            result = summary.summarize(folder)[0]
            self.assertEqual((result["attempts"], result["errors"], result["missing_usage"],
                              result["input_tokens"]), (2, 1, 1, 8))

            def fail(request):
                raise httpx.ConnectError("offline")
            with httpx.Client(transport=backend.TrackingTransport(httpx.MockTransport(fail)), trust_env=False) as client:
                with self.assertRaises(httpx.ConnectError):
                    client.post("https://example.test", json={"model": "fixture"})
            self.assertEqual(summary.summarize(folder)[-1]["errors"], 1)


if __name__ == "__main__":
    unittest.main()
