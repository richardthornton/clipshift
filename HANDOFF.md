# ClipShift — session handoff

**Last updated:** 13 August 2026, end of the session that locked the performance budget.

## What ClipShift is

A minimal, beautiful Windows app that records **one display** to a video-only file, plus **up to two optional audio files** (system loopback and audio input), all perfectly synced. Global toggle hotkey, tray icon, on-screen indicator that never appears in the recording. Performance is the point — this records multi-hour gameplay sessions, while OBS is streaming.

## Where the planning lives

**[Map: ClipShift MVP — spec and architecture](https://github.com/richardthornton/clipshift/issues/1)** — GitHub issue #1, labelled `wayfinder:map`. Read it first. It holds the destination, the standing constraints settled by grilling, the index of decisions made, the fog, and what is explicitly out of scope. Decision tickets are its sub-issues, wired with native GitHub dependencies so the frontier is visible in the tracker UI.

**Destination:** a locked technical spec and architecture decision set — enough that implementation sessions can build without making further design decisions. This is a planning effort. Do not start building the app.

## State as of this handoff

**Eleven tickets resolved and closed** — seven research tickets, three of the locking decisions, and the
experiment that validated the first of them against the real NLE.

Research findings live on `main` under [`docs/research/`](docs/research/) — roughly 5,000 lines, every claim cited to a primary source, with explicitly-marked unsettled items in each. The documents are the authority; the ticket summaries are pointers.

| Finding | Where |
|---|---|
| Display capture: **DXGI Desktop Duplication**, WGC only as a non-encoding-adapter fallback | [`display-capture-api.md`](docs/research/display-capture-api.md) |
| Encoder: **NVENC SDK API directly**, H.264 High 4:2:0, CONSTQP | [`nvenc-access-path.md`](docs/research/nvenc-access-path.md) |
| Audio: **raw WASAPI interop**, both timestamps; `Endpoint \| Process` source model | [`wasapi-audio-capture.md`](docs/research/wasapi-audio-capture.md) |
| Sync: **QPC master clock**, files as pure functions of it | [`av-sync-strategy.md`](docs/research/av-sync-strategy.md) |
| Hotkey: **Raw Input `RIDEV_INPUTSINK`**, `RegisterHotKey` as bind-time probe only | [`global-hotkey.md`](docs/research/global-hotkey.md) |
| Overlay: **`WDA_EXCLUDEFROMCAPTURE`**, works on both capture APIs | [`capture-invisible-overlay.md`](docs/research/capture-invisible-overlay.md) |
| Containers: **fragmented MP4 + soft remux**, WAV with patched sizes | [`crash-survivable-containers.md`](docs/research/crash-survivable-containers.md) |
| Resolve **does** read a killed fragmented MP4 — measured, not reviewed | [`resolve-truncated-mp4-import.md`](docs/research/resolve-truncated-mp4-import.md) |
| Resolve reads **RF64, a `JUNK` reservation, and past 4 GiB** — measured | [`resolve-audio-format.md`](docs/research/resolve-audio-format.md) |

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
2. **The machine cannot produce a deterministic benchmark load.** Only *six* games are actually
   installed — Balatro, Dorfromantik, Cairn, As Long As You're Here and Train Sim World 6. The other
   ~34 Steam entries are lingering manifests for uninstalled titles, GRID 2 (the only built-in
   benchmark) among them, and Richard ruled out installing a synthetic. So repeatability is bought
   statistically instead: a **fixed stationary camera** in Train Sim World 6, with the run design in
   §5 of the resolution doing the work a scripted benchmark would have.
3. **The preset trade #10 left open partly collapses.** At CONSTQP, `qp` fixes the quality — a slower
   preset buys better rate-distortion decisions at the *same* quality, which lands as a **smaller
   file**. p5 vs p7 is GPU-time-for-file-size, not GPU-time-for-quality. p5 stays the default and the
   burden of proof sits on displacing it.

The test splits in two: the **supporting budgets and the whole 4-hour stability run need no game at
all** (static desktop + OBS streaming, fully deterministic, and that is the cheap regression gate);
only the headline frame-time metric needs one.

## The findings that most changed the picture

1. **The project is viable.** The two risks that could have sunk it are both dead. NVENC coexists with OBS fine — 12 concurrent sessions documented, six measured clean on the reference machine, against folklore claiming 2–3. And multi-hour sync across three separately-clocked files is solvable *by construction*: make each file's length a computation from one QPC epoch rather than a measurement, and the files cannot drift apart. That bounds error at 10.4 µs against a 2 ms budget.

2. **The encoder/container coupling collapsed rather than needing arbitration.** The previous handoff flagged it as the thing to settle deliberately in #10. It settled itself: the NVENC research said libavformat would make `h264_nvenc` the cheaper path, but the container research independently disqualifies libavformat for the same reason that would have made it attractive — FFmpeg has no soft-remux equivalent, and both of its routes to a plain MP4 rewrite the whole file (~70–100 GB on the stop button). The antecedent never fires. Direct NVENC stands, and there is no LGPL obligation on this MIT project.

3. **A charting assumption was wrong, and the ticket caught it.** The capture-invisible indicator does *not* force a WGC architecture — `WDA_EXCLUDEFROMCAPTURE` works identically on both APIs, verified from Microsoft's own shipping source and by direct measurement.

4. **Two "obvious" designs would have been wrong.** Flushing to disk on a cadence for crash safety buys nothing — a killed process cannot reclaim bytes already through `WriteFile`. And copying OBS's insert/drop drift correction would be wrong here: it suits a live stream, not an editor's source material.

## Environment facts, established by probing rather than asking

These cost real effort to pin down and shape several remaining decisions. Do not re-derive them; do verify if the machine changes.

- **The NLE is DaVinci Resolve 20.3, free edition — not Studio.** No Premiere installed. On Windows, free Resolve decodes H.264/H.265 at *"8-bit OS-supported profiles"* with **GPU acceleration gated behind Studio**, while AV1 decode is GPU-accelerated in both editions. Richard confirmed free Resolve is a fixed target, not a setup he intends to upgrade. **This is the single most decision-shaping fact for the remaining format tickets.**
- **No HDR.** Three 1080p60 SDR panels — two `LG FULL HD`, one `KAMN27LSD`.
- **`E:` is the media drive** — 3.5 TB free, already holding Resolve media and OBS output. `C:` has 313 GB, `D:` has 80 GB.
- FFmpeg and OBS Studio 32.1.2 are installed; Python 3.14 is available.
- **Measurement tooling, probed during #13.** **NVIDIA FrameView SDK 1.7.12227.37421622** and **NVIDIA
  App 11.0.7.247** are installed. **PresentMon, CapFrameX, MSI Afterburner/RTSS and OCAT are not.**
  #13 chose **Intel PresentMon CLI** as the instrument, so obtaining it (a single executable) is part
  of #14's setup. GPU driver is **610.62**.
- **The game library cannot stress the card, and only six titles are actually installed** — Balatro,
  Dorfromantik, Cairn, As Long As You're Here, Train Sim World 6. Steam shows ~40 entries; the rest are
  lingering `appmanifest` files for uninstalled games, so **do not trust the manifest list** — check
  `steamapps\common` on disk. **Train Sim World 6 is the only meaningful GPU load**, with Cairn (UE5)
  as the fallback. Everything installed is UE or Unity, so nothing exercises an older presentation
  path and #14's frame-time results will be specific to flip-model presentation.
- **Resolve can be scripted, but only from inside itself.** The free edition returns `None` from
  `scriptapp("Resolve")` for any external process, and the "External scripting using" preference has
  never been written to `config.dat`. A script dropped in
  `%APPDATA%\Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts\Utility\` appears under
  `Workspace > Scripts` and runs in-process with a `resolve` global, which is how #15 was measured. It
  needs one click from Richard per run, so batch everything into a single script. Resolve's Qt UI
  exposes no named elements to UI Automation — do not try to drive the menu programmatically.

## The frontier — what to pick up next

**Takeable now, nothing blocking:**

- [#9 Design the ClipShift window](https://github.com/richardthornton/clipshift/issues/9) — prototype ticket, needs Richard in the room. Independent of everything else.
- [#12 Lock frame pacing and the constant-frame-rate policy](https://github.com/richardthornton/clipshift/issues/12) — note that the locked 1-second keyframe interval assumes CFR 60. #15 left it one question: an `elst` in a movie timescale of 1000 cannot express a sub-frame offset exactly, and whether Resolve rounds or truncates one was not tested.
- [#16 Lock the resampler and the drift-correction control loop](https://github.com/richardthornton/clipshift/issues/16) — **new, raised by #11.** Now that ClipShift owns every sample-format conversion, the resampler is its own component doing two jobs at once (44.1→48 rate conversion and the continuous drift lock). Carries a licence edge: Secret Rabbit Code is GPL and incompatible with MIT, soxr is LGPL, and #3 established there is no LGPL obligation anywhere else in the project — so this would be the first.
- [#14 Spike the capture-to-encode pipeline on real hardware](https://github.com/richardthornton/clipshift/issues/14) — **newly unblocked by #13.** This is where the architecture meets real silicon; everything else on the map is paper. It carries the budget, the load configuration and the measurement method from #13's resolution, plus the preset sweep.

**Blocked:** nothing. Every open ticket is takeable.

## Prompt for the next session

**Recommended: take #12 next, then #14.** Not #14 immediately, for a reason #13 surfaced: the whole
performance budget — and specifically the finding that ClipShift's GPU cost is per wall-clock second
rather than per game frame — rests on ClipShift pacing at a strict **60 fps CFR**. That is exactly what
#12 has not yet locked. If #12 lands on anything other than strict CFR, #14 will have spiked the wrong
pipeline and measured it against a budget built on a different assumption. **#12 is small, #14 is the
biggest ticket on the map, and doing them in that order means the expensive one is not built on a
guess.** No blocking edge has been wired between them — this is a judgement call, and it was left for
you to make rather than imposed. Wire it if you agree.

```
/wayfinder https://github.com/richardthornton/clipshift/issues/1 — work #12, Lock frame pacing and
the constant-frame-rate policy.

This is now upstream of the hardware spike in practice, even though no blocking edge is wired. #13
locked a performance budget whose central finding — that ClipShift's GPU cost is per wall-clock
second, not per game frame — assumes strict 60 fps CFR pacing. If this ticket lands anywhere else,
#13's budget needs revisiting before #14 spends real effort.

Two loose ends this inherits. #10's 1-second keyframe interval assumes CFR 60. And #15 left one
question untested: an elst in a movie timescale of 1000 cannot express a sub-frame offset exactly,
and whether Resolve rounds or truncates one was never measured. The rig is in the repo under
docs/research/experiments/resolve-truncated-import/.

Standing constraint worth restating: the NLE is DaVinci Resolve 20.3.2.9 FREE, not Studio. Check
format claims against that edition specifically.
```

To take a different one instead, name it the same way. `/wayfinder` with no ticket named takes the
first frontier ticket:

```
/wayfinder https://github.com/richardthornton/clipshift/issues/1
```

**The alternatives, with a specific reason to prefer each:**

- **#14**, if you accept the CFR-60 assumption and want the paper tested against silicon now. It is
  where every architectural decision on this map finally gets checked, and it is **substantially
  larger than any ticket resolved so far** — a DDA capture path, an NVENC session, a muxer stub, four
  capture variants and a preset sweep. Expect it to need scoping down or splitting rather than
  resolving in one session. Its setup is spelled out in a comment on the ticket, and PresentMon still
  needs obtaining.
- **#16**, if the licence question feels urgent. It is the only ticket with a legal edge rather than a
  technical one: the obvious resampler (Secret Rabbit Code) is GPL and cannot be used in an MIT
  project at all.
- **#9**, if you want a break from systems work. It is the UI prototype, needs you in the room, and is
  independent of everything else on the map.

Both Resolve experiment rigs need **one click from Richard per run** — free Resolve refuses external
scripting connections, so batch everything into a single script before asking. Budget for the harness
being wrong: #11 took four passes to get a render job accepted, and three of them failed on
`AddRenderJob` rather than on the media. **Always include a control** that is known to work; #11's
failures were only diagnosable because the control failed identically.

Rules that matter: **resolve one ticket per session** (research tickets are the exception). Claim a ticket by assigning it to yourself *before* doing any work. Record a resolution as a comment on the ticket, close it, and append a one-line pointer to the map's Decisions-so-far. Do not restate a decision on the map — the map is an index, the ticket holds the detail.

Note that `/wayfinder` is a **user-invocable skill only** (`disable-model-invocation: true`), so it will not appear in the agent's skill listing and the agent cannot launch it. Type it yourself.

## Caveats, stated plainly

- **AV1 was a close call, not a dismissal.** It is the only codec free Resolve GPU-accelerates, and it would cut file sizes 30–40%. It lost because that advantage is a 4K argument applied to a 1080p problem, while its cost lands on the muxer — the riskiest code in the project. It is a config-file value and worth revisiting once the MVP ships. Do not treat H.264 as settled *forever*, only as settled for the MVP.
- **The ~63–99 GB estimate is an estimate.** CQP produces predictable quality and unpredictable size. It has not been measured on real gameplay and should be checked during #14.
- **Five of the seven research agents were killed mid-flight by an API session limit** during the charting session. All had committed their findings; only their resolution summaries were missing, and those were written from each document's recommendation section. The full ~5,000 lines have **not** all been read. #10 required reading two of them end to end and both held up — but that is two of seven. If a conclusion does not survive closer reading, reopen the ticket.
- **The sync document's final commit was work-in-progress** when its agent died — mid-way through replacing summarised ITU figures with verbatim ones. The committed state looks coherent and the numbers are consistent, but that section still deserves a second look. Not yet done.
- **The #15 conclusions rest on one Resolve build**, 20.3.2.9 free. The render failure on an unrepaired
  file is the result most worth re-checking after an upgrade. The test kill was `taskkill /F` on
  FFmpeg, not a power loss, and the content was `testsrc2` rather than gameplay — bitrates differ, box
  structure does not.
- **The #11 conclusions rest on the same single Resolve build**, 20.3.2.9 free. The RF64 results are the
  ones most worth re-checking after an upgrade. Two things were *not* exercised: the in-place
  `JUNK`→`ds64` upgrade on a live file (both sides were tested as separate artifacts, but not the
  transition), and a kill landing between that upgrade and the next header patch. Both are ClipShift's
  own code to write and to verify.
- **A research document was overruled during #11**, not merely refined. `av-sync-strategy.md` §10.5 tells
  you to pin the capture format with `AUTOCONVERTPCM`; do not follow it. The reasoning is in #11's
  resolution. Worth knowing that the two audio documents disagree in more than one place if you are
  reading them fresh — this was the load-bearing one, but it is unlikely to be the only one.
- **#13's budget numbers are targets, not observations.** 0.30 ms mean and 1.00 ms at the 99th
  percentile were chosen from a perceptual argument about a 16.67 ms frame on a 60 Hz panel, not
  measured. If #14 finds the architecture lands at 1.4 ms, that is a conversation about whether the
  budget was set right — not automatic grounds to reopen #2. But the burden shifts to arguing the
  budget was wrong, and it should be argued explicitly rather than quietly relaxed.
- **The #13 measurement method has never been run.** Interleaved pairs, A/A control, bootstrapped
  percentile CIs — all sound on paper, none exercised. Given that #11 took four passes to get a
  harness working and #15 needed several, **assume the first #14 session is spent getting the harness
  right rather than getting numbers**. The A/A control run is the thing that will tell you the harness
  works, so run it first and do not skip it.
- **Nothing has been built.** There is no application code, no project file, no solution. That is correct — the destination is a spec.
- **Agent worktrees under `.claude/worktrees/` are gitignored** and can be deleted freely. The research branches are all pushed to origin, so nothing is lost by removing them.
