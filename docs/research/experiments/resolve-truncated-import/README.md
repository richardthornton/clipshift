# Experiment scripts for issue #15

These produced every number in
[`../../resolve-truncated-mp4-import.md`](../../resolve-truncated-mp4-import.md). They are kept so the
measurements can be repeated after a Resolve upgrade — the render failure on an unrepaired file is the
result most likely to change.

They are throwaway research scripts, not project code: paths to the working directory are hard-coded
near the top of each file and need editing before a re-run.

## Order

1. `run-crash.ps1` — starts `enc-video.cmd`, `enc-video-offset.cmd` and `enc-audio.cmd` together and
   kills all three ~30 s later with `taskkill /F`. The video carries a burned-in frame number and
   timecode; the audio carries a 30 ms 1 kHz click on every second boundary.
2. `enc-clean.cmd` — the never-truncated control pair.
3. `boxes.py <file>` — dumps the top-level box structure and flags the box whose declared size runs
   past EOF. This is what shows the kill landed mid-`mdat`.
4. `repair.py <src> <dst>` — the recovery step: keep through the last complete `moof`+`mdat`, truncate.
5. `patch.py wav <src> <dst>` — patch a killed WAV's `RIFF`/`data` sizes.
   `patch.py tfdt <src> <dst> <delta>` — add a start offset to every fragment's `baseMediaDecodeTime`.
6. `inject_elst.py <src> <dst> <offset_ms> <media_duration>` — inject an `edts`/`elst` into a
   fragmented file's init `moov`, sliding every absolute `base_data_offset` to match.

## Inside Resolve

`clipshift_15_test*.py` go in
`%APPDATA%\Blackmagic Design\DaVinci Resolve\Support\Fusion\Scripts\Utility\` and run from
`Workspace > Scripts`. The free edition refuses external scripting connections, so this is the only
route. Each writes a JSON report and renders test timelines.

- `_test.py` — import, clip properties, seek test
- `_test2.py` — repaired vs unrepaired renders, the `tfdt` offset, the damage boundary
- `_test3.py` — the two `elst` routes, plain and fragmented
- `_test4.py` — full-length audio alignment against the control

## Reading the results

`clicks.py <render>` reports where each click landed against its second boundary; validated against the
source WAV first, where clicks read 0.04 ms late (a two-sample threshold-crossing artifact), so that
value is the noise floor.

`crop-frames.cmd <render> <out.png> <n1> <n2> <n3> <n4>` crops the burned-in frame numbers from four
rendered frames into one image, which is how offset claims were checked in pixels rather than inferred
from metadata.
