import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

from codex_quota_monitor.readers import ContextReader


class ContextReaderTests(unittest.TestCase):
    def test_skips_latest_malformed_context_row(self):
        with tempfile.TemporaryDirectory() as tmp:
            home = Path(tmp)
            (home / "models_cache.json").write_text(
                json.dumps({"models": [{"slug": "gpt-test", "context_window": 1000, "effective_context_window_percent": 50}]}),
                encoding="utf-8",
            )
            con = sqlite3.connect(home / "logs_2.sqlite")
            try:
                con.execute("CREATE TABLE logs (id INTEGER PRIMARY KEY, ts INTEGER, feedback_log_body TEXT)")
                con.execute(
                    "INSERT INTO logs (id, ts, feedback_log_body) VALUES (1, 1, ?)",
                    ("event.kind=response.completed model=gpt-test input_token_count=100 conversation.id=old",),
                )
                con.execute(
                    "INSERT INTO logs (id, ts, feedback_log_body) VALUES (2, 2, ?)",
                    ("event.kind=response.completed model=gpt-test input_token_count=bad conversation.id=new",),
                )
                con.commit()
            finally:
                con.close()

            snapshot = ContextReader(home).read()

        self.assertIsNone(snapshot.error)
        self.assertEqual(snapshot.model, "gpt-test")
        self.assertEqual(snapshot.input_tokens, 100)
        self.assertEqual(snapshot.effective_window, 500)
        self.assertEqual(snapshot.skipped_rows, 1)
        self.assertEqual(snapshot.source_label, "latest global")


if __name__ == "__main__":
    unittest.main()
