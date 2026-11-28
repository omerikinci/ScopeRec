"""Inspect the actual PE import table; this is a packaging check, not an app dependency."""
import argparse
import json
from pathlib import Path
import struct
import zipfile


def inspect(data, name):
    if data[:2] != b"MZ":
        raise ValueError(f"{name}: missing executable header")
    pe = struct.unpack_from("<I", data, 60)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError(f"{name}: missing PE signature")
    machine, count = struct.unpack_from("<HH", data, pe + 4)
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    if machine != 0x8664 or struct.unpack_from("<H", data, optional)[0] != 0x20B:
        raise ValueError(f"{name}: expected native Windows x64 executable")
    sections = []
    for i in range(count):
        pos = optional + optional_size + i * 40
        label = data[pos:pos + 8].rstrip(b"\0").decode("ascii")
        size, rva, raw_size, raw = struct.unpack_from("<IIII", data, pos + 8)
        sections.append((label, rva, max(size, raw_size), raw))

    def physical(rva):
        for _, base, size, raw in sections:
            if base <= rva < base + size:
                return raw + rva - base
        raise ValueError(f"{name}: invalid section address {rva}")

    imports = []
    import_rva = struct.unpack_from("<I", data, optional + 120)[0]
    if import_rva:
        pos = physical(import_rva)
        while any(data[pos:pos + 20]):
            dll = physical(struct.unpack_from("<I", data, pos + 12)[0])
            imports.append(data[dll:data.index(b"\0", dll)].decode("ascii"))
            pos += 20
    forbidden = [dll for dll in imports if dll.lower().startswith(("vcruntime", "msvcp", "libusb"))]
    if forbidden:
        raise ValueError(f"{name}: external runtime dependency: {', '.join(forbidden)}")
    subsystem = struct.unpack_from("<H", data, optional + 68)[0]
    if subsystem != (2 if name == "scoperec-rust.exe" else 3):
        raise ValueError(f"{name}: incorrect GUI/console subsystem")
    if ".rsrc" not in [s[0] for s in sections]:
        raise ValueError(f"{name}: application icon resource missing")
    return {"architecture": "Windows x64", "subsystem": subsystem,
            "icon_resource": True, "imports": imports}


def verify(path):
    names = ["scoperec-rust.exe", "scoperec-rust-cli.exe", "scoperec-simulator.exe"]
    if path.suffix.lower() == ".zip":
        with zipfile.ZipFile(path) as archive:
            binaries = {name: inspect(archive.read(name), name) for name in names}
    else:
        binaries = {name: inspect((path / name).read_bytes(), name) for name in names}
    return {"status": "PASS", "separate_vc_runtime_required": False, "binaries": binaries}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = verify(args.path)
    output = json.dumps(result, indent=2)
    if args.report:
        args.report.write_text(output, encoding="utf-8")
    print(output)
