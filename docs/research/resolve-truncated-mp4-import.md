# Does Resolve import a crash-truncated fragmented MP4?

Verification for [issue #15](https://github.com/richardthornton/clipshift/issues/15). Unlike the other
documents under `docs/research/`, this one is not a literature review: every claim below was measured
on this machine, against the exact Resolve edition in doubt. Where a result is inferred rather than
observed it says so.

Date: 2026-08-13.

Environment: DaVinci Resolve **20.3.2.9, free edition**, Windows 11 Pro 26200; FFmpeg 8.0.1
(`h264_nvenc`); artifacts written to local disk. Resolve was driven by a Python script run from
`Workspace > Scripts` — the free edition refuses external scripting connections, so the API is only
reachable in-process.

---

## Answer

**Yes, with one mandatory repair step and one design constraint.**

Resolve imports a fragmented MP4 whose writer was killed mid-fragment, reports its duration correctly,
and seeks anywhere in it including inside the damaged tail. It will **not export it**: a render that
touches the final partial fragment fails outright with *"Error decoding full resolution media"*. A
render bounded to the last complete fragment succeeds, so the damage is confined exactly where the
byte layout predicts. Dropping the trailing partial fragment — a file copy and a `truncate`, no
media bytes rewritten — produces a file Resolve renders end to end at 100%.

**The design constraint is where the start offset lives.** A start offset carried in `tfdt`
(`baseMediaDecodeTime`) is silently ignored by Resolve while FFmpeg honours it — the worst outcome
available, because the file imports, plays, and sits a fixed number of frames early against its audio
with nothing reported. The same offset expressed as an `elst` empty edit is honoured exactly, including
in a fragmented file, where the edit list lives in the init segment and is therefore on disk before any
crash. **ClipShift's muxer must write the offset as an `elst` in the init segment. It must not rely on
`tfdt`.**

---

## 1. What was tested

Four artifacts, all 1920×1080, 60 fps, H.264 High, `CONSTQP` qp 20, zero B-frames, 60-frame keyframe
interval and fragment cadence — the format locked in [#10](https://github.com/richardthornton/clipshift/issues/10).
The video carries a burned-in frame number and timecode so a start-offset error is *visible* rather
than inferred, per the ticket's requirement. The audio carries a 30 ms 1 kHz click starting exactly on
every second boundary.

| Artifact | How it was made | What it stands for |
|---|---|---|
| `killed.mp4` | `-movflags frag_keyframe+empty_moov`, process killed with `taskkill /F` at ~30.4 s | the crash case |
| `killed.wav` | PCM s16le, killed at the same instant | the audio crash case |
| `killed-repaired.mp4` | `killed.mp4` truncated to the last complete `moof`+`mdat` | ClipShift's recovery step |
| `killed-patched.wav` | `killed.wav` with `RIFF`/`data` sizes patched | ClipShift's WAV recovery |
| `killed-tfdt-offset.mp4` | `killed.mp4` with +3840 added to every `tfdt` (0.25 s = 15 frames) | offset carried in `tfdt` |
| `offset-elst.mp4` | remuxed non-fragmented, empty edit of 250 ms | offset in a soft-remuxed file |
| `frag-elst.mp4` | `killed-repaired.mp4` with an `edts`/`elst` injected into its init `moov` | offset in a *fragmented* file |
| `clean.mp4` / `clean.wav` | same settings, closed cleanly | never-truncated control |

The kill left the file exactly as predicted by the container research: 30 complete `moof`+`mdat` pairs,
then a `moof` whose `mdat` declares 3,342,209 bytes with only 3,297,116 present. No `mfra`, no `sidx`,
no `elst` — an fMP4 written by FFmpeg carries no edit list at all, which is why `tfdt` was the obvious
candidate for the offset and why it had to be tested.

## 2. Import and duration

All four files import. Resolve identifies the truncated video as **H.264 High L4.2, 1920×1080, 60 fps,
1800 frames, `00:00:30:00`** — i.e. it trusts the fragment headers and counts the frames the last
`trun` *declares*, including those whose bytes never reached disk. FFmpeg decodes 1799 of them and
conceals the error.

The truncated WAV imports **with its size fields still `0xFFFFFFFF`**, reported as `00:00:30:02`,
identical to the patched copy. Patching remains worth doing for other readers, but Resolve does not
need it.

## 3. Seeking

`SetCurrentTimecode` was issued at `00:00`, `00:15`, `05:00`, `20:30` and `29:00` on the truncated
clip; every one landed on the exact frame requested, including positions inside the damaged final
fragment. Resolve does **not** treat the fragmented file as a non-seekable stream — the failure mode
OBS documented for Windows Media Player does not occur here.

## 4. Export — where it actually breaks

| Render | Range | Result |
|---|---|---|
| whole truncated clip | 0–1799 | **Failed** at `01:00:29:48`: *"Error decoding full resolution media for killed-tfdt-offset.mp4 … Please check that the file is accessible and has a valid codec."* |
| truncated clip, last two seconds | 1680–1799 | **Failed** |
| truncated clip, ending at the last complete fragment | 1620–1739 | **Complete** |
| repaired clip, full length | 0–1739 | **Complete**, 100% |

This is the single most important operational result. An editor who imports an unrepaired file will
scrub it happily, cut with it, and then discover at export time that the render dies — potentially
hours later. The repair is not optional polish; it is what makes the file usable.

The repair itself is trivial: walk the top-level boxes, keep everything through the last complete
`moof`+`mdat` pair, truncate. On the test file that discarded 3,297,468 bytes of a 101 MB file — one
second of video — and the result decodes clean in FFmpeg (1740 frames, no errors) and renders 100% in
Resolve.

## 5. Where the start offset can live

This is the part the ticket flagged as *"silent and plausible"* if wrong, so it was measured in pixels:
the rendered frame at each position was read back and its burned-in frame number compared to the
timeline position.

| Offset carried in | FFmpeg reports | Resolve behaviour |
|---|---|---|
| `tfdt` `baseMediaDecodeTime` | `start_time=0.250000` | **Ignored.** Rendered frame *n* = source FRAME *n*, no lead-in |
| `elst` empty edit, plain MP4 | `start_time=0.250000` | **Honoured.** Clip reports 1755 frames (1740 + 15); rendered frames 0–14 are blank, FRAME 0 lands at rendered frame 15 |
| `elst` empty edit, fragmented MP4 | `start_time=0.250000` | **Honoured in playback and render** — a constant 15-frame shift with no drift (rendered 75 → FRAME 60, rendered 119 → FRAME 104) |

The one wrinkle in the fragmented case: Resolve honours the edit for placement but still reports the
clip as 1740 frames / `00:00:29:00`, not 1755. The offset is applied; the reported length simply does
not include the empty head. That inconsistency is cosmetic for ClipShift's purposes but is worth
knowing before someone tries to derive sync from the reported frame count.

**Consequence for the muxer:** write `edts`/`elst` into the initialisation `moov`, before any fragment.
It costs 48 bytes, it is on disk from the first write, and it survives a kill at any later instant.

## 6. Audio alignment

The click track makes alignment measurable rather than eyeballed. Detection was validated against the
source WAV first: clicks land at 0.04 ms past each second boundary (a two-sample threshold-crossing
artifact of the sine ramp), so anything at that level is measurement noise, not drift.

Rendering the repaired video against the patched WAV over its full length, **all 30 clicks land at
0.04 ms past their second boundary — the source value — with the last at 29.00004 s.** The clean
control behaves identically. There is no offset and no accumulating rate error: the WAV is placed
sample-accurately and Resolve neither resamples nor nudges it.

Video holds position over the same span. Rendered frame 1739 carries burned-in FRAME 1739 (`28:59`),
the last frame that survived the kill, and frame 1740 is black where the video ends and the
longer audio continues. Frame *n* sits at position *n* from the first frame to the last, against audio
that is exact at every second boundary.

In the `frag-elst` render, video is shifted +15 frames while audio is not, which is precisely the
intended behaviour: source FRAME 60, encoded at t = 1.0 s in the video's own timeline, appears at
rendered time 1.25 s.

> The first click in every render reads 9.6 ms late. That is AAC encoder priming in Resolve's own
> delivery encoder attenuating the first frame, not a media offset — it appears identically in the
> never-truncated control, and every subsequent click is exact.

## 7. Incidental finding for the muxer

FFmpeg's fragmented MP4 writes an **absolute** `base_data_offset` in every `tfhd`. Inserting the 48-byte
`edts` into the init `moov` shifted the file and invalidated all 29 of them, producing "Invalid NAL unit
size" errors until each was patched by the same delta.

ClipShift's own muxer should set the `default-base-is-moof` flag instead, making every fragment
self-locating. Any repair, header patch, or in-place edit then becomes a byte-count change rather than
a rewrite — which matters directly, because the soft-remux at stop and the crash repair both change
byte positions near the head of the file.

## 8. Limits of this test

- One machine, one Resolve build (20.3.2.9 free). A Resolve update could change decoder behaviour;
  the render failure in §4 is the result most worth re-checking after an upgrade.
- The kill was `taskkill /F` on FFmpeg, not a power loss or a bugcheck. That matches the ticket's
  scope — a killed process — and matches what the container research says is the realistic case, but
  it does not exercise a loss of the OS cache.
- Content was `testsrc2`, not gameplay. Bitrate and fragment sizes will differ; the box structure
  will not.
- The alignment span was 29 seconds, not four hours. What that measurement settles is that Resolve
  introduces neither a fixed offset nor a rate error at import — which is the failure mode it could
  introduce. Drift *within* ClipShift's own files across a long session is a different question, owned
  by the sync design and unaffected by anything measured here.
- The offset tested was 250 ms. Sub-frame offsets — the actual output of a QPC epoch — were not
  tested, and `elst` in a movie timescale of 1000 cannot express one exactly. Whether Resolve rounds
  or truncates a sub-frame edit is an open question for
  [#12](https://github.com/richardthornton/clipshift/issues/12).
