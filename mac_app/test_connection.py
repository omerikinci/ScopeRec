import os
import tempfile
import time
import unittest
import math
import gc
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
        self.window.deleteLater()
        self.app.sendPostedEvents(None, scope.QtCore.QEvent.DeferredDelete)
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

    def test_hover_shows_original_sample_name_time_and_unit(self):
        self.window._prepare_series()
        series = self.window.series["C1_RMS"]
        for t, v in [(1.0, 0.002), (2.0, 0.004), (3.0, 0.006)]:
            series.add(t, v)
        self.window.follow_check.setChecked(False)
        self.window._redraw_plot()
        plot = self.window.plot
        plot.setXRange(0, 4, padding=0)
        plot.setYRange(0, 0.01, padding=0)
        self.app.processEvents()
        point = plot.mapFromScene(plot.getViewBox().mapViewToScene(scope.QtCore.QPointF(2.0, 0.004)))
        hover = self.window.plot_hover
        hover.update_at(point)
        self.assertTrue(hover.label.isVisible())
        self.assertIn("C1_RMS", hover.label.text())
        self.assertIn("2.000 s", hover.label.text())
        self.assertIn("4 mV", hover.label.text())
        self.assertGreater(hover.label.x(), point.x())
        self.assertLess(hover.label.geometry().bottom(), point.y())
        self.assertTrue(plot.viewport().rect().contains(hover.label.geometry()))
        self.window.curves["C1_RMS"].hide()
        hover.update_at(point)
        self.assertFalse(hover.label.isVisible())
        self.window.curves["C1_RMS"].show()
        hover.update_at(scope.QtCore.QPoint(1, 1))
        self.assertFalse(hover.label.isVisible())
        hover.update_at(point)
        self.window.clear_plot()
        self.assertFalse(hover.label.isVisible())

    def test_hover_in_recording_viewer_uses_csv_unit(self):
        csv = Path(self.directory.name) / "hover.csv"
        csv.write_text("date,time,C1_RMS[V]\nnow,1,0.002\nnow,2,0.004\nnow,3,0.006\n")
        with patch.object(scope.QtWidgets.QMessageBox, "warning") as warning:
            self.window.load_recording(str(csv))
            self.assertFalse(warning.called, warning.call_args)
        self.window.right_tabs.setCurrentIndex(2)
        self.app.processEvents()
        plot = self.window.viewer_plot
        plot.setXRange(0, 4, padding=0)
        plot.setYRange(0, 0.01, padding=0)
        self.app.processEvents()
        point = plot.mapFromScene(plot.getViewBox().mapViewToScene(scope.QtCore.QPointF(2.0, 0.004)))
        self.window.viewer_hover.update_at(point)
        self.assertIn("4 mV", self.window.viewer_hover.label.text())

    def test_repeated_graph_mouse_events_while_measuring(self):
        with patch.object(scope, "open_instrument", side_effect=lambda _: Instrument()):
            self.window.start_measure()
            self.wait_for(lambda: self.window.sample_count >= 3)
            for index in range(800):
                self.app.processEvents()
                view = self.window.plot.getViewBox()
                ranges = view.viewRange()
                point = self.window.plot.mapFromScene(view.mapViewToScene(scope.QtCore.QPointF(
                    max(0, self.window.current_t - 0.4),
                    ranges[1][0] + (ranges[1][1] - ranges[1][0]) * (0.5 + 0.4 * math.sin(index / 20)))))
                event = scope.QtGui.QMouseEvent(scope.QtCore.QEvent.MouseMove, scope.QtCore.QPointF(point),
                    scope.QtCore.QPointF(self.window.plot.viewport().mapToGlobal(point)), scope.QtCore.Qt.NoButton,
                    scope.QtCore.Qt.NoButton, scope.QtCore.Qt.NoModifier)
                self.app.sendEvent(self.window.plot.viewport(), event)
                time.sleep(0.005)
            self.assertTrue(self.window.running)
            self.assertGreater(self.window.sample_count, 10)

    def test_measurement_layout_survives_garbage_collection_with_serial_panel(self):
        self.window._set_serial_side_panel(True)
        with patch.object(scope, "open_instrument", side_effect=lambda _: Instrument()):
            self.window.toggle_instrument()
            self.wait_for(lambda: self.window.connection_worker is None)
            gc.collect()
            self.app.processEvents()
            self.window.start_measure()
            self.wait_for(lambda: self.window.sample_count >= 3)
            for _ in range(4):
                gc.collect()
                self.app.processEvents()
                root = self.window.centralWidget().layout()
                self.assertEqual(root.count(), 3)
                self.assertIs(root.itemAt(2).widget(), self.window.root_split)
                self.assertTrue(self.window.root_split.isVisible())
                self.assertTrue(self.window.stats_table.isVisible())
                self.assertTrue(self.window.plot.isVisible())
                self.assertTrue(self.window.serial_side.isVisible())
                self.assertGreater(self.window.root_split.height(), 200)

    def test_clear_graph_while_recording_keeps_recording_and_series_identity(self):
        self.window.clear_plot()
        self.window.dir_label.setText(self.directory.name)
        self.window.record_check.setChecked(True)
        with patch.object(scope, "open_instrument", side_effect=lambda _: Instrument()):
            self.window.start_measure()
            self.wait_for(lambda: self.window.sample_count >= 3)
            recorder = self.window.recorder
            count = self.window.sample_count
            legends = self.window.plot_legend.count()
            self.window.clear_plot_btn.click()
            self.assertTrue(self.window.running)
            self.assertIs(self.window.recorder, recorder)
            self.assertEqual(self.window.sample_count, count)
            self.assertTrue(all(not series.times for series in self.window.series.values()))
            self.assertEqual(self.window.plot_legend.count(), legends)
            self.wait_for(lambda: self.window.sample_count > count)
            self.window.end_test()
            self.wait_for(lambda: self.window.worker is None)
            with open(recorder.stem + ".csv", encoding="utf-8-sig") as file:
                self.assertGreaterEqual(len(file.readlines()), count + 2)
        self.window.clear_plot()

    def test_clear_terminal_is_independent_and_keeps_serial_graph(self):
        self.window.on_serial_line("Seri 1", "T:12")
        self.window.on_serial_line("Seri 2", "T:13")
        self.window.on_serial_values("Seri 1", {"T": 12.0})
        self.window.serial_logs["Seri 1"]["clear"].click()
        self.assertEqual(self.window.serial_logs["Seri 1"]["text"].toPlainText(), "")
        self.assertIn("T:13", self.window.serial_logs["Seri 2"]["text"].toPlainText())
        self.assertEqual(self.window.series["Seri 1_T"].last, 12.0)

    def test_side_panel_has_wide_handle_and_can_resize(self):
        self.window.resize(1440, 1000)
        self.window._set_serial_side_panel(True)
        self.app.processEvents()
        splitter = self.window.work_split
        before = splitter.sizes()
        self.assertGreaterEqual(splitter.handle(1).width(), 14)
        splitter.moveSplitter(before[0] - 90, 1)
        self.app.processEvents()
        after = splitter.sizes()
        self.assertNotEqual(before, after)
        self.assertGreater(after[1], before[1])
        self.assertGreaterEqual(self.window.serial_side.width(), 320)
        self.window._set_serial_side_panel(False)
        self.window._set_serial_side_panel(True)
        self.app.processEvents()
        self.assertGreater(splitter.handle(1).width(), 4)

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
