"""Offline search regressions: temporary LanceDB, mocked embedding API."""
import importlib.util
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
import threading
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import Mock, patch

import lancedb
from fastapi.testclient import TestClient


class BrokenStdout:
    def write(self, value):
        raise OSError(22, "Invalid argument")

    def flush(self):
        pass


class SearchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).resolve().parents[1] / "app" / "main.py"
        spec = importlib.util.spec_from_file_location("lavender_search_test", path)
        cls.backend = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.backend
        # Import without reading credentials or opening the user's database.
        with patch("openai.OpenAI", return_value=Mock()), patch("lancedb.connect"):
            spec.loader.exec_module(cls.backend)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.backend.db = lancedb.connect(self.temp.name)
        self.backend.CACHE_PATH = Path(self.temp.name) / "cache.sqlite3"
        self.backend.REPORT_DIRECTORY = Path(self.temp.name) / "reports"
        self.backend.sessions.clear()
        self.addCleanup(setattr, self.backend, "db", None)
        self.backend.client = Mock()
        self.backend.client.embeddings.create.return_value = SimpleNamespace(
            data=[SimpleNamespace(index=0, embedding=[1.0, 0.0, 0.0])]
        )
        self.http = TestClient(self.backend.app)
        self.addCleanup(self.http.close)

    def seed(self):
        rows = []
        for name, vector in [("Expected", [1.0, 0.0, 0.0]), ("Other", [0.0, 1.0, 0.0])]:
            rows.append(dict(
                id=name, file_path=f"{name}.cs", chunk_type="Method",
                namespace="Fixture", class_name="Example", member_name=name,
                signature=f"void {name}()", start_line=1, end_line=1,
                code=f"void {name}() {{}}", embedding_text=name, vector=vector,
            ))
        self.backend.db.create_table(self.backend.TABLE_NAME, rows)

    def test_health_identifies_running_source(self):
        import hashlib
        health = self.http.get("/").json()
        self.assertEqual(health["service"], "lavender")
        expected = hashlib.sha256(Path(self.backend.__file__).read_bytes()).hexdigest().upper()
        self.assertEqual(health["source_revision"], expected)

    def test_embedding_reports_stage_timings(self):
        response = self.http.post("/embed-project", json={"chunks": [{
            "id": "timing-fixture", "file_path": "Fixture.cs", "chunk_type": "WholeFile",
            "code": "class Fixture {}", "embedding_text": "class Fixture {}"
        }]})
        self.assertEqual(response.status_code, 200, response.text)
        timings = response.json()["timings"]
        self.assertEqual(timings["embedding_requests"], 1)
        self.assertEqual(timings["chunk_count"], 1)
        self.assertGreaterEqual(timings["embedding_api_ms"], 0)
        self.assertGreaterEqual(timings["table_write_ms"], 0)
        self.assertGreaterEqual(timings["total_ms"], timings["embedding_api_ms"] + timings["table_write_ms"])
        self.assertTrue(Path(timings["report_path"]).is_file())

    def test_search_survives_unavailable_stdout(self):
        self.seed()
        with patch.object(sys, "stdout", BrokenStdout()):
            response = self.http.post("/search", json={"query": "expected", "group_count": 1})
        self.assertEqual(response.status_code, 200, response.text)
        results = response.json()["results"]
        self.assertEqual(len(results), 1)
        self.assertEqual(results[0]["member_name"], "Expected")
        self.assertAlmostEqual(results[0]["distance"], 0.0, places=5)
        self.assertNotIn("vector", results[0])

    def test_embedding_failure_identifies_stage(self):
        self.seed()
        self.backend.client.embeddings.create.side_effect = OSError(22, "Invalid argument")
        response = self.http.post("/search", json={"query": "expected"})
        self.assertEqual(response.status_code, 500)
        self.assertIn("creating the query embedding", response.json()["detail"])

    def test_missing_index_identifies_stage(self):
        response = self.http.post("/search", json={"query": "expected"})
        self.assertEqual(response.status_code, 500)
        self.assertIn("opening the vector index", response.json()["detail"])

    def chunks(self, count):
        return [dict(id=str(i), file_path=f"{i}.cs", chunk_type="WholeFile",
                     code=f"class C{i} {{}}", embedding_text=f"class C{i} {{}}") for i in range(count)]

    def fake_embeddings(self, *, model, input):
        texts = input if isinstance(input, list) else [input]
        # Deliberately reverse response order to catch vector/chunk misassociation.
        return SimpleNamespace(data=[SimpleNamespace(index=i, embedding=[1.0, float(i), 0.0])
                                     for i in reversed(range(len(texts)))])

    def test_batches_cache_edits_deletions_and_model_changes(self):
        self.backend.client.embeddings.create.side_effect = self.fake_embeddings
        chunks = self.chunks(73)
        first = self.http.post("/embed-project", json={"chunks": chunks}).json()
        self.assertEqual(first["timings"]["embedding_requests"], 3)
        self.assertEqual([len(c.kwargs["input"]) for c in self.backend.client.embeddings.create.call_args_list], [35, 35, 3])
        table = self.backend.db.open_table(self.backend.TABLE_NAME)
        by_id = {r["id"]: r for r in table.to_arrow().to_pylist()}
        self.assertEqual(by_id["1"]["vector"], [1.0, 1.0, 0.0])
        second = self.http.post("/embed-project", json={"chunks": chunks}).json()
        self.assertEqual(second["timings"]["cache_hits"], 73)
        self.assertEqual(second["timings"]["embedding_requests"], 0)
        # Same ID, new content: exactly one new embedding; removed rows disappear.
        chunks = chunks[:2]
        chunks[0]["embedding_text"] = "changed implementation"
        third = self.http.post("/embed-project", json={"chunks": chunks}).json()
        self.assertEqual(third["timings"]["embedded_chunks"], 1)
        self.assertEqual(self.backend.db.open_table(self.backend.TABLE_NAME).count_rows(), 2)
        with patch.object(self.backend, "EMBED_MODEL", "other-model"):
            changed = self.http.post("/embed-project", json={"chunks": chunks}).json()
            self.assertEqual(changed["timings"]["cache_hits"], 0)

    def test_empty_project_removes_old_rows(self):
        self.seed()
        result = self.http.post("/embed-project", json={"chunks": []})
        self.assertEqual(result.status_code, 200)
        result = self.http.post("/search", json={"query": "anything"}).json()
        self.assertEqual(result["results"], [])
        self.assertFalse(result["is_partial"])

    def test_project_isolation_and_persistent_cache(self):
        self.backend.client.embeddings.create.side_effect = self.fake_embeddings
        self.http.post("/embed-project", json={"chunks": self.chunks(1), "project_path": "A", "index_id": "a"})
        self.backend.sessions.clear()  # Simulate backend restart; vectors are persisted separately.
        result = self.http.post("/embed-project", json={"chunks": self.chunks(1), "project_path": "B", "index_id": "b"}).json()
        self.assertEqual(result["timings"]["cache_hits"], 1)
        self.assertNotEqual(self.backend.table_name("A"), self.backend.table_name("B"))
        wrong = self.http.post("/search", json={"query": "x", "project_path": "A", "index_id": "b"})
        self.assertEqual(wrong.status_code, 500)

    def test_partial_search_waits_for_better_batch_but_close_match_returns(self):
        second_batch = threading.Event()
        release_batch = threading.Event()
        search_started = threading.Event()
        batch_calls = 0
        def embed(*, model, input):
            nonlocal batch_calls
            if isinstance(input, str):
                search_started.set()
                return SimpleNamespace(data=[SimpleNamespace(index=0, embedding=[1.0, 0.0, 0.0])])
            batch_calls += 1
            if batch_calls == 2:
                second_batch.set()
                if not release_batch.wait(10):
                    raise RuntimeError("Test batch release timed out")
            vector = [0.0, 1.0, 0.0] if batch_calls == 1 else [1.0, 0.0, 0.0]
            return SimpleNamespace(data=[SimpleNamespace(index=i, embedding=vector) for i in range(len(input))])
        self.backend.client.embeddings.create.side_effect = embed
        with ThreadPoolExecutor(max_workers=2) as pool:
            indexing = pool.submit(self.http.post, "/embed-project", json={"chunks": self.chunks(36), "index_id": "run"})
            try:
                self.assertTrue(second_batch.wait(10))
                quick = self.http.post("/search", json={"query": "x", "index_id": "run", "max_distance": 1.1}).json()
                self.assertTrue(quick["is_partial"])
                self.assertEqual(quick["indexed_chunks"], 35)
                timeout = self.http.post("/search", json={"query": "x", "index_id": "run", "wait_seconds": 0}).json()
                self.assertTrue(timeout["low_confidence"])
                search_started.clear()
                waiting = pool.submit(self.http.post, "/search", json={"query": "x", "index_id": "run"})
                self.assertTrue(search_started.wait(5))
                self.assertFalse(waiting.done())
            finally:
                release_batch.set()
            self.assertEqual(indexing.result(timeout=10).status_code, 200)
            found = waiting.result(timeout=10).json()
            self.assertEqual(found["results"][0]["file_path"], "35.cs")
            # One matching chunk does not make its whole centroid a close match.
            self.assertTrue(found["low_confidence"])

    def test_failure_stops_search_waiting(self):
        self.backend.client.embeddings.create.side_effect = RuntimeError("API unavailable")
        with self.assertRaises(self.backend.HTTPException):
            self.backend.embed_project(self.backend.EmbedProjectRequest(chunks=self.chunks(1), index_id="failed"))
        self.backend.client.embeddings.create.side_effect = self.fake_embeddings
        result = self.http.post("/search", json={"query": "x", "index_id": "failed"}).json()
        self.assertEqual(result["indexing_status"], "failed")
        self.assertIn("API unavailable", result["indexing_error"])
        self.assertTrue(result["is_partial"])

    def test_embedding_error_identifies_source_without_logging_code(self):
        row = dict(id="chunk-7", file_path="Assets/Test.cs", start_line=10,
                   end_line=90, chunk_type="Method", member_name="Test",
                   code="private source", embedding_text="private source")
        context = self.backend.embedding_failure_context(
            ValueError("Invalid 'input[1]': maximum input length is 8192 tokens."),
            ["a", "b"], {"a": [dict(row, file_path="Other.cs")], "b": [row]})
        self.assertEqual(context["failed_input"]["chunks"][0]["file_path"], "Assets/Test.cs")
        self.assertNotIn("private source", str(context))
        unknown = self.backend.embedding_failure_context(ValueError("API unavailable"),
                                                          ["b"], {"b": [row]})
        self.assertIsNone(unknown["failed_input"])
        self.assertEqual(len(unknown["batch_inputs"]), 1)

    def test_failure_after_first_batch_can_resume_from_cache(self):
        calls = 0
        def fail_second(**kwargs):
            nonlocal calls
            calls += 1
            if calls == 2:
                raise RuntimeError("Second batch failed")
            return self.fake_embeddings(**kwargs)
        self.backend.client.embeddings.create.side_effect = fail_second
        request = self.backend.EmbedProjectRequest(chunks=self.chunks(36), index_id="interrupted")
        with self.assertRaises(self.backend.HTTPException):
            self.backend.embed_project(request)
        self.backend.client.embeddings.create.side_effect = self.fake_embeddings
        retried = self.backend.embed_project(request)
        self.assertEqual(retried["timings"]["cache_hits"], 35)
        self.assertEqual(retried["timings"]["embedded_chunks"], 1)

    def test_single_assignment_means_and_complete_group_retrieval(self):
        import numpy as np
        rows = [dict(id=str(i), chunk_order=i, vector=v) for i, v in enumerate(
            [[1., 0.], [0., 1.], [0.9, 0.1], [0.8, 0.2], [0.1, 0.9]])]
        with patch.object(self.backend, "CLUSTER_COUNT", 2):
            groups = self.backend.build_groups(rows)
        self.assertEqual([[r["id"] for r in g["rows"]] for g in groups], [["0", "2", "3"], ["1", "4"]])
        np.testing.assert_allclose(groups[0]["centroid"], [0.9, 0.1])
        found, selected = self.backend.search_groups(groups, [1., 0.], 1)
        self.assertEqual({r["id"] for r in found}, {"0", "2", "3"})
        self.assertEqual(selected[0]["chunk_count"], 3)
        found, selected = self.backend.search_groups(groups, [1., 0.], 2)
        self.assertEqual(len(found), 5)
        self.assertEqual(len(selected), 2)

    def test_default_25_seeds_and_duplicate_vectors(self):
        rows = [dict(id=str(i), chunk_order=i, vector=[1., 0.]) for i in range(60)]
        groups = self.backend.build_groups(rows)
        self.assertEqual(len(groups), 25)
        self.assertEqual(sum(len(g["rows"]) for g in groups), 60)
        found, _ = self.backend.search_groups(groups, [1., 0.], 1)
        self.assertEqual(len(found), 36)  # No previous 25-chunk result cap.
        self.assertEqual(len(self.backend.build_groups(rows[:3])), 3)
        self.assertEqual(self.backend.build_groups([]), [])

    def test_group_search_uses_centroids_not_nearest_individual(self):
        groups = [dict(id=0, centroid=[0., 1.], rows=[dict(id="nearest", vector=[1., 0.])]),
                  dict(id=1, centroid=[0.8, 0.2], rows=[dict(id="group-member", vector=[0.8, 0.2])])]
        found, selected = self.backend.search_groups(groups, [1., 0.], 1)
        self.assertEqual(selected[0]["group_id"], 1)
        self.assertEqual([r["id"] for r in found], ["group-member"])

    def test_cached_and_cold_group_memberships_agree(self):
        self.backend.client.embeddings.create.side_effect = self.fake_embeddings
        chunks = self.chunks(40)
        self.http.post("/embed-project", json={"chunks": chunks})
        membership = lambda: [[r["id"] for r in g["rows"]] for g in self.backend.sessions["legacy"]["groups"]]
        cold = membership()
        self.http.post("/embed-project", json={"chunks": chunks})
        self.assertEqual(cold, membership())
        # Cached rows are published before new rows, but final seeds use request order.
        chunks[0]["embedding_text"] = "changed-first-chunk"
        self.http.post("/embed-project", json={"chunks": chunks})
        self.assertEqual(self.backend.sessions["legacy"]["groups"][0]["rows"][0]["id"], "0")


if __name__ == "__main__":
    unittest.main()

