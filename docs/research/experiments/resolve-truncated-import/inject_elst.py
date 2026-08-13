"""Inject an edts/elst into the initial moov of a fragmented MP4.

A fragmented file written by ffmpeg carries no edit list, so the only place a start
offset can live is tfdt. This builds the alternative ClipShift's own muxer could
write: an empty edit at the head of the track, present in the init segment and so
already on disk before any crash. Fragment offsets are relative to each moof, so
growing the moov by a few bytes shifts the file harmlessly.

usage: inject_elst.py <src> <dst> <offset_ms> <media_duration_movie_units|0>
"""
import struct, sys, os

src, dst, offset_ms, media_dur = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
data = bytearray(open(src, "rb").read())


def boxes(buf, start, end):
    pos = start
    while pos + 8 <= end:
        size, typ = struct.unpack_from(">I4s", buf, pos)
        if size < 8:
            break
        yield pos, size, typ.decode("latin1")
        pos += size


def find(buf, start, end, want):
    for pos, size, typ in boxes(buf, start, end):
        if typ == want:
            return pos, size
    raise KeyError(want + " not found")


moov_pos, moov_size = find(data, 0, len(data), "moov")
trak_pos, trak_size = find(data, moov_pos + 8, moov_pos + moov_size, "trak")
tkhd_pos, tkhd_size = find(data, trak_pos + 8, trak_pos + trak_size, "tkhd")

# elst: an empty edit (media_time -1) of offset_ms, then the media itself.
entries = [(offset_ms, -1), (media_dur, 0)]
elst_payload = struct.pack(">II", 0, len(entries))
for dur, mt in entries:
    elst_payload += struct.pack(">Iii", dur, mt, 0x00010000)
elst = struct.pack(">I4s", 8 + len(elst_payload), b"elst") + elst_payload
edts = struct.pack(">I4s", 8 + len(elst), b"edts") + elst

insert_at = tkhd_pos + tkhd_size
data[insert_at:insert_at] = edts
grow = len(edts)


def bump(pos):
    size = struct.unpack_from(">I", data, pos)[0]
    struct.pack_into(">I", data, pos, size + grow)


bump(trak_pos)
bump(moov_pos)

# ffmpeg's fragments carry an absolute base_data_offset in tfhd (flag 0x000001),
# so growing the moov invalidates every one of them. Slide them by the same amount.
# (A muxer that set default-base-is-moof instead would need none of this.)
patched = 0
for pos, size, typ in boxes(data, 0, len(data)):
    if typ != "moof":
        continue
    for ipos, isize, ityp in boxes(data, pos + 8, pos + size):
        if ityp != "traf":
            continue
        for tpos, tsize, ttyp in boxes(data, ipos + 8, ipos + isize):
            if ttyp != "tfhd":
                continue
            flags = struct.unpack_from(">I", data, tpos + 8)[0] & 0xFFFFFF
            if flags & 0x000001:
                base = struct.unpack_from(">Q", data, tpos + 16)[0]
                struct.pack_into(">Q", data, tpos + 16, base + grow)
                patched += 1
print(f"patched {patched} absolute base_data_offset values by +{grow}")

open(dst, "wb").write(data)
print(f"injected {grow}-byte edts at {insert_at}: empty edit {offset_ms}ms, "
      f"then media (duration {media_dur})")
print(f"{os.path.basename(dst)}: {len(data)} bytes")
