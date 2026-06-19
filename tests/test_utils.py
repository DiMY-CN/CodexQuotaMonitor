import unittest

from codex_quota_monitor.utils import compact_error, compact_token_pair, format_countdown


class FormattingTests(unittest.TestCase):
    def test_compact_token_pair(self):
        self.assertEqual(compact_token_pair(129000, 258400), "129/258")
        self.assertEqual(compact_token_pair(None, 258400), "--")

    def test_format_countdown_empty(self):
        self.assertEqual(format_countdown(None), "reset --")

    def test_compact_error_truncates(self):
        text = compact_error(RuntimeError("x" * 220))
        self.assertLessEqual(len(text), 180)
        self.assertTrue(text.endswith("..."))


if __name__ == "__main__":
    unittest.main()
