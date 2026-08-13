"""Report where the 1kHz clicks sit in a rendered file.

The source audio has a click starting exactly on every second boundary. Any constant
offset here is Resolve's own render-path delay (measure it on the clean control);
anything above that on a truncated pair is real misalignment.
"""
import wave, sys, struct, subprocess, os, tempfile

src = sys.argv[1]
tmp = os.path.join(tempfile.gettempdir(), "clicks_tmp.wav")
subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", src,
                "-vn", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", tmp], check=True)

w = wave.open(tmp, "rb")
n = w.getnframes()
rate = w.getframerate()
raw = w.readframes(n)
w.close()

samples = struct.unpack(f"<{len(raw) // 2}h", raw)
left = samples[0::2]

THRESH = 3000
onsets = []
i = 0
while i < len(left):
    if abs(left[i]) > THRESH:
        onsets.append(i)
        i += int(rate * 0.5)   # clicks are 1s apart; skip past this one
    else:
        i += 1

print(f"{os.path.basename(src)}: {n} samples ({n / rate:.4f}s), {len(onsets)} clicks")
print(f"{'click':>5} {'sample':>9} {'time_s':>10} {'vs_second':>11} {'frames@60':>10}")
for k, s in enumerate(onsets[:12]):
    t = s / rate
    dev = t - round(t)
    print(f"{k:>5} {s:>9} {t:>10.5f} {dev * 1000:>9.2f}ms {dev * 60:>10.3f}")
if len(onsets) > 1:
    first, last = onsets[0] / rate, onsets[-1] / rate
    span = len(onsets) - 1
    print(f"first click {first * 1000:.2f}ms, last click {last:.5f}s, "
          f"mean spacing {(last - first) / span:.6f}s (expected 1.000000)")
