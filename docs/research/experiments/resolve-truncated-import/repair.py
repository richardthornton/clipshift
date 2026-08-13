"""Drop the trailing partial fragment from a killed fragmented MP4.

This is the minimum recovery step: walk the top-level boxes, keep everything up to
the end of the last COMPLETE moof+mdat pair, discard the rest. No rewrite, no remux
- a file copy and a truncate.
"""
import struct, shutil, sys, os

src, dst = sys.argv[1], sys.argv[2]
size = os.path.getsize(src)

good_end = 0          # end of the last complete moof+mdat pair
pending_moof = None   # a moof whose mdat we have not yet confirmed complete
pos = 0
frag = 0

with open(src, "rb") as f:
    while pos < size:
        f.seek(pos)
        hdr = f.read(8)
        if len(hdr) < 8:
            print(f"  header truncated at {pos}")
            break
        bsize, typ = struct.unpack(">I4s", hdr)
        typ = typ.decode("latin1")
        if bsize == 1:
            bsize = struct.unpack(">Q", f.read(8))[0]
        if bsize < 8:
            print(f"  bogus box size {bsize} at {pos}")
            break
        if pos + bsize > size:
            print(f"  {typ} at {pos} declares {bsize}, only {size - pos} bytes remain -> discarding")
            break
        if typ == "moof":
            pending_moof = pos
        elif typ == "mdat" and pending_moof is not None:
            good_end = pos + bsize
            pending_moof = None
            frag += 1
        elif typ in ("ftyp", "moov"):
            good_end = pos + bsize
        pos += bsize

shutil.copyfile(src, dst)
with open(dst, "r+b") as f:
    f.truncate(good_end)

print(f"{os.path.basename(src)}: {size} bytes -> {good_end} bytes "
      f"({size - good_end} discarded), {frag} complete fragments kept")
