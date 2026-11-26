"""Manual endurance check against an attached USB scope, including real Qt mouse events."""
import faulthandler
import gc
import math
import sys
import time
import scoperec_mac as scope

faulthandler.enable()

class HardwareWindow(scope.MainWindow):
    def _load_settings(self):
        pass

    def _save_settings(self):
        pass


def main():
    app = scope.QtWidgets.QApplication([])
    window = HardwareWindow()
    window.record_check.setChecked(False)
    window.conn_usb.setChecked(True)
    window.usb_selector.setCurrentText("f4ec:ee38")
    window._set_serial_side_panel(True)
    window.show()
    window.toggle_instrument()
    deadline = time.monotonic() + 15
    while window.connection_worker and time.monotonic() < deadline:
        app.processEvents()
        time.sleep(0.01)
    assert window.idle_client is not None, window.connection_state.text()
    window.start_measure()
    duration = float(sys.argv[1]) if len(sys.argv) > 1 else 90
    start = time.monotonic()
    moves = 0
    try:
        while time.monotonic() - start < duration:
            app.processEvents()
            view = window.plot.getViewBox()
            ranges = view.viewRange()
            # Exercise samples and empty plot regions, not just the tooltip helper.
            x = max(0, window.current_t - 1 + 0.9 * math.sin(moves / 15))
            y = ranges[1][0] + (ranges[1][1] - ranges[1][0]) * (0.5 + 0.4 * math.sin(moves / 23))
            point = window.plot.mapFromScene(view.mapViewToScene(scope.QtCore.QPointF(x, y)))
            event = scope.QtGui.QMouseEvent(scope.QtCore.QEvent.MouseMove, scope.QtCore.QPointF(point),
                scope.QtCore.QPointF(window.plot.viewport().mapToGlobal(point)), scope.QtCore.Qt.NoButton,
                scope.QtCore.Qt.NoButton, scope.QtCore.Qt.NoModifier)
            app.sendEvent(window.plot.viewport(), event)
            moves += 1
            if moves % 250 == 0:
                gc.collect()
                app.processEvents()
                assert window.root_split.isVisible() and window.plot.isVisible()
                assert window.centralWidget().layout().count() == 3
                print(f"elapsed={time.monotonic()-start:.1f}s samples={window.sample_count} moves={moves}", flush=True)
            time.sleep(0.01)
        assert window.sample_count >= duration, (window.sample_count, window.log.toPlainText())
        window.grab().save("/tmp/scoperec-hardware-stability.png")
        print(f"PASS {duration}s samples={window.sample_count} mouse_moves={moves}", flush=True)
    finally:
        window.end_test()
        deadline = time.monotonic() + 10
        while window.worker and time.monotonic() < deadline:
            app.processEvents()
            time.sleep(0.01)
        window.close()
        app.processEvents()


if __name__ == "__main__":
    main()
