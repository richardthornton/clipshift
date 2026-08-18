# ClipShift — session handoff

**Last updated:** 18 August 2026, end of the session that locked mid-recording failure behaviour.

## What ClipShift is

A minimal, beautiful Windows app that records **one display** to a video-only file, plus **up to two optional audio files** (system loopback and audio input), all perfectly synced. Global toggle hotkey, tray icon, on-screen indicator that never appears in the recording. Performance is the point — this records multi-hour gameplay sessions, while OBS is streaming.

## Where the planning lives

**[Map: ClipShift MVP — spec and architecture](https://github.com/richardthornton/clipshift/issues/1)** — GitHub issue #1, labelled `wayfinder:map`. Read it first. It holds the destination, the standing constraints settled by grilling, the index of decisions made, the fog, and what is explicitly out of scope. Decision tickets are its sub-issues, wired with native GitHub dependencies so the frontier is visible in the tracker UI.

**Destination:** a locked technical spec and architecture decision set — enough that implementation sessions can build without making further design decisions. This is a planning effort. Do not start building the app.

## State as of this handoff

**Nineteen tickets resolved and closed.** Three remain open, and **only one of them is a decision**:

| Open | What it is | State |
|---|---|---|
| [#22 Lock how ClipShift surfaces faults and failures to the user](https://github.com/richardthornton/clipshift/issues/22) | grilling — the last design decision but two | **free, and the recommended pick** |
| [#18 Stand up the performance measurement harness](https://github.com/richardthornton/clipshift/issues/18) | task — scripts done, the A/A control run is not | claimed; needs you at the machine |
| [#14 Spike the capture-to-encode pipeline on real hardware](https://github.com/richardthornton/clipshift/issues/14) | the measurement and the verdict | **blocked** on #18 |

**The map is close to done.** After #22 the only fog left is *Settings persistence* and *Final spec assembly* — plus #14's numbers, which are a measurement rather than a decision.

Research findings live on `main` under [`docs/research/`](docs/research/) — roughly 5,000 lines, every claim cited to a primary source, with explicitly-marked unsettled items in each. The documents are the authority for *research*; where a document disagrees with a ticket resolution, **the ticket wins**. `av-sync-strategy.md` now carries a banner at the top listing the four sections later tickets overruled.

| Finding | Where |
|---|---|
| Display capture: **DXGI Desktop Duplication**, WGC only as a non-encoding-adapter fallback | [`display-capture-api.md`](docs/research/display-capture-api.md) |
| Encoder: **NVENC SDK API directly**, H.264 High 4:2:0, CONSTQP | [`nvenc-access-path.md`](docs/research/nvenc-access-path.md) |
| Audio: **raw WASAPI interop**, both timestamps; `Endpoint \| Process` source model | [`wasapi-audio-capture.md`](docs/research/wasapi-audio-capture.md) |
| Sync: **QPC master clock**, files as pure functions of it — **partly overruled, see the banner** | [`av-sync-strategy.md`](docs/research/av-sync-strategy.md) |
| Hotkey: **Raw Input `RIDEV_INPUTSINK`**, `RegisterHotKey` as bind-time probe only | [`global-hotkey.md`](docs/research/global-hotkey.md) |
| Overlay: **`WDA_EXCLUDEFROMCAPTURE`**, works on both capture APIs | [`capture-invisible-overlay.md`](docs/research/capture-invisible-overlay.md) |
| Containers: **fragmented MP4 + soft remux**, WAV with patched sizes | [`crash-survivable-containers.md`](docs/research/crash-survivable-containers.md) |
| Resampler options and their licence bills | [`resampler-options.md`](docs/research/resampler-options.md) |
| Resolve **does** read a killed fragmented MP4 — measured, not reviewed | [`resolve-truncated-mp4-import.md`](docs/research/resolve-truncated-mp4-import.md) |
| Resolve reads **RF64, a `JUNK` reservation, and past 4 GiB** — measured | [`resolve-audio-format.md`](docs/research/resolve-audio-format.md) |

**Code and prototypes live on their own branches, never on `main`.** Nothing here is application code; each is a throwaway instrument or mockup linked from its ticket.

| Branch | Ticket | What it is |
|---|---|---|
| [`prototype/window-design`](https://github.com/richardthornton/clipshift/tree/prototype/window-design) | #9 | three window designs across every state |
| [`spike/capture-to-encode`](https://github.com/richardthornton/clipshift/tree/spike/capture-to-encode) | #19 | DDA → NV12 → NVENC → raw H.264, the instrument #14 measures |
| [`spike/resampler-quality`](https://github.com/richardthornton/clipshift/tree/spike/resampler-quality) | #20 | the SNR harness that settled the resampler |

### Video format is locked

[Lock the video container and codec](https://github.com/richardthornton/clipshift/issues/10) — the full reasoning is in the resolution comment; the headline:

| | |
|---|---|
| Codec | H.264 High 4:2:0 8-bit |
| Container | Hybrid MP4 — fragmented while recording, soft-remuxed in place at stop |
| Muxer | ClipShift's own. No FFmpeg, no Media Foundation |
| Rate control | `CONSTQP`, `qp = 20`, preset p5/HQ, **zero B-frames**, no look-ahead or multipass, spatial AQ on |
| Keyframes | 1 second (60 frames) — also the fragment cadence and the crash window |
| Colour | Limited range, BT.709, tagged in **both** `colr` and the SPS VUI |
| Size | ~63–99 GB per four-hour session |

Config-file keys: codec, `qp`, preset, keyframe interval, container. Hard-coded with no key at all: colour range and tagging, chroma/bit depth, B-frames = 0 — each has a *silent* failure mode.

### The crash-survivability design is validated, and it added two muxer requirements

[Verify Resolve imports a truncated fragmented MP4](https://github.com/richardthornton/clipshift/issues/15)
was answered by experiment on this machine, against Resolve 20.3.2.9 free. It imports a killed
fragmented MP4, reports its duration, and seeks anywhere in it including the damaged tail. #8 and #10
are validated end to end. Two things came out of it that the paper design did not have:

1. **Recovery must truncate to the last complete `moof`+`mdat` pair.** Resolve *refuses to render* a
   file whose final fragment is partial — the render dies with a decode error — while a render ending
   at the last complete fragment succeeds and the repaired file renders at 100%. Import and scrub
   succeed either way, so an unrepaired file looks fine right up until export. The repair is a copy
   and a `truncate`; no media bytes move.
2. **The start offset must live in an `elst` in the init segment, never in `tfdt`.** Resolve honours an
   edit list, including in a fragmented file; it *silently ignores* a `tfdt` offset that FFmpeg
   honours. Getting this wrong puts video a fixed distance out against audio with nothing reported.

A third, smaller one: FFmpeg's fMP4 writes **absolute** `base_data_offset` values in `tfhd`, so any
header edit invalidates every fragment. ClipShift's muxer should set `default-base-is-moof`.

### The audio format is locked

[Lock the audio file format](https://github.com/richardthornton/clipshift/issues/11) — the full
reasoning is in the resolution comment; the headline:

| | System loopback | Microphone / audio input |
|---|---|---|
| Encoding | PCM, uncompressed | PCM, uncompressed |
| Sample rate | 48 000 Hz | 48 000 Hz |
| Bit depth | 16-bit | **24-bit** |
| Channels | stereo | **the device's own count** (1 or 2) |
| Container | RIFF + `JUNK`, upgraded in place to RF64 before 4 GiB | same |
| Timecode | BWF `bext`, `TimeReference` from T0 | same, identical value |
| 4-hour size | 2.76 GB | 2.07 GB mono / 4.15 GB stereo |

The two files share a container, a sample rate and a timecode origin; bit depth and channel count follow
the source. Sizes are patched every 1 second — matching the video's crash window, so a kill costs ≤1 s
from every file uniformly. Config keys: `audio.sampleRate`, `audio.loopback.bitDepth`,
`audio.mic.bitDepth`, `audio.headerPatchSeconds`. Hard-coded with no key: PCM with no compression
option, the >2-channel downmix, the `JUNK`→`ds64` upgrade, dither off, `bext` always written, and
rounding down to whole sample frames — each has a *silent* failure mode.

**Two research documents contradicted each other and one was overruled.** `av-sync-strategy.md` §10.5
says to pin the capture client's format with `AUTOCONVERTPCM | SRC_DEFAULT_QUALITY`;
`wasapi-audio-capture.md` §8.1 says never to. The WASAPI document wins: those flags put an in-engine
resampler in the path that absorbs exactly the drift the sync design has to measure from the per-packet
`(DevicePosition, QPCPosition)` pairs. **ClipShift does every conversion itself** — rate, sample format,
channels. That is what raised #16.

### The resampler is locked, and then measured

[Lock the resampler and the drift-correction control loop](https://github.com/richardthornton/clipshift/issues/16)
chose **NAudio's `WdlResampler`, vendored as source**, with libsamplerate (BSD-2) named as a fallback if
it missed a *measured* ≥110 dB bar. [Settle the resampler fallback by measuring WdlResampler against the
quality bar](https://github.com/richardthornton/clipshift/issues/20) then ran that measurement on
[`spike/resampler-quality`](https://github.com/richardthornton/clipshift/tree/spike/resampler-quality):
**WDL clears the bar and the fallback does not fire** — worst gated SNR **125.4 dB** at 44.1 → 48 and
131–132 dB near unity, at ~2% of a core. libsamplerate is not adopted, so its native DLL and an x64 CI
build are both avoided.

Four things from those two tickets that are easy to get wrong later:

1. **The quality knob is `sinc_interpsize`, not `sinc_size`.** At a fixed 128-phase table, 64 → 1024 taps
   buys 0.04 dB; at 64 taps, 32 → 256 phases buys **31 dB**. Following #20's own instruction to test only
   at WDL's longest sinc setting would have concluded WDL clears the bar only at 8192/4096 — over 220% of
   a core — and fired the fallback for no reason. The setting is **`sinc 256/1024`**.
2. **Group delay is a one-time pre-roll, not a per-call query**, and for WDL **D is 0** — it pre-pads its
   own input buffer. #16's pre-roll is satisfied as written and the CI requirement becomes an assertion
   that D stays 0.
3. **The loop reads the rate instead of inferring it**: feed-forward the ppm measured from
   `(DevicePosition, QPCPosition)`, integral trim only, no proportional term. That is what makes the
   estimate *separable* — kept across a same-device reconnect, discarded on a device change. The state
   variable is buffer **occupancy** (floor 0, ceiling 500 ms), not positional error, which is
   arithmetically zero under #5's invariant.
4. **No bypass at ratio 1.0, ever.** It would produce a machine-dependent fixed lip-sync offset that no
   drift test can see by construction.

Two allocation defects were found and fixed in the vendored copy: the per-call `Array.Resize`
(752 KB per 200 s, ~52 MB per four-hour session per sink) and a **worse** one beside it — the sinc table
was rebuilt on nearly every pull, and gating that is worth **12.3× throughput** at −148 dB of difference.

### The window is designed

[Design the ClipShift window](https://github.com/richardthornton/clipshift/issues/9) — a
**400 × 540 single-column stack**: four identically-shaped rows (display, system audio, mic, output),
record button on the bottom edge, hotkey footer. A spatial display picker and a launcher-style bar were
both built and both rejected; all three survive on
[`prototype/window-design`](https://github.com/richardthornton/clipshift/tree/prototype/window-design).

The decisions most likely to be second-guessed, with their reasons:

- **Rows stay visible and locked while recording.** They are fixed at `T0` by #5's invariant, but hiding
  them would cost the only mid-session confirmation that the right display and mic are being captured.
- **Meters stay, but render only while the window is visible** — stop entirely when hidden, not throttle.
  The window is in the tray for the whole session, and a dead mic is the failure this app cannot recover
  from.
- **Display rows carry Windows' own display number plus an Identify flash**, not thumbnails — which would
  mean capturing all three displays just to draw a picker.
- **The hotkey footer is the binder**, no fifth control, and a clashing chord **binds with a warning**,
  because #6 established conflicts can only be probed and blocking on a wrong probe leaves no recourse.
- **Red is reserved exclusively for recording**, so "armed" and "recording" never compete for the same
  signal. The accent is cold. **This is the constraint #22 has to work around**, since a fault during a
  recording cannot use red and the indicator pill is already carrying elapsed time.

### The performance budget is locked, and it unblocked the hardware spike

[Lock the performance budget and how it is measured](https://github.com/richardthornton/clipshift/issues/13)
— the full reasoning and the measurement procedure are in the resolution comment; the headline:

| | Budget |
|---|---|
| Mean added frame time | ≤ **0.30 ms** |
| 99th-percentile added frame time | ≤ **1.00 ms** |
| Additional dropped frames (capped at 60) | **zero** |
| CPU | ≤ 0.5 core-equivalent sustained |
| NVENC engine (ClipShift alone) | ≤ 25% — 20% measured, so this is a tripwire not a target |
| Added SM / copy-engine occupancy | ≤ 3% |
| Working set | ≤ 300 MB, **zero growth over 4 hours** |
| Disk | ~5–8 MB/s sustained; the real constraint is the 1-second burst |

**Budgeted in milliseconds and core-equivalents, never percentages** — a percentage budget silently
loosens on a light game, and it is the 60 fps case that ClipShift is built for. **No number here is
measured.** This ticket defines the budget and the method; #14 produces the numbers.

Three things came out of it that the ticket did not go in expecting:

1. **ClipShift's GPU cost is per wall-clock second, not per game frame.** It paces at 60 fps CFR no
   matter what the game presents, so its fixed cost divides across however many frames were produced
   — at 500 fps it looks eight times cheaper than at 60. **An uncapped run on a light load flatters
   the result rather than being conservative.** The load must be GPU-bound at roughly 60 fps. Uncapped
   runs still produce the frame-time numbers; capped-at-60 runs produce the dropped-frame pass/fail;
   neither substitutes for the other.
2. **The machine cannot produce a deterministic benchmark load** from what was installed at the time,
   so #13 bought repeatability statistically from a fixed camera in Train Sim World 6. **#18 replaced
   that** by deliberately installing GRID 2 for its scripted benchmark — see Environment facts.
3. **The preset trade #10 left open partly collapses.** At CONSTQP, `qp` fixes the quality — a slower
   preset buys better rate-distortion decisions at the *same* quality, which lands as a **smaller
   file**. p5 vs p7 is GPU-time-for-file-size, not GPU-time-for-quality. p5 stays the default and the
   burden of proof sits on displacing it.

The test splits in two: the **supporting budgets and the whole 4-hour stability run need no game at
all** (static desktop + OBS streaming, fully deterministic, and that is the cheap regression gate);
only the headline frame-time metric needs one.

### Frame pacing is locked

[Lock frame pacing and the constant-frame-rate policy](https://github.com/richardthornton/clipshift/issues/12)
— an app-owned **60.000 fps** grid (not 60000/1001), fixed and never following the display; one thread
draining `AcquireNextFrame` until the deadline, absolute deadlines from `T0`, and a **high-resolution
waitable timer instead of `timeBeginPeriod(1)`** so ClipShift never raises the system timer resolution
under a game. **The grid has no pause path** — it ticks through `ACCESS_LOST` recovery emitting the last
good surface, because a pause would break #5's invariant by exactly the recovery time. `T0` is the record
instant and pre-surface ticks emit counted **black frames**. Encoder backpressure **counts and continues,
never blocks** — blocking would silently turn a video problem into a sync problem. Seven distinct
duplication counters go to a rolling `%LOCALAPPDATA%` log, not a sidecar file.

### The capture-to-encode instrument exists

[Build the capture-to-encode spike](https://github.com/richardthornton/clipshift/issues/19) — a **task,
not a decision**: the thing #14 measures, on
[`spike/capture-to-encode`](https://github.com/richardthornton/clipshift/tree/spike/capture-to-encode).
DDA → GPU BGRA→NV12 through two render-target views on the encoder's own NV12 planes → NVENC direct →
raw H.264 elementary stream on #12's grid. **No muxer** — deliberately. **The output reproduces every
line of #10** — High profile, `yuv420p`, `has_b_frames=0`, limited-range BT.709 in the SPS VUI, IDR every
60 — verified by `ffprobe`, a clean full decode, and by eye.

Four findings came with it:

1. **DDA allocates exactly zero managed bytes per frame where WGC allocates ~2.7 KB** — a measured answer
   to `display-capture-api.md` §11.6, and ~2.3 GB of garbage per four-hour session.
2. **WGC's `CreateForMonitor` returns an "All displays" item whichever `HMONITOR` it is given**
   (3840×2168, the bounding box of all three displays), so the two arms are **not comparable** until #14
   settles it.
3. **WGC's capture border *was* suppressible** in an unpackaged process — `IsBorderRequired = false` stuck
   and no border appears in the pixels. This qualifies #2's decisive reason without overturning it, since
   DDA still wins on allocation.
4. **`doNotWait` returns an undocumented `OUT_OF_MEMORY`**, so the spike drains synchronously and
   `duplicated_backpressure` is implemented but untestable without async mode.

### Mid-recording failure behaviour is locked

[Lock mid-recording failure behaviour for display, disk and sustained backpressure](https://github.com/richardthornton/clipshift/issues/21)
— one rule covers all of it:

> **A recording stops itself only when the file's *existence* or its *decodability* is threatened — never
> when its *content* is merely damaged.**

That asymmetry is §7's structural property cashed out. Because every stream is a pure function of one
clock, content damage is always local and always survivable; what the invariant does *not* protect is a
file that never gets finalised, or a bitstream that stops decoding partway through.

| Stop cleanly | Degrade and continue |
|---|---|
| Output volume full or unwritable · System suspend/resume · `DXGI_ERROR_DEVICE_REMOVED` · Fatal NVENC error | Recorded display absent for **any** duration, including a return on a different adapter · Display back at a **different resolution** · Sustained backpressure for **any** duration · Everything #12 and #16 already cover |

- **An automatic stop is the record button's own code path** — byte-identical to a user stop. **No
  segmenting and no auto-restart**: a second file would need a second `T0`, and two epochs is the drift
  problem the architecture exists to prevent.
- **Video geometry is fixed at `T0`.** The NVENC session is never rebuilt; a returning display of any size
  is scaled to fit with black bars inside #19's existing conversion pass. This also handles the case that
  actually dominates the ticket's framing — **a game changing fullscreen resolution**, not a replugged
  monitor.
- **A display that returns on the iGPU stays "absent"** rather than silently starting to pay the
  per-frame cross-adapter copy the map refuses by name.
- **Disk** is checked on #11's existing 1-second header-patch cadence against the *measured* write rate:
  fault state at 5 minutes of headroom, clean stop at 60 seconds. Pre-flight at record time is a
  **warning stated in hours, never a refusal**. Keys: `disk.warnSeconds`, `disk.stopSeconds`,
  `disk.preflightHours`.
- **Faults are recorded as episodes** — enter/clear events carrying the master-clock offset from `T0` —
  kept distinct from #12's counters, plus a sticky "this session had faults" bit.

Three things came out that the ticket did not go in expecting:

1. **§7's configurable suspend threshold was deleted, not tuned.** It can only choose whether to
   *disclose* a gap, never prevent one, because the process cannot learn how long it was gone until it is
   already back. **There is no suspend key. Do not re-add one.**
2. **Sustained backpressure has no available action at all**, and that is a finding rather than a gap.
   #10's silent-failure reasoning rules out retuning the encoder mid-recording; stopping would destroy a
   correct-but-degraded four-hour session. It is a **pure surfacing problem**, which is what raised #22.
3. **Two failures the ticket never listed joined the stop bucket** — `DEVICE_REMOVED` and a fatal NVENC
   error. Neither is covered by #12, which handles `ACCESS_LOST` at the *duplication* level; these are
   *device* level and take the D3D device, the NV12 textures and the encoder session together.

## The findings that most changed the picture

1. **The project is viable.** The two risks that could have sunk it are both dead. NVENC coexists with OBS fine — 12 concurrent sessions documented, six measured clean on the reference machine, against folklore claiming 2–3. And multi-hour sync across three separately-clocked files is solvable *by construction*: make each file's length a computation from one QPC epoch rather than a measurement, and the files cannot drift apart. That bounds error at 10.4 µs against a 2 ms budget.

2. **That one invariant keeps paying out in tickets that are not about sync.** #12 refused to let the pacing grid pause because of it. #16 retired a positional-error threshold because under it positional error is arithmetically zero. #21 used it to decide that almost nothing should stop a recording, and to rule out segmenting outright. It is the single most load-bearing idea on the map.

3. **The encoder/container coupling collapsed rather than needing arbitration.** The NVENC research said libavformat would make `h264_nvenc` the cheaper path, but the container research independently disqualifies libavformat for the same reason that would have made it attractive — FFmpeg has no soft-remux equivalent, and both of its routes to a plain MP4 rewrite the whole file (~70–100 GB on the stop button). Direct NVENC stands, and there is no LGPL obligation on this MIT project. That decision then killed libswresample's case in #16 as a second-order effect.

4. **A charting assumption was wrong, and the ticket caught it.** The capture-invisible indicator does *not* force a WGC architecture — `WDA_EXCLUDEFROMCAPTURE` works identically on both APIs, verified from Microsoft's own shipping source and by direct measurement.

5. **Two "obvious" designs would have been wrong.** Flushing to disk on a cadence for crash safety buys nothing — a killed process cannot reclaim bytes already through `WriteFile`. And copying OBS's insert/drop drift correction would be wrong here: it suits a live stream, not an editor's source material.

6. **Measurement harnesses have been wrong more often than the things they measured.** #11 took four passes to get a render job accepted. #20's first harness read 149 dB where the correct one reads 78, because the low 533 Hz tone libsamplerate uses for its own ratio tests hides the defect entirely near unity. Budget for the instrument being wrong before the result is.

## Environment facts, established by probing rather than asking

These cost real effort to pin down and shape several remaining decisions. Do not re-derive them; do verify if the machine changes.

- **The NLE is DaVinci Resolve 20.3, free edition — not Studio.** No Premiere installed. On Windows, free Resolve decodes H.264/H.265 at *"8-bit OS-supported profiles"* with **GPU acceleration gated behind Studio**, while AV1 decode is GPU-accelerated in both editions. Richard confirmed free Resolve is a fixed target, not a setup he intends to upgrade. **This is the single most decision-shaping fact for the remaining format tickets.**
- **No HDR.** Three 1080p60 SDR panels — two `LG FULL HD`, one `KAMN27LSD`.
- **`E:` is the media drive** — 3.5 TB free, already holding Resolve media and OBS output. `C:` has ~287 GB, `D:` has 80 GB. At #10 and #11's worst case the whole output is ~27 GB/hour, so `E:` holds ~130 hours. **Any disk policy written for #21 will essentially never fire on this machine** — it fires on someone else's.
- FFmpeg and OBS Studio 32.1.2 are installed; Python 3.14 is available.
- **Measurement tooling, probed during #13.** **NVIDIA FrameView SDK 1.7.12227.37421622** and **NVIDIA
  App 11.0.7.247** are installed. **PresentMon, CapFrameX, MSI Afterburner/RTSS and OCAT are not.**
  #13 chose **Intel PresentMon CLI** as the instrument, so obtaining it (a single executable) is part
  of #14's setup. GPU driver is **610.62**.
- **`DuplicateOutput1` is unavailable on this machine** (`DXGI_ERROR_UNSUPPORTED`), found during #19.
  This makes `display-capture-api.md` §11.9's probe **inconclusive rather than negative**. §11.2's
  `LastPresentTime` probe is reported as **unanswerable as built**, because the spike measures polling
  phase rather than the anchor.
- **The game library — the authoritative check is `steamapps\libraryfolders.vdf`**, nothing else. It
  lists the libraries Steam actually uses and the appids in each. At time of writing it holds exactly one
  entry, `C:\Program Files (x86)\Steam`, with Steamworks Common Redistributables, Dorfromantik, Cairn,
  Balatro, As Long As You're Here, Train Sim World 6 (77 GB), and GRID 2.
- **`D:\SteamLibrary` is an ORPHANED library and is a trap.** It holds ~34 games' worth of real files
  and complete `appmanifest_*.acf` files frozen since January 2024, **but it is not registered in
  `libraryfolders.vdf`, so Steam does not know it exists and none of those games are installed.** The
  #18 session was fooled by it: an `appmanifest` with `StateFlags=4` and an on-disk size matching its
  manifest byte for byte is *still not an install* if the library is not registered. **Do not check
  `steamapps\common` directories, and do not trust stray manifests — read `libraryfolders.vdf`.**
  (Re-adding the folder via Steam → Settings → Storage → Add Drive should adopt those installs without
  re-downloading, if the ~34 games are ever wanted back.)
- **GRID 2 was installed deliberately during #18** to serve as the measurement load, replacing #13's
  fixed-camera Train Sim World 6 scene. It has a **scripted benchmark**
  (`grid2.exe -benchmark <file.xml>`, with `infinite_loop`, `skipreplays` and a `hardwaresettings`
  attribute that pins graphics settings from a file), so the load is *replayed* rather than performed
  — determinism bought outright rather than statistically. Being a 2013 DX11 title it also covers the
  older presentation path #13 noted nothing installed could reach. **Caveat: it is a light load on a
  5060 Ti and will very likely not be GPU-bound at 60 fps without DSR**, which is what #13 §3
  requires. See the perf-harness README.
- Train Sim World 6 remains the heaviest modern load and the fallback if GRID 2 cannot be driven
  GPU-bound.
- **Resolve can be scripted, but only from inside itself.** The free edition returns `None` from
  `scriptapp("Resolve")` for any external process, and the "External scripting using" preference has
  never been written to `config.dat`. A script dropped in
  `%APPDATA%\Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts\Utility\` appears under
  `Workspace > Scripts` and runs in-process with a `resolve` global, which is how #15 was measured. It
  needs one click from Richard per run, so batch everything into a single script. Resolve's Qt UI
  exposes no named elements to UI Automation — do not try to drive the menu programmatically.

## The frontier — what to pick up next

**Takeable now, nothing blocking:**

- [#22 Lock how ClipShift surfaces faults and failures to the user](https://github.com/richardthornton/clipshift/issues/22) — **new, and the recommended pick.** Graduated from the map's *Error surfacing* fog the moment #21 settled which failures exist and which stop a session. It now has every input it needs and is pure design thinking, no machine required.
- [#18 Stand up the performance measurement harness and establish the noise floor](https://github.com/richardthornton/clipshift/issues/18) — **partly done; the remaining work needs the machine.** Scripts are built, tested against synthetic data and committed. What is left is the A/A control run, which needs an elevated shell, OBS streaming and GRID 2 looping its benchmark. Read the ticket comment before starting. Already assigned.

**Blocked:**

- [#14 Spike the capture-to-encode pipeline on real hardware](https://github.com/richardthornton/clipshift/issues/14) — blocked by #18, wired natively. #19 is done, so #14 now owns only the measurement and the verdict: run the sweeps, compare the variants, decide whether the architecture holds. Note #19 left it one extra job — **WGC's `CreateForMonitor` returns an all-displays item**, so the DDA and WGC arms are not comparable until #14 resolves that.

**Still in the fog** (not yet ticketable): *Settings persistence* — where config lives and what is remembered, which should sit consistently with the `%LOCALAPPDATA%` diagnostic log #12 already chose, and which now has #21's three `disk.*` keys to house. And *Final spec assembly* — the shape of the handoff document all of this gets written into.

## Prompt for the next session

**Recommended: take #22, error surfacing.** It is the last free decision on the map, it needs nothing but a conversation, and finishing it leaves only two fog patches between here and the destination.

```
/wayfinder https://github.com/richardthornton/clipshift/issues/1 — work #22, Lock how ClipShift
surfaces faults and failures to the user.

Read #21 and #9's resolution comments before starting: #21 fixes the failure set, the two buckets
and the enter/clear rules; #9 fixes the four surfaces a message can land on and reserves red
exclusively for recording.

The hard constraint is that one: a fault during a recording cannot use red, and the indicator pill
is already carrying elapsed time. Also remember the app spends the whole session behind a
fullscreen game, so anything that can steal focus or trigger a mode change risks being worse than
the fault it reports.
```

To take a different one instead, name it the same way. `/wayfinder` with no ticket named takes the
first frontier ticket:

```
/wayfinder https://github.com/richardthornton/clipshift/issues/1
```

**The alternative:** **#18**, if you are at the machine. It is one command plus the setup around it, and it is the only thing that will tell you the instrument works before hours are spent generating numbers with it. Expect the GPU-bound-at-60 tuning to be the fiddly part. It also unblocks #14, which is the only thing left standing between the map and its destination once the decisions are done.

Both Resolve experiment rigs need **one click from Richard per run** — free Resolve refuses external
scripting connections, so batch everything into a single script before asking. Budget for the harness
being wrong: #11 took four passes to get a render job accepted, and three of them failed on
`AddRenderJob` rather than on the media. **Always include a control** that is known to work; #11's
failures were only diagnosable because the control failed identically.

Rules that matter: **resolve one ticket per session** (research tickets are the exception). Claim a ticket by assigning it to yourself *before* doing any work. Record a resolution as a comment on the ticket, close it, and append a one-line pointer to the map's Decisions-so-far. Do not restate a decision on the map — the map is an index, the ticket holds the detail.

Note that `/wayfinder` is a **user-invocable skill only** (`disable-model-invocation: true`), so it will not appear in the agent's skill listing and the agent cannot launch it. Type it yourself.

## Caveats, stated plainly

- **AV1 was a close call, not a dismissal.** It is the only codec free Resolve GPU-accelerates, and it would cut file sizes 30–40%. It lost because that advantage is a 4K argument applied to a 1080p problem, while its cost lands on the muxer — the riskiest code in the project. It is a config-file value and worth revisiting once the MVP ships. Do not treat H.264 as settled *forever*, only as settled for the MVP.
- **The ~63–99 GB estimate is an estimate.** CQP produces predictable quality and unpredictable size. It has not been measured on real gameplay and should be checked during #14. #21's disk policy deliberately uses the *measured* write rate rather than this number for exactly that reason.
- **Five of the seven research agents were killed mid-flight by an API session limit** during the charting session. All had committed their findings; only their resolution summaries were missing, and those were written from each document's recommendation section. The full ~5,000 lines have **not** all been read. #10 required reading two of them end to end and both held up — but that is two of seven. If a conclusion does not survive closer reading, reopen the ticket.
- **`av-sync-strategy.md` has now been overruled in four places** — §7's suspend row, §10.2, and §10.5 twice — and carries a banner at the top saying so. It was also the document whose final commit was **work-in-progress** when its agent died, mid-way through replacing summarised ITU figures with verbatim ones. The committed state looks coherent and the numbers are consistent, but treat this document with more suspicion than the others.
- **The #15 and #11 conclusions both rest on one Resolve build**, 20.3.2.9 free. For #15 the render failure on an unrepaired file is the result most worth re-checking after an upgrade; the test kill was `taskkill /F` on FFmpeg, not a power loss, and the content was `testsrc2` rather than gameplay. For #11 the RF64 results are the ones to re-check, and two things were **not** exercised: the in-place `JUNK`→`ds64` upgrade on a live file, and a kill landing between that upgrade and the next header patch. Both are ClipShift's own code to write and to verify.
- **#20's SNR result is one measurement of one configuration.** It clears the bar with margin, but the harness's first version was wrong and read 149 dB where the correct one reads 78. The result is only as good as the 16.85 kHz probe tone that exposed the defect — a lower tone hides it entirely near unity.
- **#13's budget numbers are targets, not observations.** 0.30 ms mean and 1.00 ms at the 99th
  percentile were chosen from a perceptual argument about a 16.67 ms frame on a 60 Hz panel, not
  measured. If #14 finds the architecture lands at 1.4 ms, that is a conversation about whether the
  budget was set right — not automatic grounds to reopen #2. But the burden shifts to arguing the
  budget was wrong, and it should be argued explicitly rather than quietly relaxed.
- **The #13 measurement method has never been run.** Interleaved pairs, A/A control, bootstrapped
  percentile CIs — all sound on paper, none exercised. **Assume the first #14 session is spent getting
  the harness right rather than getting numbers.** The A/A control run is the thing that will tell you
  the harness works, so run it first and do not skip it.
- **#19's WGC arm is not yet a fair comparison.** `CreateForMonitor` returned an all-displays item, so
  the WGC numbers measure a 3840×2168 capture against DDA's 1920×1080. #14 must settle this before
  comparing the arms, or the comparison is meaningless.
- **Nothing has been built.** There is no application code, no project file, no solution — only research documents on `main` and throwaway instruments on their own branches. That is correct: the destination is a spec.
- **Agent worktrees under `.claude/worktrees/` are gitignored** and can be deleted freely. The research branches are all pushed to origin, so nothing is lost by removing them.
