import io
import unittest
import sys

from codex_quota_monitor.__main__ import parse_args


class ArgumentTests(unittest.TestCase):
    def test_tray_flags_are_tri_state(self):
        self.assertIsNone(parse_args([]).no_tray)
        self.assertTrue(parse_args(["--no-tray"]).no_tray)
        self.assertFalse(parse_args(["--tray"]).no_tray)

    def test_interval_defaults_do_not_override_settings(self):
        args = parse_args([])
        self.assertIsNone(args.quota_interval)

    def test_silence_flag(self):
        self.assertTrue(parse_args(["--silence"]).silence)
        self.assertTrue(parse_args(["--silent"]).silence)

    def test_silence_does_not_mix_with_console_modes(self):
        original_stderr = sys.stderr
        sys.stderr = io.StringIO()
        try:
            with self.assertRaises(SystemExit):
                parse_args(["--silence", "--check"])
            with self.assertRaises(SystemExit):
                parse_args(["--silence", "--once"])
        finally:
            sys.stderr = original_stderr


if __name__ == "__main__":
    unittest.main()
