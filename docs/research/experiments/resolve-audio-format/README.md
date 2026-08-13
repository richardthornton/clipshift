# Experiment scripts for issue #11

These produced every number in [`../../resolve-audio-format.md`](../../resolve-audio-format.md). They
are kept so the measurements can be repeated after a Resolve upgrade — the RF64 results are the ones
most likely to change.

Throwaway research scripts, not project code: `SCRATCH` is hard-coded near the top of each Resolve
script and needs editing before a re-run.

## Order

1. `build-artifacts.ps1 <dir>` — writes seven of the eight WAVs. Takes about a minute; the 4.10 GiB
   file dominates. Also needs a short video bed for pass 4:

   ```
   ffmpeg -f lavfi -i "testsrc2=size=1920x1080:rate=60:duration=2" -c:v libx264 -preset veryfast -pix_fmt yuv420p vid-120.mp4
   ```

2. `stale.py <src> <dst> [seconds_short]` — rewrites a WAV's size fields to declare less audio than the
   file holds, modelling the steady state between ClipShift's header patches. Handles both the 32-bit
   `RIFF`/`data` fields and RF64's `ds64`. Run it twice:

   ```
   python stale.py ctl-riff-16-stereo.wav  riff-stale-16-stereo.wav
   python stale.py rf64-small-16-stereo.wav rf64-stale-16-stereo.wav
   ```

## Inside Resolve

`clipshift_11_test*.py` go in
`%APPDATA%\Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts\Utility\` and run from
`Workspace > Scripts`. The free edition refuses external scripting connections, so this is the only
route. Each writes a JSON report beside the artifacts.

- `_test.py` — imports all eight, dumps every clip property. **This is the pass that carries the
  import results.**
- `_test2.py`, `_test3.py` — two failed attempts at the render test, kept because the failures are
  informative rather than embarrassing. See below.
- `_test4.py` — the 4 GiB decode test that worked.

## The render harness, and why it took three tries

Passes 2 and 3 both failed with `AddRenderJob` returning an empty string for *every* case including the
control — which is exactly why a harness control is worth carrying. Two separate causes:

- **`GetRenderCodecs('wav')` returns `{}`.** Audio-only export in Resolve is a render *setting*
  (`ExportVideo: False`), not a format-plus-codec pair, so `SetCurrentRenderFormatAndCodec('wav', …)`
  can never succeed. Pass 2 also guessed the identifier wrong: `GetRenderFormats()` returns
  `{display name: extension}`, and it is the **extension** the API wants — `Wave` maps to `wav`.
- **MP4/H.264 will not take a render job for a timeline with no video track.** An audio-only timeline
  silently produces no job.

Pass 4 works around both by giving the timeline a 2-second video bed and appending the audio as a
**source range**, so the region of interest sits at timeline position 0 and only 120 frames render. That
is what makes probing 15,298 s into a 4 h 15 m file cost about a second.

## Reading the results

Render job status is necessary but not sufficient — #15 established that Resolve will import and scrub a
file it later refuses to export, and a render can equally "succeed" while producing silence. The rendered
audio is measured, not trusted:

```
ffmpeg -v error -i r4_big_tail.mp4 -map 0:a -c:a pcm_s16le -ar 48000 r4_big_tail.wav
```

then compare whole-file peak and the 1 kHz component (Goertzel) between `r4_big_head` and `r4_big_tail`.
They should be identical. The click-track control should show its clicks about one second apart, proving
the path carries real audio.
