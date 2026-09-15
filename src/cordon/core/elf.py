"""Minimal binary inspector.

Upstream CORDON reads the PE headers of ``xr_3da.exe`` to decide whether a profile needs the
x86 or the x64 backend.  On Linux the same question ("is this a native engine, and does its
architecture match my system?") is answered by reading the ELF header, plus a quick check
for Windows PE files so we can explain Proton/wine situations instead of failing later.
"""

from __future__ import annotations

import os
import platform
import struct
from dataclasses import dataclass

ELF_CLASSES = {1: 32, 2: 64}
ELF_MACHINES = {
    0x03: "i386",
    0x08: "mips",
    0x14: "ppc",
    0x16: "s390",
    0x28: "arm",
    0x2A: "sh",
    0x32: "ia64",
    0x3E: "x86_64",
    0x8C: "riscv32",
    0xB7: "aarch64",
    0xF3: "riscv64",
    0x9026: "alpha",
}
ELF_TYPES = {1: "rel", 2: "exec", 3: "pie", 4: "core"}

HOST_MACHINE_ALIASES = {
    "amd64": "x86_64",
    "x86_64": "x86_64",
    "i686": "i386",
    "i586": "i386",
    "i386": "i386",
    "armv7l": "arm",
    "armv8l": "arm",
    "aarch64": "aarch64",
    "arm64": "aarch64",
}


@dataclass(slots=True)
class BinaryInfo:
    path: str
    kind: str = "unknown"  # elf | pe | script | unknown
    bits: int = 0
    machine: str = ""
    elf_type: str = ""
    interpreter: str = ""
    description: str = ""

    @property
    def is_native_elf(self) -> bool:
        return self.kind == "elf"

    @property
    def is_windows_binary(self) -> bool:
        return self.kind == "pe"

    def summary(self) -> str:
        if self.kind == "elf":
            return f"ELF {self.bits}-бит, {self.machine}, {self.elf_type}"
        if self.kind == "pe":
            return f"Windows PE ({self.bits}-бит)"
        if self.kind == "script":
            return "скрипт"
        return "неизвестный формат"


def host_machine() -> str:
    return HOST_MACHINE_ALIASES.get(platform.machine().lower(), platform.machine().lower())


def host_bits() -> int:
    return struct.calcsize("P") * 8


def inspect(path: str) -> BinaryInfo:
    """Identify an executable file by its magic bytes."""
    info = BinaryInfo(path=os.path.abspath(path))
    try:
        with open(info.path, "rb") as handle:
            head = handle.read(64)
    except OSError as exc:
        info.description = f"не читается: {exc}"
        return info

    if head[:4] == b"\x7fELF":
        return _inspect_elf(info, head)
    if head[:2] == b"MZ":
        return _inspect_pe(info, head)
    if head[:2] == b"#!":
        info.kind = "script"
        info.description = head.split(b"\n", 1)[0].decode("utf-8", "replace")
        return info
    info.description = "не ELF и не PE"
    return info


def _inspect_elf(info: BinaryInfo, head: bytes) -> BinaryInfo:
    info.kind = "elf"
    info.bits = ELF_CLASSES.get(head[4], 0)
    little = head[5] == 1
    endian = "<" if little else ">"
    try:
        info.elf_type = ELF_TYPES.get(struct.unpack_from(endian + "H", head, 16)[0], "?")
        machine = struct.unpack_from(endian + "H", head, 18)[0]
    except struct.error:  # pragma: no cover
        return info
    info.machine = ELF_MACHINES.get(machine, f"0x{machine:04x}")
    if info.bits == 32:
        # 32-bit ELF header: e_phoff at 28, phentsize 42, phnum 44
        phoff, phentsize, phnum = struct.unpack_from(endian + "III", head, 28) if len(head) >= 40 else (0, 0, 0)
    else:
        phoff, phentsize, phnum = struct.unpack_from(endian + "QHH", head, 32) if len(head) >= 48 else (0, 0, 0)
    if phnum and phentsize and phoff < (1 << 32):
        try:
            with open(info.path, "rb") as handle:
                handle.seek(phoff)
                for _ in range(phnum):
                    entry = handle.read(phentsize)
                    if len(entry) < phentsize:
                        break
                    p_type = struct.unpack_from(endian + "I", entry, 0)[0]
                    if p_type == 3:  # PT_INTERP
                        offset = struct.unpack_from(endian + "Q", entry, 8)[0] if info.bits == 64 \
                            else struct.unpack_from(endian + "I", entry, 4)[0]
                        size = struct.unpack_from(endian + "Q", entry, 32)[0] if info.bits == 64 \
                            else struct.unpack_from(endian + "I", entry, 16)[0]
                        handle.seek(offset)
                        info.interpreter = handle.read(size).rstrip(b"\0").decode("utf-8", "replace")
                        break
        except OSError:  # pragma: no cover
            pass
    return info


def _inspect_pe(info: BinaryInfo, head: bytes) -> BinaryInfo:
    info.kind = "pe"
    try:
        with open(info.path, "rb") as handle:
            handle.seek(0x3C)
            offset = struct.unpack("<I", handle.read(4))[0]
            handle.seek(offset)
            signature = handle.read(4)
            if signature != b"PE\0\0":
                return info
            machine = struct.unpack("<H", handle.read(2))[0]
    except (OSError, struct.error):  # pragma: no cover
        return info
    info.machine = {0x014C: "i386", 0x8664: "x86_64", 0xAA64: "aarch64"}.get(machine, f"0x{machine:04x}")
    info.bits = 32 if machine == 0x014C else 64
    return info


def is_executable_file(path: str) -> bool:
    return os.path.isfile(path) and os.access(path, os.X_OK)


def arch_matches_host(info: BinaryInfo) -> bool:
    """Whether this binary can run on the current machine (best effort)."""
    if info.kind != "elf":
        return False
    host = host_machine()
    if info.machine == host:
        return True
    if host in ("x86_64", "aarch64") and info.machine in ("i386", "arm"):
        return True  # multilib / 32-bit compatibility, validated by the preflight checks
    return False
