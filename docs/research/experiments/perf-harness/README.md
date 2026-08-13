# ClipShift performance harness

The measurement instrument for [issue #13](https://github.com/richardthornton/clipshift/issues/13)'s
performance budget. Built by
[#18](https://github.com/richardthornton/clipshift/issues/18); consumed by
[#14](https://github.com/richardthornton/clipshift/issues/14).

**#13 is the authority for the budget and the method.** This document covers only how to
operate the rig, and the things that are true of the rig rather than of the budget.

| File | What it is |
|---|---|
| `fetch-presentmon.ps1` | Fetches the pinned PresentMon CLI into `tools\` and verifies its SHA-256 |
| `benchmark_clipshift.xml` | GRID 2 scripted-benchmark control file, tuned for determinism |
| `hardware_settings_clipshift.xml` | GRID 2 graphics settings, tuned to make the load GPU-bound |
| `Run-Pairs.ps1` | Drives a whole interleaved A/B session as one command |
| `analyze.py` | The statistics: paired bootstrap, CIs, verdict against the budget |

---

## Why the load is GRID 2

#13 designed its load around a stated environment fact: that only six games were
installed and **no deterministic benchmark was available**, so repeatability had to be
bought statistically from a fixed camera in Train Sim World 6.

**That fact was wrong.** There is a second Steam library on `D:` holding 34 more
fully-installed titles, GRID 2 among them — 9.64 GB on disk, `StateFlags=4`, matching its
manifest exactly. The earlier probe looked only at the `C:` library's `common` folder and
read the `D:` entries as lingering manifests for uninstalled games.

GRID 2 changes the picture in three ways:

1. **It has a scripted benchmark** — `grid2.exe -benchmark <file.xml>` — so the load is
   replayed rather than performed. That is determinism bought outright instead of
   statistically.
2. **The benchmark file pins the graphics settings**, via its `hardwaresettings`
   attribute. The load is therefore fixed by this repo, not by whatever was last clicked
   in an options menu.
3. **It is a 2013 DX11 title**, so it exercises the older presentation path that #13
   explicitly noted nothing installed could reach ("everything installed is UE or Unity,
   so the frame-time results will be specific to flip-model presentation").

It also writes its own results file to `Documents\My Games\GRID 2\benchmarks`, which is a
second, independent measurement to sanity-check PresentMon against.

## Driving the load GPU-bound

This is the part most likely to go wrong, and it is worth understanding before running
anything.

#13 requires the load to be **GPU-bound at roughly 60 fps**, for a reason that is easy to
get backwards: **ClipShift's cost is fixed per wall-clock second, not per game frame.** It
paces at 60 fps CFR whatever the game presents — 60 acquires, 60 conversions, 60 encoder
submits every second, always. So that fixed cost divides across however many frames the
game produced. At 400 fps it is spread across 400 frames and looks about seven times
cheaper than it is.

**An uncapped run on a light load therefore flatters the result rather than being
conservative.** A 2013 racing game on a 5060 Ti is a light load by default — it will run
at several hundred fps at 1080p, which is precisely the flattering case.

`hardware_settings_clipshift.xml` raises every quality knob and sets 8x MSAA. If that is
still not enough to pull the frame rate down to ~60 — **and it very likely will not be** —
the remaining lever is **DSR**:

1. NVIDIA Control Panel → Manage 3D Settings → DSR - Factors → enable 2.25x and 4.00x.
2. Raise `<resolution width= height=>` in `hardware_settings_clipshift.xml` to the DSR
   resolution (2880×1620 for 2.25x, 3840×2160 for 4.00x).
3. Re-check the frame rate and adjust until it sits near 60.

DSR is the right lever specifically because it raises the *render* resolution while the
*displayed* surface stays 1080p. DXGI Desktop Duplication captures the displayed surface,
so the scenario under measurement is still "ClipShift capturing a 1080p display" — which
is the whole point. Raising the panel resolution instead would change what is being
captured and measure a different thing.

**If GRID 2 cannot be driven to ~60 fps GPU-bound by any of this, say so and stop.** A
number taken on a load running at 300 fps is not a conservative estimate of the 60 fps
case; it is roughly a fifth of it. That finding would send the load choice back for
another look rather than being worked around quietly.

## Running a session

### One-time setup

```powershell
.\fetch-presentmon.ps1
```

Then launch the game once normally so it creates `Documents\My Games\GRID 2`, and copy
`hardware_settings_clipshift.xml` next to the benchmark file you pass on the command line.

### Elevation

**PresentMon needs elevation** — it starts an ETW trace session, and without it the run
dies with `access denied`. `Run-Pairs.ps1` refuses to start rather than discovering this
half way through a two-hour session.

Run the whole session from one elevated PowerShell window: one prompt covers however many
runs it contains. The standing alternative, if elevating each time gets old:

```powershell
net localgroup "Performance Log Users" $env:USERNAME /add   # then sign out and back in
```

### The order of operations

**The A/A control comes first, and it is not optional** (#13 §5.5). It is the only thing
that will tell you the harness works before you spend hours generating numbers with it.
This is the #11 lesson made procedural: four failed passes there were diagnosable only
because a known-good control failed identically.

```powershell
# 1. Start OBS and begin streaming.
# 2. Start the load and leave it looping:
#      grid2.exe -benchmark <full path to benchmark_clipshift.xml>
# 3. From an ELEVATED PowerShell:

.\Run-Pairs.ps1 -Mode AA -Pairs 5 -Seconds 120
python analyze.py .\results\AA-<stamp>
```

The A/A run records both arms with ClipShift off, so the true difference is **zero by
construction**. Whatever spread comes out is the noise floor. `analyze.py` will say
plainly whether that floor is tight enough to resolve #13's 0.30 ms budget — and if it is
not, the fix is more pairs, longer runs or a steadier load, not proceeding and hoping.

Once the spike from [#19](https://github.com/richardthornton/clipshift/issues/19) exists:

```powershell
.\Run-Pairs.ps1 -Mode AB -Pairs 5 -Seconds 120 `
    -ClipShiftCmd ..\..\..\..\spike\spike.exe `
    -ClipShiftArgs '--variant dda-release --preset p5' -Label dda-release-p5

python analyze.py .\results\AB-dda-release-p5-<stamp> --floor .\results\AA-<stamp>
```

`-ClipShiftArgs` is how #14's capture variants and the p4/p5/p6/p7 preset sweep get swept
without rebuilding between runs — rebuilding mid-session would break the interleaving.

## What the harness does and does not decide

`Run-Pairs.ps1` collects; `analyze.py` decides. They are separate so the statistics can be
recomputed from the CSVs without putting the machine through another two hours.

The verdict is taken against the **95% CI's upper bound, never the point estimate**
(#13 §5.3). A mean of 0.25 ms whose interval reaches 0.9 ms has not passed.

A metric whose CI sits inside the noise floor is reported **INCONCLUSIVE, not PASS**. This
matters more than it sounds: an instrument that cannot see a difference will report no
difference, and calling that a pass is how a harness launders its own blind spot into a
green light.

### Things worth knowing about the statistics

- **The bootstrap resamples pairs, not frames.** The pair is the unit of independent
  replication, because both arms of a pair share a thermal and clock state by
  construction. Resampling frames would treat 7,200 correlated samples as 7,200
  independent ones and produce absurdly tight intervals.
- **The seed is fixed** (`20260813`), so a given session and `--boot` always give the same
  answer.
- **Pin `--boot` when comparing sessions.** Near a threshold the verdict can flip between
  iteration counts, because the floor and the CI bound are both Monte Carlo estimates. The
  20,000 default is high enough that this is not a practical concern away from the
  boundary; it was observed in synthetic testing with a deliberately knife-edge floor.
- **`--v1_metrics` is passed deliberately.** #13's budget is written in v1 column names
  (`MsBetweenPresents`, `MsBetweenDisplayChange`, `Dropped`) and PresentMon 2.x defaults to
  a v2 metric set with different columns. `analyze.py` fails loudly rather than guessing if
  the columns it needs are absent.

### Verified before first use

`analyze.py` was exercised against synthetic sessions with known injected answers — a
clean A/A, an A/B with a small difference, and an A/B with +0.85 ms and deliberate p99
spikes and dropped frames. It recovered each, and correctly refused to call the small
difference a result. The generator is not committed; it is a dozen lines of `random.gauss`
and rebuilding it is cheaper than maintaining it.

**Nothing here has yet been run against real hardware.** The A/A control on the real
machine is the first thing that will exercise PresentMon, the elevation path, the GRID 2
benchmark invocation and the CSV columns together.
