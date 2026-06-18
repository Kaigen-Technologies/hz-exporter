"""
Shared utilities for reading and validating hz-engine-v5 .hza binary assets.

v5 changes from v3:
  - BlobPtr.offset is i32, self-relative (data = &offset + offset)
  - StringBlob layout: { i32 offset; u32 len } (swapped from v3)

Usage from other scripts:
    from hza import HzaReader
    r = HzaReader("path/to/file.hza")
    r.dump_header()
"""
import struct
import sys

ASSET_MAGIC = 0x54455353414800
ASSET_VERSION = 3


def fnv1a(s: str) -> int:
    h = 0x811c9dc5
    for b in s.encode("utf-8"):
        h ^= b
        h = (h * 0x01000193) & 0xFFFFFFFF
    return h


def type_hash(name: str, size: int) -> int:
    """TYPE_HASH(T) = fnv1a(name) ^ sizeof(T). Used in BlobPtr.typehash."""
    return fnv1a(name) ^ size


def asset_type(name: str) -> int:
    """ASSET_TYPE(T) = fnv1a(name). Used in BlobAssetHeader.asset_type_hash."""
    return fnv1a(name)


class BlobPtr:
    """Parsed BlobPtr / BlobArray(T). Offset is self-relative in v5."""
    __slots__ = ("offset", "size", "type_size", "typehash", "field_pos")

    def __init__(self, offset: int, size: int, type_size: int, typehash: int, field_pos: int):
        self.offset = offset
        self.size = size
        self.type_size = type_size
        self.typehash = typehash
        self.field_pos = field_pos  # file position of this BlobPtr

    @property
    def data_pos(self) -> int:
        """Absolute file position of the data this BlobPtr points to."""
        return self.field_pos + self.offset

    @property
    def count(self) -> int:
        if self.type_size == 0:
            return 0
        return self.size // self.type_size

    @property
    def empty(self) -> bool:
        return self.size == 0

    def validate(self, expected_type_name: str, expected_type_size: int) -> list[str]:
        errors = []
        if self.type_size != expected_type_size:
            errors.append(f"type_size: got {self.type_size}, expected {expected_type_size}")
        expected = type_hash(expected_type_name, expected_type_size)
        if self.typehash != expected:
            errors.append(
                f"typehash: got 0x{self.typehash:08x}, expected 0x{expected:08x} "
                f"(TYPE_HASH(\"{expected_type_name}\"))"
            )
        if self.size > 0 and self.type_size > 0 and self.size % self.type_size != 0:
            errors.append(f"size {self.size} not divisible by type_size {self.type_size}")
        return errors

    def __repr__(self):
        return (f"BlobPtr(offset={self.offset}, size={self.size}, "
                f"type_size={self.type_size}, typehash=0x{self.typehash:08x}, "
                f"count={self.count}, data_pos={self.data_pos})")


class HzaReader:
    """Reads and validates an .hza binary asset file (v5 format)."""

    def __init__(self, path: str):
        with open(path, "rb") as f:
            self.data = f.read()
        self.path = path
        self.size = len(self.data)
        self.errors: list[str] = []
        self._header_parsed = False
        self.magic = 0
        self.asset_size = 0
        self.asset_type_hash = 0
        self.version = 0
        self.deps_bp = None
        self._parse_header()

    # --- Low-level readers ---

    def read(self, offset: int, fmt: str):
        """Unpack little-endian values at offset. Returns tuple."""
        return struct.unpack_from("<" + fmt, self.data, offset)

    def read_u32(self, offset: int) -> int:
        return self.read(offset, "I")[0]

    def read_i32(self, offset: int) -> int:
        return self.read(offset, "i")[0]

    def read_f32(self, offset: int) -> float:
        return self.read(offset, "f")[0]

    def read_u64(self, offset: int) -> int:
        return self.read(offset, "Q")[0]

    def read_vec3(self, offset: int) -> tuple[float, float, float]:
        return self.read(offset, "3f")

    def read_vec4(self, offset: int) -> tuple[float, float, float, float]:
        return self.read(offset, "4f")

    def read_mat4(self, offset: int) -> tuple:
        return self.read(offset, "16f")

    def read_blob_ptr(self, offset: int) -> BlobPtr:
        """Read a BlobPtr at file offset. v5: { i32 offset, u32 size, u32 type_size, u32 typehash }"""
        off, size, ts, th = self.read(offset, "iIII")
        return BlobPtr(off, size, ts, th, field_pos=offset)

    def read_string_blob(self, sb_offset: int) -> str:
        """Read a StringBlob at sb_offset. v5 layout: { i32 offset; u32 len }, self-relative."""
        soff, slen = self.read(sb_offset, "iI")
        if slen == 0 and soff == 0:
            return ""
        abs_off = sb_offset + soff  # self-relative
        return self.data[abs_off:abs_off + slen].decode("utf-8", errors="replace")

    def read_blob_asset_ref(self, offset: int) -> tuple[str, int, int]:
        """Read a BlobAssetRef (24 bytes): { StringBlob path(8), u32 type_size(4), u32 typehash(4), HzHandle(8) }.
        Returns (path_str, type_size, typehash)."""
        path = self.read_string_blob(offset)
        ts, th = self.read(offset + 8, "II")
        # handle at offset+16 is 8 bytes, ignored (zero on disk)
        return path, ts, th

    def read_floats(self, offset: int, count: int) -> list[float]:
        return list(struct.unpack_from(f"<{count}f", self.data, offset))

    def read_u16_array(self, offset: int, count: int) -> list[int]:
        return list(struct.unpack_from(f"<{count}H", self.data, offset))

    def read_u32_array(self, offset: int, count: int) -> list[int]:
        return list(struct.unpack_from(f"<{count}I", self.data, offset))

    # --- Validation helpers ---

    def error(self, msg: str):
        self.errors.append(msg)

    def check(self, condition: bool, msg: str) -> bool:
        if not condition:
            self.error(msg)
        return condition

    def check_blob_ptr(self, bp: BlobPtr, type_name: str, type_size: int, label: str = "") -> list[str]:
        """Validate a BlobPtr and record any errors. Returns the error list."""
        errs = bp.validate(type_name, type_size)
        prefix = f"{label}: " if label else ""
        for e in errs:
            self.error(f"{prefix}{e}")
        return errs

    # --- Header ---

    def _parse_header(self):
        self.magic, self.asset_size, self.asset_type_hash, self.version, _ = \
            self.read(0, "QQQIi")
        self.deps_bp = self.read_blob_ptr(32)
        self._header_parsed = True

    def validate_header(self, expected_type_name: str | None = None):
        self.check(self.magic == ASSET_MAGIC,
                   f"magic: 0x{self.magic:014x} (expected 0x{ASSET_MAGIC:014x})")
        self.check(self.asset_size == self.size,
                   f"asset_size: {self.asset_size} (file is {self.size} bytes)")
        self.check(self.version == ASSET_VERSION,
                   f"version: {self.version} (expected {ASSET_VERSION})")
        if expected_type_name:
            expected = asset_type(expected_type_name)
            self.check(self.asset_type_hash == expected,
                       f"asset_type_hash: 0x{self.asset_type_hash:08x} "
                       f"(expected fnv1a(\"{expected_type_name}\") = 0x{expected:08x})")
        self.check_blob_ptr(self.deps_bp, "StringBlob", 8, "dependencies")

    def dump_header(self):
        ok = lambda c: "OK" if c else "MISMATCH"
        print(f"File: {self.path}")
        print(f"Size: {self.size} bytes")
        print()
        print("=== BlobAssetHeader ===")
        print(f"  magic:           0x{self.magic:014x} {ok(self.magic == ASSET_MAGIC)}")
        print(f"  asset_size:      {self.asset_size} {ok(self.asset_size == self.size)}")
        print(f"  asset_type_hash: 0x{self.asset_type_hash:08x}")
        print(f"  version:         {self.version} {ok(self.version == ASSET_VERSION)}")
        print(f"  dependencies:    {self.deps_bp}")

    # --- Result ---

    def print_result(self):
        print(f"\n{'=' * 40}")
        if self.errors:
            print(f"VALIDATION FAILED - {len(self.errors)} error(s):")
            for e in self.errors:
                print(f"  - {e}")
        else:
            print("VALIDATION PASSED")

    @property
    def ok(self) -> bool:
        return len(self.errors) == 0
