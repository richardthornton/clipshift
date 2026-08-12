# ClipShift — session handoff

**Last updated:** 12 August 2026, end of the wayfinder charting session.

## What ClipShift is

A minimal, beautiful Windows app that records **one display** to a video-only file, plus **up to two optional audio files** (system loopback and audio input), all perfectly synced. Global toggle hotkey, tray icon, on-screen indicator that never appears in the recording. Performance is the point — this records multi-hour gameplay sessions, while OBS is streaming.

## Where the planning lives

**[Map: ClipShift MVP — spec and architecture](https://github.com/richardthornton/clipshift/issues/1)** — GitHub issue #1, labelled `wayfinder:map`. Read it first. It holds the destination, the standing constraints settled by grilling, the index of decisions made, the fog, and what is explicitly out of scope. Decision tickets are its sub-issues, wired with native GitHub dependencies so the frontier is visible in the tracker UI.

**Destination:** a locked technical spec and architecture decision set — enough that implementation sessions can build without making further design decisions. This is a planning effort. Do not start building the app.

## State as of this handoff

**Seven research tickets are resolved and closed.** Their findings are on `main` under [`docs/research/`](docs/research/) — roughly 5,000 lines, every claim cited to a primary source, with explicitly-marked unsettled items in each. Each ticket carries a resolution summary; the documents are the authority.

| Finding | Where |
|---|---|
| Display capture: **DXGI Desktop Duplication**, WGC only as a non-encoding-adapter fallback | [`display-capture-api.md`](docs/research/display-capture-api.md) |
| Encoder: **NVENC SDK API directly**, H.264 High 4:2:0, CONSTQP | [`nvenc-access-path.md`](docs/research/nvenc-access-path.md) |
| Audio: **raw WASAPI interop**, both timestamps; `Endpoint \| Process` source model | [`wasapi-audio-capture.md`](docs/research/wasapi-audio-capture.md) |
| Sync: **QPC master clock**, files as pure functions of it | [`av-sync-strategy.md`](docs/research/av-sync-strategy.md) |
| Hotkey: **Raw Input `RIDEV_INPUTSINK`**, `RegisterHotKey` as bind-time probe only | [`global-hotkey.md`](docs/research/global-hotkey.md) |
| Overlay: **`WDA_EXCLUDEFROMCAPTURE`**, works on both capture APIs | [`capture-invisible-overlay.md`](docs/research/capture-invisible-overlay.md) |
| Containers: **fragmented MP4 + soft remux**, WAV with patched sizes | [`crash-survivable-containers.md`](docs/research/crash-survivable-containers.md) |

### The three findings that most changed the picture

1. **The project is viable.** The two risks that could have sunk it are both dead. NVENC coexists with OBS fine — 12 concurrent sessions documented, six measured clean on the reference machine, against folklore claiming 2–3. And multi-hour sync across three separately-clocked files is solvable *by construction*: make each file's length a computation from one QPC epoch rather than a measurement, and the files cannot drift apart. That bounds error at 10.4 µs against a 2 ms budget.

2. **A charting assumption was wrong, and the ticket caught it.** I assumed the capture-invisible indicator forced a WGC architecture. It does not — `WDA_EXCLUDEFROMCAPTURE` works identically on both APIs, verified from Microsoft's own shipping source and by direct measurement. The capture API decision was free to be made on its own merits, and was.

3. **Two "obvious" designs would have been wrong.** Flushing to disk on a cadence for crash safety buys nothing — a killed process cannot reclaim bytes already through `WriteFile`. And copying OBS's insert/drop drift correction would be wrong here: it suits a live stream, not an editor's source material.

## The frontier — what to pick up next

**Takeable now, nothing blocking:**

- [#9 Design the ClipShift window](https://github.com/richardthornton/clipshift/issues/9) — prototype ticket, needs Richard in the room. Independent of everything else. **Good first pick.**
- [#10 Lock the video container and codec](https://github.com/richardthornton/clipshift/issues/10)
- [#11 Lock the audio file format](https://github.com/richardthornton/clipshift/issues/11)
- [#12 Lock frame pacing and the constant-frame-rate policy](https://github.com/richardthornton/clipshift/issues/12)
- [#13 Lock the performance budget and how it is measured](https://github.com/richardthornton/clipshift/issues/13)

**Blocked:** [#14 Spike the capture-to-encode pipeline on real hardware](https://github.com/richardthornton/clipshift/issues/14), waiting on #13. This is where the architecture meets real silicon — everything above it is paper.

### Watch for this when working #10

The encoder and container decisions are **coupled**, and neither ticket owns the coupling. NVENC returns an elementary bitstream, not a file. The encoder research recommends calling NVENC directly — but explicitly notes that *if* the container decision lands on libavformat, then `h264_nvenc` via libavcodec becomes the cheaper path, since FFmpeg would already be a dependency. Meanwhile the container research recommends reimplementing OBS's ~60-line soft-remux, which points away from libavformat. **Settle that interaction deliberately in #10 rather than letting it resolve by accident.** FFmpeg's LGPL/GPL split is a real (but bounded) consideration for this MIT-licensed project; the licensing analysis is in the encoder document.

## Prompt for the next session

```
/wayfinder https://github.com/richardthornton/clipshift/issues/1
```

That loads the map and takes the first frontier ticket. To pick a specific one, name it:

```
/wayfinder https://github.com/richardthornton/clipshift/issues/1 — work #9, Design the ClipShift window
```

Rules that matter: **resolve one ticket per session** (research tickets are the exception). Claim a ticket by assigning it to yourself *before* doing any work. Record a resolution as a comment on the ticket, close it, and append a one-line pointer to the map's Decisions-so-far. Do not restate a decision on the map — the map is an index, the ticket holds the detail.

## Caveats from this session, stated plainly

- **Five of the seven research agents were killed mid-flight by an API session limit.** All five had already written and committed their findings; only their resolution summaries were missing. I read each document's recommendation section directly, wrote the summaries from it, and closed the tickets. I did **not** read all ~5,000 lines. If a conclusion does not hold up on closer reading, reopen the ticket — the closure reflects my verification of each recommendation, not a full audit.
- **The sync document's final commit was work-in-progress** when its agent died: it was mid-way through replacing summarised ITU figures with verbatim ones. The committed state looks coherent and the numbers are consistent, but that section deserves a second look.
- **Nothing has been built.** There is no application code, no project file, no solution. That is correct — the destination is a spec.
- **Agent worktrees under `.claude/worktrees/` are gitignored** and can be deleted freely. The research branches are all pushed to origin, so nothing is lost by removing them.
