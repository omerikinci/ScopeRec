"""Opt-in USB hardware check; never runs during automated unit tests."""
import json
import math
import os
import re
import sys
import time

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
import scoperec_mac as scope


def main():
    queries = []
    frames = []

    class AuditedUsb(scope.UsbTmcClient):
        def query_text(self, command):
            response = super().query_text(command)
            if "PAVA?" in command:
                queries.append((command, response))
            return response

    class HardwareWindow(scope.MainWindow):
        def _load_settings(self):
            pass

        def _save_settings(self):
            pass

        def on_sample(self, timestamp, values):
            super().on_sample(timestamp, values)
            frames.append(dict(values))
            if len(frames) == 3:
                self.clear_plot()

    app = scope.QtWidgets.QApplication([])
    window = HardwareWindow()
    window.record_check.setChecked(False)
    window.conn_usb.setChecked(True)
    window.usb_selector.setCurrentText(sys.argv[1] if len(sys.argv) > 1 else "f4ec:ee38")
    for index, checkbox in enumerate(window.channel_checks):
        checkbox.setChecked(index < 2)
    for index in range(window.param_list.count()):
        item = window.param_list.item(index)
        item.setCheckState(scope.QtCore.Qt.Checked if item.data(scope.QtCore.Qt.UserRole) in
                           ("RMS", "MEAN", "PKPK") else scope.QtCore.Qt.Unchecked)
    window.resize(1440, 1000)
    window.show()
    scope.open_instrument = lambda config: AuditedUsb(config["usb_selector"])

    def wait_for(predicate, timeout=15):
        deadline = time.monotonic() + timeout
        while not predicate() and time.monotonic() < deadline:
            app.processEvents()
            time.sleep(0.01)
        if not predicate():
            raise AssertionError("Hardware test timed out")

    try:
        window.start_measure()
        wait_for(lambda: len(frames) >= 6)
        window.pause_measure()
        wait_for(lambda: window.worker is None)
        window._refresh_stats()
        for index, frame in enumerate(frames):
            raw_by_key = {}
            for command, response in queries[index * 2:index * 2 + 2]:
                channel = command.split(":", 1)[0]
                for code, value in re.findall(r"(?:^|[ ,])(RMS|MEAN|PKPK),([^,\r\n]+)", response):
                    raw_by_key[f"{channel}_{code}"] = scope.parse_number(value) if "*" not in value else math.nan
            for key, value in frame.items():
                expected = raw_by_key[key]
                assert (math.isnan(value) and math.isnan(expected)) or value == expected, (key, value, expected)
        report = []
        for row, (key, series) in enumerate(window.series.items()):
            curve = window.curves[key]
            item = window.plot_legend.item(row)
            swatch = item.icon().pixmap(24, 8).toImage().pixelColor(12, 4).name()
            table_swatch = window.stats_table.item(row, 0).icon().pixmap(24, 8).toImage().pixelColor(12, 4).name()
            pen = curve.opts["pen"].color().name()
            assert swatch == table_swatch == pen == series.color, key
            assert item.data(scope.QtCore.Qt.UserRole) == key, key
            assert key.split("_")[0] in item.text() and key.split("_")[1] in item.text(), key
            assert curve.yData[-1] == series.last or (math.isnan(curve.yData[-1]) and math.isnan(series.last)), key
            assert window.stats_table.item(row, 1).text() == scope.fmt_value(series.last, series.unit), key
            report.append({"key": key, "legend": item.text(), "color": pen,
                           "last": series.last if math.isfinite(series.last) else None})
        assert any(math.isfinite(frame["C1_RMS"]) for frame in frames)
        window.grab().save("/tmp/scoperec-real-colors.png")
        print(json.dumps({"status": "PASS", "frames": len(frames), "raw_measurement_queries": len(queries),
                          "cleared_while_measuring": True, "series": report}, ensure_ascii=False, indent=2))
    finally:
        window.end_test()
        if window.worker:
            wait_for(lambda: window.worker is None)
        window.close()
        app.processEvents()


if __name__ == "__main__":
    main()
