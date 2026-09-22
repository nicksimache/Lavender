"""Offline search regressions: temporary LanceDB, mocked embedding API."""
import importlib.util
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
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
        self.addCleanup(setattr, self.backend, "db", None)
        self.backend.client = Mock()
        self.backend.client.embeddings.create.return_value = SimpleNamespace(
            data=[SimpleNamespace(embedding=[1.0, 0.0, 0.0])]
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

    def test_search_survives_unavailable_stdout(self):
        self.seed()
        with patch.object(sys, "stdout", BrokenStdout()):
            response = self.http.post("/search", json={"query": "expected", "top_k": 1})
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


if __name__ == "__main__":
    unittest.main()
