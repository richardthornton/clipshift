# Sample-rate conversion options for an MIT-licensed .NET 8 app on Windows

Research for [issue #17](https://github.com/richardthornton/clipshift/issues/17) — which SRC
implementations ClipShift can actually use, and what each one obliges.

**This is a survey, not a recommendation.** The choice belongs to
[#16](https://github.com/richardthornton/clipshift/issues/16). What follows lays out the options, ranks
them within each criterion where the evidence supports it, and says plainly where an option is
disqualified by a hard constraint.

Date: 2026-08-13. Every claim carries a URL to the source that owns it. Sources are licence files in the
projects' own repositories, the projects' own headers and source code, their own documentation, GNU's own
licence text, and Microsoft Learn. Where a primary source could not be found, the claim is labelled
**[unverified]** rather than filled in from secondary material. Nothing here was measured on the
reference machine; there are no `[measured]` claims in this document, and §10.1 says what that costs.

---

## 0. The result in one table

The two hard filters come first, because they eliminate more candidates than licensing does.

| Implementation | Licence (from its own licence file) | Ratio adjustable *while streaming*? | Group delay exposed? | Verdict against ClipShift's filters |
|---|---|---|---|---|
| **libsamplerate** (Secret Rabbit Code) | **BSD-2-Clause** since 0.1.9 — *not* GPL | **Yes** — `src_ratio` per `src_process` call (smoothed) or `src_set_ratio` (step) | **No API at all** | Survives the ratio filter; **fails the group-delay requirement as an API** |
| **soxr** (constant-rate engine) | **LGPL-2.1-or-later** | **No** — rate fixed at `soxr_create` | Yes — `soxr_delay()` is computed for real | **Disqualified on the ratio filter** |
| **soxr** (`SOXR_VR` variable-rate engine) | LGPL-2.1-or-later | **Yes** — `soxr_set_io_ratio(p, ratio, slew_len)`, with a built-in slew | **No — `vr_delay()` is a hard-coded `return 100; /* TODO */`** | Survives the ratio filter; **the delay it reports is a stub**, and quality is fixed and *not* SOXR_HQ |
| **FFmpeg libswresample** | LGPL-2.1-or-later (LGPL-3 with `--enable-version3`; GPL only with `--enable-gpl`) | **Yes** — `swr_set_compensation(s, sample_delta, distance)`, allocation-free after the first arming | **Yes — `swr_get_delay(s, base)`**, the exact call OBS uses | Survives both filters. Heaviest dependency. |
| **Media Foundation Audio Resampler DSP** | Ships with Windows; no redistribution, no licence cost | **No** — ratio is the media type, and changing it mid-stream is explicitly an error condition | Not exposed | **Disqualified on the ratio filter** |
| **WASAPI `AUTOCONVERTPCM` / `SRC_DEFAULT_QUALITY`** | Ships with Windows | Not applicable — it is not a controllable resampler | Not exposed | **Already ruled out by [#11](https://github.com/richardthornton/clipshift/issues/11)** — it absorbs the drift the design must measure |
| **speexdsp resampler** | **BSD-3-Clause** (Xiph) | **Yes** — `speex_resampler_set_rate_frac(num, den, …)` | **Yes** — `get_input_latency()` / `get_output_latency()` | Survives both filters, but **every ratio change regenerates the sinc table** |
| **Cockos WDL `WDL_Resampler`** | zlib-style permissive, *or* LGPL-2+ at your option | **Yes** — `SetRates(double, double)`, no filter rebuild, no allocation | Partly — `GetCurrentLatency()`, different semantics from `swr_get_delay` | Survives both filters |
| **NAudio `WdlResampler`** (managed C# port of the above) | **MIT** (NAudio), WDL notice retained | **Yes** — same `SetRates(double, double)` | Same as WDL | Survives both filters; **no native DLL at all**, but `ResamplePrepare` calls `Array.Resize` per call |
| **r8brain-free-src** | **MIT** | **No** — rates are constructor arguments; the whole multi-stage filter chain is designed at construction | `getLatency()` returns 0 (latency removed internally) | **Disqualified on the ratio filter** |
| **miniaudio** | Public domain (Unlicense) **or** MIT-0 | Not assessed in the detail the others were — **[unverified]** | Not assessed — **[unverified]** | Not assessed; see §9 |
| **Write one from scratch** | ClipShift's own, MIT | By construction | By construction | Possible; §8 is about what it actually costs, which is mostly the test rig |

Two of the ticket's premises turned out to be wrong, and both change the shape of the decision:

1. **libsamplerate is not GPL.** It was relicensed to 2-clause BSD in **0.1.9, 2016-09-23**. The presumed
   licence blocker does not exist.
2. **soxr's variable-rate mode is a different, lower-quality engine from its famous one**, it ignores the
   quality spec entirely, and it does not report its true delay. "soxr is high quality" is a true
   statement about the engine ClipShift *cannot* use.

---

## 1. What this resampler actually has to do

Restated from the settled documents, because these are the filters, not preferences.

- **Output is computed, not measured.** [`av-sync-strategy.md`](av-sync-strategy.md) §5.3: every 10 ms of
  master clock, `need = round(R × (t − T0)/1e9) − written_total`, and the resampler is asked for exactly
  `need` output sample-frames. The resampler is **output-driven**. An input-driven API is usable but has
  to be wrapped.
- **The ratio is a control variable, not a configuration value.** §10.5: "The resampler's ratio is
  adjusted slowly (a PI loop with a time constant of seconds) to hold input-queue occupancy at a ~50 ms
  setpoint." A resampler whose ratio is fixed at init cannot do this job **whatever its licence**.
- **The ratio excursions are tiny.** Microsoft's own figure for endpoint crystal error is ±30–50 ppm
  ([`av-sync-strategy.md`](av-sync-strategy.md) §1.2), so the drift-lock ratio lives in
  `1 ± 5×10⁻⁵`. This is not varispeed. It matters because several implementations rebuild their filter
  bank when the ratio changes, and at this excursion the filter is functionally identical each time —
  pure waste.
- **Group delay must be knowable.** §10.5: "That delay must be subtracted when mapping output sample
  index to master-clock time, or every audio file acquires a constant offset equal to the filter delay."
  OBS does exactly this, using `swr_get_delay`
  ([`libobs/media-io/audio-resampler-ffmpeg.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/media-io/audio-resampler-ffmpeg.c)).
  A wrong constant here shows up as fixed lip-sync error, which **no drift test will catch**.
- **The rate conversion itself is usually the identity.**
  [#11](https://github.com/richardthornton/clipshift/issues/11) locked capture at whatever
  `GetMixFormat` returns, output at 48 kHz. On the reference machine the loopback mix is 48 kHz, so the
  loopback sink's nominal ratio is 1.0 and only the drift correction moves it. The 44.1 → 48 kHz case is
  the *microphone*, and it is the only sink where a real rate conversion happens.
- **No per-frame managed allocation.** Established by
  [#3](https://github.com/richardthornton/clipshift/issues/3) and
  [`nvenc-access-path.md`](nvenc-access-path.md): native code is called over
  `delegate* unmanaged[Cdecl]` off a function table, with blittable structs and no marshalling stubs.
- **MIT, and — per [#3](https://github.com/richardthornton/clipshift/issues/3) — no LGPL obligation
  anywhere in the project today.** §5 of this document is about what changing that would cost.

A useful sanity figure before any of the CPU discussion: at 48 kHz stereo, ClipShift's *entire* audio
workload is **96,000 samples per second per sink**. Video is 1920×1080×60 = 124 million pixels per
second. Audio SRC is four orders of magnitude smaller than the frame path. §7 says this more carefully,
but no option in this survey is going to be rejected on CPU.

---

## 2. The hard filter: is the ratio adjustable while streaming?

### 2.1 libsamplerate — yes, two ways, and the distinction is documented

`SRC_DATA` carries `src_ratio` and it is read on **every** `src_process` call
([`include/samplerate.h`](https://github.com/libsndfile/libsamplerate/blob/master/include/samplerate.h)).
The documentation states the difference between the two mechanisms:

> When using the **src_process** or **src_callback_process** APIs and updating the **src_ratio** field of
> the **SRC_DATA** struct, the library will try to smoothly transition between the conversion ratio of
> the last call and the conversion ratio of the current call.

and `src_set_ratio` is the escape hatch from that:

> Set a new SRC ratio. This allows step responses in the conversion ratio.

Source: <http://libsndfile.github.io/libsamplerate/api_full.html>, and the header comment itself.

The valid range is bounded by `SRC_MAX_RATIO`, 256, checked by `src_is_valid_ratio`
([`src/common.h`](https://github.com/libsndfile/libsamplerate/blob/master/src/common.h),
`#define SRC_MAX_RATIO 256`). ClipShift's `1 ± 5×10⁻⁵` is nowhere near it.

**This is the cleanest variable-ratio API in the survey.** Smoothing between calls is exactly the
behaviour a PI loop wants, and it is a documented property rather than an accident of implementation.

### 2.2 soxr — only in `SOXR_VR` mode, and that is a different engine

`soxr_create` takes the rates as `double`s, but there is no way to change them afterwards for the normal
engine. The variable-rate path is opt-in at creation
([`src/soxr.h`](https://sourceforge.net/p/soxr/code/ci/master/tree/src/soxr.h)):

```c
#define SOXR_VR               32u  /* Variable-rate resampling. */

/* For variable-rate resampling. See example # 5 for how to create a
 * variable-rate resampler and how to use this function. */
SOXR soxr_error_t soxr_set_io_ratio(soxr_t, double io_ratio, size_t slew_len);
```

soxr's own example 5 documents the usage precisely, including something no other library in this survey
offers — a **built-in slew** over a caller-specified number of output samples
([`examples/5-variable-rate.c`](https://sourceforge.net/p/soxr/code/ci/master/tree/examples/5-variable-rate.c)):

```c
  /* When creating a var-rate resampler, q_spec must be set as follows: */
  soxr_quality_spec_t q_spec = soxr_quality_spec(SOXR_HQ, SOXR_VR);

  /* The ratio of the given input rate and output rates must equate to the
   * maximum I/O ratio that will be used: */
  soxr_t soxr = soxr_create(1 << OCTAVES, 1, 1, &error, NULL, &q_spec, NULL);
  ...
  /* Calculate an ioratio for this position and instruct the resampler to
   * move smoothly to the new value, over the course of outputting the next
   * 'block_len' samples (or give 0 for an instant change instead): */
  soxr_set_io_ratio(soxr, ioratio(pos, fm), block_len);
```

Two consequences worth stating explicitly:

- **The rates passed to `soxr_create` in VR mode are not the working rates**; they define the *maximum*
  I/O ratio. This is easy to get wrong.
- **`SOXR_HQ` in that call is decorative.** `vr_create` in
  [`src/vr32.c`](https://sourceforge.net/p/soxr/code/ci/master/tree/src/vr32.c) ends with
  `(void)shared, (void)q_spec, (void)r_spec;` — the quality spec and the runtime spec are discarded. The
  VR engine's filters are compile-time constants: `POLY_FIR_LEN_D 20` / `PHASES0_D 12` for downsampling
  and `POLY_FIR_LEN_U 12` / `PHASES0_U 6` for upsampling, with linear coefficient interpolation, plus
  half-band FIR and IIR stages per octave. The engine's name, returned by `vr_id()`, is `"vr32"` — it is
  float32 only.

So the practical statement is: **soxr's variable-rate mode passes the ratio filter, but it is a
20-tap/12-tap poly-FIR engine, not the DFT-based constant-rate engine soxr is known for**, and no quality
knob reaches it.

### 2.3 libswresample — yes, and this is the mechanism OBS's upstream provides

`swr_set_compensation` is documented as "Activate resampling compensation ("soft" compensation)"
([`libswresample/swresample.h`](https://github.com/FFmpeg/FFmpeg/blob/master/libswresample/swresample.h)).
What it does mechanically, from
[`libswresample/resample.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libswresample/resample.c):

```c
static int set_compensation(ResampleContext *c, int sample_delta, int compensation_distance){
    int ret;
    if (compensation_distance && sample_delta) {
        ret = rebuild_filter_bank_with_compensation(c);
        if (ret < 0) return ret;
    }
    c->compensation_distance= compensation_distance;
    if (compensation_distance)
        c->dst_incr = c->ideal_dst_incr - c->ideal_dst_incr * (int64_t)sample_delta / compensation_distance;
    else
        c->dst_incr = c->ideal_dst_incr;
    c->dst_incr_div   = c->dst_incr / c->src_incr;
    c->dst_incr_mod   = c->dst_incr % c->src_incr;
    return 0;
}
```

Three properties fall straight out of that, and they matter to the control loop:

1. **It is a step-rate adjustment, expressed as integer increments.** No filter redesign, no interpolation
   of coefficients — the phase step changes. That is exactly what a fine drift correction is.
2. **It is self-cancelling.** In `multiple_resample`, once `compensation_distance` output samples have
   been produced, `c->dst_incr` is restored to `c->ideal_dst_incr`. A continuous drift lock must
   therefore **re-arm the compensation every control period**, not set it once. This is a behavioural
   detail with no counterpart in libsamplerate or WDL, and it is easy to get wrong in a way that looks
   like a sluggish loop.
3. **It allocates at most once.** `rebuild_filter_bank_with_compensation` opens with
   `if (phase_count == c->phase_count) return 0;` — after the first arming the phase count already
   matches, so every subsequent `swr_set_compensation` call is pure integer arithmetic in the calling
   thread. **This is the only implementation in the survey that is provably allocation-free on repeated
   ratio changes** without the caller having to avoid a code path.

One restriction: with the soxr engine selected inside swresample
(`resampler=soxr`), FFmpeg's own documentation says "compensation, and filter options `filter_size`,
`phase_shift`, `exact_rational`, `filter_type` & `kaiser_beta`, are not applicable in this case"
([`doc/resampler.texi`](https://github.com/FFmpeg/FFmpeg/blob/master/doc/resampler.texi)). The
compensation mechanism belongs to the native `swr` engine only.

`async`, `min_comp` and `min_hard_comp` are the higher-level automatic wrapper around this — "simple
1 parameter audio sync to timestamps using stretching, squeezing, filling and trimming… larger values
represent the maximum amount in samples that the data may be stretched or squeezed for each second".
That is FFmpeg driving its own loop from PTS; ClipShift already has a better master-clock signal and
would drive `swr_set_compensation` directly. Recorded here because
[`av-sync-strategy.md`](av-sync-strategy.md) §3.2–3.3 already analysed those thresholds.

### 2.4 speexdsp — yes, but every change rebuilds the filter

`speex_resampler_set_rate_frac` takes a rational ratio and can be called at any time
([`include/speex/speex_resampler.h`](https://github.com/xiph/speexdsp/blob/master/include/speex/speex_resampler.h)):

> Set (change) the input/output sampling rates and resampling ratio (fractional values in Hz supported).

But in [`libspeexdsp/resample.c`](https://github.com/xiph/speexdsp/blob/master/libspeexdsp/resample.c),
`speex_resampler_set_rate_frac` ends with `if (st->initialised) return update_filter(st);`, and
`update_filter` unconditionally regenerates the sinc table:

```c
      for (i=-4;i<(spx_int32_t)(st->oversample*st->filt_len+4);i++)
         st->sinc_table[i+4] = sinc(st->cutoff,(i/(float)st->oversample - st->filt_len/2), st->filt_len, quality_map[st->quality].window_func);
```

and may `speex_realloc` both the sinc table and the delay-line memory. At quality 5 the table is
`filt_len × oversample = 80 × 16 = 1280` entries, each a windowed-sinc evaluation. At a 100 Hz control
rate that is ~128,000 transcendental evaluations per second **to move the ratio by fifty parts per
million**. It works; it is silly.

Note also the `if (st->in_rate == in_rate && st->out_rate == out_rate && st->num_rate == ratio_num &&
st->den_rate == ratio_den) return RESAMPLER_ERR_SUCCESS;` early-out, and the `compute_gcd` reduction —
with a fine-grained rational ratio the reduced denominator is large and unpredictable, which decides
between the "direct" and "interpolated" sinc-table paths and hence whether a `realloc` happens. That
unpredictability is itself a reason to be wary on a real-time thread.

The library's own stated design goal, from the header, is worth quoting because it is honest and it is
the correct frame for judging it: *"The design goals of this code are: - Very fast algorithm - Low memory
requirement - Good \*perceptual\* quality (and not best SNR)"*.

### 2.5 WDL / NAudio — yes, and it is the cheapest ratio change of all

[`WDL/resample.h`](https://github.com/justinfrankel/WDL/blob/main/WDL/resample.h) declares
`void SetRates(double rate_in, double rate_out);`. The managed port in NAudio
([`NAudio.Core/Dsp/WdlResampler.cs`](https://github.com/naudio/NAudio/blob/master/NAudio.Core/Dsp/WdlResampler.cs))
implements it in full as:

```csharp
        public void SetRates(double rate_in, double rate_out)
        {
            if (rate_in < 1.0) rate_in = 1.0;
            if (rate_out < 1.0) rate_out = 1.0;
            if (rate_in != m_sratein || rate_out != m_srateout)
            {
                m_sratein = rate_in; m_srateout = rate_out;
                m_ratio = m_sratein / m_srateout;
            }
        }
```

Three floating-point stores. No allocation, no filter rebuild, no validity check to trip over. The rates
are `double`, so a ratio of `48000.0 / 47997.6` is expressible directly.

The catch is in `ResampleOut`, not `SetRates`: in **sinc mode** it calls
`BuildLowPass(1.0 / (m_ratio * 1.03))` per block, and `BuildLowPass` early-outs only when
`m_filter_ratio == filtpos`. A continuously varying ratio therefore rebuilds the Blackman-Harris windowed
sinc table (`sincsize × sincoversize` entries, each three `Math.Cos` and one `Math.Sin`) on every block —
the same waste as speexdsp. In **non-sinc mode** (`SetMode(interp, filtercnt, false)`, which is what
NAudio's own `WdlResamplingSampleProvider` selects with `SetMode(true, 2, false)`) `BuildLowPass` is never
called and the ratio change is genuinely free — at materially lower quality.

`WDL_Resampler` is also **the only implementation here with a first-class output-driven mode**:
`SetFeedMode(false)` plus `ResamplePrepare(req_samples, …)` means "I want exactly this many output
samples; tell me how much input to hand you". That is [`av-sync-strategy.md`](av-sync-strategy.md) §5.3's
`need` loop expressed as an API, with no wrapper.

### 2.6 r8brain-free-src — no

`CDSPResampler`'s rates are constructor parameters
([`CDSPResampler.h`](https://github.com/avaneev/r8brain-free-src/blob/master/CDSPResampler.h)):

```cpp
	CDSPResampler( const double SrcSampleRate, const double DstSampleRate,
		const int aMaxInLen, const double ReqTransBand = 2.0,
		const double ReqAtten = 206.91,
		const EDSPFilterPhaseResponse ReqPhase = fprLinearPhase )
```

and the constructor body *builds the whole multi-stage conversion chain* from them — it inspects the
ratio for "power of 2" and other "common efficient ratios requiring only a single step", then allocates a
sequence of `CDSPProcessor` stages. There is no setter; `clear()` resets state, not rates. **Disqualified
on the ratio filter**, and it is the cleanest disqualification in the survey, because the ratio is
structural rather than parametric in this design. That is also why its quality is so high — it is worth
understanding as the opposite end of the trade.

### 2.7 Media Foundation's Audio Resampler DSP — no

The conversion ratio is the pair of media types, and Microsoft's own documentation for the DSP describes
no ratio API at all: the only tunables are `MFPKEY_WMRESAMP_FILTERQUALITY`, `MFPKEY_WMRESAMP_CHANNELMTX`
and `MFPKEY_WMRESAMP_LOWPASS_BANDWIDTH`, plus `IWMResamplerProps`
(<https://learn.microsoft.com/en-us/windows/win32/medfound/audioresampler>).

To change the ratio you must call `SetInputType`/`SetOutputType`, and Microsoft documents a dedicated
failure for doing so mid-stream:

> **MF_E_TRANSFORM_CANNOT_CHANGE_MEDIATYPE_WHILE_PROCESSING** — The MFT cannot switch types while
> processing data. Try draining or flushing the MFT.

Source: <https://learn.microsoft.com/en-us/windows/win32/api/mftransform/nf-mftransform-imftransform-setoutputtype>

Even setting that aside, a drift lock needs sub-ppm ratio resolution. `MF_MT_AUDIO_SAMPLES_PER_SECOND` is
an integer. There is a `double`-typed `MF_MT_AUDIO_FLOAT_SAMPLES_PER_SECOND`
(<https://learn.microsoft.com/en-us/windows/win32/medfound/mf-mt-audio-float-samples-per-second-attribute>),
whose entire Remarks section is "The GUID constant for this attribute is exported from mfuuid.lib" —
**whether `CResamplerMediaObject` honours a fractional rate from it is not documented anywhere I could
find, and is [unverified]**. Even if it did, the drain-or-flush requirement is fatal on its own: a
flush discards the filter state, which is a discontinuity in the output, ~100 times a second.

**Disqualified on the ratio filter.** Worth saying clearly, because a standalone MF transform genuinely
*is* a different thing from WASAPI's `AUTOCONVERTPCM` and deserved the check — it is not the same
objection as [#11](https://github.com/richardthornton/clipshift/issues/11)'s. It fails for its own
reasons.

Its one real merit, for the record: it is the only option with zero dependency footprint whatsoever, it
has shipped since Windows Vista / Server 2008, and quality is adjustable over a documented range —
`IWMResamplerProps::SetHalfFilterLength`, "Specifies the quality of the output. The valid range is 1 to
60, inclusive", default 30
(<https://learn.microsoft.com/en-us/windows/win32/api/wmcodecdsp/nf-wmcodecdsp-iwmresamplerprops-sethalffilterlength>).
If a *fixed-ratio* conversion were ever needed elsewhere in ClipShift — a one-shot 44.1→48 with no drift
lock — this is the free answer.

### 2.8 WASAPI itself — nothing usable, and already ruled out

`AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM` with `AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY` is the only SRC
WASAPI exposes, and [`wasapi-audio-capture.md`](wasapi-audio-capture.md) §8.1 already ruled it out,
because it "put[s] an undocumented in-engine resampler between the hardware and the file for four hours,
and that resampler **absorbs exactly the error the drift correction needs to measure**"
([#11](https://github.com/richardthornton/clipshift/issues/11)'s resolution). Nothing in this survey
changes that. It is also not a *controllable* resampler in any case: there is no ratio, no delay query,
and its capture-side behaviour is documented by Microsoft only for render streams.

Recorded for completeness and **not assessed in depth**: `Windows.Media.Audio.AudioGraph` also performs
rate conversion internally. It is a WinRT audio engine rather than a resampler component, it exposes no
conversion-ratio control, and its projection into .NET is the RCW-per-object shape that
[`display-capture-api.md`](display-capture-api.md) rejected for the video path. **[unverified]** in
detail; it does not look like a candidate and was not pursued.

---

## 3. Group delay: which implementations tell you their own latency

This is the requirement most likely to be under-weighted, because getting it wrong produces a *constant*
offset, and constant offsets survive every drift test in
[`av-sync-strategy.md`](av-sync-strategy.md) §8.

| Implementation | Delay API | What it actually returns |
|---|---|---|
| libswresample | `int64_t swr_get_delay(SwrContext*, int64_t base)` | "the delay the next input sample will experience relative to the next output sample… Swresample can buffer data if more input has been provided than available output space, also converting between sample rates needs a delay. **This function returns the sum of all such delays.**" Base selectable: 1 = seconds, 1000 = ms, a sample rate = samples, the LCM = "an exact rounding-free delay". |
| soxr, constant-rate engine | `double soxr_delay(soxr_t)` | Real: `_soxr_delay` in [`src/cr.c`](https://sourceforge.net/p/soxr/code/ci/master/tree/src/cr.c) is `return (double)p->samples_in / p->io_ratio - (double)p->samples_out;` |
| **soxr, `SOXR_VR` engine** | same `soxr_delay(soxr_t)` | **A stub.** [`src/vr32.c`](https://sourceforge.net/p/soxr/code/ci/master/tree/src/vr32.c): `static double vr_delay(rate_t * p) { return 100; /* TODO */ (void)p; }`, wired into the engine's dispatch table as `(fn_t)vr_delay`. `soxr_delay()` dispatches through `control_block[5]` ([`src/soxr.c`](https://sourceforge.net/p/soxr/code/ci/master/tree/src/soxr.c)), so a VR resampler reports a constant 100 output samples no matter its true state. |
| speexdsp | `speex_resampler_get_input_latency()` / `..._get_output_latency()` | "Get the latency introduced by the resampler measured in input samples" / "…output samples". Two calls, both documented. |
| WDL / NAudio | `double GetCurrentLatency()` | "amount of input that has been received but not yet converted to output, in seconds"; implemented as `((double)m_samples_in_rsinbuf - m_filtlatency) / m_sratein`. **Related to but not identical with `swr_get_delay`'s definition** — it is a net backlog after subtracting tracked filter priming, not a total input→output delay. Usable, but the mapping has to be derived and tested rather than assumed. |
| **libsamplerate** | **none** | There is no delay, latency or group-delay function anywhere in [`samplerate.h`](https://github.com/libsndfile/libsamplerate/blob/master/include/samplerate.h). The full public API is `src_new`, `src_clone`, `src_callback_new`, `src_delete`, `src_process`, `src_callback_read`, `src_simple`, `src_get_name`/`_description`/`_version`, `src_set_ratio`, `src_get_channels`, `src_reset`, `src_is_valid_ratio`, `src_error`, `src_strerror`, and four array converters. |
| r8brain | `getLatency()` returns 0 | The README states the resampler "removes the initial processing latency automatically", and `CDSPResampler::getLatency()` is literally `return( 0 );` with `getLatencyFrac()` alongside. Different design — the delay is compensated rather than reported. |
| MF Audio Resampler DSP | none documented | The DSP page documents no latency property. **[unverified]** whether one is discoverable via `IMFTransform::GetAttributes`. |

Two consequences the owner should weigh directly:

- **libsamplerate's missing delay API is a real cost, not a formality.** The delay of an SRC_SINC
  converter is a function of the converter type and the ratio, and it is *not* a published constant.
  Using libsamplerate means either (a) deriving the delay from the source, which is fine but is now
  ClipShift's problem to keep correct across library upgrades, or (b) measuring it empirically once with
  an impulse and hard-coding it, which is testable but brittle, or (c) not compensating and eating a
  fixed lip-sync offset. Option (c) is unacceptable per §10.5.
- **soxr VR's stub is worse than a missing API**, because it returns a plausible-looking number. Code
  that calls `soxr_delay()` on a VR resampler compiles, runs, and silently applies a wrong constant. If
  soxr VR were chosen, the delay would have to be derived from the source or measured, exactly as for
  libsamplerate — and then the LGPL price would have been paid for a feature that does not work.

---

## 4. Licences, from the licence text

### 4.1 libsamplerate — BSD-2-Clause, and the GPL belief is out of date

[`COPYING`](https://github.com/libsndfile/libsamplerate/blob/master/COPYING) is the 2-clause BSD licence.
Every source file carries the same header, e.g.
[`include/samplerate.h`](https://github.com/libsndfile/libsamplerate/blob/master/include/samplerate.h):

```
** Copyright (c) 2002-2016, Erik de Castro Lopo <erikd@mega-nerd.com>
** All rights reserved.
**
** This code is released under 2-clause BSD license. Please see the
** file at : https://github.com/libsndfile/libsamplerate/blob/master/COPYING
```

The change is dated in the project's own changelog:
[`NEWS`](https://github.com/libsndfile/libsamplerate/blob/master/NEWS), **Version 0.1.9 (2016-09-23)** —
*"Relicense under 2 clause BSD license."* Current release is 0.2.2
(<https://github.com/libsndfile/libsamplerate>).

**Obligation for ClipShift: retain the copyright notice and disclaimer in the documentation or other
materials accompanying the binary distribution.** That is the whole of it, and it is the same obligation
ClipShift already carries for nv-codec-headers ([`nvenc-access-path.md`](nvenc-access-path.md) §Licensing).

The commercial-licence question in the ticket is therefore moot — there is nothing to buy out of. The
historical dual-licensing arrangement is not something a primary source was found for and is not
relevant to a post-0.1.9 version; **[unverified]** and not pursued.

### 4.2 soxr — LGPL-2.1-or-later, and what that actually obliges

soxr's [`LICENCE`](https://sourceforge.net/p/soxr/code/ci/master/tree/LICENCE), verbatim in its operative
part:

> SoX Resampler Library       Copyright (c) 2007-18 robs@users.sourceforge.net
>
> This library is free software; you can redistribute it and/or modify it under the terms of the GNU
> Lesser General Public License as published by the Free Software Foundation; either version 2.1 of the
> License, or (at your option) any later version.

with two notes appended, the second of which is a live gotcha:

> 2. If building with pffft.c, see the licence embedded in that file.

(That file's embedded licence is the FFTPACK/UCAR BSD-3-style grant with a no-endorsement clause —
<https://sourceforge.net/p/soxr/code/ci/master/tree/src/pffft.c> — permissive and compatible, but a
separate notice to carry. It is only relevant to the **constant-rate** engine; `vr32.c` uses poly-FIR and
IIR stages and no DFT at all, so a VR-only build does not touch it. soxr can alternatively be built
against libavcodec's FFT (`avfft32.c`), which would pull LGPL FFmpeg in behind soxr — a build-configuration
trap worth checking if soxr is ever built for ClipShift.)

The relevant terms of LGPL-2.1 itself, quoted from
<https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt>:

**§5 — what ClipShift would be.**

> 5. A program that contains no derivative of any portion of the Library, but is designed to work with
> the Library by being compiled or linked with it, is called a "work that uses the Library". Such a work,
> in isolation, is not a derivative work of the Library, and therefore falls outside the scope of this
> License.
>
> However, linking a "work that uses the Library" with the Library creates an executable that is a
> derivative of the Library (because it contains portions of the Library), rather than a "work that uses
> the library". The executable is therefore covered by this License. Section 6 states terms for
> distribution of such executables.

ClipShift P/Invoking `soxr.dll` and using only `soxr.h` — which contains function declarations, struct
layouts and small `#define`s — falls squarely under §5's own carve-out:

> If such an object file uses only numerical parameters, data structure layouts and accessors, and small
> macros and small inline functions (ten lines or less in length), then the use of the object file is
> unrestricted, regardless of whether it is legally a derivative work.

A .NET assembly is stronger still: it contains **no** soxr code, not even inlined header material,
because P/Invoke resolves entirely at runtime. **ClipShift's own binary would not become a derivative
work, and ClipShift stays MIT.** That much of "LGPL is fine if you link dynamically" is true.

**§6 — what distributing the combination obliges. This is the part the slogan omits.**

> 6. As an exception to the Sections above, you may also combine or link a "work that uses the Library"
> with the Library to produce a work containing portions of the Library, and distribute that work under
> terms of your choice, **provided that the terms permit modification of the work for the customer's own
> use and reverse engineering for debugging such modifications.**
>
> **You must give prominent notice with each copy of the work that the Library is used in it and that the
> Library and its use are covered by this License. You must supply a copy of this License.** If the work
> during execution displays copyright notices, you must include the copyright notice for the Library
> among them, as well as a reference directing the user to the copy of this License. Also, you must do
> one of these things:

then one of 6a–6e. The two that could apply:

> a) Accompany the work with the complete corresponding machine-readable source code for the Library
> including whatever changes were used in the work (which must be distributed under Sections 1 and 2
> above); and, if the work is an executable linked with the Library, with the complete machine-readable
> "work that uses the Library", as object code and/or source code, **so that the user can modify the
> Library and then relink to produce a modified executable containing the modified Library.**

> b) Use a suitable shared library mechanism for linking with the Library. A suitable mechanism is one
> that **(1) uses at run time a copy of the library already present on the user's computer system**,
> rather than copying library functions into the executable, and **(2) will operate properly with a
> modified version of the library**, if the user installs one, as long as the modified version is
> interface-compatible with the version that the work was made with.

> d) If distribution of the work is made by offering access to copy from a designated place, offer
> equivalent access to copy the above specified materials from the same place.

**Translating that into a concrete checklist for a shipped ClipShift .zip or installer containing
`soxr.dll`:**

1. **MIT satisfies the §6 preamble condition.** MIT grants the right to "modify" without restriction and
   imposes no anti-reverse-engineering term, so ClipShift's own licence "permit[s] modification of the
   work for the customer's own use and reverse engineering for debugging such modifications". No licence
   change is required. This is the finding that matters most and it is a clean yes.
2. **A prominent notice is mandatory, and it is not the same as the MIT notice.** ClipShift must state,
   with each copy, *that soxr is used* and *that soxr and its use are covered by the LGPL*. A line in a
   `THIRD-PARTY-NOTICES.md` shipped in the distribution is the normal form. "Prominent" is not defined;
   burying it in a source file would not be prudent.
3. **A full copy of LGPL-2.1 must ship in the distribution.** Not a link — "You must supply a copy of
   this License."
4. **If ClipShift ever displays copyright notices at runtime** (an about box, a `--version` banner that
   lists copyrights), soxr's copyright must appear among them with a pointer to the licence copy. A
   `--version` that prints only a version number does not trigger this; one that prints
   "© 2026 Richard Thornton" does.
5. **Shipping `soxr.dll` at all makes ClipShift a distributor of the Library under §4**, independently of
   §6: §4 permits distributing the Library in object form "provided that you accompany it with the
   complete corresponding machine-readable source code". So the *source of the exact soxr build shipped*
   has to be available. In practice: attach the soxr source tarball to the same GitHub release as the
   ClipShift binary — that satisfies 6d ("equivalent access… from the same place") and §4 at once, and it
   is what FFmpeg's own compliance checklist prescribes for the analogous case
   (<https://www.ffmpeg.org/legal.html>).
6. **6b alone is a weaker position than it looks, and this is where the slogan actually breaks.** 6b's
   clause (1) says the mechanism "uses at run time a copy of the library **already present on the user's
   computer system**". soxr is not present on any Windows system; ClipShift would be putting it there.
   6b was written for `/usr/lib`, not for a DLL shipped in the app's own folder. Relying on 6b alone
   while also being the party that supplies the DLL is an argument, not a certainty. **Doing 6d as well
   costs one file attached to a release and removes the question.** (This is a reading of the licence
   text, not legal advice.)
7. **6b's clause (2) is an engineering constraint, and it is the one most likely to be broken by
   accident.** The shipped app must "operate properly with a modified version of the library, if the user
   installs one". That means:
   - `soxr.dll` must be a **loose file next to the executable**, resolvable by name at load time.
   - **.NET single-file publish with `IncludeNativeLibrariesForSelfExtract=true` embeds native libraries
     inside the bundle**, extracting them to a temp directory the user cannot meaningfully edit. Doing
     that would defeat 6b(2). If ClipShift ever adopts single-file publishing — a plausible thing for a
     small tool to want — this is the constraint that has to survive it.
   - Static linking of soxr is straightforwardly out under 6b, and under 6a would require shipping
     ClipShift's own object code so a user could relink. Do not statically link soxr.
8. **If ClipShift patches soxr** — and §3 gives an obvious reason to want to, namely `vr_delay`'s stub —
   §2 applies on top: the modified files must "carry prominent notices stating that you changed the files
   and the date of any change", and the modified library stays LGPL. The patch would have to be published
   with the source.

**The practical bottom line:** the relinking obligation, for a P/Invoke'd DLL, is *substantively already
satisfied* by the architecture — a user can drop in their own `soxr.dll` and ClipShift will load it, with
no relinking of ClipShift required at all. That is the strongest possible form of what §6 is trying to
guarantee. What it costs is **three artifacts in the distribution** (notice, licence copy, matching
source) **and one permanent engineering constraint** (the DLL stays loose and replaceable). It does not
threaten ClipShift's MIT licence and it does not reach ClipShift's source. It is also the project's first
compliance checklist, which has a maintenance cost that is small but not zero — the source attached to a
release must match the binary shipped in that release, every release, forever.

### 4.3 libswresample — LGPL-2.1-or-later, and the build configuration is the whole story

The file header of
[`libswresample/swresample.h`](https://github.com/FFmpeg/FFmpeg/blob/master/libswresample/swresample.h)
states it directly:

> libswresample is free software; you can redistribute it and/or modify it under the terms of the GNU
> Lesser General Public License as published by the Free Software Foundation; either version 2.1 of the
> License, or (at your option) any later version.

and [`LICENSE.md`](https://github.com/FFmpeg/FFmpeg/blob/master/LICENSE.md) governs the project:

> Most files in FFmpeg are under the GNU Lesser General Public License version 2.1 or later (LGPL v2.1+)…
> Some optional parts of FFmpeg are licensed under the GNU General Public License version 2 or later (GPL
> v2+)… **None of these parts are used by default, you have to explicitly pass `--enable-gpl` to
> configure to activate them. In this case, FFmpeg's license changes to GPL v2+.**

**How build configuration interacts, precisely:**

| Configure flag | Effect on the licence |
|---|---|
| default | LGPL-2.1+ |
| `--enable-gpl` | **whole build becomes GPL-2+** — would relicense a statically-combined ClipShift; must not be used |
| `--enable-version3` | upgrades to (L)GPL-3; required if combining with gmp, libaribb24, liblensfun, or the Apache-2.0 libraries (VMAF, mbedTLS, RK MPI, OpenCORE, VisualOn) |
| `--enable-nonfree` | "This will cause the resulting binary to be unredistributable." Never. |
| `--enable-libsoxr` | brings soxr's own LGPL-2.1+ in behind swresample — same licence family, but now **two** compliance obligations, and per §2.3 it disables `swr_set_compensation` |

The GPL list in `LICENSE.md` is a handful of x86 asm files, some build/test tools, and ~30 libavfilter
filters. **Nothing in `libswresample/` is GPL — with one pointed exception:**
`libswresample/tests/swresample.c`, swresample's own test harness, is named in the GPL list. So
swresample's *code* is LGPL but the reference test for it is GPL and cannot be lifted into an MIT project.
That is a small, specific, easy-to-trip-over fact and it matters directly to §8's testing discussion.

The §6 obligation analysis in §4.2 applies identically to a shipped `swresample-*.dll`, with FFmpeg
publishing its own version of the checklist: dynamic linking; provide the FFmpeg source matching the
shipped binaries, hosted on the same server; state *"This software uses code of FFmpeg licensed under the
LGPLv2.1"*; mention LGPLv2.1 in the about box and EULA (<https://www.ffmpeg.org/legal.html>). That is the
same three artifacts plus a prescribed wording.

**The dependency's size is the real cost here, not the licence.** libswresample links against libavutil;
a minimal build is two DLLs rather than one. Against that, [`nvenc-access-path.md`](nvenc-access-path.md)
§Tradeoffs already flagged that **if the container/muxer decision lands on libavformat, FFmpeg is a
dependency anyway** — in which case swresample is free of marginal cost and is the option with the best
API fit (§2.3, §3). Whether that happens is
[#10](https://github.com/richardthornton/clipshift/issues/10)'s business, but the two decisions are
coupled and #16 should know it.

### 4.4 The permissive options

| Library | Licence file | What ClipShift owes |
|---|---|---|
| libsamplerate 0.2.2 | [`COPYING`](https://github.com/libsndfile/libsamplerate/blob/master/COPYING) — BSD-2-Clause | Reproduce copyright notice + conditions + disclaimer in materials accompanying the binary |
| speexdsp | [`COPYING`](https://github.com/xiph/speexdsp/blob/master/COPYING) — BSD-3-Clause (Xiph); `speex_resampler.h`'s own header is the same 3-clause text with the author-endorsement restriction | Same, plus the no-endorsement clause |
| r8brain-free-src | [`LICENSE`](https://github.com/avaneev/r8brain-free-src/blob/master/LICENSE) — MIT | Retain notice |
| Cockos WDL `resample.h` | Header notice — zlib-style: "Permission is granted to anyone to use this software for any purpose, including commercial applications… 1. The origin of this software must not be misrepresented… 2. Altered source versions must be plainly marked as such… 3. This notice may not be removed or altered from any source distribution." Plus: **"You may also distribute this software under the LGPL v2 or later."** | Keep the notice in the source; do not claim authorship; mark modifications. The LGPL option is *additional*, not a condition — take the zlib-style terms and the LGPL never engages. |
| NAudio | [`license.txt`](https://github.com/naudio/NAudio/blob/master/license.txt) — MIT, © 2020 Mark Heath. `WdlResampler.cs` retains the WDL notice and states *"Used in NAudio with permission from Justin Frankel"* | MIT notice + the retained WDL notice |
| miniaudio | [`LICENSE`](https://github.com/mackron/miniaudio/blob/master/LICENSE) — dual: public domain (Unlicense) **or** MIT-0, at the user's choice | Nothing, under either alternative |

**None of these costs anything beyond a `THIRD-PARTY-NOTICES` entry.** There is no compliance checklist,
no source-availability obligation, no per-release maintenance, and no constraint on how ClipShift is
published. That difference — not the difference in code quality — is the substance of the permissive vs
LGPL choice.

Two are also *header-only or source-drop-in*, which removes the native-DLL question entirely: r8brain is
"basically header-only and does not have dependencies beside the standard C++ library"
(<https://github.com/avaneev/r8brain-free-src>), and WDL's resampler is two files.

### 4.5 The Windows-native options

`Resampledmo.dll` ships with Windows (Vista / Server 2008 and later,
<https://learn.microsoft.com/en-us/windows/win32/medfound/audioresampler>). Nothing is redistributed,
nothing is licensed, nothing is owed. It is the only option with a licensing cost of exactly zero — which
is worth noting precisely because it is disqualified for other reasons (§2.7).

---

## 5. Reachability from .NET, and the allocation constraint

The house rule from [#3](https://github.com/richardthornton/clipshift/issues/3): native code is reached
over unmanaged function pointers with no per-frame managed allocation
(<https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code#function-pointers>,
<https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices>).

| Option | Shape | Interop verdict |
|---|---|---|
| libsamplerate | Flat C API, opaque `SRC_STATE*`, one blittable struct (`SRC_DATA`: two `float*`, four `long`, one `int`, one `double`) | **Ideal.** `SRC_DATA` is blittable and can live as a reusable field or `stackalloc`; `src_process` takes a pointer to it. `DllImport` or `NativeLibrary.TryLoad` + `delegate* unmanaged[Cdecl]`, either works. Simplest interop in the survey. |
| soxr | Flat C API, opaque `soxr_t`; `soxr_process` takes raw pointers and `size_t*` out-params | **Ideal, same shape.** `size_t` must be mapped as `nuint`. `soxr_error_t` is `char const*`, so the *error* path allocates a managed string — but only on error, never on the hot path. |
| libswresample | Flat C API, opaque `SwrContext*`; `swr_convert` takes `uint8_t**` plane arrays | **Good, with a wrinkle.** The `uint8_t**` plane arrays must be a pinned or `stackalloc`'d pointer array per call — trivial with `stackalloc byte*[2]`, but it is a thing to get right rather than a straight blittable struct. Two DLLs to load (swresample + avutil). |
| speexdsp | Flat C API, opaque state, `spx_uint32_t*` in/out length params | **Fine**, same shape as libsamplerate. |
| WDL (C++) | **C++ class.** No C API | **Needs a C shim DLL of ClipShift's own.** That is a build-system cost (an MSVC native project in the repo) that none of the C libraries carry. |
| NAudio `WdlResampler` | **Pure managed C#** | **No interop at all** — no DLL to ship, no P/Invoke, no marshalling, and it debugs in the same process under the same debugger. Against that: see the allocation note below. |
| r8brain | C++ header-only | Same shim cost as WDL — and disqualified anyway (§2.6). |
| MF Audio Resampler DSP | COM, `IMFTransform` / `IMediaObject` | The interop shape [`nvenc-access-path.md`](nvenc-access-path.md) explicitly avoided; CsWin32 `allowMarshaling: false` would be needed. Disqualified anyway (§2.7). |

**Existing .NET bindings, and whether they can be used:**

| NuGet package | Version | Licence (from the package's own metadata on nuget.org) | Usable by an MIT project? |
|---|---|---|---|
| `LibSampleRate` | 1.1.0 | **BSD-2-Clause** | Yes — "Managed wrapper for the native SRC/libsamplerate resampling library". Small download count (604 total), so treat it as reference code rather than a maintained dependency. |
| `Aurio.LibSampleRate` | 4.2.2 | **AGPL-3.0-only** | **No.** AGPL is incompatible with shipping an MIT closed-directory app without adopting AGPL. |
| `Aurio.Soxr` | 4.2.2 | **AGPL-3.0-only** | **No.** Same. Note the trap: soxr *itself* is LGPL, but this binding to it is AGPL — the binding's licence is the one that binds. |
| `NAudio` | current | **MIT** | Yes. But NAudio is a large audio framework; ClipShift would want `NAudio.Core` for the one `WdlResampler` class, or to vendor the file with its notices intact. |

(Queried from the NuGet v3 registration API,
`https://api.nuget.org/v3/registration5-gz-semver2/{id}/index.json`, 2026-08-13.)

**Allocation, stated per-option rather than in general:**

- **libsamplerate, soxr, speexdsp, libswresample**: all allocate their working buffers at init and none
  allocate managed memory per call. The managed side is a struct write plus a function-pointer call.
- **libswresample re-arming**: `swr_set_compensation` allocates at most once, ever (§2.3). Confirmed from
  source, not inferred.
- **speexdsp ratio changes**: may `speex_realloc` (§2.4). Native, not GC, so it does not affect GC
  pressure — but it is a `malloc` on a real-time thread, which is its own hazard.
- **NAudio `WdlResampler` — the one genuine managed-allocation hazard in the survey.**
  `ResamplePrepare` contains `Array.Resize(ref m_rsinbuf, (m_samples_in_rsinbuf + sreq) * nch);` on every
  call, where `sreq = (int)(m_ratio * out_samples) + 4 + fsize - m_samples_in_rsinbuf` in output-driven
  mode. `Array.Resize` is a no-op only when the length is unchanged; with a continuously varying `m_ratio`
  and a varying `need`, that length jitters, so **it will allocate a new `float[]` on a large fraction of
  calls** — 100 times a second, for four hours. It is fixable (the class is one MIT-licensed file; the
  buffer can be made a fixed high-water-mark allocation), but it must be fixed, not assumed away. This is
  the price of "no interop at all".

---

## 6. Group delay in the specific case ClipShift will hit

Worth pinning one number, because §3 is otherwise abstract. soxr's own README states, for its
constant-rate HQ engine:

> when using the `High Quality' configuration to resample between 44100Hz and 48000Hz, the latency is
> around 1000 output samples, i.e. roughly 20ms

Source: <https://sourceforge.net/p/soxr/code/ci/master/tree/README>

**20 ms is roughly 1¼ video frames at 60 fps.** That is the scale of the fixed lip-sync error an
uncompensated resampler introduces — well above any plausible perceptual threshold and well above
[`av-sync-strategy.md`](av-sync-strategy.md) §1.4's budget. It is also, notably, *not* something the
4-hour drift test detects, because it does not grow. This is the concrete argument for treating §3's
column as a hard requirement rather than a nicety.

Nothing comparable is published for libsamplerate, speexdsp or swresample; those figures would have to
be measured. **[unverified]**

---

## 7. Quality and CPU

### 7.1 What each project publishes about its own quality

| Implementation | Published figures, from the project's own documentation |
|---|---|
| libsamplerate | Per converter, from <http://libsndfile.github.io/libsamplerate/api_misc.html>: `SRC_SINC_BEST_QUALITY` — "worst case Signal-to-Noise Ratio (SNR) of 97 decibels (dB) at a bandwidth of 97%"; `SRC_SINC_MEDIUM_QUALITY` — "SNR of 97dB and a bandwidth of 90%… much faster"; `SRC_SINC_FASTEST` — "SNR of 97dB and a bandwidth of 80%". `SRC_ZERO_ORDER_HOLD` and `SRC_LINEAR` are documented as "not bandlimited" and "the quality is poor". The project's own regression targets are stricter than the prose: `snr_bw_test.c` demands ≥145 dB from `SRC_SINC_MEDIUM_QUALITY` on several cases (§8.3). |
| soxr, constant-rate | "Bit-perfect within practical occupied-bandwidth limits" (README). Precision is a parameter: `soxr_quality_spec.precision` in bits, default 20, with `SOXR_16_BITQ` … `SOXR_32_BITQ` recipes. FFmpeg's docs restate the mapping: "The default value of 20… gives SoX's 'High Quality'; a value of 28 gives SoX's 'Very High Quality'" ([`doc/resampler.texi`](https://github.com/FFmpeg/FFmpeg/blob/master/doc/resampler.texi)). |
| **soxr, `SOXR_VR`** | **Nothing published, and the quality spec is discarded (§2.2).** The design is inferable from the source — 20-tap poly-FIR downsampling, 12-tap upsampling, order-1 (linear) coefficient interpolation — but soxr publishes no SNR or bandwidth figure for VR mode. **[unverified]**, and this is a gap that matters, because it means "soxr is the high-quality one" cannot be carried over to the mode ClipShift would use. |
| libswresample | No SNR figures published. Parameters are exposed and documented: `filter_size` default 32, `phase_shift` default 10, `cutoff` "6dB point… Default value is 0.97 with swr", `linear_interp` on by default, `exact_rational` on by default ([`doc/resampler.texi`](https://github.com/FFmpeg/FFmpeg/blob/master/doc/resampler.texi)). **[unverified]** against any published measurement. |
| speexdsp | Comments in the source, not documentation: `quality_map[]` in [`libspeexdsp/resample.c`](https://github.com/xiph/speexdsp/blob/master/libspeexdsp/resample.c) annotates Q3 as "84.9% cutoff ( ~80 dB stop)", Q5 as "89.1% cutoff (~100 dB stop)", Q7 as "93.1% cutoff (~100 dB stop)", Q10 as "96.6% cutoff (~100 dB stop)". Filter lengths run 8 taps (Q0) to 256 (Q10). Stated design goal is perceptual quality, explicitly "not best SNR". |
| r8brain | Configurable: `ReqAtten` "Required stop-band attenuation in decibel", default **206.91**, with the guidance "The general formula for selecting the `ReqAtten` is `6.02 * Bits + 40`". No headline SNR number in the README. |
| MF Audio Resampler DSP | Quality is `SetHalfFilterLength`, 1–60, default 30. No SNR published. **[unverified]** |

**Ranking, on published figures only:** r8brain and soxr's constant-rate engine sit at the top and are
both unavailable to ClipShift (ratio filter). Among the survivors, **libsamplerate's `SRC_SINC_*` family
is the only one with a published SNR figure at all** (97 dB worst case), and it is the only one whose
project ships an automated SNR regression with numeric targets in the repository (§8.3). speexdsp's
in-source annotations put Q5+ at ~100 dB stopband, which is comparable, from a weaker kind of source.
swresample and soxr-VR are unquantified from primary sources.

### 7.2 CPU, and why it is not the deciding criterion

**No project in this survey publishes CPU figures for 48 kHz stereo**, and none publishes anything at all
about behaviour on a machine simultaneously game-rendering, NVENC-encoding and OBS-streaming. That
question cannot be answered from primary sources, and this document does not pretend otherwise. What can
be said:

- libsamplerate ships its own benchmark rather than a published number —
  [`tests/throughput_test.c`](https://github.com/libsndfile/libsamplerate/blob/master/tests/throughput_test.c)
  runs `src_simple` at ratio 0.99 in a loop for ≥3 seconds of CPU time and reports frames/sec per
  converter, after a 2-second sleep to let the machine settle. That is the right shape for an answer, and
  it is a small program to port.
- soxr's README documents an architectural cost rather than a number: it "may have a higher latency than
  non-FFT based resamplers", and "multi-channel resampling can utilise multiple CPU-cores" (which for
  ClipShift should be *disabled* — `soxr_runtime_spec(1)` — because spawning worker threads under a
  game is the opposite of what this project wants).
- The scale argument from §1 stands on its own: 96,000 samples/second/sink against a 124-Mpixel/second
  video path. [`av-sync-strategy.md`](av-sync-strategy.md) §5.2 already recorded the expected cost as
  "one polyphase SRC per audio sink at 48 kHz stereo — negligible beside NVENC".
- The one CPU fact that *is* established from source, and that a benchmark would miss, is the ratio-change
  cost: speexdsp regenerates its sinc table per change (§2.4), WDL in sinc mode rebuilds its lowpass per
  block under a varying ratio (§2.5), swresample does neither (§2.3), libsamplerate does neither.

**Recommendation for #16 on this point specifically: do not choose on CPU without measuring, and if
measuring, measure the ratio-change path, not the steady-state convert path.** The steady state will be
negligible for every candidate; the control path is where the differences are.

---

## 8. Writing one from scratch

### 8.1 What the algorithm actually is

The canonical primary reference is Julius O. Smith III's *Digital Audio Resampling Home Page*
(<https://ccrma.stanford.edu/~jos/resample/>), which is the source the field descends from — it covers
the theory of ideal bandlimited interpolation, filter-table design, interpolation between filter
coefficients, and the error analysis that ties table resolution to output SNR. libsamplerate's
`SRC_SINC_*` converters are an implementation of this method; so, structurally, are speexdsp's and WDL's.

The pieces of an implementation, and roughly what each costs:

1. **A windowed-sinc prototype lowpass**, cutoff at the lower of the two Nyquist frequencies with a
   guard band. Every implementation in this survey does this and every one is readable. WDL's is 45
   lines of C# and uses a Blackman-Harris window
   ([`WdlResampler.cs`](https://github.com/naudio/NAudio/blob/master/NAudio.Core/Dsp/WdlResampler.cs),
   `BuildLowPass`); speexdsp's is Kaiser 6/8/10/12 by quality
   ([`resample.c`](https://github.com/xiph/speexdsp/blob/master/libspeexdsp/resample.c), `quality_map`).
   **This is the easy part**, and it is a day.
2. **A polyphase decomposition of that filter into phases**, plus a rule for what to do when the required
   phase falls between two tabulated ones. This is the design decision that sets both quality and memory:
   speexdsp chooses between a "direct" table of `filt_len × den_rate` and an "interpolated" table of
   `filt_len × oversample + 8` at runtime, based on which is smaller; soxr's VR engine uses 1024 phases
   × 20 taps down / 512 × 12 up with linear coefficient interpolation; swresample's defaults are
   `filter_size 32` and `phase_shift 10` (i.e. 1024 phases). **This is the part where a naive
   implementation quietly loses 40 dB**, because the interpolation error between phases, not the filter
   itself, becomes the noise floor. Smith's page is specifically about bounding that error.
3. **A fixed-point or rational phase accumulator** that survives four hours without drifting. swresample
   carries `index`, `frac`, `dst_incr_div`, `dst_incr_mod` against `src_incr` as exact integers
   ([`resample.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libswresample/resample.c)); soxr's VR
   engine uses a 32.32 fixed-point step (`MULT32 (65536. * 65536.)`). **A `double` phase accumulator
   incremented 691 million times over a 4-hour session is exactly the kind of thing that passes a
   10-second test and fails an acceptance run**, and this project's whole sync design is built to make
   that class of bug impossible elsewhere. Doing it correctly here is not hard, but it must be done
   deliberately.
4. **The output-driven pull loop and the ratio-change semantics** — which, given
   [`av-sync-strategy.md`](av-sync-strategy.md) §5.3, ClipShift has to write anyway around whichever
   library it picks.
5. **An exact, derivable group delay** — the one thing a bespoke implementation gets *for free* that
   half the libraries in this survey do not provide (§3). For a linear-phase FIR of length `N` the group
   delay is `(N−1)/2` input samples plus the queue state, and it is exact by construction rather than
   reverse-engineered.

### 8.2 The honest scope

Steps 1, 3, 4 and 5 are a few days of careful work for someone who has the reference open. Step 2 is
where the range between "a week" and "a month" lives, and it is not the coding — it is not knowing
whether the thing is *right*.

### 8.3 How such a thing is actually tested — the part that decides the estimate

This is answerable from primary sources, because libsamplerate ships its entire test rig in the open
under BSD-2-Clause, which means **the test methodology is not just documentable but directly reusable by
an MIT project**. (By contrast, `libswresample/tests/swresample.c` is named in FFmpeg's
[`LICENSE.md`](https://github.com/FFmpeg/FFmpeg/blob/master/LICENSE.md) GPL list, so swresample's own test
harness cannot be lifted. That asymmetry is worth knowing before choosing a reference to copy.)

The suite is
<https://github.com/libsndfile/libsamplerate/tree/master/tests>. The tests that matter, and what each one
actually does:

**1. SNR-in-the-frequency-domain, with per-converter numeric targets — `snr_bw_test.c` + `calc_snr.c`.**
This is the core quality test and it is more subtle than "compare to a reference output". The method:

- Generate windowed sine(s) at known normalised frequencies, resample at a known ratio, then take the
  log-magnitude spectrum of the *output* via FFTW.
- Identify the expected number of passband peaks, and call everything else noise.
- The trap, and its fix, are documented in the source itself
  ([`calc_snr.c`](https://github.com/libsndfile/libsamplerate/blob/master/tests/calc_snr.c)):

  > There is a slight problem with trying to measure SNR with the method used here; the side lobes of the
  > windowed FFT can look like a noise/aliasing peak. The solution is to smooth the magnitude spectrum by
  > wiping out troughs between adjacent peaks as done here. This removes side lobe peaks without
  > affecting noise/aliasing peaks.

- Targets are a hard-coded table per converter per case. For `SRC_SINC_MEDIUM_QUALITY`, at ratios
  3.0 / 0.6 / 0.3 / 1.0 / 1.001, the required SNRs are **145 / 132 / 138 / 157 / 148 dB**; for
  `SRC_SINC_FASTEST`, 100 / 99 / 100 / 150 / 100 dB; for `SRC_LINEAR`, 73 dB; for `SRC_ZERO_ORDER_HOLD`,
  28 dB. Note the deliberate inclusion of **ratio 1.001** — a near-unity ratio, which is exactly
  ClipShift's operating point, and which is a distinct and harder case than 2:1.
- Also included: a separate `bandwidth_test`, and dual-tone cases (`{0.011111, 0.324}` at ratio 1.9999)
  that specifically probe aliasing of a high tone across Nyquist.

**This is the test that separates a week from a month.** Writing it needs an FFT, a windowed-sine
generator, a peak finder, and the side-lobe smoothing trick above — and without it, a bespoke resampler's
quality is a guess. libsamplerate makes it optional on FFTW being present, which is itself informative:
the quality tests are the ones with a dependency.

**2. Variable-ratio-specific tests — `varispeed_test.c`.** Two distinct things, and both are directly
relevant to ClipShift:

- A **round-trip SNR test**: resample at ratio 3.0, reverse the data, `src_reset`, resample back, and
  require the result to meet a target SNR — **115 dB for `SRC_SINC_FASTEST`**, 10 dB for the
  non-bandlimited converters. Round-tripping is a good test precisely because it needs no reference
  signal.
- A **bounds/robustness sweep**: for every channel count 1…9, and every ordered pair from
  `{0.1, 0.01, 20}`, process one chunk at the first ratio then hard-switch with `src_set_ratio` to the
  second, feeding 128-frame chunks in a loop with a `max_loop_count` of 100,000 "to enable the detection
  of infinite loops (due to end of input not being detected)", asserting that the loop terminates, that
  output was produced, and that **no output sample is NaN**. The input is all zeros — the comment says
  "Interested in array boundary conditions, so all zero data here is fine."

The second test is the one worth internalising: **a ratio change is a buffer-management bug generator
before it is a quality problem**, and the failure modes it hunts are hangs, index overruns and NaNs, not
distortion.

**3. Structural tests, cheap and high-value.** `termination_test.c` (does the converter consume all input
and terminate), `streaming_test.c` (does chunked processing equal one-shot processing),
`reset_test.c`, `clone_test.c`, `multi_channel_test.c`, `nullptr_test.c`, `callback_hang_test.c`,
`float_short_test.c`, `simple_test.c`, `downsample_test.c`, plus `throughput_test.c` and
`multichan_throughput_test.c` for performance. Full listing:
<https://github.com/libsndfile/libsamplerate/tree/master/tests>.

**4. What ClipShift would need *on top* of all that**, which no library's suite provides because no
library owns the problem:

- **A group-delay assertion.** Feed an impulse, find the output peak, assert it matches the delay the
  implementation reports. This is the test that would have caught soxr's `vr_delay` stub, and it is
  perhaps ten lines. It should exist regardless of which option #16 picks — **it is the single highest
  value test in this whole document**, because it is the only cheap defence against the failure mode of
  §3 and §6.
- **The ledger assertion already specified in [`av-sync-strategy.md`](av-sync-strategy.md) §8.1**, which
  tests the *integration* — that `written_total == round(R × (t − T0))` — and is independent of the
  resampler's internals. Note that this assertion holds by construction under §5.3's formulation
  regardless of resampler choice, so it does **not** substitute for the SNR and group-delay tests.
- **A drift-lock convergence test** — inject a synthetic input clock at +50 ppm and assert the input
  queue occupancy settles at the setpoint without oscillation. That belongs to
  [#16](https://github.com/richardthornton/clipshift/issues/16)'s PI constants, not here, but it is part
  of the same test rig and should be budgeted with it.

### 8.4 The estimate, stated plainly

A bespoke polyphase resampler that *works* is a week. A bespoke polyphase resampler that is *known* to
work — with an FFT-based SNR harness carrying numeric targets, a ratio-change robustness sweep, and a
group-delay assertion — is the month, and roughly two thirds of that month is the harness rather than the
resampler. The mitigating fact is that **most of that harness is worth building anyway**: the group-delay
assertion and the drift-lock convergence test are needed whichever option #16 picks, and the SNR harness
would be the only way to make a claim about soxr-VR's or swresample's unpublished quality (§7.1).

---

## 9. Options looked at and set aside quickly

- **miniaudio** — dual public-domain / MIT-0, single-header, and it does contain a resampler
  (`ma_resampler`, `ma_linear_resampler`). Its licence is the most permissive in the survey. **It was not
  assessed in the same depth as the others: I could not retrieve the resampler section of its manual or
  the relevant part of `miniaudio.h` within this research, so its variable-ratio API, its latency API and
  its quality are all [unverified] here.** Given that it is a whole audio-device framework in one header
  and ClipShift already owns its capture path, it is a poor shape for this job even if the resampler is
  good — but that is a judgement about shape, not evidence about capability. If #16 wants it ruled in or
  out properly, that is a small follow-up.
- **libresample / the CCRMA `resample` program** — the direct descendant of Smith's work, but LGPL and
  long unmaintained; strictly worse than soxr on every axis including licence. Not pursued.
- **SoundTouch** — LGPL-2.1, and it is a time/pitch-stretcher whose resampler is incidental. Not
  pursued.
- **zita-resampler's `VResampler`** — worth a specific mention because it is the closest thing in
  existence to a purpose-built drift-lock resampler: its documentation
  (<https://kokkinizita.linuxaudio.org/linuxaudio/zita-resampler/resampler.html>) says it "was developed
  for converting between two nominally fixed sample rates with a ratio which is not known exactly and may
  even drift slowly", offers `set_rratio()` (real-time safe, ratio 0.95–16 relative to the configured
  one), `set_rrfilt()` for a first-order filter on ratio changes — i.e. slew, built in — and `inpdist()`
  / `inpsize()` for exact delay accounting. **That is the best API fit in this entire survey.** It is
  excluded here for one reason: **I could not retrieve its licence text from a primary source.** The
  documentation page carries no licence statement and the canonical distribution is a tarball on the
  author's own site rather than a browsable repository. It is commonly described as GPL, which would be
  disqualifying, but **that is [unverified] and I will not assert it.** If #16 finds the other options
  unsatisfying, confirming zita-resampler's licence from the tarball's own `COPYING` is a ten-minute job
  with a potentially decisive payoff.

---

## 10. What is NOT settled

1. **No CPU measurement exists, for any option.** No project publishes figures at 48 kHz stereo, and
   nothing here was measured on the reference machine. §7.2 argues from scale that this will not be the
   deciding criterion, but that is reasoning, not measurement. If #16 wants a number, the shape of the
   experiment is libsamplerate's own `throughput_test.c` re-run per candidate, with the *ratio-change*
   path exercised — and under the real load (game + OBS + NVENC), which no library benchmark does.
2. **soxr's `SOXR_VR` quality is unquantified.** The engine's structure is known from source (20/12-tap
   poly-FIR, linear coefficient interpolation, half-band IIR stages); its SNR and bandwidth are published
   nowhere. Any claim that "soxr is the high-quality choice" is a claim about an engine ClipShift cannot
   use.
3. **libswresample's quality is unquantified from primary sources.** Its parameters are documented; its
   output SNR is not.
4. **Whether `CResamplerMediaObject` honours `MF_MT_AUDIO_FLOAT_SAMPLES_PER_SECOND`** — i.e. whether MF's
   resampler can express a fractional rate at all — is undocumented. It does not change the verdict (the
   drain-or-flush requirement is independently fatal) but the fact itself is unestablished.
5. **zita-resampler's licence.** The best-fitting API in the survey, excluded only because its licence
   could not be read from a primary source. See §9.
6. **miniaudio's resampler was not assessed.** Licence is settled and excellent; capability is not
   assessed. See §9.
7. **WDL/NAudio `GetCurrentLatency()`'s exact relationship to `swr_get_delay()`'s definition** is
   inferred from the implementation, not documented. If that option is chosen, the impulse test of §8.3.4
   is what establishes it — which is another argument for building that test first regardless.
8. **The LGPL analysis in §4.2 is a reading of the licence text, not legal advice.** In particular, the
   §6b question — whether a DLL the application itself installs counts as "already present on the user's
   computer system" — is a genuine ambiguity in a 1999 licence applied to 2026 Windows app distribution.
   The document's position is to satisfy 6d as well, which sidesteps it entirely at the cost of one file
   per release. Nobody with legal authority has been asked.
9. **`--enable-libsoxr` inside FFmpeg was not explored as a combination.** It is noted in §4.3 that it
   disables `swr_set_compensation`, which makes it uninteresting for ClipShift, but the interaction was
   not tested.
10. **Nothing here settles the choice, the PI loop constants, or segment-boundary behaviour.** Those are
    [#16](https://github.com/richardthornton/clipshift/issues/16)'s, per the ticket's own scope statement.
11. **One coupling #16 should be told about explicitly:** if the container decision
    ([#10](https://github.com/richardthornton/clipshift/issues/10)) lands on libavformat, FFmpeg is
    already a dependency and libswresample's marginal cost drops to zero — at which point the option with
    the best API fit is also the cheapest. If it does not, swresample is two DLLs and a compliance
    checklist for one function. **The resampler decision is not fully independent of the muxer decision.**

---

## Sources

**Licence texts**

- libsamplerate `COPYING` (BSD-2-Clause) — <https://github.com/libsndfile/libsamplerate/blob/master/COPYING>
- libsamplerate `NEWS` (relicensing date) — <https://github.com/libsndfile/libsamplerate/blob/master/NEWS>
- soxr `LICENCE` (LGPL-2.1+) — <https://sourceforge.net/p/soxr/code/ci/master/tree/LICENCE>
- GNU LGPL version 2.1, full text — <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt>
- FFmpeg `LICENSE.md` — <https://github.com/FFmpeg/FFmpeg/blob/master/LICENSE.md>
- FFmpeg legal / LGPL compliance checklist — <https://www.ffmpeg.org/legal.html>
- speexdsp `COPYING` (BSD-3-Clause) — <https://github.com/xiph/speexdsp/blob/master/COPYING>
- r8brain-free-src `LICENSE` (MIT) — <https://github.com/avaneev/r8brain-free-src/blob/master/LICENSE>
- miniaudio `LICENSE` (Unlicense or MIT-0) — <https://github.com/mackron/miniaudio/blob/master/LICENSE>
- NAudio `license.txt` (MIT) — <https://github.com/naudio/NAudio/blob/master/license.txt>
- Cockos WDL notice — <https://github.com/justinfrankel/WDL/blob/main/WDL/resample.h>
- pffft / FFTPACK notice inside soxr — <https://sourceforge.net/p/soxr/code/ci/master/tree/src/pffft.c>

**Headers, source and project documentation**

- libsamplerate `include/samplerate.h` — <https://github.com/libsndfile/libsamplerate/blob/master/include/samplerate.h>
- libsamplerate `src/common.h` (`SRC_MAX_RATIO`) — <https://github.com/libsndfile/libsamplerate/blob/master/src/common.h>
- libsamplerate full API docs — <http://libsndfile.github.io/libsamplerate/api_full.html>
- libsamplerate converter descriptions — <http://libsndfile.github.io/libsamplerate/api_misc.html>
- libsamplerate test suite — <https://github.com/libsndfile/libsamplerate/tree/master/tests>
- soxr `src/soxr.h` — <https://sourceforge.net/p/soxr/code/ci/master/tree/src/soxr.h>
- soxr `src/soxr.c` (delay dispatch) — <https://sourceforge.net/p/soxr/code/ci/master/tree/src/soxr.c>
- soxr `src/vr32.c` (variable-rate engine, `vr_delay` stub) — <https://sourceforge.net/p/soxr/code/ci/master/tree/src/vr32.c>
- soxr `src/cr.c` (`_soxr_delay`) — <https://sourceforge.net/p/soxr/code/ci/master/tree/src/cr.c>
- soxr `examples/5-variable-rate.c` — <https://sourceforge.net/p/soxr/code/ci/master/tree/examples/5-variable-rate.c>
- soxr `README` — <https://sourceforge.net/p/soxr/code/ci/master/tree/README>
- FFmpeg `libswresample/swresample.h` — <https://github.com/FFmpeg/FFmpeg/blob/master/libswresample/swresample.h>
- FFmpeg `libswresample/swresample.c` — <https://github.com/FFmpeg/FFmpeg/blob/master/libswresample/swresample.c>
- FFmpeg `libswresample/resample.c` — <https://github.com/FFmpeg/FFmpeg/blob/master/libswresample/resample.c>
- FFmpeg `doc/resampler.texi` — <https://github.com/FFmpeg/FFmpeg/blob/master/doc/resampler.texi>
- FFmpeg resampler options (rendered) — <https://ffmpeg.org/ffmpeg-resampler.html>
- speexdsp `include/speex/speex_resampler.h` — <https://github.com/xiph/speexdsp/blob/master/include/speex/speex_resampler.h>
- speexdsp `libspeexdsp/resample.c` — <https://github.com/xiph/speexdsp/blob/master/libspeexdsp/resample.c>
- r8brain `CDSPResampler.h` — <https://github.com/avaneev/r8brain-free-src/blob/master/CDSPResampler.h>
- r8brain README — <https://github.com/avaneev/r8brain-free-src>
- WDL `resample.h` / `resample.cpp` — <https://github.com/justinfrankel/WDL/blob/main/WDL/resample.h>
- NAudio `WdlResampler.cs` — <https://github.com/naudio/NAudio/blob/master/NAudio.Core/Dsp/WdlResampler.cs>
- NAudio `WdlResamplingSampleProvider.cs` — <https://github.com/naudio/NAudio/blob/master/NAudio.Core/Wave/SampleProviders/WdlResamplingSampleProvider.cs>
- zita-resampler documentation — <https://kokkinizita.linuxaudio.org/linuxaudio/zita-resampler/resampler.html>
- OBS `libobs/media-io/audio-resampler-ffmpeg.c` — <https://github.com/obsproject/obs-studio/blob/master/libobs/media-io/audio-resampler-ffmpeg.c>
- Julius O. Smith III, *Digital Audio Resampling Home Page*, CCRMA — <https://ccrma.stanford.edu/~jos/resample/>

**Microsoft Learn**

- Audio Resampler DSP — <https://learn.microsoft.com/en-us/windows/win32/medfound/audioresampler>
- `IWMResamplerProps::SetHalfFilterLength` — <https://learn.microsoft.com/en-us/windows/win32/api/wmcodecdsp/nf-wmcodecdsp-iwmresamplerprops-sethalffilterlength>
- `IMFTransform::SetOutputType` — <https://learn.microsoft.com/en-us/windows/win32/api/mftransform/nf-mftransform-imftransform-setoutputtype>
- `MF_MT_AUDIO_FLOAT_SAMPLES_PER_SECOND` — <https://learn.microsoft.com/en-us/windows/win32/medfound/mf-mt-audio-float-samples-per-second-attribute>
- .NET function pointers — <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code#function-pointers>
- .NET native interop best practices — <https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices>

**Package metadata**

- NuGet v3 registration API, queried 2026-08-13 — `https://api.nuget.org/v3/registration5-gz-semver2/{libsamplerate,aurio.soxr,aurio.libsamplerate}/index.json`
