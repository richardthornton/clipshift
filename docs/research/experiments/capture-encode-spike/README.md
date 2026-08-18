# Capture-to-encode spike

The instrument for [issue #14](https://github.com/richardthornton/clipshift/issues/14), built under
[issue #19](https://github.com/richardthornton/clipshift/issues/19). It captures one display, converts
BGRA→NV12 on the GPU, feeds NVENC directly, and writes a **raw H.264 elementary stream**. No muxer, no
UI, no audio, no config. It is throwaway: a measurement instrument, not the beginnings of the app.

Reference machine: RTX 5060 Ti (Blackwell), Ryzen 7 9800X3D, Windows 11 Pro 26200, .NET 8.0.419,
three 1920×1080 SDR displays all enumerated on the NVIDIA adapter.

## Running it

```
dotnet build -c Release
bin\Release\net8.0-windows10.0.22621.0\ClipShiftSpike.exe --list
bin\Release\net8.0-windows10.0.22621.0\ClipShiftSpike.exe --display 0 --seconds 20 --out spike.h264
```

Variant flags, which are the point of the spike:

| Flag | Meaning |
|---|---|
| `--hold` / `--release` | DDA frame ownership: defer `ReleaseFrame` until just before the next acquire, or release as soon as the pixels are out (OBS's behaviour). Default `--hold`. |
| `--srv-direct` / `--copy` | Convert from an SRV on the captured surface, or from a full-frame `CopyResource` of it first. Default `--srv-direct`. |
| `--wgc` | Use Windows.Graphics.Capture instead of DDA — the comparison arm. `--hold`/`--release` do not apply. |
| `--preset N` | NVENC preset p4…p7. Default 5, per #10. |
| `--qp N`, `--gop N` | CONSTQP value and keyframe interval. Defaults 20 and 60, per #10. |
| `--probe-composited-ui` | Probe `DXGI_OUTDUPL_COMPOSITED_UI_CAPTURE_ONLY` (§11.9). |
| `--diag`, `--wgc-slot N` | Debug aids kept from bring-up; see *What bring-up cost* below. |

`motion.ps1` puts a continuously repainting window on display 0. Without it the desktop is static, DDA
delivers nothing, and the spike records black — correctly, but uninformatively.

Play the output with `ffplay spike.h264`, or wrap it: `ffmpeg -r 60 -i spike.h264 -c copy spike.mp4`.

## What it self-reports

Everything #19 asked for, printed at the end of a run:

- **The seven counters of [#12](https://github.com/richardthornton/clipshift/issues/12)**, kept
  distinct — `duplicated_idle`, `duplicated_lagged`, `duplicated_backpressure`,
  `duplicated_recovery`, `black_lead_in`, `superseded`, `capture_missed`.
- **Pacing**, split into two distributions that were originally conflated: *wake lateness* (how late
  the grid started the tick) and *tick work* (what the tick then cost). Only their sum exceeding one
  frame interval loses a frame, and mixing them hides which half is at fault.
- **Managed allocation on the hot path**, measured from frame 60 so that lazily-created one-time
  buffers do not read as a per-frame cost.
- **Zero-copy confirmation**, as properties rather than inference.

## Verified output

`ffprobe` on a 12-second DDA run, against what [#10](https://github.com/richardthornton/clipshift/issues/10) locked:

| | Locked by #10 | Measured |
|---|---|---|
| Codec / profile | H.264 High | `h264` / `High` ✔ |
| Chroma / depth | 4:2:0 8-bit | `yuv420p` ✔ |
| B-frames | zero | `has_b_frames=0` ✔ |
| Colour | limited range BT.709 in the SPS VUI | `color_range=tv`, `color_space`/`color_primaries`/`color_transfer` all `bt709` ✔ |
| Keyframes | 1 second | `IPPP…` in runs of 60 ✔ |

`ffmpeg -i spike.h264 -f null -` decodes the whole file with no errors, and an extracted frame is
pixel-correct: right colours, right geometry, no chroma swap, no plane misalignment.

## Findings

Measured on the reference machine. None of these is a decision — #19 is a `task` ticket and the
decisions belong to #14 and to the tickets that own each area.

### 1. Managed allocation: DDA is zero per frame, WGC is ~2.7 KB per frame

Six-second runs, same display, same motion, measured from frame 60:

| Arm | Steady-state allocation | Gen0/1/2 collections |
|---|---|---|
| DDA (hold, SRV-direct) | **0 bytes over 301 frames — 0.00 B/frame** | 0 / 0 / 0 |
| WGC | **802,016 bytes over 301 frames — 2664 B/frame** | 0 / 0 / 0 |

This is a direct answer to **§11.6** of `display-capture-api.md` ("whether the CsWinRT projection
allocates a fresh RCW per `TryGetNextFrame`"): it does, at roughly 2.7 KB per delivered frame. Over a
four-hour session at 60 fps that is ~2.3 GB of garbage the DDA path never creates. The WGC arm uses
the projection deliberately — hand-rolling the ABI would have answered a question nobody asked.

Note the DDA figure is exactly zero, not nearly zero, which is the property the stack decision needs.

### 2. WGC's `CreateForMonitor` returns an "All displays" item, whichever monitor you pass

`IGraphicsCaptureItemInterop::CreateForMonitor` was given each of the three displays' `HMONITOR`s in
turn — values cross-checked against `MonitorFromPoint`, all three distinct and in agreement — and
every call returned an item with `DisplayName == "All displays"` sized **3840×2168**, which is exactly
the bounding box of the three displays at their virtual-desktop coordinates. An extracted frame
confirms it: all three desktops, tiled.

The vtable slot is not the explanation. Slot 3 rejects an `HMONITOR` with `E_INVALIDARG` (it is
`CreateForWindow`), and slot 4 is the one that succeeds, which is the header's declaration order.

**Consequence for #14:** the WGC arm currently encodes 3840×2168 while the DDA arm encodes 1920×1080.
They are not comparable until this is settled, and the spike now prints a warning when the two sizes
disagree rather than letting the comparison look valid. Unresolved: whether this is specific to three
displays on one adapter, to this Windows build, or to `CreateForMonitor` generally.

### 3. WGC's capture border *was* suppressible here — worth revisiting against #2

[Issue #2](https://github.com/richardthornton/clipshift/issues/2) rejected WGC partly because "WGC's
capture border is the decisive problem". On this machine, in an **unpackaged** .NET process:
`GraphicsCaptureSession.IsBorderRequired` is present, setting it to `false` was accepted, it read back
`false`, and **no border appears in the captured pixels** (checked on an extracted frame's edges).

Stated narrowly on purpose: one machine, one Windows build, an unpackaged process, a whole-desktop
item. It does not overturn #2 — DDA still wins on §1 above and on the per-frame allocation constraint —
but the specific claim that the border cannot be suppressed did not reproduce, and #14 should carry
that rather than repeat it.

`IsCursorCaptureEnabled` is settable in both directions (the spike sets it back to enabled, since the
cursor is recorded per the standing constraints).

### 4. `DuplicateOutput1` returns `DXGI_ERROR_UNSUPPORTED` here; the spike falls back to `DuplicateOutput`

Tried first with a BGRA-only format list, then with the three-entry list Microsoft's own sample passes
(`R8G8B8A8_UNORM`, `B8G8R8A8_UNORM`, `R16G16B16A16_FLOAT`). Both return `0x887A0004`
`DXGI_ERROR_UNSUPPORTED`, on all displays. `IDXGIOutput5` itself QIs successfully; it is the call that
fails. `DuplicateOutput` then succeeds immediately.

This matters because §3 of `display-capture-api.md` recommends `DuplicateOutput1` specifically to avoid
a format conversion penalty on fullscreen apps presenting in a non-BGRA format. That option is not
available on this machine, and the reason is not established.

### 5. The `DXGI_OUTDUPL_COMPOSITED_UI_CAPTURE_ONLY` probe is inconclusive, not negative

The §11.9 probe returns `DXGI_ERROR_UNSUPPORTED` — but so does `DuplicateOutput1` with `flags = 0`
(finding 4). The probe therefore says nothing about the flag: it only re-reports that
`DuplicateOutput1` is unavailable here. Recorded as inconclusive rather than as an answer.

### 6. The `LastPresentTime` probe as built cannot answer §11.2, and here is why

The spike measures `now − LastPresentTime` at each acquire. Across runs the mean moved from 1.8 ms to
14.2 ms and the spread from 5.4 ms to 13.6 ms with load. That looks like a varying anchor — the thing
§11.2 warns would break the sync design — but it is not evidence of one, because **`now` is when the
spike happened to poll**, so the statistic is dominated by polling phase rather than by the anchor.

Answering §11.2 properly needs a known-cadence source (a fullscreen app presenting on a fixed vblank
schedule) and a comparison of `LastPresentTime` deltas against that cadence, not against poll time.
The probe is left in because the plumbing is right and the numbers are cheap; the interpretation is
what is wrong, and stating that is more use than a confident wrong reading.

### 7. NVENC in synchronous mode: `doNotWait` returns `OUT_OF_MEMORY`, not `LOCK_BUSY`

`NvEncLockBitstream` with `doNotWait = 1` returns `NV_ENC_ERR_OUT_OF_MEMORY` on this driver
(NVENC API 13.1). The header documents synchronous mode as *possibly* returning `NV_ENC_ERR_LOCK_BUSY`
there; `OUT_OF_MEMORY` is not a documented outcome. The spike therefore drains synchronously —
submit, then block on the lock.

**Consequence:** the in-flight ring never fills, so `duplicated_backpressure` cannot be exercised. The
counter and the count-and-continue rule from #12 are implemented and correct, but they are untested by
this build. Exercising them needs asynchronous mode (`enableEncodeAsync = 1` plus registered
completion events), which is a bounded piece of work if #14 wants backpressure numbers.

The visible cost of synchronous mode is in *tick work*, which is the encode latency: p4 ≈ 2.4 ms mean,
p5/p6 ≈ 4.5 ms, p7 ≈ 4.7 ms at 1080p60. Wake lateness is unaffected — p50 1.8 µs, p99 58 µs — so the
high-resolution waitable timer of #12 does its job without `timeBeginPeriod`.

### 8. `superseded` is structurally zero on a 60 Hz display

Expected, and worth writing down so it is not read as a broken counter: at 60 Hz the desktop never
delivers two images inside one 60.000 fps tick. The counter earns its place only above 60 Hz.

## What bring-up cost, and the one technique worth keeping

The NVENC structs are transcribed from `nv-codec-headers`' `nvEncodeAPI.h` at SDK 13.1. Because no C
compiler was available to check them, the layouts were computed from the header and asserted at
startup — `NvEncStructs.AssertLayout()` checks fifteen struct sizes and five field offsets.

**It caught a real bug on the first run**: `NV_ENC_PIC_PARAMS.codecPicParams` landed at offset 76
instead of 80, because a C# `fixed byte` buffer has alignment 1 while the union it stands in for is
8-aligned. The total struct size was still correct, so nothing else would have noticed — the driver
would simply have read every codec-specific field four bytes out. That is the failure mode this
interop actually has: not a crash, but a plausible-looking recording that is subtly wrong.

The second bug the assertions *reported* was not a bug: the driver leaves `rcParams.version` zero on
the way out of `NvEncGetEncodePresetConfigEx` (it is an `[in]` field), so checking it read as layout
drift when the layout was fine. The check now uses the config's own `version` plus `gopLength` and
`frameIntervalP`, which sit past the 16-byte `profileGUID` and so actually catch a shift.

`--diag` prints the preset config the driver returns; `--wgc-slot N` selects the interop vtable slot,
kept from the investigation in finding 2.
