import struct, sys

path = sys.argv[1]

def parse(f, start, end, depth, counts, want):
    pos = start
    while pos < end:
        f.seek(pos)
        hdr = f.read(8)
        if len(hdr) < 8:
            print("  " * depth + f"[TRUNCATED header at {pos}, {len(hdr)} bytes left]")
            return
        size, typ = struct.unpack(">I4s", hdr)
        typ = typ.decode("latin1")
        hdrsize = 8
        if size == 1:
            size = struct.unpack(">Q", f.read(8))[0]
            hdrsize = 16
        elif size == 0:
            size = end - pos
        counts[typ] = counts.get(typ, 0) + 1
        truncated = pos + size > end
        if depth < 4 and (counts[typ] <= 2 or truncated):
            note = f"  <-- TRUNCATED (declares {size}, only {end - pos} bytes remain)" if truncated else ""
            print("  " * depth + f"@{pos:<12} {typ}  size={size}{note}")
        if typ == "elst":
            f.seek(pos + hdrsize)
            ver_flags = struct.unpack(">I", f.read(4))[0]
            ver = ver_flags >> 24
            n = struct.unpack(">I", f.read(4))[0]
            print("  " * (depth + 1) + f"version={ver} entry_count={n}")
            for i in range(n):
                if ver == 1:
                    dur, mt = struct.unpack(">Qq", f.read(16))
                else:
                    dur, mt = struct.unpack(">Ii", f.read(8))
                rate = struct.unpack(">i", f.read(4))[0]
                print("  " * (depth + 1) + f"  entry {i}: segment_duration={dur} media_time={mt} rate={rate / 65536:.2f}")
        if typ == "mvhd":
            f.seek(pos + hdrsize + 4)
            ct, mt, ts, dur = struct.unpack(">IIII", f.read(16))
            print("  " * (depth + 1) + f"timescale={ts} duration={dur}")
        if typ == "tkhd":
            f.seek(pos + hdrsize + 4)
            ct, mt, tid, res, dur = struct.unpack(">IIIII", f.read(20))
            print("  " * (depth + 1) + f"track_id={tid} duration={dur}")
        if typ == "mdhd":
            f.seek(pos + hdrsize + 4)
            ct, mt, ts, dur = struct.unpack(">IIII", f.read(16))
            print("  " * (depth + 1) + f"timescale={ts} duration={dur}")
        if typ == "colr":
            f.seek(pos + hdrsize)
            kind = f.read(4).decode("latin1")
            p, t, m = struct.unpack(">HHH", f.read(6))
            rng = f.read(1)[0] >> 7 if kind == "nclx" else None
            print("  " * (depth + 1) + f"type={kind} primaries={p} transfer={t} matrix={m} full_range={rng}")
        if typ in ("moov", "trak", "mdia", "minf", "stbl", "edts", "mvex", "moof", "traf", "dinf") and not truncated:
            parse(f, pos + hdrsize, pos + size, depth + 1, counts, want)
        if typ == "stsd" and not truncated:
            parse(f, pos + hdrsize + 8, pos + size, depth + 1, counts, want)
        if typ in ("avc1", "hev1") and not truncated:
            parse(f, pos + hdrsize + 78, pos + size, depth + 1, counts, want)
        if truncated:
            return
        pos += size

import os
size = os.path.getsize(path)
counts = {}
print(f"=== {path}  ({size} bytes) ===")
with open(path, "rb") as f:
    parse(f, 0, size, 0, counts, None)
print("\nbox counts:", {k: v for k, v in sorted(counts.items()) if v > 1 or k in ("mfra", "moov", "mvex", "elst", "sidx")})
for b in ("mfra", "sidx", "elst", "mvex"):
    print(f"  {b}: {'present' if b in counts else 'ABSENT'}")
