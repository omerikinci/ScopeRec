#!/usr/bin/env python3
import csv
import bisect
import faulthandler
import json
import math
import os
import queue
import re
import socket
import sys
import threading
import time
import traceback
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

from PySide6 import QtCore, QtGui, QtWidgets
import pyqtgraph as pg
import pyqtgraph.exporters
from pyqtgraph.util.garbage_collector import GarbageCollector

try:
    import serial
    from serial.tools import list_ports
except Exception:
    serial = None
    list_ports = None

try:
    import usb.core
    import usb.util
    import usb.backend.libusb1
except Exception:
    usb = None

USB_BACKEND = None


def usb_backend():
    global USB_BACKEND
    if usb is None:
        return None
    if USB_BACKEND is not None:
        return USB_BACKEND
    paths = [
        str(Path(getattr(sys, "_MEIPASS", "")) / "libusb-1.0.dylib") if getattr(sys, "_MEIPASS", "") else "",
        "/opt/homebrew/lib/libusb-1.0.dylib",
        "/opt/homebrew/opt/libusb/lib/libusb-1.0.dylib",
        "/usr/local/lib/libusb-1.0.dylib",
        "libusb-1.0.dylib",
    ]
    for path in paths:
        try:
            backend = usb.backend.libusb1.get_backend(find_library=lambda _name, p=path: p)
            if backend is not None:
                USB_BACKEND = backend
                return USB_BACKEND
        except Exception:
            pass
    try:
        USB_BACKEND = usb.backend.libusb1.get_backend()
    except Exception:
        USB_BACKEND = None
    return USB_BACKEND


APP_NAME = "ScopeRec"
PARAMS = [
    ("RMS", "RMS", "V"),
    ("MEAN", "Ortalama", "V"),
    ("PKPK", "Tepe-tepe", "V"),
    ("MAX", "Maksimum", "V"),
    ("MIN", "Minimum", "V"),
    ("AMPL", "Genlik", "V"),
    ("TOP", "Üst seviye", "V"),
    ("BASE", "Alt seviye", "V"),
    ("CRMS", "Çevrim RMS", "V"),
    ("CMEAN", "Çevrim ort.", "V"),
    ("FREQ", "Frekans", "Hz"),
    ("PER", "Periyot", "s"),
    ("DUTY", "Doluluk", "%"),
    ("PWID", "Pozitif darbe", "s"),
    ("NWID", "Negatif darbe", "s"),
    ("RISE", "Yükselme", "s"),
    ("FALL", "Düşme", "s"),
]
PARAM_UNITS = {p[0]: p[2] for p in PARAMS}
CHANNELS = ["C1", "C2", "C3", "C4"]
COLORS = ["#2d7ff9", "#f59e0b", "#16a34a", "#dc2626", "#7c3aed", "#0891b2", "#be123c", "#4b5563"]

_runtime_log = None
_runtime_errors = []


def enable_crash_logging():
    """Windowed macOS bundles have no stderr; retain native and Python error traces."""
    global _runtime_log
    directory = Path.home() / "Library" / "Logs" / "ScopeRec" if sys.platform == "darwin" else Path.home() / ".scoperec_logs"
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / "runtime.log"
    if path.exists() and path.stat().st_size > 8 * 1024 * 1024:
        path.replace(directory / "runtime.previous.log")
    _runtime_log = open(path, "a", buffering=1, encoding="utf-8")
    _runtime_log.write(f"\n{datetime.now().isoformat()} START pid={os.getpid()} Python={sys.version.split()[0]} Qt={QtCore.qVersion()} frozen={bool(getattr(sys, 'frozen', False))}\n")
    if sys.stderr is None:
        os.dup2(_runtime_log.fileno(), 2)
        sys.stderr = _runtime_log
    if sys.stdout is None:
        sys.stdout = _runtime_log
    faulthandler.enable(file=_runtime_log, all_threads=True)

    def exception_hook(kind, value, trace):
        _runtime_errors.append(f"{kind.__name__}: {value}")
        traceback.print_exception(kind, value, trace, file=_runtime_log)
        _runtime_log.flush()
        if sys.stderr is not _runtime_log:
            traceback.print_exception(kind, value, trace)

    sys.excepthook = exception_hook


def ensure_gui_garbage_collection():
    """Qt wrappers must never be destroyed by Python GC on a measurement thread.

    Use pyqtgraph's supported GUI-event-loop collector instead of CPython's
    allocation-triggered collector, which can run inside any Python thread.
    """
    app = QtWidgets.QApplication.instance()
    if not hasattr(app, "_scoperec_garbage_collector"):
        collector = GarbageCollector(interval=1.0)
        collector.timer.setParent(app)
        app._scoperec_garbage_collector = collector
    return app._scoperec_garbage_collector


def now_stamp():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def safe_float(text):
    try:
        if text is None or str(text).strip() == "":
            return math.nan
        return float(str(text).replace(",", "."))
    except Exception:
        return math.nan


def fmt_value(v, unit=""):
    if v is None or math.isnan(v) or math.isinf(v):
        return "—"
    av = abs(v)
    if unit == "Hz" and av >= 1000:
        return f"{v / 1000:.5g} kHz"
    if unit == "V" and 0 < av < 1:
        return f"{v * 1000:.5g} mV"
    if unit == "s" and 0 < av < 1:
        return f"{v * 1000:.5g} ms"
    suffix = f" {unit}" if unit else ""
    return f"{v:.6g}{suffix}"


def parse_number(text):
    matches = re.findall(r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", text or "")
    if not matches:
        return math.nan
    try:
        value = float(matches[-1])
        return math.nan if abs(value) > 9e36 else value
    except Exception:
        return math.nan


class ScpiError(Exception):
    pass


def check_cancelled(client):
    event = getattr(client, "cancel_event", None)
    if event is not None and event.is_set():
        raise InterruptedError("Ölçüm durduruldu")


class ScpiClient:
    def __init__(self, address, timeout=3.0):
        host = address.strip()
        port = 5025
        if ":" in host:
            left, right = host.rsplit(":", 1)
            if right.isdigit():
                host, port = left, int(right)
        if not host:
            raise ScpiError("IP adresi boş")
        self.sock = socket.create_connection((host, port), timeout=timeout)
        self.sock.settimeout(timeout)
        self.lock = threading.Lock()

    def close(self):
        try:
            self.sock.close()
        except Exception:
            pass

    def write(self, cmd):
        with self.lock:
            self.sock.sendall((cmd.rstrip() + "\n").encode("ascii", "replace"))

    def read(self):
        with self.lock:
            first = self.sock.recv(1)
            if not first:
                raise ScpiError("bağlantı kapandı")
            data = bytearray(first)
            if first == b"#":
                nd = self.sock.recv(1)
                data.extend(nd)
                n = int(nd.decode("ascii", "ignore") or "0")
                if 0 < n <= 9:
                    digits = self._recv_exact(n)
                    data.extend(digits)
                    size = int(digits.decode("ascii", "ignore") or "0")
                    data.extend(self._recv_exact(size))
                    return bytes(data)
            if first == b"B":
                second = self.sock.recv(1)
                data.extend(second)
                if second == b"M":
                    header = self._recv_exact(4)
                    data.extend(header)
                    size = int.from_bytes(data[2:6], "little", signed=False)
                    if 1000 < size < 64000000:
                        data.extend(self._recv_exact(size - 6))
                        return bytes(data)
            while not data.endswith(b"\n"):
                chunk = self.sock.recv(4096)
                if not chunk:
                    break
                data.extend(chunk)
            return bytes(data)

    def _recv_exact(self, count):
        buf = bytearray()
        while len(buf) < count:
            chunk = self.sock.recv(count - len(buf))
            if not chunk:
                raise ScpiError("eksik veri")
            buf.extend(chunk)
        return bytes(buf)

    def query(self, cmd):
        with self.lock:
            check_cancelled(self)
            self.sock.sendall((cmd.rstrip() + "\n").encode("ascii", "replace"))
            return self._read_unlocked()

    def _read_unlocked(self):
        first = self.sock.recv(1)
        if not first:
            raise ScpiError("bağlantı kapandı")
        data = bytearray(first)
        if first == b"#":
            nd = self.sock.recv(1)
            data.extend(nd)
            n = int(nd.decode("ascii", "ignore") or "0")
            if 0 < n <= 9:
                digits = self._recv_exact(n)
                data.extend(digits)
                size = int(digits.decode("ascii", "ignore") or "0")
                data.extend(self._recv_exact(size))
                return bytes(data)
        if first == b"B":
            second = self.sock.recv(1)
            data.extend(second)
            if second == b"M":
                header = self._recv_exact(4)
                data.extend(header)
                size = int.from_bytes(data[2:6], "little", signed=False)
                if 1000 < size < 64000000:
                    data.extend(self._recv_exact(size - 6))
                    return bytes(data)
        while not data.endswith(b"\n"):
            chunk = self.sock.recv(4096)
            if not chunk:
                break
            data.extend(chunk)
        return bytes(data)

    def query_text(self, cmd):
        return self.query(cmd).decode("ascii", "replace").strip("\r\n\0 ")

    def clear(self):
        self.sock.setblocking(False)
        try:
            while True:
                try:
                    if not self.sock.recv(65536):
                        break
                except BlockingIOError:
                    break
        finally:
            self.sock.setblocking(True)
            self.sock.settimeout(3.0)


class UsbTmcClient:
    READ_PACKET = 65536
    READ_PAYLOAD = READ_PACKET - 12

    def __init__(self, selector="", timeout=3.0):
        if usb is None:
            raise ScpiError("PyUSB/libusb yüklü değil")
        self.timeout_ms = int(timeout * 1000)
        self.tag = int(time.time()) & 0x7f or 1
        self._closed = False
        self._pending_read = False
        self._supports_local = False
        self._claimed = False
        self.intf = None
        self.dev = self._find_device(selector)
        if self.dev is None:
            raise ScpiError("USBTMC cihazı bulunamadı")
        try:
            self._configure_interface()
        except Exception:
            self.close()
            raise

    def _configure_interface(self):
        try:
            self.cfg = self.dev.get_active_configuration()
        except usb.core.USBError:
            self.dev.set_configuration()
            self.cfg = self.dev.get_active_configuration()
        self.intf = None
        self.ep_in = None
        self.ep_out = None
        for intf in self.cfg:
            if intf.bInterfaceClass == 0xFE and intf.bInterfaceSubClass == 3:
                self.intf = intf
                break
        if self.intf is None:
            raise ScpiError("USB Test & Measurement arayüzü bulunamadı")
        try:
            if self.dev.is_kernel_driver_active(self.intf.bInterfaceNumber):
                self.dev.detach_kernel_driver(self.intf.bInterfaceNumber)
        except Exception:
            pass
        usb.util.claim_interface(self.dev, self.intf.bInterfaceNumber)
        self._claimed = True
        for ep in self.intf:
            if usb.util.endpoint_type(ep.bmAttributes) != usb.util.ENDPOINT_TYPE_BULK:
                continue
            if usb.util.endpoint_direction(ep.bEndpointAddress) == usb.util.ENDPOINT_IN:
                self.ep_in = ep
            else:
                self.ep_out = ep
        if self.ep_in is None or self.ep_out is None:
            self.close()
            raise ScpiError("USBTMC bulk uçları bulunamadı")
        try:
            caps = self.dev.ctrl_transfer(0xA1, 7, 0, self.intf.bInterfaceNumber, 24, timeout=250)
            self._supports_local = len(caps) >= 15 and caps[0] == 1 and bool(caps[14] & 2)
        except usb.core.USBError:
            pass

    @staticmethod
    def devices():
        if usb is None:
            return []
        found = []
        backend = usb_backend()
        if backend is None:
            return []
        for dev in usb.core.find(find_all=True, backend=backend):
            try:
                cfg = dev.get_active_configuration()
            except Exception:
                try:
                    cfg = dev[0]
                except Exception:
                    continue
            for intf in cfg:
                if intf.bInterfaceClass == 0xFE and intf.bInterfaceSubClass == 3:
                    found.append(dev)
                    break
        return found

    @staticmethod
    def device_label(dev):
        def get(attr):
            try:
                idx = getattr(dev, attr)
                return usb.util.get_string(dev, idx) if idx else ""
            except Exception:
                return ""
        vendor = get("iManufacturer")
        product = get("iProduct")
        serial_no = get("iSerialNumber")
        base = f"{dev.idVendor:04x}:{dev.idProduct:04x}"
        text = " ".join(p for p in [base, vendor, product, serial_no] if p)
        return text or base

    def _find_device(self, selector):
        selector = (selector or "").strip().lower()
        devices = self.devices()
        if not selector:
            return devices[0] if devices else None
        vid_pid = re.match(r"^(?:0x)?([0-9a-f]{4})\s*[:/]\s*(?:0x)?([0-9a-f]{4})$", selector)
        for dev in devices:
            label = self.device_label(dev).lower()
            if vid_pid and dev.idVendor == int(vid_pid.group(1), 16) and dev.idProduct == int(vid_pid.group(2), 16):
                return dev
            if selector in label:
                return dev
        return None

    def close(self):
        if self._closed:
            return
        self._closed = True
        if self._pending_read:
            self.clear()
        if self._supports_local:
            for request in (161, 160):  # USB488 GO_TO_LOCAL, then deassert REN.
                try:
                    self.dev.ctrl_transfer(0xA1, request, 0, self.intf.bInterfaceNumber, 1, timeout=250)
                except usb.core.USBError:
                    pass
        try:
            if self._claimed:
                usb.util.release_interface(self.dev, self.intf.bInterfaceNumber)
        except Exception:
            pass
        try:
            usb.util.dispose_resources(self.dev)
        except Exception:
            pass

    def _next_tag(self):
        self.tag = 1 if self.tag >= 255 else self.tag + 1
        return self.tag

    def write(self, cmd):
        payload = (cmd.rstrip() + "\n").encode("ascii", "replace")
        tag = self._next_tag()
        packet_len = (12 + len(payload) + 3) & ~3
        packet = bytearray(packet_len)
        packet[0] = 1
        packet[1] = tag
        packet[2] = (~tag) & 0xFF
        packet[4:8] = len(payload).to_bytes(4, "little")
        packet[8] = 1
        packet[12:12 + len(payload)] = payload
        self.ep_out.write(packet, self.timeout_ms)

    def read(self):
        self._pending_read = True
        out = bytearray()
        while True:
            check_cancelled(self)
            tag = self._next_tag()
            req = bytearray(12)
            req[0] = 2
            req[1] = tag
            req[2] = (~tag) & 0xFF
            req[4:8] = self.READ_PAYLOAD.to_bytes(4, "little")
            self.ep_out.write(req, self.timeout_ms)
            stale = 0
            while True:
                raw = bytes(self.ep_in.read(self.READ_PACKET, self.timeout_ms))
                if len(raw) < 12 or raw[0] != 2:
                    raise ScpiError("USBTMC senkron hatası")
                size = int.from_bytes(raw[4:8], "little")
                if size > self.READ_PAYLOAD:
                    raise ScpiError("USBTMC cevap boyutu istenen sınırı aşıyor")
                eom = bool(raw[8] & 1)
                got = min(len(raw) - 12, size)
                if raw[1] == tag:
                    out.extend(raw[12:12 + got])
                    while got < size:
                        check_cancelled(self)
                        read_len = self.READ_PACKET
                        chunk = bytes(self.ep_in.read(read_len, self.timeout_ms))
                        if not chunk:
                            raise ScpiError("USBTMC eksik veri")
                        take = min(len(chunk), size - got)
                        out.extend(chunk[:take])
                        got += take
                    if eom:
                        self._pending_read = False
                        return bytes(out)
                    break
                stale += 1
                if stale > 8:
                    raise ScpiError("USBTMC eski cevaplar temizlenemedi")

    def query(self, cmd):
        check_cancelled(self)
        self.write(cmd)
        return self.read()

    def query_text(self, cmd):
        return self.query(cmd).decode("ascii", "replace").strip("\r\n\0 ")

    def clear(self):
        deadline = time.monotonic() + 1.5
        for _ in range(64):
            if time.monotonic() >= deadline:
                break
            try:
                self.ep_in.read(self.READ_PACKET, 20)
            except Exception:
                break
        interface = self.intf.bInterfaceNumber
        try:
            result = self.dev.ctrl_transfer(0xA1, 5, 0, interface, 1, timeout=200)
            if result and result[0] == 1:
                for _ in range(50):
                    if time.monotonic() >= deadline:
                        break
                    state = self.dev.ctrl_transfer(0xA1, 6, 0, interface, 2, timeout=200)
                    if state[0] != 2:
                        break
                    if len(state) > 1 and state[1] & 1:
                        try:
                            self.ep_in.read(self.READ_PACKET, 50)
                        except usb.core.USBTimeoutError:
                            pass
                    else:
                        time.sleep(0.01)
            self.dev.clear_halt(self.ep_in.bEndpointAddress)
            self.dev.clear_halt(self.ep_out.bEndpointAddress)
        except usb.core.USBError:
            pass
        self._pending_read = False


def open_instrument(config):
    if config.get("connection") == "USB":
        return UsbTmcClient(config.get("usb_selector", ""), timeout=3)
    return ScpiClient(config["address"], timeout=3)


class Dialect:
    names = ["Otomatik", "Siglent", "Rigol", "Keysight / Agilent", "Tektronix"]

    def __init__(self, name, screenshot_cmd=None, screenshot_ext=".bmp"):
        self.name = name
        self.screenshot_cmd = screenshot_cmd
        self.screenshot_ext = screenshot_ext

    @staticmethod
    def pick(index, idn):
        text = (idn or "").upper()
        if index == 0:
            if "SIGLENT" in text:
                index = 1
            elif "RIGOL" in text:
                index = 2
            elif "KEYSIGHT" in text or "AGILENT" in text:
                index = 3
            elif "TEKTRONIX" in text:
                index = 4
            else:
                return TemplateDialect("Genel SCPI", ":MEASure:{0}? CHANnel{1}", KEYSIGHT_MAP)
        if index == 2:
            return TemplateDialect("Rigol", ":MEASure:ITEM? {0},CHANnel{1}", RIGOL_MAP, {"DUTY": 100.0})
        if index == 3:
            return TemplateDialect("Keysight / Agilent", ":MEASure:{0}? CHANnel{1}", KEYSIGHT_MAP)
        if index == 4:
            return TemplateDialect("Tektronix", "MEASUrement:IMMed:SOUrce1 CH{1};:MEASUrement:IMMed:TYPe {0};:MEASUrement:IMMed:VALue?", TEK_MAP)
        return SiglentDialect()

    def measure(self, client, channel, codes):
        raise NotImplementedError


RIGOL_MAP = {
    "RMS": "VRMS", "MEAN": "VAVG", "PKPK": "VPP", "MAX": "VMAX", "MIN": "VMIN", "AMPL": "VAMP",
    "TOP": "VTOP", "BASE": "VBASe", "CRMS": "PVRMS", "FREQ": "FREQuency", "PER": "PERiod",
    "DUTY": "PDUTy", "PWID": "PWIDth", "NWID": "NWIDth", "RISE": "RTIMe", "FALL": "FTIMe",
}
KEYSIGHT_MAP = {
    "RMS": "VRMS", "MEAN": "VAVerage", "PKPK": "VPP", "MAX": "VMAX", "MIN": "VMIN", "AMPL": "VAMPlitude",
    "TOP": "VTOP", "BASE": "VBASe", "FREQ": "FREQuency", "PER": "PERiod", "DUTY": "DUTYcycle",
    "PWID": "PWIDth", "NWID": "NWIDth", "RISE": "RISetime", "FALL": "FALLtime",
}
TEK_MAP = {
    "RMS": "RMS", "MEAN": "MEAN", "PKPK": "PK2pk", "MAX": "MAXImum", "MIN": "MINImum", "AMPL": "AMPlitude",
    "TOP": "HIGH", "BASE": "LOW", "CRMS": "CRMs", "CMEAN": "CMEan", "FREQ": "FREQuency", "PER": "PERIod",
    "DUTY": "PDUty", "PWID": "PWIdth", "NWID": "NWIdth", "RISE": "RISe", "FALL": "FALL",
}


class SiglentDialect(Dialect):
    def __init__(self):
        super().__init__("Siglent", "SCDP", ".bmp")

    def measure(self, client, channel, codes):
        ask = codes[0] if len(codes) == 1 else "ALL"
        resp = client.query_text(f"C{channel}:PAVA? {ask}")
        tail = resp.split(" ")[-1]
        parts = [p.strip() for p in tail.split(",")]
        data = {}
        for i in range(0, len(parts) - 1, 2):
            data[parts[i].upper()] = parts[i + 1]
        return [parse_number(data.get(code, "")) if "*" not in data.get(code, "") else math.nan for code in codes]


class TemplateDialect(Dialect):
    def __init__(self, name, template, mapping, scale=None):
        super().__init__(name)
        self.template = template
        self.mapping = mapping
        self.scale = scale or {}

    def measure(self, client, channel, codes):
        values = []
        for code in codes:
            item = self.mapping.get(code)
            if not item:
                values.append(math.nan)
                continue
            v = parse_number(client.query_text(self.template.format(item, channel)))
            values.append(v * self.scale.get(code, 1.0) if not math.isnan(v) else v)
        return values


@dataclass
class Series:
    key: str
    unit: str
    color: str
    times: list = field(default_factory=list)
    values: list = field(default_factory=list)
    last: float = math.nan
    minv: float = math.inf
    maxv: float = -math.inf
    total: float = 0.0
    count: int = 0
    revision: int = 0

    def add(self, t, value):
        self.revision += 1
        self.times.append(t)
        self.values.append(value)
        if len(self.times) > 200000:
            del self.times[:20000]
            del self.values[:20000]
        self.last = value
        if value is not None and not math.isnan(value):
            self.minv = min(self.minv, value)
            self.maxv = max(self.maxv, value)
            self.total += value
            self.count += 1

    @property
    def avg(self):
        return self.total / self.count if self.count else math.nan


class Recorder:
    def __init__(self, base_dir, prefix, series_keys):
        Path(base_dir).mkdir(parents=True, exist_ok=True)
        safe_prefix = re.sub(r"[^A-Za-z0-9_. -]+", "_", prefix).strip()
        if safe_prefix:
            safe_prefix += "_"
        self.stem = str(Path(base_dir) / f"{safe_prefix}{datetime.now().strftime('%Y%m%d_%H%M%S')}")
        self.csv_file = open(self.stem + ".csv", "w", newline="", encoding="utf-8-sig")
        self.csv = csv.writer(self.csv_file, delimiter=";")
        self.csv.writerow(["zaman", "t[s]"] + series_keys)
        self.log_file = open(self.stem + "_log.txt", "w", encoding="utf-8")
        self.events_file = open(self.stem + "_olaylar.csv", "w", newline="", encoding="utf-8-sig")
        self.events = csv.writer(self.events_file, delimiter=";")
        self.events.writerow(["zaman", "t[s]", "olay", "deger", "sure[s]"])
        self.last_flush = time.monotonic()

    def flush(self, force=False):
        now = time.monotonic()
        if not force and now - self.last_flush < 1.0:
            return
        self.csv_file.flush()
        self.log_file.flush()
        self.events_file.flush()
        self.last_flush = now

    def row(self, t, values):
        self.csv.writerow([now_stamp(), f"{t:.2f}"] + ["" if v is None or math.isnan(v) else f"{v:.12g}" for v in values])
        self.flush()

    def log(self, t, message):
        self.log_file.write(f"{now_stamp()}\t{t:.2f}\t{message}\n")
        self.flush()

    def event(self, t, name, value=math.nan, duration=""):
        val = "" if value is None or math.isnan(value) else f"{value:.12g}"
        self.events.writerow([now_stamp(), f"{t:.2f}", name, val, duration])
        self.flush()

    def close(self):
        self.flush(force=True)
        for f in [self.csv_file, self.log_file, self.events_file]:
            try:
                f.close()
            except Exception:
                pass


class ConnectionWorker(QtCore.QThread):
    def __init__(self, config):
        super().__init__()
        self.config = config
        self.client = None
        self.idn = ""
        self.error = ""

    def run(self):
        try:
            self.client = open_instrument(self.config)
            self.client.clear()
            self.idn = self.client.query_text("*IDN?")
            if not self.idn:
                raise ScpiError("Cihaz kimliği alınamadı")
        except Exception as exc:
            self.error = str(exc)
            if self.client:
                self.client.close()
                self.client = None


class MeasurementWorker(QtCore.QThread):
    sample = QtCore.Signal(float, dict)
    status = QtCore.Signal(str)
    log = QtCore.Signal(str)

    def __init__(self, config):
        super().__init__()
        self.config = config
        self.stop_event = threading.Event()
        self.shot_request = None
        self.client = None
        self.dialect = None

    def stop(self):
        self.stop_event.set()

    def request_screenshot(self, path):
        self.shot_request = path

    def run(self):
        errors = 0
        start = time.monotonic() - self.config.get("time_offset", 0.0)
        codes = self.config["params"]
        try:
            while not self.stop_event.is_set():
                try:
                    if self.client is None:
                        self.status.emit("Bağlanıyor")
                        self.client = open_instrument(self.config)
                        self.client.cancel_event = self.stop_event
                        try:
                            self.client.clear()
                        except Exception:
                            pass
                        idn = self.client.query_text("*IDN?")
                        self.dialect = Dialect.pick(self.config["dialect"], idn)
                        self.status.emit(f"Bağlandı: {idn} / {self.dialect.name}")
                        self.log.emit(f"Bağlandı: {idn} / {self.dialect.name}")

                    if self.shot_request:
                        target = self.shot_request
                        self.shot_request = None
                        if self.dialect and self.dialect.screenshot_cmd:
                            data = self.client.query(self.dialect.screenshot_cmd)
                            with open(target + self.dialect.screenshot_ext, "wb") as f:
                                f.write(data)
                            self.log.emit(f"Osiloskop ekranı kaydedildi: {target + self.dialect.screenshot_ext}")
                        else:
                            self.log.emit("Bu komut setinde osiloskop ekran kaydı desteklenmiyor")

                    values = {}
                    for ch in self.config["channels"]:
                        if self.stop_event.is_set():
                            break
                        measured = self.dialect.measure(self.client, int(ch[1]), codes)
                        for code, value in zip(codes, measured):
                            values[f"{ch}_{code}"] = value
                    self.sample.emit(time.monotonic() - start, values)
                    errors = 0
                    wait = max(0.04, self.config["interval_ms"] / 1000.0)
                    self.stop_event.wait(wait)
                except Exception as exc:
                    if self.stop_event.is_set():
                        break
                    errors += 1
                    self.log.emit(f"İletişim hatası: {exc}")
                    if self.client:
                        self.client.close()
                        self.client = None
                    self.status.emit("Koptu, tekrar deneniyor")
                    self.stop_event.wait(min(5, 0.8 * errors))
        finally:
            if self.client:
                self.client.close()
            self.status.emit("Durdu")


class SerialWorker(QtCore.QThread):
    line = QtCore.Signal(str, str)
    values = QtCore.Signal(str, dict)
    status = QtCore.Signal(str)

    def __init__(self, name, port, baud):
        super().__init__()
        self.name = name
        self.port = port
        self.baud = baud
        self.stop_event = threading.Event()
        self.outbox = queue.Queue()
        self.handle = None

    def stop(self):
        self.stop_event.set()

    def send_text(self, text):
        self.outbox.put(text)

    def run(self):
        try:
            if self.port.lower().startswith("tcp://"):
                hp = self.port[6:]
                host, raw_port = hp.rsplit(":", 1)
                self.handle = socket.create_connection((host, int(raw_port)), timeout=3)
                self.handle.settimeout(0.1)
                reader = self._read_socket
                writer = lambda b: self.handle.sendall(b)
            else:
                if serial is None:
                    raise RuntimeError("pyserial yüklü değil")
                self.handle = serial.Serial(self.port, self.baud, timeout=0.1, write_timeout=2, dsrdtr=False, rtscts=False)
                reader = lambda: self.handle.read(4096)
                writer = self.handle.write
            self.status.emit(f"{self.name}: bağlı")
            buf = bytearray()
            while not self.stop_event.is_set():
                while not self.outbox.empty():
                    writer(self.outbox.get().encode("latin1", "replace"))
                data = reader()
                if data:
                    for b in data:
                        if b in (10, 13):
                            if buf:
                                text = buf.decode("latin1", "replace")
                                buf.clear()
                                self.line.emit(self.name, text)
                                parsed = parse_serial_values(text)
                                if parsed:
                                    self.values.emit(self.name, parsed)
                        elif b:
                            buf.append(b)
                else:
                    self.msleep(15)
        except Exception as exc:
            self.status.emit(f"{self.name}: {exc}")
        finally:
            try:
                if self.handle:
                    self.handle.close()
            except Exception:
                pass

    def _read_socket(self):
        try:
            return self.handle.recv(4096)
        except socket.timeout:
            return b""


PAIR_RE = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)\s*[:=]\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)")
PLAIN_RE = re.compile(r"^\s*[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?(?:\s*[,;\t ]\s*[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)*\s*$")


def parse_serial_values(text):
    result = {}
    for name, raw in PAIR_RE.findall(text):
        result[name] = safe_float(raw)
    if not result and PLAIN_RE.match(text or ""):
        parts = re.split(r"[,;\t ]+", text.strip())
        for i, raw in enumerate([p for p in parts if p], 1):
            result[f"S{i}"] = safe_float(raw)
    return result


class GripHandle(QtWidgets.QSplitterHandle):
    def __init__(self, orientation, parent):
        super().__init__(orientation, parent)
        self.setCursor(QtCore.Qt.SplitHCursor if orientation == QtCore.Qt.Horizontal else QtCore.Qt.SplitVCursor)

    def paintEvent(self, event):
        painter = QtGui.QPainter(self)
        dark = self.splitter().property("darkTheme")
        painter.fillRect(self.rect(), QtGui.QColor("#293540" if dark else "#cbd2d6"))
        painter.setPen(QtCore.Qt.NoPen)
        painter.setBrush(QtGui.QColor("#a1adb8" if dark else "#5d6973"))
        for offset in (-8, 0, 8):
            x = self.width() // 2 + (0 if self.orientation() == QtCore.Qt.Horizontal else offset)
            y = self.height() // 2 + (offset if self.orientation() == QtCore.Qt.Horizontal else 0)
            painter.drawEllipse(QtCore.QPoint(x, y), 2, 2)


class GripSplitter(QtWidgets.QSplitter):
    def __init__(self, orientation=QtCore.Qt.Horizontal, parent=None):
        super().__init__(orientation, parent)
        self.setHandleWidth(14)
        self.setChildrenCollapsible(False)

    def createHandle(self):
        return GripHandle(self.orientation(), self)


class PlotHover(QtCore.QObject):
    """Inspect original samples without changing the plot or capturing mouse input."""
    def __init__(self, plot, curves, unit_for_key):
        super().__init__(plot)
        self.plot, self.curves, self.unit_for_key = plot, curves, unit_for_key
        self.label = QtWidgets.QLabel(plot.viewport())
        self.label.setTextFormat(QtCore.Qt.PlainText)
        self.label.setAttribute(QtCore.Qt.WA_TransparentForMouseEvents)
        self.label.hide()
        plot.viewport().setMouseTracking(True)
        plot.viewport().installEventFilter(self)

    def eventFilter(self, watched, event):
        if event.type() == QtCore.QEvent.MouseMove:
            if event.buttons():
                self.label.hide()
            else:
                self.update_at(event.position().toPoint())
        elif event.type() in (QtCore.QEvent.Leave, QtCore.QEvent.Hide, QtCore.QEvent.MouseButtonPress,
                              QtCore.QEvent.Wheel, QtCore.QEvent.Resize):
            self.label.hide()
        return False

    def update_at(self, mouse):
        view = self.plot.getViewBox()
        scene = self.plot.mapToScene(mouse)
        if not view.sceneBoundingRect().contains(scene):
            self.label.hide()
            return
        target = view.mapSceneToView(scene)
        span = view.viewRange()[0][1] - view.viewRange()[0][0]
        nearest = None
        for key, curve in self.curves.items():
            if not curve.isVisible():
                continue
            times, values = curve.getOriginalDataset()
            if times is None or len(times) == 0:
                continue
            index = bisect.bisect_left(times, target.x())
            candidates = [i for i in (index - 1, index) if 0 <= i < len(times)]
            index = min(candidates, key=lambda i: abs(times[i] - target.x()))
            t, value = float(times[index]), float(values[index])
            if not math.isfinite(value) or abs(t - target.x()) > max(1.0, span / 100):
                continue
            point = view.mapViewToScene(QtCore.QPointF(t, value))
            if not view.sceneBoundingRect().contains(point):
                continue
            distance = abs(point.y() - scene.y())
            if nearest is None or distance < nearest[0]:
                nearest = (distance, key, t, value, curve)
        if nearest is None:
            self.label.hide()
            return
        _, key, t, value, curve = nearest
        color = curve.opts['pen'].color().name()
        style = f"background:#202c3b; color:#f1f5f9; border:1px solid {color}; padding:6px; border-radius:4px;"
        if self.label.styleSheet() != style:
            self.label.setStyleSheet(style)
        self.label.setText(f"{key}\nZaman: {t:.3f} s\nDeğer: {fmt_value(value, self.unit_for_key(key))}")
        self.label.adjustSize()
        width, height = self.label.width(), self.label.height()
        bounds = self.plot.viewport().rect()
        x, y = mouse.x() + 14, mouse.y() - height - 10
        if x + width > bounds.width() - 2:
            x = mouse.x() - width - 14
        if y < 2:
            y = mouse.y() + 18
        self.label.move(max(2, min(x, bounds.width() - width - 2)),
                        max(2, min(y, bounds.height() - height - 2)))
        self.label.show()
        self.label.raise_()


class MainWindow(QtWidgets.QMainWindow):
    def __init__(self):
        super().__init__()
        ensure_gui_garbage_collection()
        self.setWindowTitle(APP_NAME)
        self.resize(1440, 900)
        self.settings_path = Path.home() / ".scoperec_mac.json"
        self.series = {}
        self.curves = {}
        self.viewer_curves = {}
        self.serial_workers = {}
        self.serial_logs = {}
        self.mark_lines = []
        self.worker = None
        self.connection_worker = None
        self.idle_client = None
        self.instrument_id = ""
        self.recorder = None
        self.running = False
        self.test_active = False
        self.test_started_at = 0.0
        self._ending_test = False
        self._closing = False
        self.dirty_plot = False
        self.plot_interacting = False
        self.plot_revisions = {}
        self.interaction_timer = QtCore.QTimer(self)
        self.interaction_timer.setSingleShot(True)
        self.interaction_timer.setInterval(160)
        self.interaction_timer.timeout.connect(self._finish_plot_interaction)
        self.follow_plot = True
        self.current_t = 0.0
        self.out_of_limit = False
        self.out_since = 0.0
        self.sample_count = 0
        self.violation_count = 0
        self.record_dir = str(Path.cwd() / "kayitlar")
        self._build_ui()
        self._load_settings()
        self._apply_theme(self.theme.currentData())
        self.ui_timer = QtCore.QTimer(self)
        self.ui_timer.timeout.connect(self._refresh_stats)
        self.ui_timer.start(250)
        self._update_measurement_actions()

    def _build_ui(self):
        central = QtWidgets.QWidget()
        self.setCentralWidget(central)
        root = QtWidgets.QVBoxLayout(central)
        root.setContentsMargins(8, 8, 8, 8)

        top = QtWidgets.QHBoxLayout()
        root.addLayout(top)
        options = QtWidgets.QHBoxLayout()
        root.addLayout(options)
        self.conn_lan = QtWidgets.QRadioButton("LAN")
        self.conn_usb = QtWidgets.QRadioButton("USB")
        self.conn_lan.setChecked(True)
        self.conn_group = QtWidgets.QButtonGroup(self)
        self.conn_group.addButton(self.conn_lan)
        self.conn_group.addButton(self.conn_usb)
        self.conn_lan.toggled.connect(self._connection_changed)
        self.conn_usb.toggled.connect(self._connection_changed)
        self.address = QtWidgets.QLineEdit()
        self.address.setPlaceholderText("Osiloskop IP: 192.168.1.50 veya 192.168.1.50:5555")
        self.usb_selector = QtWidgets.QComboBox()
        self.usb_selector.setEditable(True)
        self.usb_selector.setPlaceholderText("İlk USBTMC cihazı veya 1234:5678")
        self.usb_refresh = QtWidgets.QPushButton("USB tara")
        self.usb_refresh.clicked.connect(self.refresh_usb_devices)
        self.dialect = QtWidgets.QComboBox()
        self.dialect.addItems(Dialect.names)
        self.interval = QtWidgets.QSpinBox()
        self.interval.setRange(40, 600000)
        self.interval.setValue(250)
        self.interval.setSuffix(" ms")
        self.theme = QtWidgets.QComboBox()
        self.theme.addItem("Koyu", "dark")
        self.theme.addItem("Açık", "light")
        self.theme.setMinimumWidth(120)
        self.theme.currentIndexChanged.connect(lambda: self._apply_theme(self.theme.currentData()))
        self.dialect.setMinimumWidth(180)
        self.interval.setMinimumWidth(125)
        top.addWidget(QtWidgets.QLabel("Bağlantı"))
        conn_row = QtWidgets.QHBoxLayout()
        conn_row.setContentsMargins(0, 0, 0, 0)
        conn_row.setSpacing(8)
        conn_row.addWidget(self.conn_lan)
        conn_row.addWidget(self.conn_usb)
        conn_box = QtWidgets.QWidget()
        conn_box.setLayout(conn_row)
        conn_box.setMinimumWidth(116)
        top.addWidget(conn_box)
        top.addWidget(QtWidgets.QLabel("Adres"))
        top.addWidget(self.address, 2)
        top.addWidget(self.usb_selector, 2)
        top.addWidget(self.usb_refresh)
        self.connect_btn = QtWidgets.QPushButton("Osiloskopa bağlan")
        self.connect_btn.clicked.connect(self.toggle_instrument)
        top.addWidget(self.connect_btn)
        options.addWidget(QtWidgets.QLabel("Komut seti"))
        options.addWidget(self.dialect)
        options.addWidget(QtWidgets.QLabel("Aralık"))
        options.addWidget(self.interval)
        options.addWidget(QtWidgets.QLabel("Tema"))
        options.addWidget(self.theme)
        self.connection_state = QtWidgets.QLabel("Bağlı değil")
        self.connection_state.setWordWrap(True)
        options.addWidget(self.connection_state, 1)

        # Retain Python subclasses as well as their C++ parent relationship. Qt/PySide
        # can otherwise collect a wrapper cycle and remove the entire central panel.
        split = self.root_split = GripSplitter(parent=central)
        root.addWidget(split, 1)
        left = QtWidgets.QWidget()
        left.setMinimumWidth(260)
        left.setMaximumWidth(440)
        split.addWidget(left)
        left_layout = QtWidgets.QVBoxLayout(left)
        left_layout.setContentsMargins(0, 0, 8, 0)

        ch_box = QtWidgets.QGroupBox("Kanallar")
        ch_lay = QtWidgets.QGridLayout(ch_box)
        self.channel_checks = []
        for i, ch in enumerate(CHANNELS):
            cb = QtWidgets.QCheckBox(ch)
            cb.setChecked(i == 0)
            self.channel_checks.append(cb)
            ch_lay.addWidget(cb, i // 2, i % 2)
        left_layout.addWidget(ch_box)

        p_box = QtWidgets.QGroupBox("Ölçümler")
        p_lay = QtWidgets.QVBoxLayout(p_box)
        self.param_list = QtWidgets.QListWidget()
        for code, name, unit in PARAMS:
            item = QtWidgets.QListWidgetItem(f"{code}  {name} [{unit}]")
            item.setData(QtCore.Qt.UserRole, code)
            item.setCheckState(QtCore.Qt.Checked if code in ("RMS", "PKPK") else QtCore.Qt.Unchecked)
            self.param_list.addItem(item)
        p_lay.addWidget(self.param_list)
        left_layout.addWidget(p_box, 1)

        rec_box = QtWidgets.QGroupBox("Kayıt ve limit")
        form = QtWidgets.QFormLayout(rec_box)
        self.test_name = QtWidgets.QLineEdit()
        self.record_check = QtWidgets.QCheckBox("CSV/log kaydet")
        self.record_check.setChecked(True)
        self.dir_label = QtWidgets.QLineEdit(self.record_dir)
        self.dir_button = QtWidgets.QPushButton("Klasör")
        self.dir_button.clicked.connect(self._pick_dir)
        dir_row = QtWidgets.QHBoxLayout()
        dir_row.addWidget(self.dir_label)
        dir_row.addWidget(self.dir_button)
        self.limit_check = QtWidgets.QCheckBox("Limit kontrolü")
        self.limit_series = QtWidgets.QComboBox()
        self.low_limit = QtWidgets.QLineEdit()
        self.high_limit = QtWidgets.QLineEdit()
        form.addRow("Test adı", self.test_name)
        form.addRow("", self.record_check)
        form.addRow("Kayıt yeri", dir_row)
        form.addRow("", self.limit_check)
        form.addRow("Seri", self.limit_series)
        form.addRow("Alt limit", self.low_limit)
        form.addRow("Üst limit", self.high_limit)
        left_layout.addWidget(rec_box)

        buttons = QtWidgets.QGridLayout()
        left_layout.addLayout(buttons)
        self.start_btn = QtWidgets.QPushButton("Başlat")
        self.start_btn.clicked.connect(self.start_measure)
        self.pause_btn = QtWidgets.QPushButton("Duraklat")
        self.pause_btn.clicked.connect(self.pause_measure)
        self.end_btn = QtWidgets.QPushButton("Testi bitir")
        self.end_btn.clicked.connect(self.end_test)
        self.mark_btn = QtWidgets.QPushButton("İşaret koy")
        self.mark_btn.clicked.connect(self.add_mark)
        self.shot_btn = QtWidgets.QPushButton("Osiloskop ekranı")
        self.shot_btn.clicked.connect(self.save_scope_screen)
        self.chart_btn = QtWidgets.QPushButton("Grafik PNG")
        self.chart_btn.clicked.connect(self.save_chart_image)
        for button, icon in (
            (self.start_btn, QtWidgets.QStyle.SP_MediaPlay),
            (self.pause_btn, QtWidgets.QStyle.SP_MediaPause),
            (self.end_btn, QtWidgets.QStyle.SP_MediaStop),
            (self.chart_btn, QtWidgets.QStyle.SP_DialogSaveButton),
        ):
            button.setIcon(self.style().standardIcon(icon))
            button.setIconSize(QtCore.QSize(16, 16))
        for i, btn in enumerate([self.start_btn, self.pause_btn, self.end_btn, self.mark_btn, self.shot_btn, self.chart_btn]):
            btn.setMinimumHeight(34)
            buttons.addWidget(btn, i // 2, i % 2)

        work_split = GripSplitter(parent=split)
        self.work_split = work_split
        split.addWidget(work_split)
        right_tabs = QtWidgets.QTabWidget()
        self.right_tabs = right_tabs
        right_tabs.setMinimumWidth(350)
        work_split.addWidget(right_tabs)
        split.setSizes([360, 1080])

        live = QtWidgets.QWidget()
        live_lay = QtWidgets.QVBoxLayout(live)
        live_split = self.live_split = GripSplitter(QtCore.Qt.Vertical, live)
        live_lay.addWidget(live_split, 1)
        self.stats_table = QtWidgets.QTableWidget(0, 5)
        self.stats_table.setHorizontalHeaderLabels(["Seri", "Son", "Min", "Maks", "Ortalama"])
        self.stats_table.horizontalHeader().setSectionResizeMode(QtWidgets.QHeaderView.Stretch)
        self.stats_table.setMinimumHeight(90)
        live_split.addWidget(self.stats_table)

        plot_panel = QtWidgets.QWidget()
        plot_lay = QtWidgets.QVBoxLayout(plot_panel)
        plot_lay.setContentsMargins(0, 0, 0, 0)
        nav = QtWidgets.QHBoxLayout()
        self.follow_check = QtWidgets.QCheckBox("Sonu izle")
        self.follow_check.setChecked(True)
        self.follow_check.toggled.connect(self._set_follow)
        self.window_spin = QtWidgets.QDoubleSpinBox()
        self.window_spin.setRange(5, 864000)
        self.window_spin.setDecimals(0)
        self.window_spin.setValue(120)
        self.window_spin.setSuffix(" sn")
        for label, handler in [
            ("◀◀", lambda: self.shift_plot(-1.0)),
            ("◀", lambda: self.shift_plot(-0.25)),
            ("▶", lambda: self.shift_plot(0.25)),
            ("▶▶", lambda: self.shift_plot(1.0)),
        ]:
            btn = QtWidgets.QPushButton(label)
            btn.setFixedWidth(44)
            btn.clicked.connect(handler)
            nav.addWidget(btn)
        self.fit_x_btn = QtWidgets.QPushButton("Tüm zaman")
        self.fit_x_btn.clicked.connect(self.fit_all_time)
        self.auto_y_btn = QtWidgets.QPushButton("Y otomatik")
        self.auto_y_btn.clicked.connect(lambda: self.plot.enableAutoRange(axis="y", enable=True))
        nav.addWidget(self.follow_check)
        self.time_window_control = QtWidgets.QFrame()
        self.time_window_control.setObjectName("timeWindowControl")
        time_row = QtWidgets.QHBoxLayout(self.time_window_control)
        time_row.setContentsMargins(8, 4, 8, 4)
        time_row.addWidget(QtWidgets.QLabel("Zaman aralığı"))
        self.window_spin.setFixedWidth(128)
        time_row.addWidget(self.window_spin)
        nav.addWidget(self.time_window_control)
        self.clear_plot_btn = QtWidgets.QPushButton("Grafiği temizle")
        self.clear_plot_btn.setIcon(self.style().standardIcon(QtWidgets.QStyle.SP_TrashIcon))
        self.clear_plot_btn.setToolTip("Yalnızca grafiği temizler; ölçüm ve dosya kaydı devam eder")
        self.clear_plot_btn.clicked.connect(self.clear_plot)
        nav.addStretch(1)
        nav_widget = QtWidgets.QWidget()
        nav_widget.setLayout(nav)
        nav.setSizeConstraint(QtWidgets.QLayout.SetMinimumSize)
        nav_scroll = QtWidgets.QScrollArea()
        nav_scroll.setFrameShape(QtWidgets.QFrame.NoFrame)
        nav_scroll.setWidgetResizable(True)
        nav_scroll.setFixedHeight(64)
        nav_scroll.setVerticalScrollBarPolicy(QtCore.Qt.ScrollBarAlwaysOff)
        nav_scroll.setWidget(nav_widget)
        plot_lay.addWidget(nav_scroll)
        plot_actions = QtWidgets.QHBoxLayout()
        plot_actions.addWidget(self.fit_x_btn)
        plot_actions.addWidget(self.auto_y_btn)
        plot_actions.addWidget(self.clear_plot_btn)
        plot_actions.addStretch(1)
        plot_lay.addLayout(plot_actions)
        self.plot_legend = self._create_legend(self.curves)
        plot_lay.addWidget(self.plot_legend)
        self.plot = pg.PlotWidget()
        self.plot.showGrid(x=True, y=True, alpha=0.25)
        self.plot.setLabel("bottom", "Süre", units="s")
        self.plot.setClipToView(True)
        self.plot.setDownsampling(auto=True, mode="peak")
        self.plot.getPlotItem().setMenuEnabled(False)
        self.plot.getViewBox().setMouseMode(pg.ViewBox.PanMode)
        self.plot.getViewBox().sigRangeChangedManually.connect(self._manual_plot_range)
        self.plot_hover = PlotHover(self.plot, self.curves, lambda key: self.series[key].unit if key in self.series else "")
        plot_lay.addWidget(self.plot, 1)
        live_split.addWidget(plot_panel)
        self.log = QtWidgets.QTextEdit()
        self.log.setReadOnly(True)
        self.log.setMinimumHeight(90)
        live_split.addWidget(self.log)
        live_split.setSizes([150, 560, 150])
        right_tabs.addTab(live, "Canlı ölçüm")

        serial_tab = QtWidgets.QWidget()
        serial_lay = QtWidgets.QVBoxLayout(serial_tab)
        serial_header = QtWidgets.QHBoxLayout()
        serial_header.addWidget(QtWidgets.QLabel("Seri port"))
        serial_header.addStretch(1)
        self.serial_side_btn = QtWidgets.QPushButton("Sağ panelde göster")
        self.serial_side_btn.setCheckable(True)
        self.serial_side_btn.toggled.connect(self._set_serial_side_panel)
        serial_header.addWidget(self.serial_side_btn)
        serial_lay.addLayout(serial_header)
        self.serial_tab_host = QtWidgets.QWidget()
        self.serial_tab_host_lay = QtWidgets.QVBoxLayout(self.serial_tab_host)
        self.serial_tab_host_lay.setContentsMargins(0, 0, 0, 0)
        self.serial_tabs = QtWidgets.QTabWidget()
        self.serial_tab_host_lay.addWidget(self.serial_tabs)
        serial_lay.addWidget(self.serial_tab_host, 1)
        for i in range(3):
            self._add_serial_tab(i)
        right_tabs.addTab(serial_tab, "Seri port")

        self.serial_side = QtWidgets.QFrame()
        self.serial_side.setFrameShape(QtWidgets.QFrame.StyledPanel)
        self.serial_side.setMinimumWidth(320)
        side_lay = QtWidgets.QVBoxLayout(self.serial_side)
        side_head = QtWidgets.QHBoxLayout()
        side_title = QtWidgets.QLabel("Seri port")
        side_title.setStyleSheet("font-weight: 700")
        side_close = QtWidgets.QPushButton("Sekmeye al")
        side_close.clicked.connect(lambda: self._set_serial_side_panel(False))
        side_head.addWidget(side_title)
        side_head.addStretch(1)
        side_head.addWidget(side_close)
        side_lay.addLayout(side_head)
        self.serial_side_host = QtWidgets.QWidget()
        self.serial_side_host_lay = QtWidgets.QVBoxLayout(self.serial_side_host)
        self.serial_side_host_lay.setContentsMargins(0, 0, 0, 0)
        side_lay.addWidget(self.serial_side_host, 1)
        self.serial_side.hide()
        work_split.addWidget(self.serial_side)
        work_split.setSizes([900, 420])

        viewer = QtWidgets.QWidget()
        viewer_lay = QtWidgets.QVBoxLayout(viewer)
        bar = QtWidgets.QHBoxLayout()
        self.open_csv_btn = QtWidgets.QPushButton("CSV aç")
        self.open_csv_btn.clicked.connect(self.open_recording)
        self.viewer_info = QtWidgets.QLabel("Kayıt dosyası açın veya CSV'yi buraya sürükleyin.")
        bar.addWidget(self.open_csv_btn)
        bar.addWidget(self.viewer_info, 1)
        viewer_lay.addLayout(bar)
        self.viewer_legend = self._create_legend(self.viewer_curves)
        viewer_lay.addWidget(self.viewer_legend)
        self.viewer_plot = pg.PlotWidget()
        self.viewer_plot.showGrid(x=True, y=True, alpha=0.25)
        self.viewer_hover = PlotHover(self.viewer_plot, self.viewer_curves,
                                     lambda key: key.rsplit("[", 1)[1].rstrip("]") if "[" in key
                                     else PARAM_UNITS.get(key.rsplit("_", 1)[-1], ""))
        viewer_lay.addWidget(self.viewer_plot, 1)
        self.viewer_events = QtWidgets.QTextEdit()
        self.viewer_events.setReadOnly(True)
        self.viewer_events.setMaximumHeight(160)
        viewer_lay.addWidget(self.viewer_events)
        right_tabs.addTab(viewer, "Kayıt görüntüleyici")

        self.status = QtWidgets.QStatusBar()
        self.setStatusBar(self.status)
        self.status.showMessage("Hazır")
        self._connection_changed()

    def _create_legend(self, curves):
        legend = QtWidgets.QListWidget()
        legend.setFlow(QtWidgets.QListView.LeftToRight)
        legend.setWrapping(True)
        legend.setResizeMode(QtWidgets.QListView.Adjust)
        legend.setMovement(QtWidgets.QListView.Static)
        legend.setSelectionMode(QtWidgets.QAbstractItemView.NoSelection)
        legend.setFixedHeight(64)
        legend.setSpacing(3)
        legend.setIconSize(QtCore.QSize(24, 8))
        legend.itemChanged.connect(lambda item: curves[item.data(QtCore.Qt.UserRole)].setVisible(
            item.checkState() == QtCore.Qt.Checked) if item.data(QtCore.Qt.UserRole) in curves else None)
        return legend

    @staticmethod
    def _series_swatch(color):
        pixmap = QtGui.QPixmap(24, 8)
        pixmap.fill(QtGui.QColor(color))
        return QtGui.QIcon(pixmap)

    def _add_legend_item(self, legend, key, label, color):
        item = QtWidgets.QListWidgetItem(self._series_swatch(color), label)
        item.setData(QtCore.Qt.UserRole, key)
        item.setCheckState(QtCore.Qt.Checked)
        item.setToolTip(label + " — çizgiyi göster/gizle")
        legend.addItem(item)

    def _set_serial_side_panel(self, enabled):
        if not hasattr(self, "serial_tabs"):
            return
        self.serial_tabs.setParent(None)
        if enabled:
            self.serial_side_host_lay.addWidget(self.serial_tabs)
            self.serial_side.show()
            self.right_tabs.setCurrentIndex(0)
        else:
            self.serial_tab_host_lay.addWidget(self.serial_tabs)
            self.serial_side.hide()
        if self.serial_side_btn.isChecked() != enabled:
            self.serial_side_btn.blockSignals(True)
            self.serial_side_btn.setChecked(enabled)
            self.serial_side_btn.blockSignals(False)

    def _add_serial_tab(self, idx):
        page = QtWidgets.QWidget()
        lay = QtWidgets.QVBoxLayout(page)
        row = QtWidgets.QHBoxLayout()
        port = QtWidgets.QComboBox()
        port.setEditable(True)
        self._fill_ports(port)
        baud = QtWidgets.QComboBox()
        baud.setEditable(True)
        baud.addItems(["9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600"])
        baud.setCurrentText("115200")
        baud.setFixedWidth(110)
        port.setMinimumWidth(80)
        port.setSizePolicy(QtWidgets.QSizePolicy.Ignored, QtWidgets.QSizePolicy.Fixed)
        connect = QtWidgets.QPushButton("Bağlan")
        refresh = QtWidgets.QPushButton("Yenile")
        plot_values = QtWidgets.QCheckBox("Grafikte göster")
        plot_values.setChecked(True)
        row.addWidget(QtWidgets.QLabel("Port"))
        row.addWidget(port, 2)
        row.addWidget(QtWidgets.QLabel("Baud"))
        row.addWidget(baud)
        lay.addLayout(row)
        actions = QtWidgets.QHBoxLayout()
        actions.addWidget(refresh)
        actions.addWidget(connect)
        actions.addWidget(plot_values)
        actions.addStretch(1)
        lay.addLayout(actions)
        terminal_header = QtWidgets.QHBoxLayout()
        terminal_header.addWidget(QtWidgets.QLabel("Terminal"))
        terminal_header.addStretch(1)
        clear = QtWidgets.QPushButton("Temizle")
        clear.setIcon(self.style().standardIcon(QtWidgets.QStyle.SP_TrashIcon))
        clear.setToolTip("Terminal ekranını temizler; bağlantıyı ve kayıt dosyasını etkilemez")
        terminal_header.addWidget(clear)
        lay.addLayout(terminal_header)
        text = QtWidgets.QTextEdit()
        text.setReadOnly(True)
        text.document().setMaximumBlockCount(5000)
        lay.addWidget(text, 1)
        send_row = QtWidgets.QHBoxLayout()
        send = QtWidgets.QLineEdit()
        send_btn = QtWidgets.QPushButton("Gönder")
        send_row.addWidget(send, 1)
        send_row.addWidget(send_btn)
        lay.addLayout(send_row)
        name = f"Seri {idx + 1}"
        state = {"port": port, "baud": baud, "text": text, "send": send, "connect": connect, "plot": plot_values, "clear": clear}
        clear.clicked.connect(lambda _=None, n=name: self.clear_serial_terminal(n))
        refresh.clicked.connect(lambda _=None, p=port: self._fill_ports(p))
        connect.clicked.connect(lambda _=None, n=name, st=state: self.toggle_serial(n, st))
        send_btn.clicked.connect(lambda _=None, n=name, st=state: self.send_serial(n, st))
        send.returnPressed.connect(lambda n=name, st=state: self.send_serial(n, st))
        self.serial_logs[name] = state
        self.serial_tabs.addTab(page, name)

    def _fill_ports(self, combo):
        current = combo.currentText()
        combo.clear()
        combo.addItem("tcp://127.0.0.1:9000")
        if list_ports:
            for p in list_ports.comports():
                combo.addItem(p.device)
        if current:
            combo.setCurrentText(current)

    def refresh_usb_devices(self):
        current = self.usb_selector.currentText()
        self.usb_selector.clear()
        try:
            devices = UsbTmcClient.devices()
            for dev in devices:
                self.usb_selector.addItem(UsbTmcClient.device_label(dev))
            if not devices:
                self.usb_selector.addItem("")
        except Exception as exc:
            self.usb_selector.addItem("")
            self.append_log(f"USB tarama hatası: {exc}")
        if current:
            self.usb_selector.setCurrentText(current)

    def connection_mode(self):
        return "USB" if self.conn_usb.isChecked() else "LAN"

    def connection_config(self):
        return {"connection": self.connection_mode(),
                "address": self.address.text().strip(),
                "usb_selector": self.usb_selector.currentText().strip()}

    def _connection_controls(self, enabled):
        for control in (self.conn_usb, self.conn_lan, self.address,
                        self.usb_selector, self.usb_refresh, self.dialect):
            control.setEnabled(enabled)

    def toggle_instrument(self):
        if self.worker:
            self.pause_measure()
            return
        if self.idle_client:
            self.idle_client.close()
            self.idle_client = None
            self.instrument_id = ""
            self.connection_state.setText("Bağlı değil")
            self.connect_btn.setText("Osiloskopa bağlan")
            self._connection_controls(True)
            return
        if self.connection_worker:
            return
        self._connection_controls(False)
        self.connect_btn.setEnabled(False)
        self.start_btn.setEnabled(False)
        self.connection_state.setText("Bağlanıyor…")
        self.connection_worker = ConnectionWorker(self.connection_config())
        self.connection_worker.finished.connect(self._instrument_ready)
        self.connection_worker.start()

    def _instrument_ready(self):
        probe = self.connection_worker
        self.idle_client = probe.client
        self.instrument_id = probe.idn
        error = probe.error
        self.connection_worker = None
        probe.deleteLater()
        self.connect_btn.setEnabled(True)
        self.start_btn.setEnabled(True)
        if self.idle_client:
            self.connection_state.setText("Bağlandı: " + self.instrument_id)
            self.connect_btn.setText("Bağlantıyı kes")
            self.append_log("Bağlandı: " + self.instrument_id)
        else:
            self.connection_state.setText("Bağlantı kurulamadı: " + error)
            self.append_log("Bağlantı hatası: " + error)
            self._connection_controls(True)
        self._update_measurement_actions()
        if self._closing:
            QtCore.QTimer.singleShot(0, self.close)

    def _connection_changed(self, *args):
        is_usb = self.connection_mode() == "USB"
        self.address.setVisible(not is_usb)
        self.usb_selector.setVisible(is_usb)
        self.usb_refresh.setVisible(is_usb)
        if is_usb:
            self.refresh_usb_devices()

    def _apply_theme(self, name):
        app = QtWidgets.QApplication.instance()
        app.setStyle("Fusion")
        sizing = """
            QComboBox { min-height:26px; padding:3px 24px 3px 8px; }
            QComboBox::drop-down { width:22px; }
            QSpinBox, QDoubleSpinBox { min-height:26px; padding:3px 22px 3px 6px; }
        """
        if name == "light":
            app.setStyleSheet(sizing)
            pg.setConfigOption("background", "w")
            pg.setConfigOption("foreground", "k")
        else:
            app.setStyleSheet("""
                QWidget { background:#111827; color:#e5e7eb; }
                QLineEdit, QTextEdit, QPlainTextEdit, QComboBox, QListWidget, QTableWidget {
                    background:#0b1220; color:#e5e7eb; border:1px solid #374151; border-radius:4px; padding:4px;
                }
                QPushButton { background:#1f2937; border:1px solid #4b5563; border-radius:5px; padding:6px 10px; }
                QPushButton:hover { background:#374151; }
                QGroupBox { border:1px solid #374151; border-radius:6px; margin-top:8px; padding-top:8px; }
                QGroupBox::title { subcontrol-origin: margin; left:8px; padding:0 4px; }
            """ + sizing)
            pg.setConfigOption("background", "#0b1220")
            pg.setConfigOption("foreground", "#d1d5db")
        for plot in (self.plot, self.viewer_plot):
            plot.setBackground("w" if name == "light" else "#0b1220")
            for axis in ("left", "bottom"):
                plot.getAxis(axis).setTextPen("k" if name == "light" else "#d1d5db")
        self._style_action_buttons(name)
        self.connect_btn.setMinimumWidth(max(150, self.connect_btn.sizeHint().width()))
        for splitter in self.findChildren(GripSplitter):
            splitter.setProperty("darkTheme", name == "dark")
            for index in range(1, splitter.count()):
                splitter.handle(index).update()
        time_background = "#263e49" if name == "dark" else "#dce7e8"
        time_text = "#e5e7eb" if name == "dark" else "#24303a"
        self.time_window_control.setStyleSheet(f"""
            QFrame#timeWindowControl {{ background:{time_background}; border:1px solid #536773; border-radius:5px; }}
            QFrame#timeWindowControl QLabel {{ background:transparent; color:{time_text}; font-weight:600; }}
            QFrame#timeWindowControl QDoubleSpinBox {{ background:#344b5b; color:#eef1f4; border:1px solid #657784; }}
        """)

    def _style_action_buttons(self, theme):
        disabled_bg, disabled_text = (("#263244", "#94a3b8") if theme == "dark"
                                     else ("#e5e7eb", "#6b7280"))
        for button, base, hover, pressed, text in (
            (self.start_btn, "#294d3e", "#345b4a", "#203e32", "#e5e7eb"),
            (self.pause_btn, "#655537", "#746342", "#51442d", "#e5e7eb"),
            (self.end_btn, "#643b42", "#73474e", "#512f35", "#e5e7eb"),
            (self.mark_btn, "#344b5b", "#40596a", "#2a3d4a", "#e5e7eb"),
            (self.shot_btn, "#364b4a", "#425a58", "#2c3e3d", "#e5e7eb"),
            (self.chart_btn, "#40464d", "#4d545c", "#33383e", "#e5e7eb"),
        ):
            button.setStyleSheet(f"""
                QPushButton {{ background-color:{base}; color:{text}; border:1px solid {base};
                    border-radius:5px; padding:6px 10px; font-weight:600; }}
                QPushButton:hover {{ background-color:{hover}; border-color:{hover}; }}
                QPushButton:pressed {{ background-color:{pressed}; border-color:{pressed}; }}
                QPushButton:focus {{ border:2px solid #9aa4ad; padding:5px 9px; }}
                QPushButton:disabled {{ background-color:{disabled_bg}; color:{disabled_text};
                    border-color:{disabled_bg}; }}
            """)

    def _pick_dir(self):
        path = QtWidgets.QFileDialog.getExistingDirectory(self, "Kayıt klasörü", self.dir_label.text())
        if path:
            self.dir_label.setText(path)

    def selected_channels(self):
        return [cb.text() for cb in self.channel_checks if cb.isChecked()]

    def selected_params(self):
        out = []
        for i in range(self.param_list.count()):
            item = self.param_list.item(i)
            if item.checkState() == QtCore.Qt.Checked:
                out.append(item.data(QtCore.Qt.UserRole))
        return out

    def _prepare_series(self):
        self.series.clear()
        self.curves.clear()
        self.plot_revisions.clear()
        self.plot_legend.clear()
        self.plot.clear()
        self.limit_series.clear()
        idx = 0
        for ch in self.selected_channels():
            for code in self.selected_params():
                key = f"{ch}_{code}"
                color = COLORS[idx % len(COLORS)]
                self.series[key] = Series(key, PARAM_UNITS.get(code, ""), color)
                self.curves[key] = self.plot.plot([], [], pen=pg.mkPen(color, width=1), antialias=False, name=key)
                self.curves[key].setDownsampling(auto=True, method="peak")
                self.curves[key].setClipToView(True)
                self._add_legend_item(self.plot_legend, key, f"{ch} - {code} [{PARAM_UNITS.get(code, '')}]", color)
                self.limit_series.addItem(key)
                idx += 1
        self.stats_table.setRowCount(len(self.series))
        for row, key in enumerate(self.series):
            label = QtWidgets.QTableWidgetItem(self._series_swatch(self.series[key].color), key)
            self.stats_table.setItem(row, 0, label)
            for col in range(1, 5):
                self.stats_table.setItem(row, col, QtWidgets.QTableWidgetItem("—"))

    def start_measure(self):
        if self.worker or self.connection_worker or self._ending_test or self._closing:
            return
        if self.connection_mode() == "LAN" and not self.address.text().strip():
            QtWidgets.QMessageBox.warning(self, APP_NAME, "Osiloskop IP adresini girin.")
            return
        if not self.selected_channels() or not self.selected_params():
            QtWidgets.QMessageBox.warning(self, APP_NAME, "En az bir kanal ve bir ölçüm seçin.")
            return
        if not self.test_active:
            self._prepare_series()
            self.sample_count = 0
            self.current_t = 0.0
            self.violation_count = 0
            self.out_of_limit = False
            self.test_started_at = time.monotonic()
            self.test_active = True
            if self.record_check.isChecked():
                self.recorder = Recorder(self.dir_label.text(), self.test_name.text(), list(self.series.keys()))
                self.append_log(f"Kayıt başladı: {self.recorder.stem}.csv")
        else:
            gap = time.monotonic() - self.test_started_at
            for series in self.series.values():
                series.add(gap, math.nan)
        config = {
            "connection": self.connection_mode(),
            "address": self.address.text().strip(),
            "usb_selector": self.usb_selector.currentText().strip(),
            "dialect": self.dialect.currentIndex(),
            "channels": self.selected_channels(),
            "params": self.selected_params(),
            "interval_ms": self.interval.value(),
            "time_offset": time.monotonic() - self.test_started_at,
        }
        self.worker = MeasurementWorker(config)
        if self.idle_client:
            self.worker.client = self.idle_client
            self.worker.client.cancel_event = self.worker.stop_event
            self.worker.dialect = Dialect.pick(self.dialect.currentIndex(), self.instrument_id)
            self.idle_client = None
        self._connection_controls(False)
        self.connect_btn.setText("Bağlantıyı kes")
        self.worker.sample.connect(self.on_sample)
        self.worker.status.connect(self.status.showMessage)
        self.worker.status.connect(self.connection_state.setText)
        self.worker.log.connect(self.append_log)
        self.worker.finished.connect(self._worker_stopped)
        self.worker.start()
        self.running = True
        self._update_measurement_actions()
        self._save_settings()

    def pause_measure(self):
        if self.worker:
            self.worker.stop()
            self.connection_state.setText("Ölçüm durduruluyor, cihaz bırakılıyor…")
            self.start_btn.setEnabled(False)
            self.pause_btn.setEnabled(False)
            self.connect_btn.setEnabled(False)

    def end_test(self):
        self._ending_test = True
        if self.worker:
            self.pause_measure()
            return
        if self.idle_client:
            self.idle_client.close()
            self.idle_client = None
            self.connection_state.setText("Bağlı değil")
            self.connect_btn.setText("Osiloskopa bağlan")
        self._finalize_test()

    def _finalize_test(self):
        if self.recorder:
            recorder = self.recorder
            self.recorder = None
            recorder.log(self.current_t, f"Test bitti. Okuma: {self.sample_count}, limit dışı: {self.violation_count}")
            recorder.close()
            self.append_log(f"Dosyalar kapatıldı: {recorder.stem}")
        self.test_active = False
        self._ending_test = False
        self._connection_controls(True)
        self._update_measurement_actions()

    def _update_measurement_actions(self):
        busy = self.worker is not None or self.connection_worker is not None
        self.start_btn.setText("Devam et" if self.test_active and not self.worker else "Başlat")
        self.start_btn.setEnabled(not busy and not self._ending_test and not self._closing)
        self.pause_btn.setEnabled(self.worker is not None and not self._ending_test)
        self.end_btn.setEnabled(self.test_active or self.idle_client is not None)
        for control in (*self.channel_checks, self.param_list, self.record_check,
                        self.test_name, self.dir_label, self.dir_button):
            control.setEnabled(not self.test_active)

    def _worker_stopped(self):
        finished_worker = self.worker
        self.worker = None
        self.running = False
        self.connect_btn.setText("Osiloskopa bağlan")
        self.connection_state.setText("Bağlı değil")
        self.connect_btn.setEnabled(True)
        self._connection_controls(not self.test_active)
        self.append_log("Ölçüm durdu")
        if finished_worker:
            finished_worker.deleteLater()
        if self._ending_test:
            self._finalize_test()
        self._update_measurement_actions()
        if self._closing:
            QtCore.QTimer.singleShot(0, self.close)

    def on_sample(self, t, values):
        self.sample_count += 1
        self.current_t = t
        ordered_values = []
        for key, value in values.items():
            if key in self.series:
                self.series[key].add(t, value)
                ordered_values.append(value)
        self.dirty_plot = True
        if self.recorder:
            self.recorder.row(t, [values.get(k, math.nan) for k in self.series.keys()])
        self._check_limit(t, values)

    def _check_limit(self, t, values):
        if not self.limit_check.isChecked() or not self.limit_series.currentText():
            return
        key = self.limit_series.currentText()
        value = values.get(key, math.nan)
        lo, hi = safe_float(self.low_limit.text()), safe_float(self.high_limit.text())
        bad = math.isnan(value) or (not math.isnan(lo) and value < lo) or (not math.isnan(hi) and value > hi)
        if bad and not self.out_of_limit:
            self.out_of_limit = True
            self.out_since = t
            self.violation_count += 1
            msg = f"LİMİT DIŞI {key} = {fmt_value(value, self.series.get(key, Series(key, '', '')).unit)}"
            self.append_log(msg)
            if self.recorder:
                self.recorder.log(t, msg)
                self.recorder.event(t, "limit disi", value)
            QtWidgets.QApplication.beep()
        elif not bad and self.out_of_limit:
            self.out_of_limit = False
            msg = f"Normale döndü {key}; süre {t - self.out_since:.1f} s"
            self.append_log(msg)
            if self.recorder:
                self.recorder.log(t, msg)
                self.recorder.event(t, "normale dondu", value, f"{t - self.out_since:.2f}")

    def _refresh_stats(self):
        if self._closing:
            return
        if self.plot_interacting:
            return
        if self.dirty_plot:
            self._redraw_plot()
        for row, key in enumerate(self.series):
            s = self.series[key]
            vals = [fmt_value(s.last, s.unit), fmt_value(s.minv, s.unit), fmt_value(s.maxv, s.unit), fmt_value(s.avg, s.unit)]
            for col, text in enumerate(vals, 1):
                item = self.stats_table.item(row, col)
                if item and item.text() != text:
                    item.setText(text)
        if self.series:
            self.status.showMessage(f"Okuma: {self.sample_count}  Limit dışı: {self.violation_count}")

    def _redraw_plot(self):
        for key, curve in self.curves.items():
            s = self.series.get(key)
            if s and self.plot_revisions.get(key) != s.revision:
                curve.setData(s.times, s.values)
                self.plot_revisions[key] = s.revision
        if self.follow_plot and self.current_t > 0:
            window = self.window_spin.value()
            self.plot.setXRange(max(0, self.current_t - window), max(window, self.current_t), padding=0)
        self.dirty_plot = False

    def clear_plot(self):
        self.plot_hover.label.hide()
        for series in self.series.values():
            series.times.clear()
            series.values.clear()
            series.revision += 1
        for curve in self.curves.values():
            curve.setData([], [])
        for mark in self.mark_lines:
            self.plot.removeItem(mark)
        self.mark_lines.clear()
        self.plot_revisions.clear()
        self.dirty_plot = True
        self.follow_check.setChecked(True)
        self._refresh_stats()

    def clear_serial_terminal(self, name):
        state = self.serial_logs.get(name)
        if state:
            state["text"].clear()

    def _set_follow(self, enabled):
        self.follow_plot = enabled
        if enabled:
            self._redraw_plot()

    def _manual_plot_range(self, *args):
        self.plot_interacting = True
        self.interaction_timer.start()
        if self.follow_check.isChecked():
            self.follow_check.blockSignals(True)
            self.follow_check.setChecked(False)
            self.follow_check.blockSignals(False)
        self.follow_plot = False

    def _finish_plot_interaction(self):
        self.plot_interacting = False
        self._refresh_stats()

    def shift_plot(self, fraction):
        self.follow_check.setChecked(False)
        lo, hi = self.plot.getViewBox().viewRange()[0]
        width = max(1.0, hi - lo)
        step = width * fraction
        new_lo = max(0.0, lo + step)
        self.plot.setXRange(new_lo, new_lo + width, padding=0)

    def fit_all_time(self):
        self.follow_check.setChecked(False)
        last = max((s.times[-1] for s in self.series.values() if s.times), default=0.0)
        self.plot.setXRange(0, max(5, last), padding=0.02)
        self.plot.enableAutoRange(axis="y", enable=True)

    def append_log(self, text):
        line = f"{datetime.now().strftime('%H:%M:%S')}  {text}"
        self.log.append(line)
        if self.recorder:
            self.recorder.log(0, text)

    def add_mark(self):
        text, ok = QtWidgets.QInputDialog.getText(self, "İşaret", "Not:")
        if not ok:
            return
        t = 0.0
        if self.series:
            t = max((s.times[-1] for s in self.series.values() if s.times), default=0.0)
        line = pg.InfiniteLine(t, angle=90, pen=pg.mkPen("#60a5fa", style=QtCore.Qt.DashLine))
        self.plot.addItem(line)
        self.mark_lines.append(line)
        msg = f"İŞARET: {text} (t={t:.2f}s)"
        self.append_log(msg)
        if self.recorder:
            self.recorder.event(t, "isaret", math.nan, text)

    def save_scope_screen(self):
        if not self.worker:
            QtWidgets.QMessageBox.information(self, APP_NAME, "Önce osiloskopa bağlanın.")
            return
        base = str(Path(self.dir_label.text()) / f"scope_screen_{datetime.now().strftime('%Y%m%d_%H%M%S')}")
        Path(self.dir_label.text()).mkdir(parents=True, exist_ok=True)
        self.worker.request_screenshot(base)

    def save_chart_image(self):
        Path(self.dir_label.text()).mkdir(parents=True, exist_ok=True)
        path = str(Path(self.dir_label.text()) / f"grafik_{datetime.now().strftime('%Y%m%d_%H%M%S')}.png")
        exporter = pg.exporters.ImageExporter(self.plot.plotItem)
        legend = pg.LegendItem(offset=(10, 10), colCount=2)
        legend.setParentItem(self.plot.getViewBox())
        for key, curve in self.curves.items():
            if curve.isVisible():
                legend.addItem(curve, key)
        try:
            exporter.export(path)
        finally:
            self.plot.scene().removeItem(legend)
        self.append_log(f"Grafik kaydedildi: {path}")

    def toggle_serial(self, name, state):
        if name in self.serial_workers:
            self.serial_workers[name].stop()
            self.serial_workers[name].wait(2000)
            del self.serial_workers[name]
            state["connect"].setText("Bağlan")
            return
        worker = SerialWorker(name, state["port"].currentText().strip(), int(state["baud"].currentText()))
        worker.line.connect(self.on_serial_line)
        worker.values.connect(self.on_serial_values)
        worker.status.connect(self.append_log)
        self.serial_workers[name] = worker
        state["connect"].setText("Kes")
        worker.start()

    def send_serial(self, name, state):
        text = state["send"].text()
        if not text:
            return
        worker = self.serial_workers.get(name)
        if worker:
            worker.send_text(text + "\n")
            state["send"].clear()

    def on_serial_line(self, name, text):
        state = self.serial_logs.get(name)
        if state:
            state["text"].append(f"{datetime.now().strftime('%H:%M:%S')}  {text}")
        if self.recorder:
            tag = name.replace(" ", "").lower()
            with open(self.recorder.stem + f"_{tag}.txt", "a", encoding="utf-8") as f:
                f.write(f"{now_stamp()}\t{text}\n")

    def on_serial_values(self, name, values):
        state = self.serial_logs.get(name)
        if not state or not state["plot"].isChecked():
            return
        t = max((s.times[-1] for s in self.series.values() if s.times), default=0.0)
        for key, value in values.items():
            full = f"{name}_{key}"
            if full not in self.series:
                color = COLORS[len(self.series) % len(COLORS)]
                self.series[full] = Series(full, "", color)
                self.curves[full] = self.plot.plot([], [], pen=pg.mkPen(color, width=1, style=QtCore.Qt.DotLine), antialias=False, name=full)
                self.curves[full].setDownsampling(auto=True, method="peak")
                self.curves[full].setClipToView(True)
                self._add_legend_item(self.plot_legend, full, full, color)
            self.series[full].add(t, value)
            self.dirty_plot = True

    def open_recording(self):
        path, _ = QtWidgets.QFileDialog.getOpenFileName(self, "Kayıt aç", self.dir_label.text(), "CSV (*.csv);;All files (*)")
        if path:
            self.load_recording(path)

    def load_recording(self, path):
        self.viewer_hover.label.hide()
        self.viewer_plot.clear()
        self.viewer_curves.clear()
        self.viewer_legend.clear()
        self.viewer_events.clear()
        try:
            with open(path, newline="", encoding="utf-8-sig") as f:
                sample = f.read(2048)
                f.seek(0)
                dialect = csv.Sniffer().sniff(sample, delimiters=";,")
                rows = list(csv.reader(f, dialect))
            if len(rows) < 2:
                raise ValueError("CSV boş")
            header = rows[0]
            t_idx = 1 if len(header) > 1 else 0
            xs = []
            ys = {h: [] for h in header[2:]}
            for row in rows[1:]:
                if len(row) <= t_idx:
                    continue
                t = safe_float(row[t_idx])
                xs.append(t)
                for i, h in enumerate(header[2:], 2):
                    ys[h].append(safe_float(row[i]) if i < len(row) else math.nan)
            for i, (key, vals) in enumerate(ys.items()):
                color = COLORS[i % len(COLORS)]
                curve = self.viewer_plot.plot(xs, vals, pen=pg.mkPen(color, width=1), antialias=False, name=key)
                curve.setDownsampling(auto=True, method="peak")
                curve.setClipToView(True)
                self.viewer_curves[key] = curve
                self._add_legend_item(self.viewer_legend, key, key, color)
            base = str(Path(path).with_suffix(""))
            log_path = base + "_log.txt"
            if os.path.exists(log_path):
                self.viewer_events.setPlainText(Path(log_path).read_text(encoding="utf-8", errors="replace"))
            self.viewer_info.setText(Path(path).name)
        except Exception as exc:
            QtWidgets.QMessageBox.warning(self, APP_NAME, f"Kayıt açılamadı: {exc}")

    def _load_settings(self):
        if not self.settings_path.exists():
            return
        try:
            data = json.loads(self.settings_path.read_text())
            if data.get("connection", "LAN") == "USB":
                self.conn_usb.setChecked(True)
            else:
                self.conn_lan.setChecked(True)
            self.address.setText(data.get("address", ""))
            self.usb_selector.setCurrentText(data.get("usb_selector", ""))
            self.dir_label.setText(data.get("record_dir", self.record_dir))
            self.interval.setValue(int(data.get("interval", 250)))
            self.theme.setCurrentIndex(max(0, self.theme.findData(data.get("theme", "dark"))))
            self._set_serial_side_panel(bool(data.get("serial_side_panel", False)))
        except Exception:
            pass

    def _save_settings(self):
        data = {
            "connection": self.connection_mode(),
            "address": self.address.text(),
            "usb_selector": self.usb_selector.currentText(),
            "record_dir": self.dir_label.text(),
            "interval": self.interval.value(),
            "theme": self.theme.currentData(),
            "serial_side_panel": self.serial_side.isVisible(),
        }
        try:
            self.settings_path.write_text(json.dumps(data, indent=2))
        except Exception:
            pass

    def closeEvent(self, event):
        self._closing = True
        if self.connection_worker is not None:
            event.ignore()
            self.status.showMessage("Bağlantı denemesi bitiriliyor…")
            return
        if self.worker is not None:
            self.end_test()
            event.ignore()
            return
        if self.idle_client:
            self.idle_client.close()
            self.idle_client = None
        self._save_settings()
        self.end_test()
        for worker in list(self.serial_workers.values()):
            worker.stop()
        if any(worker.isRunning() for worker in self.serial_workers.values()):
            event.ignore()
            QtCore.QTimer.singleShot(100, self.close)
            return
        self.ui_timer.stop()
        self.interaction_timer.stop()
        self.plot_hover.label.hide()
        self.viewer_hover.label.hide()
        # Remove closed plots from pyqtgraph's global exit callbacks while Qt is
        # still alive; leave no callbacks referring to disposed graphics widgets.
        for plot in (self.plot, self.viewer_plot):
            view = plot.getViewBox()
            if view in pg.ViewBox.AllViews:
                view.unregister()
        event.accept()


def main():
    if "--self-test" in sys.argv:
        def server():
            s = socket.socket()
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            s.bind(("127.0.0.1", 17025))
            s.listen(1)
            conn, _ = s.accept()
            try:
                while True:
                    data = conn.recv(1024)
                    if not data:
                        break
                    cmd = data.decode("ascii", "replace").strip()
                    if cmd == "*IDN?":
                        conn.sendall(b"SIGLENT,SDS1104X-E,SELFTEST,1.0\n")
                    elif "PAVA?" in cmd:
                        conn.sendall(b"C1:PAVA PKPK,3.30,RMS,1.20,MEAN,0.01,FREQ,1000\n")
                    else:
                        conn.sendall(b"0\n")
            finally:
                conn.close()
                s.close()

        thread = threading.Thread(target=server, daemon=True)
        thread.start()
        client = ScpiClient("127.0.0.1:17025")
        idn = client.query_text("*IDN?")
        dialect = Dialect.pick(0, idn)
        vals = dialect.measure(client, 1, ["RMS", "PKPK", "FREQ"])
        client.close()
        parsed = parse_serial_values("SET:1500,ACT:1498,T:+45.3")
        backend_ok = usb_backend() is not None
        ok = dialect.name == "Siglent" and vals == [1.2, 3.3, 1000.0] and parsed["ACT"] == 1498 and backend_ok
        print("SELFTEST OK" if ok else "SELFTEST FAILED", idn, vals, parsed, "usb_backend=", backend_ok)
        sys.exit(0 if ok else 2)

    enable_crash_logging()
    app = QtWidgets.QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    asset_root = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent.parent))
    app.setWindowIcon(QtGui.QIcon(str(asset_root / "logo" / "scoperec-kare.png")))
    pg.setConfigOptions(antialias=True)
    win = MainWindow()
    win.show()
    if "--hardware-stability-test" in sys.argv:
        from hardware_endurance import install_endurance_test
        position = sys.argv.index("--hardware-stability-test")
        duration = float(sys.argv[position + 1])
        output = Path(sys.argv[position + 2])
        install_endurance_test(sys.modules[__name__], app, win, duration, output)
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
