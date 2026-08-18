# Settling the resampler: `WdlResampler` against #16's quality bar

Experiment for [#20](https://github.com/richardthornton/clipshift/issues/20).
[#16](https://github.com/richardthornton/clipshift/issues/16) chose NAudio's `WdlResampler`
**with a decision rule attached, not a settled outcome**: clear ≥ 110 dB SNR near unity and
≥ 100 dB at 44.1 → 48, measured, or the libsamplerate fallback fires. It put the odds at
roughly 60/40 and noted nobody publishes an SNR for WDL.

This directory is the measurement.

## Headline

**`WdlResampler` clears the bar, and the fallback does not fire.** But the reason it clears
is not the one #16 assumed, and the setting that gets it there is not the one anyone would
have picked from reading the API.

| | |
|---|---|
| **Implementation** | **`WdlResampler`, vendored.** libsamplerate is not needed. |
| **Setting** | `SetMode(true, 0, true, 256, 1024)` — **256 taps, 1024-phase table** |
| **Worst gated SNR** | **125.4 dB** at 44.1 → 48; 131–132 dB near unity. Bar is 100 / 110. |
| **Group delay D** | **0.** Not "small" — zero, at every setting and every ratio. |
| **Cost** | **~2 % of one core** per sink, 1 MB of coefficient table |
| **Steady-state allocation** | **0 bytes**, measured over 200 s of pulls with a live drifting ratio |

Everything below is reproducible with `dotnet run -c Release`; the full output of the run
these numbers come from is in [`results/run.log`](results/run.log), and the whole SNR matrix
is in [`results/snr.csv`](results/snr.csv).

## The finding that matters: the quality knob is the phase table, not the filter

`SetMode(interp, filtercnt, sinc, sinc_size, sinc_interpsize)`. `sinc_size` is the filter
length in taps and is what looks like the quality parameter. `sinc_interpsize` is the number
of phases the sinc is pre-computed at; between phases WDL **linearly interpolates** the
coefficients.

That linear interpolation is the entire error budget. Worst-case SNR across the gated
operating points, from the sweep in [`results/run.log`](results/run.log):

| taps ↓ / phases → | 32 | 64 | 128 | 256 | 512 | 1024 | 4096 |
|---|---|---|---|---|---|---|---|
| **64** | 76.4 | 88.3 | 100.7 | 107.5 | 107.5 | 107.5 | 107.5 |
| **128** | | | 100.7 | | | 104.6 | 104.6 |
| **256** | | | | | | **125.4** | |
| **512** | | | 100.7 | | | | |
| **1024** | | | 100.7 | | | | |
| **2048** | | | | 113.2 | | | |
| **8192** | | | | | | | 144.4 |

Read the rows: at 128 phases, going from 64 taps to 1024 taps — a 16× increase in cost —
buys **0.04 dB**. Read the columns: at 64 taps, going from 32 phases to 256 phases buys
**31 dB**, and costs essentially nothing, because a phase table is memory rather than
arithmetic. Twelve dB per doubling of the phase count is the exact signature of linear
interpolation error, which falls as 1/L².

The practical consequence is large. The ticket said to "test at WDL's longest sinc setting;
if it misses there, it misses" — and 8192/4096 does reach 144 dB, but it costs **over 220 % of a
core**, i.e. it cannot run at all. Had the sweep only varied `sinc_size`, as the API's naming
invites, the conclusion would have been that WDL clears the bar only at a setting that does
not fit the machine, and the fallback would have fired for no reason.

`sinc 256/1024` is the recommendation rather than the cheapest passing setting
(`sinc 64/256`, 107.5 dB at 0.3 % of a core) because 64 taps saturate at 107.5 dB at
44.1 → 48 no matter how large the phase table gets — that is the filter length asserting
itself — leaving only 7.5 dB over the bar. 256/1024 clears every gate by more than 15 dB at
a cost that is still noise against #13's 0.5-core budget, and a 1 MB table against a 300 MB
working set. If the CPU ever needs reclaiming, `sinc 64/512` is the fallback within the
fallback: 126 dB near unity, 107.5 dB at 44.1 → 48, 0.4 % of a core.

## D = 0, and what that does to #16's pre-roll

#16 established that group delay is a **one-time pre-roll**: push D samples through at init,
discard the first D outputs, after which output sample *n* is master time `T0 + n/R` exactly.
It listed the value of D as unknowable from documentation and therefore a deliverable here.

**D is zero.** An impulse at input frame 1024 emerges at output frame `1024/ratio` — 1024 at
unity, 1115 at 44.1 → 48 (1114.6 exact), 1536 at 32 → 48, 512 at 96 → 48 — with a sub-sample
residual never exceeding 0.14 frames at any setting or ratio tested.

The mechanism is in `ResamplePrepare`: on the first call, WDL pre-pads its own input buffer
with `sinc_size/2 - 1` zeros before the caller's first sample. It has already done the
pre-roll. The caller's input therefore sits centred under the filter from the first output
sample onward.

So the pre-roll #16 specifies is **already satisfied**, and the requirement it created —
"D must be knowable and asserted, not reported" — becomes an assertion that D stays 0. That
is what `Driver.MeasureDelay` does, and it belongs in CI exactly as #16 argued: it is the
test that would have caught soxr's stubbed `vr_delay` immediately, and it is equally the test
that catches a future WDL revision quietly changing its head padding.

**This does not touch #5's per-sink manual offset**, which absorbs fixed *acquisition*
latency in hardware. D is filter delay. One being zero says nothing about the other.

## The allocation fix

`ResamplePrepare` calls `Array.Resize(ref m_rsinbuf, …)` on **every** call. Under #16's
control loop the ratio is continuously corrected, so the requested length jitters, so the
resize reallocates nearly every time.

Measured, patched against a pristine copy of the same class doing identical work — 20 000
pulls of 480 stereo frames with the ratio re-set every pull:

| | bytes over 200 s of audio |
|---|---|
| pristine `WdlResampler` | **752 112** — 38 bytes per pull |
| ClipShift's copy | **0** |

38 bytes per pull sounds small; it extrapolates to **52 MB of garbage per four-hour session,
per sink**, on the one hot path the project has a hard constraint about. The fix is a
grow-only high-water-mark buffer; the high-water mark is reached during warm-up and the
counter never moves again (`0 buffer growths` across the measured run).

A second offender turned up alongside it. `BuildLowPass` rebuilds the entire Blackman-Harris
windowed-sinc table whenever the requested cutoff differs from the last one **by even one
ULP** — and `ResampleOut` calls it on every pull with a cutoff derived from the live ratio.
That is not an allocation, it is worse: at `sinc 256/1024` it is a quarter-million `sin`/`cos`
pairs per pull. Gating it on a relative epsilon of 1e-4 cuts rebuilds from **275 to 3** over a
5.5-second sweep and is worth **12.3× throughput at the candidate setting** — up to 30× at
large phase tables.

The gate is the one patch that changes what the resampler computes rather than only how it
allocates, so it is measured rather than argued: identical input and identical ratio schedule
through gated and ungated code, across a ±50 ppm sweep that crosses 1.0 in both directions,
differ by **−148.3 dB RMS**. That is roughly 20 dB below the resampler's own approximation
error at that setting. (Peak difference is ~1.5 LSB at 24-bit, so the claim is "far below the
error already present", not "bit-identical".)

## Two smaller findings

**WDL applies a 3 % guard band only when the ratio exceeds 1.0.** `ResampleOut` calls
`BuildLowPass(1.0 / (ratio * 1.03))` when `ratio > 1` and `BuildLowPass(1.0)` otherwise. Under
#16's control loop the ratio sits at 1.0 ± 50 ppm and crosses continuously, so the output is
band-limited to ~23.3 kHz whenever the device runs fast and to 24 kHz whenever it runs slow.
Both are above the audible band and the transition is measured above as inaudible, so nothing
needs doing — but it explains why the impulse peak reads 0.967 in one drift direction and
0.996 in the other, and it should not be rediscovered as a bug later.

**The output-side IIR filter ignores its output offset.** In `ResampleOut`, the
`m_filtercnt > 0` post-filter pass calls `m_iirfilter.Apply(outBuffer, x, outBuffer, x, …)`,
starting at index `x` rather than at `outBufferIndex`. Writing output at a non-zero offset
with `filtercnt > 0` and `ratio < 1` therefore corrupts the head of the buffer. ClipShift is
unaffected — `SetMode` zeroes `filtercnt` in sinc mode — but the vendored copy carries the
bug, and the non-sinc modes must not be used at an offset.

## Trusting the numbers

An SNR harness that measures the wrong thing produces confident, wrong decisions, and the
first version of this one did. Two guards:

**The port is validated against libsamplerate's own published floors.** WDL's linear and
point-sampling modes are the same algorithms as `SRC_LINEAR` and `SRC_ZERO_ORDER_HOLD`, and
`snr_bw_test.c` states the SNR its suite asserts for each at four ratios. The port reproduces
all eight (measured 36.8/37.4/36.6/38.8 against floors 28/36/36/38 for zero-order hold;
74.3/74.9/74.0/89.9 against 73/73/73/77 for linear). Whatever these numbers are, they are the
same numbers libsamplerate publishes its own with.

**The test tone had to be moved up.** libsamplerate's ratio tests use a 0.0111 cycles/sample
tone — 533 Hz at 48 kHz. At that tone *every* WDL setting measured 140–150 dB, including
64 taps at 32 phases, which is not credible. Near unity the fractional phase advances by
~5e-5 per output frame, so the interpolation error is a very slowly varying modulation whose
sidebands land in the bins immediately either side of the carrier — precisely where
`calc_snr`'s side-lobe smoothing wipes them out. Repeating at 0.3511 cycles/sample (16.85 kHz,
libsamplerate's own high-frequency test tone) collapsed 64/32 from 149 dB to 78 dB and
produced the phase-table result above. **The low tone hides the defect this ticket exists to
find.** Both tones are run and the worst case is reported.

Two further checks: re-running the gates over 2^18 output frames instead of 2^15 moves the
result by under 2 dB (so the short block is not flattering the near-unity case), and
libsamplerate's `varispeed_test.c` bounds sweep — hard ratio switches of 0.1 / 0.01 / 20
mid-stream across 1 to 9 channels, 54 cases — terminates with finite output in every case.

The FFT is a radix-2 transform in `Fft.cs` rather than FFTW, which is the only substitution
made in the port. Every transform length here is a power of two by construction.

## What is not settled

- **No CPU measurement under load.** The throughput figures are an idle-machine
  single-thread rate. #13's budget is measured with PresentMon against a running game; the
  audio path has never been in one of those runs. Section 5's numbers say the cost is ~2 % of
  a core, which is comfortably inside the budget, not that the budget has been re-measured.
- **Only sinc mode is characterised.** Linear and point-sampling appear solely as harness
  validation. ClipShift will never use them.
- **Only single tones.** No multi-tone intermodulation case, and no bandwidth (−3 dB rolloff)
  measurement — `snr_bw_test.c`'s `bandwidth_test` was not ported, since a fixed 48 kHz
  output makes the rolloff point a property of the filter rather than a choice.
- **The 44.1 → 48 tap saturation is unexplained.** 64 taps stop at 107.5 dB there whatever the
  phase count, and 128 taps measure *worse* than 64 (104.6 dB). Both clear the bar and
  256 taps clear it by 25 dB, so nothing hangs on it, but the non-monotonicity was not chased.

## Layout

```
Vendor/WdlResampler.cs            the vendored, patched copy -- what ClipShift ships
Vendor/Baseline/                  a pristine copy, referenced only by the allocation baseline
Fft.cs                            radix-2 FFT, standing in for FFTW
SnrCalculator.cs                  port of libsamplerate calc_snr.c (BSD-2)
SignalGen.cs                      port of gen_windowed_sines from util.c (BSD-2)
Driver.cs                         WDL configuration, the output-driven pull loop, impulse delay
Tests.cs                          the eight measurements
Program.cs                        sweep definitions and the verdict
results/run.log                   full output of the run quoted above
results/snr.csv                   the complete SNR matrix
```

## Licensing

`WdlResampler` is NAudio (MIT), itself a port of the Cockos WDL resampler (zlib-style). The
vendored copy is marked as an altered source version per clause 2 of that licence, with the
modifications listed in its header. `SnrCalculator.cs` and `SignalGen.cs` are ports of
libsamplerate test sources, which are **BSD-2** — the relicensing #17 established, and the
reason they are directly reusable here. Nothing in this directory obliges ClipShift beyond
attribution, and no GPL or LGPL code is involved.
