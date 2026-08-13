# Which WAV containers and formats does Resolve free actually read?

Verification for [issue #11](https://github.com/richardthornton/clipshift/issues/11). Like
[`resolve-truncated-mp4-import.md`](resolve-truncated-mp4-import.md) and unlike the rest of
`docs/research/`, this is not a literature review: every claim below was measured on this machine
against the exact Resolve edition in doubt.

Date: 2026-08-13.

Environment: DaVinci Resolve **20.3.2.9, free edition**, Windows 11 Pro 26200; FFmpeg 8.0.1. Resolve was
driven by a Python script run from `Workspace > Scripts` — the free edition refuses external scripting
connections, so the API is only reachable in-process.

---

## Answer

**Every container and format the audio decision needs, Resolve free reads correctly.** Nothing was
disqualified. The three results that were genuinely in doubt:

1. **RF64 is read**, including a 4.10 GiB file, whose duration is reported exactly (`04:15:00:00`).
2. **A `JUNK` reservation ahead of `fmt ` is tolerated** — the file imports identically to a plain RIFF.
   This was the important one: it is the *common* case of the locked container design, so a failure here
   would have broken ordinary recordings rather than rare ones.
3. **Samples past the 4 GiB boundary decode correctly.** Reporting a 64-bit duration only proves the
   `ds64` chunk was parsed; it does not prove a sample at byte 4,294,967,296+ can be addressed. Rendered
   audio from 15,298 s into the big file is identical in level and spectral content to audio from its
   head.

Two further results that were not in doubt but are load-bearing, so they were measured anyway:

4. **BWF `TimeReference` is read into Start TC**, correctly above 2³¹ — a value of 2,505,600,000 samples
   appears as `14:30:00:00`. The belt-and-braces timecode path is real, not hopeful.
5. **An under-declared size field is honoured by stopping early**, with no error and no complaint. A file
   declaring one second less than it holds reports exactly one second less. This is ClipShift's steady
   state between header patches, and it confirms the asymmetry the container research asserted:
   under-declaring is safe, over-declaring is a bet on the reader.

---

## 1. What was tested

Eight WAV artifacts, all 48 kHz, built by `build-artifacts.ps1`. Content is a 1 kHz click on every second
boundary — the same generator as #15 — except the 4.10 GiB file, where 735 M samples of expression
evaluation was too slow and a continuous 1 kHz sine was used instead.

| Artifact | What it is | What it stands for |
|---|---|---|
| `ctl-riff-16-stereo.wav` | plain RIFF, 16-bit stereo, 30 s | control — the shape #15 already verified |
| `junk-riff-16-stereo.wav` | RIFF with a 28-byte `JUNK` ahead of `fmt ` | the locked container's steady state |
| `rf64-small-16-stereo.wav` | `RF64` + real `ds64` from byte zero, 30 s | RF64 parser, small file |
| `rf64-big-24-stereo.wav` | RF64, 24-bit stereo, 4 h 15 m, **4,406,400,138 bytes** | past the 32-bit ceiling |
| `mic-24-mono.wav` | plain RIFF, 24-bit **mono**, 30 s | the locked microphone format |
| `bext-tc-16-stereo.wav` | plain RIFF + BWF `bext`, `TimeReference` = 2,505,600,000 | timecode carrier |
| `riff-stale-16-stereo.wav` | control with sizes rewritten 1 s short | patch-cadence steady state |
| `rf64-stale-16-stereo.wav` | RF64 with `ds64` sizes rewritten 1 s short | same, in 64-bit fields |

The `TimeReference` value is 14:30:00.000 local expressed as a sample count (52,200 × 48,000). It is
deliberately above 2³¹ so that a signed-32 defect in the reader would surface as a wrong or negative
timecode rather than passing by luck.

FFmpeg's reading of the same files was recorded first, as the "charitable reader" baseline: it reports
the big file as 15300.000000 s and both stale files as 29.000000 s — stopping at the declared size
rather than erroring.

## 2. Import

All eight import. No refusals, no errors.

| Artifact | Duration | Channels | Sample rate | Start TC |
|---|---|---|---|---|
| `ctl-riff-16-stereo` | `00:00:30:00` | 2 | 48000 | `00:00:00:00` |
| `junk-riff-16-stereo` | `00:00:30:00` | 2 | 48000 | `00:00:00:00` |
| `rf64-small-16-stereo` | `00:00:30:00` | 2 | 48000 | `00:00:00:00` |
| **`rf64-big-24-stereo`** | **`04:15:00:00`** | 2 | 48000 | `00:00:00:00` |
| `mic-24-mono` | `00:00:30:00` | **1** | 48000 | `00:00:00:00` |
| **`bext-tc-16-stereo`** | `00:00:30:00` | 2 | 48000 | **`14:30:00:00`** |
| `riff-stale-16-stereo` | **`00:00:29:00`** | 2 | 48000 | `00:00:00:00` |
| `rf64-stale-16-stereo` | **`00:00:29:00`** | 2 | 48000 | `00:00:00:00` |

The stale pair reporting 29 s rather than 30 s is the intended result, not a defect: the header declares
29 s of data and Resolve believes it. Both the 32-bit and 64-bit paths behave the same way.

## 3. Decoding past 4 GiB

The 4 GiB boundary in `rf64-big-24-stereo.wav` falls at 04:08:33. Reporting `04:15:00:00` at import
proves only that the `ds64` chunk was read.

The test places a 120-frame **source range** of the big file at timeline position 0 over a 2-second video
bed, so probing the tail of a 4 h 15 m file costs one short render. Three cases, all rendered to
MP4/H.264 and analysed afterwards with FFmpeg rather than judged by the job status alone:

| Render | Source frames | Job | Whole-file peak | 1 kHz component |
|---|---|---|---|---|
| `ctl` | 0–119 of the click track | Complete, 100% | 19726, clicks at 0.010 s and 1.030 s | — |
| `big_head` | 0–119 | Complete, 100% | 2921, continuous | 2393.9 |
| **`big_tail`** | **917880–917999** (15,298 s in) | Complete, 100% | 2921, continuous | 2393.9 |

`big_tail` is identical to `big_head` on every measure. A 32-bit file-offset defect would have produced
silence or garbage there; it produced the same clean tone. The click track control confirms the harness
carries real audio through to the rendered file, so the identical readings are a measurement rather than
an artifact of a dead signal path.

> Unexplained and unimportant: the rendered sine peaks at −21.1 dBFS where the source is full-scale.
> Whatever attenuation Resolve's delivery path applies, it applies identically to head and tail, which is
> the only property this test depends on. Not investigated.

## 4. What this rules in

Every option that was on the table survives, so the decision was made on design grounds rather than by
elimination — see the resolution comment on
[#11](https://github.com/richardthornton/clipshift/issues/11) for the reasoning. What the measurements
settle:

- **RIFF-with-`JUNK` upgraded in place to RF64 is safe at both ends.** The pre-upgrade file is read as an
  ordinary WAV; the post-upgrade file is read correctly including past 4 GiB.
- **Writing RF64 unconditionally would also have worked.** It was not chosen, but it is a live fallback
  if the in-place upgrade proves awkward to implement, and this document is the evidence for that.
- **24-bit mono is a first-class format**, so a mono microphone can stay mono rather than being
  duplicated into a stereo file for compatibility's sake.
- **The 1-second header patch cadence is sound.** The worst a reader sees between patches is a file one
  second shorter than it truly is — reported cleanly, not as an error.
- **`bext` `TimeReference` is worth writing.** Resolve surfaces it as Start TC, which is what feeds
  *Auto Sync Audio → Based on Timecode*.

## 5. Limits of this test

- One machine, one Resolve build (20.3.2.9 free). The RF64 results are the ones most worth re-checking
  after an upgrade.
- **No file was killed mid-write here.** The stale artifacts model the patch-cadence steady state by
  rewriting size fields, which is byte-identical to what a patched-then-killed file looks like, but the
  kill path itself was exercised in #15 (on a plain RIFF WAV) rather than in this pass. The RF64 kill
  path — a process dying between the `JUNK`→`ds64` upgrade and the next patch — was not tested.
- **The in-place `JUNK`→`ds64` upgrade was not performed on a live file.** Both sides of it were tested
  as separate artifacts. The transition itself is ClipShift's code to write and its own thing to verify.
- The big file is a continuous sine, not gameplay. It proves offset addressing, not anything about
  content.
- Sample-accurate *alignment* was not re-measured; #15 settled that, finding no offset and no rate error
  across a clip.
