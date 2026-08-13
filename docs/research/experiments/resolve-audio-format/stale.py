"""Under-declare a WAV's size fields, modelling ClipShift's steady state.

ClipShift patches RIFF/ds64/data sizes on a 1 s cadence, so at any instant between
patches the header declares LESS than the file holds. The container research claims
that asymmetry is safe - a reader given a short size just stops early - while
over-declaring (FFmpeg's 0xFFFFFFFF placeholder, tested in #15) is the risky one.

This makes the under-declared case concretely, by rewriting the size fields to
describe one second less audio than is present. The sample bytes are untouched.

Usage: stale.py <src> <dst> [seconds_short]
"""
import struct
import sys


def read_chunks(buf):
    """Yield (fourcc, header_offset, payload_offset, declared_size) for top-level chunks."""
    pos = 12
    while pos + 8 <= len(buf):
        fourcc = bytes(buf[pos:pos + 4])
        size = struct.unpack_from("<I", buf, pos + 4)[0]
        yield fourcc, pos, pos + 8, size
        if size == 0xFFFFFFFF:
            break
        pos += 8 + size + (size & 1)


def main():
    src, dst = sys.argv[1], sys.argv[2]
    short_seconds = float(sys.argv[3]) if len(sys.argv) > 3 else 1.0

    with open(src, "rb") as f:
        buf = bytearray(f.read())

    riff = bytes(buf[0:4])
    if riff not in (b"RIFF", b"RF64"):
        raise SystemExit(f"not a RIFF/RF64 file: {riff!r}")

    chunks = {c[0]: c for c in read_chunks(buf)}
    if b"fmt " not in chunks or b"data" not in chunks:
        raise SystemExit("missing fmt or data chunk")

    _, _, fmt_at, _ = chunks[b"fmt "]
    channels = struct.unpack_from("<H", buf, fmt_at + 2)[0]
    rate = struct.unpack_from("<I", buf, fmt_at + 4)[0]
    bits = struct.unpack_from("<H", buf, fmt_at + 14)[0]
    block_align = channels * (bits // 8)

    _, data_hdr, data_at, declared = chunks[b"data"]
    actual = len(buf) - data_at
    if riff == b"RIFF" and declared not in (0xFFFFFFFF,):
        actual = min(actual, declared)

    drop = int(short_seconds * rate) * block_align
    new_data = max(0, (actual - drop) // block_align * block_align)
    new_riff = data_at + new_data - 8

    if riff == b"RIFF":
        struct.pack_into("<I", buf, 4, new_riff)
        struct.pack_into("<I", buf, data_hdr + 4, new_data)
    else:
        # RF64 keeps 0xFFFFFFFF in the RIFF/data size fields and puts the truth in ds64.
        if b"ds64" not in chunks:
            raise SystemExit("RF64 file with no ds64 chunk")
        _, _, ds64_at, _ = chunks[b"ds64"]
        struct.pack_into("<Q", buf, ds64_at, new_riff)              # riffSize
        struct.pack_into("<Q", buf, ds64_at + 8, new_data)          # dataSize
        struct.pack_into("<Q", buf, ds64_at + 16, new_data // block_align)  # sampleCount

    with open(dst, "wb") as f:
        f.write(buf)

    print(f"{src} -> {dst}")
    print(f"  {riff.decode()}, {rate} Hz, {channels} ch, {bits}-bit")
    print(f"  data present {actual} bytes, now declaring {new_data} "
          f"({(actual - new_data) / block_align / rate:.3f} s short)")


if __name__ == "__main__":
    main()
