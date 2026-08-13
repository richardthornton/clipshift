"""Patch artifacts the way ClipShift's recovery path would.

tfdt mode: add a start offset to every fragment's baseMediaDecodeTime, which is how a
fragmented MP4 carries a start offset (there is no elst while fragmented).
wav mode: patch the RIFF and data sizes of a WAV whose writer was killed.
"""
import struct, shutil, sys, os

mode = sys.argv[1]

if mode == "tfdt":
    src, dst, delta = sys.argv[2], sys.argv[3], int(sys.argv[4])
    shutil.copyfile(src, dst)
    size = os.path.getsize(dst)
    patched = 0
    with open(dst, "r+b") as f:
        pos = 0
        while pos < size:
            f.seek(pos)
            hdr = f.read(8)
            if len(hdr) < 8:
                break
            bsize, typ = struct.unpack(">I4s", hdr)
            typ = typ.decode("latin1")
            if bsize < 8 or pos + bsize > size:
                break
            if typ == "moof":
                # walk into moof -> traf -> tfdt
                inner = pos + 8
                while inner < pos + bsize:
                    f.seek(inner)
                    isize, ityp = struct.unpack(">I4s", f.read(8))
                    ityp = ityp.decode("latin1")
                    if ityp == "traf":
                        t = inner + 8
                        while t < inner + isize:
                            f.seek(t)
                            tsize, ttyp = struct.unpack(">I4s", f.read(8))
                            ttyp = ttyp.decode("latin1")
                            if ttyp == "tfdt":
                                f.seek(t + 8)
                                ver = struct.unpack(">I", f.read(4))[0] >> 24
                                if ver == 1:
                                    base = struct.unpack(">Q", f.read(8))[0]
                                    f.seek(t + 12)
                                    f.write(struct.pack(">Q", base + delta))
                                else:
                                    base = struct.unpack(">I", f.read(4))[0]
                                    f.seek(t + 12)
                                    f.write(struct.pack(">I", base + delta))
                                patched += 1
                                if patched <= 2:
                                    print(f"  tfdt v{ver} @{t}: {base} -> {base + delta}")
                            t += tsize
                    inner += isize
            pos += bsize
    print(f"patched {patched} tfdt boxes by +{delta}")

elif mode == "wav":
    src, dst = sys.argv[2], sys.argv[3]
    shutil.copyfile(src, dst)
    size = os.path.getsize(dst)
    with open(dst, "r+b") as f:
        assert f.read(4) == b"RIFF"
        riff_declared = struct.unpack("<I", f.read(4))[0]
        assert f.read(4) == b"WAVE"
        pos = 12
        while pos < size - 8:
            f.seek(pos)
            cid = f.read(4)
            csize = struct.unpack("<I", f.read(4))[0]
            print(f"  chunk {cid.decode('latin1')} @{pos} declared size={csize}"
                  f"{'  <-- runs past EOF' if pos + 8 + csize > size else ''}")
            if cid == b"data":
                actual = size - (pos + 8)
                # ClipShift's recovery: truncate to a whole frame, then patch both sizes.
                frame = 4  # 2 channels * 16-bit
                actual -= actual % frame
                f.seek(pos + 4)
                f.write(struct.pack("<I", actual))
                f.seek(4)
                f.write(struct.pack("<I", (pos + 8 + actual) - 8))
                print(f"  patched data size -> {actual}, RIFF size -> {pos + actual}")
                print(f"  samples={actual // frame} duration={actual / frame / 48000:.6f}s")
                break
            pos += 8 + csize + (csize & 1)
    print(f"RIFF size was 0x{riff_declared:08x}")
