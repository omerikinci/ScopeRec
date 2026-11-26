"""Opt-in real-device test inside the exact packaged QApplication event loop."""
import csv
import gc
import json
import math
import os
import sys
import time
import traceback
import threading
import resource
from pathlib import Path


def install_endurance_test(scope, app, window, duration, output):
    class Endurance:
        def __init__(self):
            self.start = None
            self.stage = "connect"
            self.deadline = time.monotonic() + 20
            self.moves = 0
            self.actions = []
            self.period = -1
            self.stem = None
            self.error = None
            self.gui_thread = threading.get_ident()
            self.gc_threads = set()
            self.disposed_threads = []
            self.last_probe = -1
            self.stop_pressure = threading.Event()
            self.pressure_thread = threading.Thread(target=self.allocate_cycles, daemon=True)
            self.pressure_thread.start()
            self.gc_callback = lambda phase, info: self.gc_threads.add(threading.get_ident()) if phase == "start" else None
            gc.callbacks.append(self.gc_callback)
            self.output = Path(output)
            self.output.mkdir(parents=True, exist_ok=True)
            self.stats = open(self.output / "progress.log", "w", buffering=1)
            self.timer = scope.QtCore.QTimer(window)
            self.timer.setInterval(20)
            self.timer.timeout.connect(self.tick)
            window._save_settings = lambda: None
            window.conn_usb.setChecked(True)
            window.usb_selector.setCurrentText("f4ec:ee38")
            window.record_check.setChecked(True)
            window.dir_label.setText(str(self.output))
            window.test_name.setText("endurance")
            window.interval.setValue(100)
            for i, channel in enumerate(window.channel_checks):
                channel.setChecked(i < 2)
            for i in range(window.param_list.count()):
                item = window.param_list.item(i)
                item.setCheckState(scope.QtCore.Qt.Checked if item.data(scope.QtCore.Qt.UserRole) in
                                   ("RMS", "MEAN", "PKPK") else scope.QtCore.Qt.Unchecked)
            window._set_serial_side_panel(True)
            window.connect_btn.click()
            self.timer.start()

        def allocate_cycles(self):
            while not self.stop_pressure.is_set():
                for _ in range(128):
                    item = []
                    item.append(item)
                self.stop_pressure.wait(0.02)

        def create_disposal_probe(self):
            disposed = self.disposed_threads

            class Probe(scope.QtCore.QObject):
                def __init__(self):
                    super().__init__()
                    self.timer = scope.QtCore.QTimer(self)
                    self.timer.start(50)
                    self.cycle = self

                def __del__(self):
                    disposed.append(threading.get_ident())

            Probe()

        def say(self, message):
            self.stats.write(message + "\n")
            print(message, flush=True)

        def tick(self):
            try:
                self.step()
            except Exception:
                self.error = traceback.format_exc()
                self.say(self.error)
                self.stage = "finish"
                self.deadline = time.monotonic() + 15
                window.end_test()

        def step(self):
            now = time.monotonic()
            if self.stage == "connect":
                if window.connection_worker:
                    assert now < self.deadline, "Connection timed out"
                    return
                assert window.idle_client, window.connection_state.text()
                self.idn = window.instrument_id
                window.start_btn.click()
                assert window.running and window.recorder
                self.stem = window.recorder.stem
                self.start = now
                self.stage = "measure"
                self.say("CONNECTED " + self.idn)
            if self.stage == "resume":
                if window.worker:
                    assert now < self.deadline, "Pause timed out"
                    return
                assert window.recorder.stem == self.stem
                window.start_btn.click()
                self.actions.append("resume")
                self.stage = "measure"
            if self.stage == "measure":
                elapsed = now - self.start
                assert window.root_split.isVisible() and window.plot.isVisible(), "Central panel disappeared"
                assert window.centralWidget().layout().count() == 3
                if elapsed >= duration:
                    self.stage = "finish"
                    self.deadline = now + 15
                    window.end_btn.click()
                    return
                view = window.plot.getViewBox()
                ranges = view.viewRange()
                x = max(0, window.current_t - 1 + math.sin(self.moves / 15))
                y = ranges[1][0] + (ranges[1][1] - ranges[1][0]) * (0.5 + 0.4 * math.sin(self.moves / 23))
                point = window.plot.mapFromScene(view.mapViewToScene(scope.QtCore.QPointF(x, y)))
                event = scope.QtGui.QMouseEvent(scope.QtCore.QEvent.MouseMove, scope.QtCore.QPointF(point),
                    scope.QtCore.QPointF(window.plot.viewport().mapToGlobal(point)), scope.QtCore.Qt.NoButton,
                    scope.QtCore.Qt.NoButton, scope.QtCore.Qt.NoModifier)
                app.sendEvent(window.plot.viewport(), event)
                self.moves += 1
                if int(elapsed) != self.last_probe:
                    self.last_probe = int(elapsed)
                    self.create_disposal_probe()
                assert not gc.isenabled(), "Automatic GC was re-enabled on worker threads"
                period = int(elapsed // 15)
                if period != self.period:
                    self.period = period
                    window.fit_all_time()
                    window.shift_plot(-0.25)
                    window.plot.enableAutoRange(axis="y", enable=True)
                    window.follow_check.setChecked(True)
                    window._set_serial_side_panel(period % 2 == 0)
                    window.work_split.setSizes([750 if period % 2 else 900, 420])
                    self.actions.append("navigate_resize_gc")
                    self.say(f"elapsed={elapsed:.1f}s samples={window.sample_count} mouse_moves={self.moves} layout={window.centralWidget().layout().count()}")
                    if period > 0 and period % 4 == 0:
                        window.pause_btn.click()
                        self.actions.append("pause")
                        self.stage = "resume"
                        self.deadline = now + 15
            if self.stage == "finish":
                assert now < self.deadline, "Shutdown timed out"
                if window.worker or window.connection_worker:
                    return
                window.end_test()
                self.stop_pressure.set()
                self.pressure_thread.join(2)
                gc.collect()
                if not self.error:
                    with open(self.stem + ".csv", encoding="utf-8-sig", newline="") as file:
                        rows = list(csv.reader(file, delimiter=";"))
                    assert len(rows) - 1 == window.sample_count, (len(rows), window.sample_count)
                    assert window.sample_count >= duration, window.log.toPlainText()
                    assert any(math.isfinite(s.last) for s in window.series.values())
                    assert "İletişim hatası" not in window.log.toPlainText(), window.log.toPlainText()
                    assert self.gc_threads == {self.gui_thread}, self.gc_threads
                    assert self.disposed_threads and set(self.disposed_threads) == {self.gui_thread}, self.disposed_threads
                    assert not scope._runtime_errors, scope._runtime_errors
                    # Reopen the real instrument after the app released USB.
                    client = scope.open_instrument(window.connection_config())
                    try:
                        client.clear()
                        assert client.query_text("*IDN?")
                    finally:
                        client.close()
                report = {"status": "FAIL" if self.error else "PASS", "frozen": bool(getattr(sys, "frozen", False)),
                          "pid": os.getpid(), "python": sys.version, "qt": scope.QtCore.qVersion(),
                          "seconds": round(now - self.start, 2) if self.start else 0,
                          "samples": window.sample_count, "mouse_moves": self.moves,
                          "gui_thread": self.gui_thread, "gc_threads": sorted(self.gc_threads),
                          "disposal_probes": len(self.disposed_threads),
                          "peak_rss_bytes": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss,
                          "unhandled_exceptions": scope._runtime_errors,
                          "device": getattr(self, "idn", ""), "actions": self.actions, "error": self.error}
                (self.output / "result.json").write_text(json.dumps(report, indent=2))
                window.grab().save(str(self.output / "final.png"))
                self.say(json.dumps(report))
                gc.callbacks.remove(self.gc_callback)
                self.timer.stop()
                window.close()
                app.exit(1 if self.error else 0)

    window._endurance_test = Endurance()
