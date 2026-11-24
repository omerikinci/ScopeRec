import os
import tempfile
import time
import unittest
import math
from types import SimpleNamespace
from pathlib import Path
from unittest.mock import patch

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
import scoperec_mac as scope


class Instrument:
    def __init__(self):
        self.closed = False

    def clear(self):
        pass

    def close(self):
        self.closed = True

    def query_text(self, command):
        if command == "*IDN?":
            return "Siglent Technologies,SDS1104X-E,TEST,1.0"
        return "C1:PAVA RMS,0.008,PKPK,0.016"


class ConnectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = scope.QtWidgets.QApplication.instance() or scope.QtWidgets.QApplication([])

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        class TestWindow(scope.MainWindow):
            def _load_settings(self):
                pass
        self.window = TestWindow()
        self.window.settings_path = Path(self.directory.name) / "settings.json"
        self.window.record_check.setChecked(False)
        self.window.address.setText("127.0.0.1")
        self.window.show()

    def tearDown(self):
        self.window.pause_measure()
        if self.window.worker:
            self.wait_for(lambda: self.window.worker is None)
        self.window.close()
        self.app.processEvents()
        self.directory.cleanup()

    def wait_for(self, predicate):
        deadline = time.monotonic() + 5
        while not predicate() and time.monotonic() < deadline:
            self.app.processEvents()
            time.sleep(0.01)
        self.assertTrue(predicate())

    def test_connection_is_reused_by_measurement(self):
        instrument = Instrument()
        with patch.object(scope, "open_instrument", return_value=instrument) as opened:
            self.window.toggle_instrument()
            self.wait_for(lambda: self.window.connection_worker is None)
            self.assertIs(self.window.idle_client, instrument)
            self.window.start_measure()
            self.wait_for(lambda: self.window.sample_count >= 3)
            self.assertEqual(opened.call_count, 1)
            self.assertAlmostEqual(self.window.series["C1_RMS"].last, 0.008)
            self.window.pause_measure()
            self.wait_for(lambda: self.window.worker is None)
            self.assertTrue(instrument.closed)

    def test_failed_connection_restores_controls(self):
        with patch.object(scope, "open_instrument", side_effect=scope.ScpiError("unavailable")):
            self.window.toggle_instrument()
            self.wait_for(lambda: self.window.connection_worker is None)
        self.assertIsNone(self.window.idle_client)
        self.assertTrue(self.window.connect_btn.isEnabled())
        self.assertTrue(self.window.conn_lan.isEnabled())
        self.assertIn("unavailable", self.window.connection_state.text())

    def test_legend_identifies_and_toggles_each_curve(self):
        self.window._prepare_series()
        self.assertEqual(self.window.plot_legend.count(), len(self.window.curves))
        item = self.window.plot_legend.item(0)
        key = item.data(scope.QtCore.Qt.UserRole)
        self.assertIn("C1", item.text())
        self.assertFalse(item.icon().isNull())
        self.assertFalse(self.window.stats_table.item(0, 0).icon().isNull())
        item.setCheckState(scope.QtCore.Qt.Unchecked)
        self.assertFalse(self.window.curves[key].isVisible())
        item.setCheckState(scope.QtCore.Qt.Checked)
        self.assertTrue(self.window.curves[key].isVisible())
        self.window._prepare_series()
        self.assertEqual(self.window.plot_legend.count(), len(self.window.curves))

    def test_pause_resume_retains_recording_and_measurements(self):
        self.window.dir_label.setText(self.directory.name)
        self.window.record_check.setChecked(True)
        with patch.object(scope, "open_instrument", side_effect=lambda _: Instrument()):
            self.window.start_measure()
            self.wait_for(lambda: self.window.sample_count >= 2)
            recorder = self.window.recorder
            self.window.pause_measure()
            self.wait_for(lambda: self.window.worker is None)
            count = self.window.sample_count
            self.assertIs(self.window.recorder, recorder)
            self.window.start_measure()
            self.wait_for(lambda: self.window.sample_count > count)
            self.assertIs(self.window.recorder, recorder)
            self.window.end_test()
            self.wait_for(lambda: self.window.worker is None)
            self.assertIsNone(self.window.recorder)
            self.assertFalse(self.window.test_active)
            values = self.window.series["C1_RMS"].values
            self.assertTrue(any(math.isnan(value) for value in values))
            self.assertGreaterEqual(len(values), count + 2)
            self.assertTrue(Path(recorder.stem + ".csv").exists())

    def test_close_waits_for_worker_and_releases_instrument(self):
        instrument = Instrument()
        with patch.object(scope, "open_instrument", return_value=instrument):
            self.window.start_measure()
            self.wait_for(lambda: self.window.sample_count >= 1)
            self.window.close()
            self.wait_for(lambda: self.window.worker is None and not self.window.isVisible())
        self.assertTrue(instrument.closed)

    def test_usb_close_clears_pending_transfer_and_returns_local(self):
        requests = []
        class Device:
            def ctrl_transfer(self, request_type, request, value, interface, size, timeout):
                requests.append((request_type, request, value, interface))
                return bytes([1, 0])[:size]

            def clear_halt(self, endpoint):
                pass

        class Endpoint:
            bEndpointAddress = 0x81

            def read(self, size, timeout):
                raise scope.usb.core.USBTimeoutError("empty")

        client = object.__new__(scope.UsbTmcClient)
        client.dev = Device()
        client.intf = SimpleNamespace(bInterfaceNumber=0)
        client.ep_in = client.ep_out = Endpoint()
        client._closed = False
        client._pending_read = True
        client._supports_local = True
        client._claimed = True
        with patch.object(scope.usb.util, "release_interface") as release, \
             patch.object(scope.usb.util, "dispose_resources") as dispose:
            client.close()
            client.close()
        self.assertEqual([r[1] for r in requests], [5, 6, 161, 160])
        self.assertTrue(all(r[0] == 0xA1 and r[2:] == (0, 0) for r in requests))
        release.assert_called_once()
        dispose.assert_called_once()

    def test_closing_during_connection_releases_new_client(self):
        class SlowInstrument(Instrument):
            def query_text(self, command):
                time.sleep(0.1)
                return super().query_text(command)
        instrument = SlowInstrument()
        with patch.object(scope, "open_instrument", return_value=instrument):
            self.window.toggle_instrument()
            self.window.close()
            self.wait_for(lambda: self.window.connection_worker is None and not self.window.isVisible())
        self.assertTrue(instrument.closed)

    def test_themes_and_compact_layout(self):
        for theme in ("dark", "light"):
            self.window.theme.setCurrentIndex(self.window.theme.findData(theme))
            self.window.resize(1100, 900)
            self.app.processEvents()
            self.assertGreaterEqual(self.window.theme.width(), 120)
            self.assertLess(self.window.connect_btn.geometry().right(), self.window.width())
            tabs_top = self.window.right_tabs.mapTo(self.window.centralWidget(), scope.QtCore.QPoint()).y()
            self.assertLess(self.window.theme.geometry().bottom(), tabs_top)

    def test_navigation_keeps_samples_and_defers_display_updates(self):
        self.window._prepare_series()
        key = next(iter(self.window.series))
        self.window.on_sample(1, {key: 0.008})
        self.window._refresh_stats()
        self.window._manual_plot_range()
        self.window.on_sample(2, {key: 0.016})
        self.window._refresh_stats()
        self.assertEqual(self.window.series[key].values, [0.008, 0.016])
        self.assertEqual(len(self.window.curves[key].yData), 1)
        self.window._finish_plot_interaction()
        self.assertEqual(len(self.window.curves[key].yData), 2)
        self.assertFalse(self.window.follow_plot)
        self.window._prepare_series()
        self.window.on_sample(1, {key: 0.024})
        self.window._refresh_stats()
        self.assertAlmostEqual(self.window.curves[key].yData[0], 0.024)


if __name__ == "__main__":
    unittest.main()
