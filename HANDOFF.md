# ClipShift — session handoff

**Last updated:** 13 August 2026, end of the session that verified Resolve reads a crash-truncated file.

## What ClipShift is

A minimal, beautiful Windows app that records **one display** to a video-only file, plus **up to two optional audio files** (system loopback and audio input), all perfectly synced. Global toggle hotkey, tray icon, on-screen indicator that never appears in the recording. Performance is the point — this records multi-hour gameplay sessions, while OBS is streaming.

## Where the planning lives

**[Map: ClipShift MVP — spec and architecture](https://github.com/richardthornton/clipshift/issues/1)** — GitHub issue #1, labelled `wayfinder:map`. Read it first. It holds the destination, the standing constraints settled by grilling, the index of decisions made, the fog, and what is explicitly out of scope. Decision tickets are its sub-issues, wired with native GitHub dependencies so the frontier is visible in the tracker UI.

**Destination:** a locked technical spec and architecture decision set — enough that implementation sessions can build without making further design decisions. This is a planning effort. Do not start building the app.

## State as of this handoff

**Nine tickets resolved and closed** — seven research tickets, the first of the locking decisions, and
the experiment that validated it against the real NLE.

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
- [#11 Lock the audio file format](https://github.com/richardthornton/clipshift/issues/11) — will need the Resolve-free fact above.
- [#12 Lock frame pacing and the constant-frame-rate policy](https://github.com/richardthornton/clipshift/issues/12) — note that the locked 1-second keyframe interval assumes CFR 60. #15 left it one question: an `elst` in a movie timescale of 1000 cannot express a sub-frame offset exactly, and whether Resolve rounds or truncates one was not tested.
- [#13 Lock the performance budget and how it is measured](https://github.com/richardthornton/clipshift/issues/13) — **owns the encoder preset trade.** p5 was locked in #10 as the conservative default because it matches the ~20% NVENC-engine figure measured on this card; #13 may revisit it with real numbers.

**Blocked:** [#14 Spike the capture-to-encode pipeline on real hardware](https://github.com/richardthornton/clipshift/issues/14), waiting on #13. This is where the architecture meets real silicon — everything above it is paper.

## Prompt for the next session

```
/wayfinder https://github.com/richardthornton/clipshift/issues/1
```

That loads the map and takes the first frontier ticket. To pick a specific one, name it:

```
/wayfinder https://github.com/richardthornton/clipshift/issues/1 — work #11, Lock the audio file format
```

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
- **Nothing has been built.** There is no application code, no project file, no solution. That is correct — the destination is a spec.
- **Agent worktrees under `.claude/worktrees/` are gitignored** and can be deleted freely. The research branches are all pushed to origin, so nothing is lost by removing them.
