import argparse
import tempfile
import unittest
from pathlib import Path

from codex_quota_monitor.settings import AppSettings, apply_cli_overrides, load_settings, save_settings


class SettingsTests(unittest.TestCase):
    def test_missing_settings_uses_defaults(self):
        with tempfile.TemporaryDirectory() as tmp:
            settings = load_settings(Path(tmp) / "missing.json")
        self.assertEqual(settings.quota_interval, 180)

    def test_round_trip_settings(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "settings.json"
            original = AppSettings(quota_interval=300, no_tray=True, window_width=280)
            save_settings(original, path=path)
            loaded = load_settings(path)
        self.assertEqual(loaded.quota_interval, 300)
        self.assertTrue(loaded.no_tray)
        self.assertEqual(loaded.window_width, 280)

    def test_corrupt_settings_falls_back(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "settings.json"
            path.write_text("{not-json", encoding="utf-8")
            loaded = load_settings(path)
        self.assertEqual(loaded.quota_interval, 180)

    def test_cli_overrides_settings(self):
        settings = AppSettings(quota_interval=300, no_tray=True)
        args = argparse.Namespace(quota_interval=120, no_tray=False)
        merged = apply_cli_overrides(settings, args)
        self.assertEqual(merged.quota_interval, 120)
        self.assertFalse(merged.no_tray)


if __name__ == "__main__":
    unittest.main()
