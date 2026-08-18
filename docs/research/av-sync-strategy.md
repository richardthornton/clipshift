# A/V synchronisation strategy for separately-clocked output files

**Ticket:** [#5 — Find how three independently-clocked streams stay aligned for 4 hours](https://github.com/richardthornton/clipshift/issues/5)
**Status:** research complete — recommendation below is stated to be implementable as written.
**Date:** 2026-08-11

> **⚠️ Parts of this document have been overruled by later decisions.** It is kept as the research
> record of ticket #5, not as the current spec. Where it disagrees with a ticket resolution, **the ticket
> wins**.
>
> | Section | Overruled by | What changed |
> |---|---|---|
> | §7, the suspend/resume row | [#21](https://github.com/richardthornton/clipshift/issues/21) | The configurable threshold is **deleted, not tuned**. *Any* completed suspend/resume finalises and stops the recording — there is no short-gap padding case and no key to set. A threshold can only choose whether to *disclose* a gap, never prevent one. |
> | §10.2 | [#12](https://github.com/richardthornton/clipshift/issues/12) | `T0` is the record instant unconditionally. Ticks before the first real surface emit counted **black frames**; waiting for a first surface hangs forever on a static screen. |
> | §10.5, format pinning | [#11](https://github.com/richardthornton/clipshift/issues/11) | Do **not** pin the capture format with `AUTOCONVERTPCM \| SRC_DEFAULT_QUALITY`. ClipShift does every conversion itself — those flags put an in-engine resampler in the path that absorbs exactly the drift the sync design has to measure. |
> | §10.5, the resampler and its ~20 ms positional-error threshold | [#16](https://github.com/richardthornton/clipshift/issues/16) | Overruled **in full on the resampler**. Group delay is a one-time pre-roll, not a per-call query; positional error is arithmetically zero under the invariant, so the real state variable is buffer **occupancy** — floor 0, ceiling 500 ms. |
>
> §7's *other* rows stand. So does its structural note — that every stream being a pure function of one
> clock keeps the three files mutually aligned even under a *wrong* response — which is the load-bearing
> argument #21 used to decide that almost nothing should stop a recording.

---

## 0. The answer in one paragraph

Drift is **fully solvable by construction**, and the construction is simpler than the framing of the ticket suggests. Pick one master clock (`QueryPerformanceCounter`). Make every output file a *pure function of that clock*: at any master-clock instant `t`, the video file must contain exactly `round(60 × (t − T0))` frames and each audio file exactly `round(48000 × (t − T0))` sample-frames, where `T0` is a single shared epoch. Never let a device's own clock decide how many items get written — the device decides only *what* the items contain. Under that rule the three files cannot drift apart, because their lengths are not measurements, they are computations from a shared variable. Rate discrepancy between the device clock and the master clock is absorbed by a drift-locked resampler on the audio path (continuous, inaudible) and by frame duplication on the video path (which OBS already does). Genuine discontinuities — glitches, device changes, suspend — are absorbed by inserting silence / repeating frames for exactly the gap duration, which preserves the invariant.

**What is *not* solvable:** the fixed, undocumented acquisition-latency offset between "the instant the microphone diaphragm moved" and "the QPC timestamp WASAPI attached to that sample", versus the equivalent for the display path. That is a constant bias of order milliseconds to tens of milliseconds, it is **not drift**, and no software can remove it without measuring the specific rig. OBS does not solve it either — it ships a per-source manual **Sync Offset** control. ClipShift should do the same: a per-sink offset in the config file, default 0, with the measured value from the acceptance test as the recommended setting. See [§9](#9-what-is-genuinely-unsolved).

**The numbers, up front.** Two independent consumer crystals differ by 60–100 ppm in the worst case, which is **0.86–1.44 seconds over four hours** and one 60 fps frame of error **within six minutes** if uncorrected ([§1.3](#13-what-that-means-for-a-4-hour-clipshift-session)). The budget to hit is **≤ 2 ms at four hours**, taken from ITU-R BT.1359-1's allowance for a segment outside the broadcaster's control ([§1.4](#14-how-much-error-is-allowed--the-budget-from-the-standard)). The construction above bounds the error at **half a sample — 10.4 µs — permanently**, so the target is met with roughly 200× of margin. There is no tuning here to get wrong.

---

## 1. Where the drift comes from, quantified

### 1.1 The clocks in play

| Clock | What runs on it | Windows API surface |
|---|---|---|
| Platform counter (invariant TSC / HPET / ACPI PM) | `QueryPerformanceCounter`; WASAPI's `QPCPosition`; WGC's `SystemRelativeTime`; DXGI's `LastPresentTime` | `QueryPerformanceCounter` / `QueryPerformanceFrequency` |
| Audio endpoint clock (codec crystal, per device) | how fast sample-frames actually appear | `IAudioCaptureClient::GetBuffer` → `pu64DevicePosition` |
| GPU / display scanout clock | when the compositor presents | not exposed as a rate |

The audio endpoint and the platform counter are **different crystals on different boards**. There is no phase-locked relationship between them.

### 1.2 The magnitude — Microsoft states it directly

Microsoft quantifies consumer oscillator tolerance on the QPC page ([Acquiring high-resolution time stamps](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps)):

> Crystal oscillators that are used in personal computers and servers are typically manufactured with a frequency tolerance of ± 30 to 50 parts per million, and rarely, crystals can be off by as much as 500 ppm.

> A convenient reference is that a frequency error of 100 ppm causes an error of 8.64 seconds after 24 hours.

Their published accumulation table (for ±10 ppm) includes **± 36 milliseconds over 1 hour** and **± 0.86 seconds over 1 day**.

Corroborating bounds from the timekeeping standards, which are designed around exactly this component class:

- [RFC 5905 (NTPv4)](https://www.rfc-editor.org/rfc/rfc5905.txt) sets the *disciplined* frequency tolerance `PHI` at **15 ppm** — "It increases at a rate equal to the maximum disciplined system clock frequency tolerance (PHI), typically 15 ppm" — and its reference implementation caps undisciplined error at `#define MAXFREQ 500e-6 /* frequency tolerance (500 ppm) */`. It uses **100 ppm** as its worked illustrative "frequency difference".
- The Linux kernel's clock discipline uses the same bound: `#define MAXFREQ 500000 /* max frequency error (ns/s) */` — 500 ppm — in [`include/linux/timex.h`](https://raw.githubusercontent.com/torvalds/linux/master/include/linux/timex.h).

### 1.3 What that means for a 4-hour ClipShift session

4 h = 14,400 s. Audio at 48 kHz, video at 60 fps. **Uncorrected** relative error:

| Relative clock error | Offset at 4 h | In 48 kHz samples | In 60 fps frames | Time to reach one 60 fps frame (16.67 ms) |
|---:|---:|---:|---:|---:|
| 1 ppm | 14.4 ms | 691 | 0.86 | 4 h 37 m |
| 10 ppm | 144 ms | 6,912 | 8.6 | 27 m 47 s |
| **30 ppm** | **432 ms** | 20,736 | 25.9 | 9 m 16 s |
| **50 ppm** | **720 ms** | 34,560 | 43.2 | 5 m 33 s |
| **100 ppm** | **1.44 s** | 69,120 | 86.4 | 2 m 47 s |
| 500 ppm | 7.20 s | 345,600 | 432 | 33 s |

Because the audio crystal and the platform crystal are *independently* toleranced at ±30–50 ppm, their **relative** error is the sum in the worst case: **60–100 ppm, i.e. 0.86–1.44 seconds over a 4-hour session.**

**The headline number for the project:** a naively written audio file — one that simply concatenates whatever WASAPI hands it and declares 48000 Hz in the header — will be visibly out of lip-sync **within about six minutes**, and will end a 4-hour session roughly **one second** adrift. This is not a subtle effect and it is not optional to fix.

### 1.4 How much error is allowed — the budget from the standard

[ITU-R BT.1359-1, *Relative timing of sound and vision for broadcasting*](https://www.itu.int/dms_pubrec/itu-r/rec/bt/R-REC-BT.1359-1-199811-I!!PDF-E.pdf) (1998) is the primary source. Verbatim, from Appendix 1 §3:

> Tests conducted have shown that the thresholds of detectability are about + 45 ms to –125 ms and thresholds of acceptability are about +90 ms to –185 ms on the average.

with **NOTE 1 – "A positive value indicates that sound is advanced with respect to vision."** The asymmetry is real and worth internalising: the ear tolerates sound *late* (light arrives before sound in nature) far better than sound *early*.

The `recommends` clauses give the engineering budget:

> 2 that the overall tolerance in sound/picture timing (between points 1' and 6') shall not exceed +90 ms or –185 ms;
> 4 that the timing difference in the path from the output of the final programme source selection element to the input to the transmitter for emission should be kept within the values +22.5 ms and –30 ms;
> 5 if correction of errors is not possible then each downstream segment that is not under the control of the broadcaster shall not introduce any timing error in excess of ±2 ms.

ClipShift sits at the very top of the chain — it *is* the source. Its output is not the finished programme; everything downstream (the edit, encoding, the viewer's TV) adds its own error to the same budget. The correct posture is therefore clause 5's: **be a segment that contributes no more than ±2 ms**, not one that spends the whole ±90 ms allowance.

So: **design target ≤ 2 ms of total A/V misalignment at 4 hours**, and a hard fail at one video frame (16.67 ms at 60 fps). Both are far tighter than the ±45/–125 ms detectability thresholds — deliberately. The construction in §5.3 bounds the error at half a sample (10.4 µs) *permanently*, so this is not an ambitious target; it is roughly 200× of margin, and anything worse indicates a bug rather than a tuning shortfall.

### 1.5 The one thing that does *not* matter

QPC's own ±30–50 ppm absolute error is **irrelevant to this requirement.** Every ClipShift stream is derived from the same QPC reading, so an inaccurate QPC stretches all three files by the same factor and they stay mutually aligned. It affects only whether "4 hours" of recording is really 4 hours of wall-clock time (worst case ~0.7 s out over 4 h), which nobody will notice or care about. **Do not spend engineering effort disciplining the master clock.** Spend it on making every stream a function of it.

---

## 2. What OBS actually does — read from the source

Repository: [obsproject/obs-studio](https://github.com/obsproject/obs-studio), `master` at commit `14e3dae`.

### 2.1 The master clock is QPC, full stop

[`libobs/util/platform-windows.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/util/platform-windows.c), `os_gettime_ns()`:

```c
uint64_t os_gettime_ns(void)
{
	LARGE_INTEGER current_time;
	QueryPerformanceCounter(&current_time);
	return util_mul_div64(current_time.QuadPart, 1000000000, get_clockfreq());
}
```

`os_sleepto_ns()` in the same file busy-waits on `QueryPerformanceCounter` for the final millisecond. Everything downstream is expressed in QPC nanoseconds.

### 2.2 Video is a synthesised constant-rate grid, not a capture timeline

[`libobs/obs-video.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/obs-video.c), `obs_graphics_thread()` / `video_sleep()`:

```c
obs->video.video_time = os_gettime_ns();          /* the epoch, read once */
...
static inline void video_sleep(struct obs_core_video *video, uint64_t *p_time, uint64_t interval_ns)
{
	uint64_t cur_time = *p_time;
	uint64_t t = cur_time + interval_ns;
	int count;

	if (os_sleepto_ns(t)) {
		*p_time = t;                       /* exact grid step */
		count = 1;
	} else {
		/* overran: advance by however many whole intervals were missed */
		count = (int)(clamped_diff / interval_ns);
		*p_time = cur_time + interval_ns * count;
	}

	video->total_frames += count;
	video->lagged_frames += count - 1;

	vframe_info.timestamp = cur_time;          /* the IDEAL time, not the actual */
	vframe_info.count = count;
```

Three things to take from this:

1. The frame timestamp is `cur_time` — the **ideal grid position** — never `os_gettime_ns()` at the moment of capture. The grid is exact by construction: `T0 + n × interval_ns`.
2. When the render loop overruns, OBS does not stretch the timeline; it advances the grid by `count` intervals and emits `count` copies of the frame (`queue_frame()` loops on `vframe_info->count`). The output frame *rate* is preserved; the *content* degrades.
3. `lagged_frames += count - 1` — the degradation is counted and surfaced in OBS's stats. ClipShift should keep the same ledger.

**OBS ignores the display-capture API's own presentation timestamps entirely.** A grep of the whole tree for `LastPresentTime` and `SystemRelativeTime` returns nothing; `d3d11-duplicator.cpp` calls `AcquireNextFrame(0, &info, res.Assign())` and never reads `info.LastPresentTime`. Display capture in OBS is a *texture source* — the graphics thread samples whatever the latest surface happens to be when the grid ticks. This is a deliberate and correct choice, and it is the reason DXGI Desktop Duplication's change-driven nature (it returns `DXGI_ERROR_WAIT_TIMEOUT` forever on a static screen — see [`AcquireNextFrame`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe)) is not a problem for OBS.

### 2.3 The encoder makes video CFR by fiat

[`libobs/obs-encoder.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/obs-encoder.c), `receive_video()`:

```c
	enc_frame.frames = 1;
	enc_frame.pts = encoder->cur_pts;

	if (do_encode(encoder, &enc_frame, &frame->timestamp))
		encoder->cur_pts += encoder->timebase_num * encoder->frame_rate_divisor;
```

The encoded PTS is a **pure integer counter**. It is never derived from a wall-clock timestamp. The output is constant-frame-rate by definition. This is exactly what NLE ingest needs.

**Encoder overload does not break the grid either — and this matters directly for the "1080p60 while OBS is streaming" constraint.** In [`libobs/media-io/video-io.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/media-io/video-io.c), when the frame cache is exhausted because a consumer cannot keep up, `video_output_lock_frame()` does *not* discard the tick:

```c
	if (video->available_frames == 0) {
		video->cache[video->last_added].count += count;
		video->cache[video->last_added].skipped += count;
		locked = false;
	}
```

The tick is folded into the last cached frame's `count`, and `video_output_cur_frame()` then delivers that same frame `count` times, advancing its timestamp by exactly one interval each time (`frame_info->frame.timestamp += video->frame_time;`) while incrementing `skipped_frames`. So OBS's "skipped frames due to encoding lag" statistic counts **content** loss, never **timeline** loss: the number of frames handed to the encoder always equals the number of grid ticks.

For ClipShift this is the reassuring answer to the obvious worry about GPU contention with a concurrently-streaming OBS: under load you lose picture freshness, not synchronisation — *provided* the design folds overruns into repeats rather than dropping ticks.

### 2.4 Audio: sample-count authoritative within ±70 ms, step-corrected beyond

This is the mechanism the ticket asks about, and it lives in [`libobs/obs-source.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/obs-source.c), `source_output_audio_data()`:

```c
/* time threshold in nanoseconds to ensure audio timing is as seamless as possible */
#define TS_SMOOTHING_THRESHOLD 70000000ULL          /* 70 ms   (obs-source.c) */
#define MAX_TS_VAR             2000000000ULL        /* 2 s     (obs-internal.h) */
...
	} else if (source->next_audio_ts_min != 0) {
		diff = uint64_diff(source->next_audio_ts_min, in.timestamp);

		/* smooth audio if within threshold */
		if (diff > MAX_TS_VAR && !using_direct_ts)
			handle_ts_jump(source, source->next_audio_ts_min, in.timestamp, diff, os_time);
		else if (diff < TS_SMOOTHING_THRESHOLD) {
			...
			in.timestamp = source->next_audio_ts_min;    /* OVERRIDE the device ts */
		} else {
			blog(LOG_DEBUG, "Audio timestamp for '%s' exceeded TS_SMOOTHING_THRESHOLD, ...");
		}
	}

	source->next_audio_ts_min = in.timestamp + conv_frames_to_time(sample_rate, in.frames);
```

Decoded, this is a three-band policy:

| Discrepancy between accumulated sample count and the incoming device timestamp | OBS behaviour |
|---|---|
| < 70 ms | **Ignore the device timestamp.** Overwrite it with `next_audio_ts_min`, which advances by exactly `frames / sample_rate`. The timeline is pure sample count. |
| 70 ms – 2 s | Use the device timestamp as-is. `source_output_audio_place()` then places the buffer at the new offset — which **inserts a silence gap or discards samples**, i.e. a step correction of ≥70 ms. Logged at `LOG_DEBUG` only. |
| > 2 s | `handle_ts_jump()`: flush the source's audio buffer entirely and re-anchor (`reset_audio_timing` + `reset_audio_data`). |

The silence is literal, not figurative. `source_output_audio_place()` calls `deque_place(&source->audio_input_buf[i], buf_placement, ...)`, and `deque_upsize()` in [`libobs/util/deque.h`](https://github.com/obsproject/obs-studio/blob/master/libobs/util/deque.h) `memset`s the newly exposed region to zero before the incoming buffer is written past it. A backward jump is handled by the following `deque_pop_back()`, which truncates everything after the new end — i.e. samples are discarded.

So OBS is **timestamp-authoritative in the large and sample-count-authoritative in the small**, with a 70 ms deadband, and it corrects drift by **discrete insert/drop, never by resampling**. At a realistic 50 ppm that deadband is crossed every `0.070 / 50e-6 = 1400 s ≈ 23 minutes`, so an OBS 4-hour recording contains roughly **ten 70-millisecond audio splices**. This is fine for a live stream and merely tolerable for a recording; it is *not* what ClipShift should ship, because ClipShift can do better and its consumer is an editor, not a viewer. See [§5](#5-timestamp-authoritative-vs-resampling).

### 2.5 The audio mix timeline is also an exact grid

[`libobs/media-io/audio-io.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/media-io/audio-io.c), `audio_thread()`:

```c
	uint64_t samples = 0;
	uint64_t start_time = os_gettime_ns();
	...
	while (os_event_try(audio->stop_event) == EAGAIN) {
		samples += AUDIO_OUTPUT_FRAMES;                              /* 1024 */
		uint64_t audio_time = start_time + audio_frames_to_ns(rate, samples);
		os_sleepto_ns_fast(audio_time);
		input_and_output(audio, audio_time, prev_time);
		prev_time = audio_time;
	}
```

Same shape as the video grid: one QPC epoch, then exact nominal-rate arithmetic. **OBS's video timeline and OBS's audio timeline therefore have exactly zero relative drift by construction** — they are both `epoch + n × nominal_interval` on the same clock. All the messy per-device drift handling happens *upstream* of this grid, at the source. That is the architectural idea worth stealing.

### 2.6 How OBS establishes the common t=0

[`libobs/obs-encoder.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/obs-encoder.c), `buffer_audio()` / `calc_offset_size()` / `start_from_buffer()`:

```c
static inline size_t calc_offset_size(struct obs_encoder *encoder, uint64_t v_start_ts, uint64_t a_start_ts)
{
	uint64_t offset = v_start_ts - a_start_ts;
	offset = util_mul_div64(offset, encoder->samplerate, 1000000000ULL);
	return (size_t)offset * encoder->blocksize;
}
...
	if (!encoder->start_ts && paired_encoder) {
		uint64_t v_start_ts = paired_encoder->start_ts;

		if (!v_start_ts) { success = false; goto fail; }        /* no video yet — hold audio */

		end_ts += util_mul_div64(data->frames, 1000000000ULL, encoder->samplerate);
		if (end_ts <= v_start_ts) { success = false; goto fail; }  /* audio still behind video */

		if (data->timestamp < v_start_ts)
			offset_size = calc_offset_size(encoder, v_start_ts, data->timestamp);
		if (data->timestamp <= v_start_ts)
			clear_audio(encoder);

		encoder->start_ts = v_start_ts;

		if (v_start_ts < data->timestamp)
			start_from_buffer(encoder, v_start_ts);
	}
```

**Video wins.** OBS waits for the first video frame, takes its grid timestamp as `v_start_ts`, then **trims leading audio sample-frames** so that audio sample 0 corresponds to `v_start_ts` — sample-accurate to within the integer truncation of `util_mul_div64`, i.e. ≤1 sample ≈ 20.8 µs at 48 kHz.

This is *precisely* the primitive ClipShift needs, and it transfers to separate files unchanged. Note that OBS's *final container* alignment is coarser than this: `get_interleaved_start_idx()` in [`obs-output.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/obs-output.c) picks "the point where audio and video are closest together", which for AAC means alignment to within one 1024-sample frame (21.3 ms). ClipShift writing PCM has no such constraint and can keep the full sample-accurate alignment.

### 2.7 What OBS does about WASAPI specifically

[`plugins/win-wasapi/win-wasapi.cpp`](https://github.com/obsproject/obs-studio/blob/master/plugins/win-wasapi/win-wasapi.cpp), `ProcessCaptureData()`:

```c
		res = capture->GetBuffer(&buffer, &frames, &flags, &pos, &ts);
		...
		if (!sawBadTimestamp && flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) {
			blog(LOG_WARNING, "[WASAPISource::ProcessCaptureData] Timestamp error!");
			sawBadTimestamp = true;
		}

		if (flags & AUDCLNT_BUFFERFLAGS_SILENT) {
			uint32_t requiredBufSize = get_audio_channels(speakers) * frames * 4;
			if (silence.size() < requiredBufSize) silence.resize(requiredBufSize);
			buffer = silence.data();                 /* substitute zeros, KEEP the frame count */
		}
		...
		if (sourceType == SourceType::ProcessOutput) {
			data.timestamp = ts * 100;                                   /* QPC, 100ns → ns */
		} else {
			data.timestamp = useDeviceTiming ? ts * 100 : os_gettime_ns();
			if (!useDeviceTiming)
				data.timestamp -= util_mul_div64(frames, UINT64_C(1000000000), sampleRate);
		}
```

Four load-bearing details:

1. **`AUDCLNT_BUFFERFLAGS_SILENT` substitutes zeros but keeps `frames`.** Skipping the packet would shorten the timeline. Copy this exactly.
2. **The defaults differ by source type.** `GetWASAPIDefaultsDeviceOutput()` sets `use_device_timing = true`; `GetWASAPIDefaultsInput()` sets it `false`. So desktop/loopback audio is stamped from WASAPI's `QPCPosition`, and microphone input is stamped from `os_gettime_ns()` at arrival minus the packet duration. OBS trusts the loopback device's timestamps and does not trust capture devices' — a pragmatic judgement encoded as a shipping default.
3. `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY` is **not handled at all**. The 70 ms/2 s bands in `obs-source.c` catch the consequences generically.
4. **The silent-loopback fix.** `WASAPISource::ClearBuffer()` is called for `SourceType::DeviceOutput` only:

```c
	/* Silent loopback fix. Prevents audio stream from stopping and */
	/* messing up timestamps and other weird glitches during silence */
	/* by playing a silent sample all over again. */
	res = client->GetBufferSize(&frames);
	...
	res = render->GetBuffer(frames, &buffer);
	memset(buffer, 0, (size_t)frames * (size_t)wfex->nBlockAlign);
	render->ReleaseBuffer(frames, 0);
```

OBS opens a **render** client on the endpoint and pushes a buffer of zeros, to stop the endpoint going idle and starving the loopback capture. This is a workaround for behaviour Microsoft has never documented (see [§4.3](#43-loopback-during-silence-an-undocumented-hazard)).

---

## 3. What FFmpeg does

### 3.1 `-async` is gone, and what it was

`-async` **was removed in FFmpeg 6.0** — commit [`3d86a13b`](https://github.com/FFmpeg/FFmpeg/commit/3d86a13b47b726e49c2d780c5f723c290e8a36b4), *"fftools/ffmpeg: drop the -async option — It has been deprecated in favor of the aresample filter for almost 10 years."* It is present in `release/5.1` and absent from 6.0 onward; the string does not occur anywhere in the current [ffmpeg.html](https://ffmpeg.org/ffmpeg.html).

In its last incarnation it was pure sugar — `fftools/ffmpeg_filter.c`, `configure_input_audio_filter()` (release/5.1):

```c
if (audio_sync_method > 0) {
    av_strlcatf(args, sizeof(args), "async=%d", audio_sync_method);
    if (audio_drift_threshold != 0.1)
        av_strlcatf(args, sizeof(args), ":min_hard_comp=%f", audio_drift_threshold);
    if (!fg->reconfiguration)
        av_strlcatf(args, sizeof(args), ":first_pts=0");
    AUTO_INSERT_FILTER_INPUT("-async", "aresample", args);
}
```

So the real question is what `aresample` does.

### 3.2 `aresample` / swresample: the actual thresholds

Defaults from the AVOption table in `libswresample/options.c` (docs: [ffmpeg-resampler.html](https://ffmpeg.org/ffmpeg-resampler.html)):

| Option | Default | Meaning |
|---|---|---|
| `min_comp` | **`FLT_MAX`** | below this diff (s), do nothing — **compensation is off by default** |
| `min_hard_comp` | **`0.1`** | above 100 ms diff, hard fill/trim |
| `comp_duration` | **`1`** | seconds over which a soft stretch is spread |
| `max_soft_comp` | **`0`** | max stretch factor — **0 means soft compensation never fires** |
| `async` | **`0`** | 0 = off; 1 = fill/trim only; >1 = max samples/sec of stretch |
| `first_pts` | `AV_NOPTS_VALUE` | assumed first pts, in 1/sample_rate units |

`swr_init()` in `libswresample/swresample.c` desugars `async`:

```c
if (s->async) {
    if (s->min_compensation >= FLT_MAX/2)
        s->min_compensation = 0.001;                 /* 1 ms */
    if (s->async > 1.0001)
        s->max_soft_compensation = s->async / (double) s->in_sample_rate;
}
```

And `swr_next_pts()` is the decision:

```c
if (fabs(fdelta) > s->min_compensation) {
    if (s->outpts == s->firstpts || fabs(fdelta) > s->min_hard_compensation){
        if (delta > 0) ret = swr_inject_silence(s,  delta / s->out_sample_rate);
        else           ret = swr_drop_output   (s, -delta / s-> in_sample_rate);
    } else if (s->soft_compensation_duration && s->max_soft_compensation) {
        int duration = s->out_sample_rate * s->soft_compensation_duration;
        int comp = av_clipf(fdelta, -max_soft_compensation, max_soft_compensation) * duration;
        swr_set_compensation(s, comp, duration);
    }
}
```

Which gives:

| Discrepancy | With `async=1` | With `async=N`, N>1 |
|---|---|---|
| ≤ 1 ms | nothing | nothing |
| 1 ms – 100 ms | **nothing** (`max_soft_comp` is 0, so the soft branch is dead code) | **soft**: resample-stretch, ≤ N samples/sec, spread over 1 s |
| > 100 ms | **hard**: `swr_inject_silence()` or `swr_drop_output()` | hard |
| first frame | hard, unconditionally — this is what `first_pts=0` uses to pad/trim the head | same |

**So FFmpeg's default live-capture behaviour is structurally identical to OBS's**: let error accumulate to a threshold, then splice. `-async 1` (the incantation everyone copies) reaches soft compensation **never** — every correction is an audible 100 ms silence insert or sample drop. To get continuous, inaudible correction you must ask for it explicitly: `-af aresample=async=1000:first_pts=0` (1000 samples/sec of permitted stretch).

That two major implementations both default to splice-at-a-threshold is not evidence that splicing is right — it is evidence that both are optimised for live playback, where a rare click beats a permanent offset. ClipShift's output is editorial source material, which inverts that trade.

### 3.3 How soft compensation actually changes the ratio

[`swr_set_compensation()`](https://ffmpeg.org/doxygen/trunk/group__lswr.html) — "Activate resampling compensation ("soft" compensation). This function is internally called when needed in `swr_next_pts()`." — takes `sample_delta` ("delta in PTS per sample") and `compensation_distance` ("number of samples to compensate for"). The mechanism is in `libswresample/resample.c`, `set_compensation()`:

```c
c->compensation_distance = compensation_distance;
if (compensation_distance)
    c->dst_incr = c->ideal_dst_incr - c->ideal_dst_incr * (int64_t)sample_delta / compensation_distance;
else
    c->dst_incr = c->ideal_dst_incr;
```

The phase increment is scaled by `(1 − sample_delta/compensation_distance)` — a fractional rate change — and it is **temporary**: `multiple_resample()` decrements `compensation_distance` as it produces output and restores `dst_incr = ideal_dst_incr` when it reaches zero. This is precisely the primitive ClipShift's drift-locked resampler needs (§5.3): a per-window ratio nudge, not a permanent rate change. Note the side effect — calling it forces `SWR_FLAG_RESAMPLE` on and re-inits, so a same-rate passthrough silently becomes a real resampler.

### 3.4 `-use_wallclock_as_timestamps` — a warning, not a tool

It is a demuxer option (documented in [ffmpeg-formats.html](https://ffmpeg.org/ffmpeg-formats.html), not ffmpeg.html), default 0. Its entire implementation, in `libavformat/demux.c`:

```c
/* TODO: audio: time filter; video: frame reordering (pts != dts) */
if (s->use_wallclock_as_timestamps)
    pkt->dts = pkt->pts = av_rescale_q(av_gettime(), AV_TIME_BASE_Q, st->time_base);
```

It **discards device timestamps entirely** and stamps PTS and DTS with `av_gettime()` **at the moment the demuxer thread dequeues the packet** — including device buffering, driver latency and scheduler jitter, with no smoothing (note the in-source `TODO: audio: time filter`). For audio capture this replaces a monotonic sample-accurate position with a jittery arrival time, i.e. it *creates* the drift that `aresample` then has to fix. **ClipShift must not adopt this pattern.** Timestamp the *acquisition instant* (WASAPI's `QPCPosition`), not the *arrival instant*.

`-copyts` is documented as "Do not process input timestamps, but keep their values without trying to sanitize them. In particular, do not remove the initial start time offset value" — with the caveat that "depending on the `fps_mode` option or on specific muxer processing (e.g. in case the format option `avoid_negative_ts` is enabled) the output timestamps may mismatch with the input timestamps even when this option is selected."

### 3.5 What FFmpeg's Windows capture device does about timestamps

FFmpeg has **no WASAPI capture device**; Windows audio capture goes through `dshow`. `libavdevice/dshow_pin.c`, `ff_dshow_meminputpin_Receive()`:

```c
hr = IMediaSample_GetTime(sample, &sampletime, &dummy);
IReferenceClock_GetTime(clock, &graphtime);
if (devtype == VideoDevice && !ctx->use_video_device_timestamps) {
    /* PTS from video devices is unreliable. */
    chosentime = graphtime;
    ...
```

The `use_video_device_timestamps` option ([ffmpeg-devices.html](https://ffmpeg.org/ffmpeg-devices.html): "If set to `false`, the timestamp for video frames will be derived from the wallclock instead of the timestamp provided by the capture device. This allows working around devices that provide unreliable timestamps") defaults to **1** — `libavdevice/dshow.c` has `AV_OPT_TYPE_BOOL, {.i64 = 1}`. Note that "wallclock" here means the **DirectShow graph reference clock**, not OS time; both branches read the same clock family, and the stream timebase is 100 ns (`avpriv_set_pts_info(st, 64, 1, 10000000)`).

The same tension appears in OBS (§2.7): trust the device's timestamps for some source classes and not others. Both projects chose per-class defaults rather than a universal rule. That is worth respecting — ClipShift should make the timestamp source per-sink and configurable, defaulting to WASAPI `QPCPosition` for loopback and arrival-time-minus-packet-duration for capture devices, exactly as OBS does.

### 3.6 What FFmpeg can and cannot put in the files

**WAV/RF64 start time.** FFmpeg's WAV muxer can write a BWF `bext` chunk, but it is **off by default** — `libavformat/wavenc.c`: `{ "write_bext", "Write BEXT chunk.", ..., { .i64 = 0 }, 0, 1, ENC }`, documented as "Defaults to false". `bwf_write_bext_chunk()` sources every field from `s->metadata`, including the origin:

```c
if (tmp_tag = av_dict_get(s->metadata, "time_reference", NULL, 0))
    time_reference = strtoll(tmp_tag->value, NULL, 10);
avio_wl64(s->pb, time_reference);
```

**`time_reference` is a 64-bit count of sample-frames since midnight** (the BWF timecode origin) and **FFmpeg never computes it** — absent the metadata tag it writes 0. The demuxer (`wavdec.c`, `wav_parse_bext_tag()`) reads it back into metadata as a decimal string and **does nothing else with it**: it does not affect stream timestamps or `start_time`. It is inert.

Sony Wave64: `ff_w64_muxer` is in the same file but has **no options at all**, and `w64_write_header()` writes only `riff`/`wave`/`fmt`/`fact`/`data`. **No `bext`, no origin.** RF64 uses the same `wavenc.c` path, so `write_bext` works there.

Bottom line: **a WAV file carries no start time in its own right.** The only origin channel is a `bext` chunk the writer populates deliberately, and no FFmpeg consumer acts on it.

**MP4 start time.** Carried as an `edts`/`elst` edit list. `libavformat/movenc.c`, `mov_write_edts_tag()`:

```c
delay = av_rescale_rnd(start_dts + start_ct, mov->movie_timescale, track->timescale, AV_ROUND_DOWN);
entry_count = 1 + (delay > 0);
...
if (delay > 0) {                    /* add an empty edit to delay presentation */
    avio_wb32(pb, delay);  avio_wb32(pb, -1);   /* segment_duration, media_time = -1 */
    avio_wb32(pb, 0x00010000);                  /* media_rate 1.0 */
```

A positive start becomes a two-entry list whose first entry is an *empty edit* — a presentation gap. `use_editlist` defaults to auto, which resolves to **on for regular MP4** and off for fragmented MP4 without `+delay_moov`. Watch the coupling in `mov_init()`: turning the edit list off silently flips `avoid_negative_ts` to `make_zero`, destroying the absolute origin.

**For ClipShift this is a hazard to avoid, not a feature to use.** An empty edit at the head of the video file is exactly the "manual nudging" the brief forbids — some tools honour it, some ignore it, and the two behaviours differ by the delay amount. Write first PTS = 0 with **no** edit list, and carry the wall-clock origin as a `tmcd` timecode track instead.

`-muxdelay` (default 0.7 s → `AVFormatContext.max_delay`) and `-muxpreload` are MPEG-PS concepts; `movenc.c` reads neither. They will not shift an MP4 start time. The only MP4 box that records a true wall-clock origin is `prft` (`-write_prft`, default off), which carries an NTP timestamp — but it is written per-fragment and is effectively fragmented-MP4 only.

---

## 4. The Windows clock APIs — what they actually promise

All facts here are from learn.microsoft.com; quotes are verbatim.

### 4.1 QueryPerformanceCounter

From [Acquiring high-resolution time stamps](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps):

- **Monotonic:** "*Is the performance counter monotonic (non-decreasing)?* — Yes. QPC does not go backward."
- **Fixed frequency:** "The frequency of the performance counter is fixed at system boot and is consistent across all processors so you only need to query the frequency from QueryPerformanceFrequency as the application initializes, and then cache the result."
- **Unaffected by power management:** "*Is QPC accuracy affected by processor frequency changes caused by power management or Turbo Boost technology?* — No."
- **Cross-core consistent**, with a ±1 tick ordering ambiguity between threads.
- **Counts sleep — this is the trap:** "QueryPerformanceCounter reads the performance counter and returns the total number of ticks that have occurred since the Windows operating system was started, **including the time when the machine was in a sleep state such as standby, hibernate, or connected standby**."

That last point is the one that silently ruins a long recording: QPC advances through a suspend, but no audio frames and no video frames are produced. The documented way to detect it is [`QueryUnbiasedInterruptTimePrecise`](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryunbiasedinterrupttime), whose count "does not include time the system spends in sleep or hibernation" — compare its delta with the QPC delta over the same interval.

Also: do not infer the hardware rate from `QueryPerformanceFrequency` — "when running under a hypervisor that implements the hypervisor version 1.0 interface (or always in some newer versions of Windows), the performance counter frequency is fixed to 10 MHz."

### 4.2 WASAPI capture

From [`IAudioCaptureClient::GetBuffer`](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer):

- `pu64DevicePosition` — "the device position of the first audio frame in the data packet. The device position is expressed as the number of audio frames from the start of the stream." It is **stream-relative and resets to 0 on every stream re-initialisation**.
- `pu64QPCPosition` — "the value of the performance counter at the time that the audio endpoint device recorded the device position of the first audio frame in the data packet. The method converts the counter value to 100-nanosecond units before writing it to *pu64QPCPosition." The documented formula is `*pu64QPCPosition = 10,000,000 · t / f`.

  **Units gotcha:** WASAPI hands you 100 ns units; DXGI's `DXGI_OUTDUPL_FRAME_INFO.LastPresentTime` hands you **raw QPC ticks**. Normalise at the boundary, and do the multiply before the divide in 128-bit.

- On an empty buffer the method "does not write to the variables that are pointed to by the ppData, pu64DevicePosition, and pu64QPCPosition parameters."

The three buffer flags, quoted in full from [`_AUDCLNT_BUFFERFLAGS`](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags) — this is the *entire* normative text, and Microsoft gives no guidance on what to do about any of them:

> `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY` — "The data in the packet is not correlated with the previous packet's device position; this is possibly due to a stream state transition or timing glitch."
> `AUDCLNT_BUFFERFLAGS_SILENT` — "Treat all of the data in the packet as silence and ignore the actual data values."
> `AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR` — "The time at which the device's stream position was recorded is uncertain. Thus, the client might be unable to accurately set the time stamp for the current data packet."

**Crucially — the position is drift-corrected but the frequency is not.** From [`IAudioClock::GetFrequency`](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getfrequency):

> If the clock generated by an audio device runs at a nominally constant frequency, the frequency might still vary slightly over time due to drift or jitter with respect to a reference clock. The reference clock might be a wall clock or the system clock used by the QueryPerformanceCounter function. **The GetFrequency method ignores such variations and simply reports a constant frequency. However, the position reported by the IAudioClient::GetPosition method takes all such variations into account to report an accurate position value each time it is called.**

This is the single most useful sentence in the WASAPI documentation for this problem. It means **the (`DevicePosition`, `QPCPosition`) pair is a direct, in-band measurement of the audio device's true rate against QPC** — no external instrument required:

```
ppm = ( Δframes / nominal_rate ) / ( Δqpc_seconds ) − 1, ×10⁶
```

Microsoft publishes **no ppm figure for audio device clock drift** anywhere in the WASAPI documentation. That gap is fine, because ClipShift can measure it at runtime with the formula above and log it.

**`AUDCLNT_STREAMFLAGS_RATEADJUST` does not help us.** From the [stream flags reference](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants): "**This flag is valid only for a rendering device. Otherwise the GetService call fails with the error code AUDCLNT_E_WRONG_ENDPOINT_TYPE.**" You cannot slave a capture stream's rate to your master clock. Rate correction on capture must be done by our own resampler.

**Pin the format so a mix-format change cannot change our output rate.** [Device Formats](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-formats): "the format for an application stream typically must have the same number of channels and the same sample rate as the stream format used by the device." The escape hatch is `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY`, which inserts "a channel matrixer and a sample rate converter … as necessary". Microsoft's own ApplicationLoopback sample does exactly this.

**Format/device changes still kill the stream.** Per [Relevant Device Notifications for Stream Routing](https://learn.microsoft.com/en-us/windows/win32/coreaudio/relevant-device-notifications-for-stream-routing), a format change surfaces as `IAudioSessionEvents::OnSessionDisconnected` with `DisconnectReasonFormatChanged`, and "The client can handle the notification by reopening the stream in the new format." Microsoft's own [stream routing implementation guide](https://learn.microsoft.com/en-us/windows/win32/coreaudio/stream-routing-implementation-considerations) makes step 6 "Perform position mapping calculations" and warns: "During the transition, the application must ensure that the clock does not get out of synchronization, resulting in out-of-sync audio and video streams."

### 4.3 Loopback during silence — an undocumented hazard

**Microsoft nowhere documents whether an endpoint loopback stream (`AUDCLNT_STREAMFLAGS_LOOPBACK`) delivers packets while nothing is rendering.** There is no positive statement, no negative statement, and no mitigation guidance across [Loopback Recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording), [Capturing a Stream](https://learn.microsoft.com/en-us/windows/win32/coreaudio/capturing-a-stream), and [`IAudioClient::Initialize`](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize). The `Initialize` page's phrasing "the relevant event handles are now set for loopback-enabled streams **that are active**" leaves the idle case explicitly undefined.

By contrast, the **process loopback** API (Windows 10 build 20348+, `ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`) gives a contractual guarantee — from the [ApplicationLoopback sample page](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/):

> If the processes whose audio will be captured does not have any audio rendering streams, then the capturing process receives silence.

Neither Microsoft sample is a model for a timeline-accurate recorder. `ApplicationLoopback/cpp/LoopbackCapture.cpp` retrieves `u64DevicePosition` and `u64QPCPosition` into locals and **never uses them**, declares `dwCaptureFlags` and **never examines it**, and blindly `WriteFile`s the bytes. `Win7Samples/multimedia/audio/CaptureSharedTimerDriven/WASAPICapture.cpp` passes `NULL, NULL` for the position parameters entirely, handles `SILENT` correctly (zero-fill), and ignores `DATA_DISCONTINUITY`.

**Consequence for ClipShift:** treat endpoint-loopback continuity as undefined behaviour. Apply OBS's silent-render trick ([§2.7](#27-what-obs-does-about-wasapi-specifically)) *and* design the gap-fill path to be load-bearing rather than defensive. Process loopback with an exclude-tree ("everything except me") is the more contractually solid long-term path and is already on the ClipShift roadmap for per-app audio — that is now a *sync* argument for it, not only a feature argument.

### 4.4 Video capture: neither API gives you a constant rate

**DXGI Desktop Duplication** — [`DXGI_OUTDUPL_FRAME_INFO`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info):

> `LastPresentTime` — "The time stamp of the last update of the desktop image. The operating system calls the **QueryPerformanceCounter** function to obtain the value. **A zero value indicates that the desktop image was not updated** since an application last called the IDXGIOutputDuplication::AcquireNextFrame method…"

> "**If only the pointer was updated (that is, the desktop image was not updated), the AccumulatedFrames, TotalMetadataBufferSize, and LastPresentTime members are set to zero.**"

`AcquireNextFrame` "returns if the interval elapses, and a new desktop image is not available", with `DXGI_ERROR_WAIT_TIMEOUT`. It is strictly change-driven: **a static desktop produces no frames at all.** It also returns `DXGI_ERROR_ACCESS_LOST` on "Desktop switch; Mode change; Switch from DWM on, DWM off, or other full-screen application" — over four hours with a fullscreen game, UAC prompts and a lock screen, this will fire repeatedly and the duplication interface must be recreated.

**Windows.Graphics.Capture** — [`Direct3D11CaptureFrame.SystemRelativeTime`](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime) is documented in a single sentence: "The **QPC (Query Performance Counter)** time at which the compositor rendered the frame." The [screen capture guide](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) adds: "The SystemRelativeTime is QPC (QueryPerformanceCounter) time that can be used to synchronize other media elements." `TryGetNextFrame` returns null when the pool is empty; `FrameArrived` "fires every time a new frame is available" — again change-driven.

*Unverified:* the property's type is `TimeSpan` (100 ns ticks) but Microsoft never states whether the raw counter was scaled. Given WASAPI's identical `10,000,000 · t / f` pattern, 100 ns is near-certain, but **verify empirically on target hardware** before trusting it.

**Both paths therefore require ClipShift to run its own pacing clock and re-emit the last acquired surface when nothing new has arrived — exactly what OBS does.** This is not a workaround; it is the only correct design for a fixed-rate output.

---

## 5. Timestamp-authoritative vs resampling

The ticket frames this as two families. In practice a correct implementation needs **both**, applied to different phenomena, and the important realisation is that they are not alternatives at the same level.

### 5.1 Why pure timestamp-authoritative is wrong here

"Stamp everything against one master clock and let the container carry irregular timing" works for a container that can express irregular timing. It fails for ClipShift twice over:

- **WAV cannot express it at all.** A RIFF/WAVE file is a bare sample array plus a declared rate. There is no per-sample timestamp, no gap representation, no edit list. Sample *n* is at *n*/rate. Full stop. A "timestamp-authoritative" audio stream must therefore be *rendered down* to a uniform sample array before it can be written, which is precisely the operation this section is about.
- **VFR video is hostile to NLEs.** Even where the container can carry it (MP4 with per-frame durations), variable frame rate material is a known problem for editors, and OBS itself refuses to produce it (§2.3, `cur_pts` is an integer counter).

So the *output* must be uniform. Timestamps are an internal representation, not an output format.

### 5.2 Insert/drop vs continuous resampling, quantified

Given the output must be uniform at exactly *R* samples per master-clock second, and the device supplies *R*(1+ε) samples per master-clock second, the surplus/deficit must go somewhere. Two mechanisms:

| | **Insert/drop (OBS)** | **Drift-locked resampling (recommended)** |
|---|---|---|
| Mechanism | discard or duplicate/silence-pad whole blocks | vary the resampler's conversion ratio |
| Correction granularity in OBS | 70 ms steps | n/a |
| Events over 4 h at 50 ppm | ~10 splices of 70 ms | continuous, zero splices |
| If done at 1-sample granularity | 2.4 sample edits/sec, **34,560 edits over 4 h** | — |
| Artefact | click / dropout at each splice | pitch shift of ε |
| Magnitude of artefact at 50 ppm | audible discontinuity | 1200·log₂(1.00005) = **0.087 cents** |
| CPU | ~0 | one polyphase SRC per audio sink at 48 kHz stereo — negligible beside NVENC |
| Handles *gaps* (device glitch, suspend)? | yes, natively | **no** — a 300 ms hole is not a rate error |

0.087 cents is roughly two orders of magnitude below the just-noticeable difference for pitch. The resampler's artefact is inaudible; the splice's is not. For a tool whose output goes into an editor and then to an audience, resampling is the correct choice — and it is what asynchronous sample-rate conversion exists to do.

**But resampling cannot fix discontinuities.** A dropped buffer, a device format change, or a system suspend removes real time from the stream. That must be filled with silence of exactly the missing duration. So:

> **Rate error → resampler. Gap → silence insertion. Two mechanisms, two phenomena, one threshold between them.**

### 5.3 The framing that makes drift impossible rather than small

The important move is to stop treating the output length as something that is *measured* and start treating it as something that is *computed*.

Drive the resampler by **output demand**, not input supply. On each audio tick (say every 10 ms of master clock at time *t*):

```
target_total = round( R × (t − T0) / 1e9 )       // R = 48000, t and T0 in ns
need         = target_total − written_total
```

Ask the resampler for exactly `need` output sample-frames and write them. Adjust the conversion ratio slowly so that the resampler's *input* buffer occupancy stays near a setpoint (e.g. 50 ms); if the input buffer is empty because of a genuine gap, emit silence for the shortfall.

Under this formulation `written_total == round(R × (t − T0))` **at every instant, exactly, by construction.** Drift is not "small", it is not a tuning outcome, and it is not a property that can regress: it is arithmetically zero. The control loop governs only *audio quality* (avoiding buffer starvation and excessive latency), never correctness. Video is the identical shape with `target_frames = round(fps × (t − T0))`.

This is the single most important design statement in this document.

---

## 6. The separate-files complication

With one container the muxer arbitrates: it holds a shared timebase, and a decoder reading the file learns each stream's start offset from the container. With three files there is no arbiter, so the *file format itself* must carry enough to reconstruct alignment — or the alignment must be baked into the sample data.

### 6.1 What an NLE actually does when you drag three files onto a timeline

This is the decisive practical fact and it is worth being blunt about: **dragging clips onto a timeline positions them at the playhead / insertion point, not by their embedded timecode.** Timecode-based alignment in DaVinci Resolve is an explicit, separate operation — *Auto Sync Audio → Based on Timecode* in the Media Pool, or *Clip → Auto Align Clips* on the Edit page. Premiere's equivalent is *Merge Clips* with a Timecode synchronisation point.

Therefore:

> **Embedded timecode is a convenience, not the mechanism. The mechanism must be that all three files have the same start instant and the same time-base semantics, so that dropping them at a common playhead is correct with no further action.**

That is "guaranteed by construction" in the literal sense the ticket asks for. Timecode metadata is then a valuable *belt-and-braces* addition: it lets a user who *has* nudged something re-derive the truth, and it lets Resolve's auto-sync recover alignment if clips get separated.

### 6.2 What each file must carry

| | Video (MP4/MOV) | Audio (WAV/RF64) |
|---|---|---|
| **Start instant** | first frame PTS = 0, **no edit-list offset** | sample 0 = T0, achieved by head trim/pad |
| **Rate semantics** | fixed `timescale`/`sample_delta`, CFR | declared sample rate in `fmt ` |
| **Exact item count** | exactly `round(fps × duration)` frames | exactly `round(R × duration)` sample-frames |
| **Timecode (optional, recommended)** | `tmcd` track | BWF `bext` `TimeReferenceLow/High` |
| **Session identity** | filename prefix | filename prefix + `bext` `Description`/`OriginatorReference` |

The head trim/pad is the operation OBS already implements as `calc_offset_size()` (§2.6): convert `T0 − first_audio_qpc` into a sample count and discard (or, if audio started late, pad with) exactly that many sample-frames. Accuracy is one sample — 20.8 µs.

**BWF `TimeReference` is the right timecode carrier, and its units are convenient.** From [EBU Tech 3285, *Broadcast Wave Format Specification*](https://tech.ebu.ch/docs/tech/tech3285.pdf), verbatim:

> `DWORD TimeReferenceLow;  /* First sample count since midnight, low word */`
> `DWORD TimeReferenceHigh; /* First sample count since midnight, high word */`

> **TimeReference** — These fields shall contain the time-code of the sequence. It is a 64-bit value which contains the first sample count since midnight. The number of samples per second depends on the sample frequency which is defined in the field `<nSamplesPerSec>` from the `<format chunk>`.

It is a **sample count**, not an HH:MM:SS:FF timecode — so there is no frame-rate rounding to negotiate, and the value ClipShift writes is exactly `round(48000 × seconds_since_local_midnight(T0))`. Both audio files get the *same* value (they share `T0`), which is what makes timecode-based auto-sync recover the correct relationship. The matching `tmcd` track on the video file must be derived from the same `T0`.

FFmpeg will write this chunk (`-write_bext 1 -metadata time_reference=…`) but **never computes the value** and defaults it to 0 (§3.6) — so if ClipShift ever muxes through FFmpeg rather than writing WAV directly, this is a field it must populate itself.

### 6.3 The 4 GiB problem

RIFF chunk sizes are 32-bit. Over a 4-hour session at 48 kHz:

| Audio format | Bytes/sec | 4 h total | Fits in RIFF (< 4 GiB)? |
|---|---:|---:|---|
| 16-bit stereo | 192,000 | 2.76 GB (2.58 GiB) | yes (but > 2 GiB — some tools use signed offsets) |
| 24-bit stereo | 288,000 | 4.15 GB (3.86 GiB) | **barely** — overflows at 4 h 8 m |
| 32-bit float stereo | 384,000 | 5.53 GB (5.15 GiB) | **no — overflows at 3 h 6 m** |
| 16-bit 5.1 | 576,000 | 8.29 GB | no |

WASAPI's shared-mode mix format is float32, so the naive "just write what WASAPI gives you" path **overflows RIFF at three hours and six minutes** — inside the stated 4-hour target. Either convert to 24-bit PCM on write (and still be within 8 minutes of the ceiling) or use RF64. Given the crash-survivability constraint, the right answer is to write RF64 from the start, or to write a `JUNK` placeholder large enough to be rewritten as a `ds64` chunk on finalise.

*(See §6.4 for the verified container details.)*

### 6.4 RF64, and how to grow into it

The mechanism is described in [EBU Tech 3306](https://tech.ebu.ch/docs/tech/tech3306.pdf), verbatim:

> RF64 achieved backwards compatibility with 32-bit BWF files by enabling on-the-fly switching from the BWF RIFF size field to the 64-bit `riffSize` value registered in a `<ds64>` chunk. This typically happens when a recording application passes the 4 Gbyte file size.

(Tech 3306 also records that the format was adopted by the ITU as **Recommendation ITU-R BS.2088** — "BW64" — in October 2015, which is the standard to cite for anything new.)

The RIFF→RF64 upgrade is worth understanding in detail because it interacts with the crash-survivability constraint. FFmpeg implements the canonical pattern in [`libavformat/wavenc.c`](https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavformat/wavenc.c):

```c
#define RF64_AUTO   (-1)
#define RF64_NEVER  0
#define RF64_ALWAYS 1
...
{ "rf64", "Use RF64 header rather than RIFF for large files.", OFFSET(rf64), AV_OPT_TYPE_INT, { .i64 = RF64_NEVER }, -1, 1, ENC, .unit = "rf64" },
```

**Default is `never`** — FFmpeg will happily write a WAV that overflows its own size fields unless you ask otherwise.

- `RF64_ALWAYS` writes the `RF64` FourCC up front with `avio_wl32(pb, -1); /* RF64 chunk size: use size in ds64 */` and an empty `ds64` chunk to be filled in later.
- `RF64_AUTO` writes a `JUNK` placeholder before `fmt `, and at finalise, `if (wav->rf64 == RF64_ALWAYS || (wav->rf64 == RF64_AUTO && file_size - 8 > UINT32_MAX))`, seeks back to overwrite `RIFF`→`RF64` and `JUNK`→`ds64` (a 28-byte chunk carrying the 64-bit RIFF size, data size and sample count), rewriting `fmt ` after it. It also upgrades when `number_of_samples > UINT32_MAX`.

The threshold is `UINT32_MAX`, confirming the 4 GiB ceiling in §6.3.

**The crash-survivability interaction:** both modes require a **seek-back at finalise** to write correct sizes. A killed process therefore leaves a WAV whose header sizes are wrong (and, in `AUTO` mode, a stray `JUNK` chunk). Most tools will still play such a file by reading to EOF, but not all, and the declared duration will be wrong — which for ClipShift means a *recovered* recording could silently violate the alignment guarantee. Whatever recovery path the crash-survivability ticket lands on must reconstruct the `data` size from the actual file length rather than trusting the header. This is another argument for writing `RF64_ALWAYS`-style headers with a fixed layout from byte zero: the only unknown is then the length, which is recoverable from the filesystem.

---

## 7. Discontinuities: what breaks and how alignment survives

The invariant from §5.3 — *every file contains exactly `round(rate × (t − T0))` items* — is what makes each of these survivable. In every case the answer is the same shape: **work out how much master-clock time was lost, convert it to items at the nominal rate, and emit that many silent/duplicate items.**

| Event | Detection | Response | Alignment impact |
|---|---|---|---|
| Dropped/late video frame (render overran) | pacing clock overshoot: `count > 1` | emit `count` copies of the last surface; increment a lagged-frame counter | none — grid preserved |
| Static screen (no new capture frame) | `DXGI_ERROR_WAIT_TIMEOUT` / `TryGetNextFrame` returns null | emit a duplicate of the last surface | none |
| DDA `AccumulatedFrames > 1` | frame info field | you missed presents; only the last is recoverable | content loss, no timing loss |
| Encoder/GPU can't keep up (e.g. OBS streaming concurrently) | output queue full | fold the tick into a repeat of the last frame; **never drop the tick**; count it | none — see §2.3 |
| DDA `DXGI_ERROR_ACCESS_LOST` (mode change, DWM transition, fullscreen switch) | HRESULT | tear down and recreate duplication; keep emitting the last good surface meanwhile | none, if the pacing clock never pauses |
| Audio buffer glitch | `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY`, or `QPCPosition` jumping ahead of `DevicePosition` | insert silence for the measured gap; **re-anchor from `QPCPosition`, not from frame count** | none |
| `AUDCLNT_BUFFERFLAGS_SILENT` | flag | write `frames` sample-frames of zeros — **never skip the packet** | none |
| `AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR` | flag | keep the *previous* anchor; do not re-anchor on an untrusted timestamp | none |
| Device format change / default-device change | `OnSessionDisconnected(DisconnectReasonFormatChanged)`, `OnDefaultDeviceChanged` | re-init the client; `DevicePosition` **resets to 0**, so maintain your own cumulative timeline; silence-fill the reconnection gap | none, if the gap is measured on the master clock |
| Audio device vanishes | `AUDCLNT_E_DEVICE_INVALIDATED` | keep writing silence at the nominal rate until it returns or the session ends | none — the file stays the right length |
| System suspend/resume | QPC delta ≫ `QueryUnbiasedInterruptTimePrecise` delta | short gap: pad all streams identically. Long gap (> configurable, suggest 5 s): finalise and stop. | none either way, because all streams pad from the same clock |

Note the structural property: **because every stream is a pure function of one clock, even a *wrong* response keeps the three files aligned with each other** — it only makes the recording's absolute duration wrong. Mutual alignment, the thing the brief actually requires, is robust to almost every failure mode listed above.

---

## 8. Verification — the pass/fail test

Three tests, in increasing cost. All three should exist.

### 8.1 Ledger assertion (unit-level, runs on every build)

Every sink maintains and logs:

```
items_written, items_expected = round(rate × (t_last − T0) / 1e9),
frames_from_device, silence_padded, dropped, resampler_ratio_now, measured_device_ppm
```

**Pass:** `items_written == items_expected` exactly, at every tick. This is an `assert`, not a tolerance. It verifies the *construction*. It does not verify that samples landed where they claim, which is what §8.3 is for.

`measured_device_ppm` comes free from WASAPI, per §4.2:

```
ppm = ( (Δframes / nominal_rate) / Δqpc_seconds − 1 ) × 1e6
```

Over a 600-second window with ~1 ms of QPC-anchor jitter this resolves to roughly 1.6 ppm — ample. **Log it.** It turns "clocks drift" into a number the user's own hardware produces, and it is the single best early-warning signal that something is wrong.

### 8.2 Length test (integration, ~5 minutes and once at 4 hours)

After a recording, from the files alone:

```
video_duration = frame_count / fps
audio_duration = sample_count / sample_rate      (per audio file)
```

**Pass:** all durations equal to within 1 ms.

This is nearly tautological given §8.1, which is exactly why it is a good regression test — it catches muxer/encoder bugs (a dropped frame at the encoder, an edit list written by the muxer, a truncated WAV `data` chunk) that the in-process ledger cannot see.

### 8.3 Flash-and-burst drift test (the real acceptance test, 4 hours)

**Stimulus.** A full-screen test page that, every 60 seconds, simultaneously:
- renders one frame of full-screen white (all other frames black), and
- emits a 20 ms 1 kHz sine burst with a hard onset.

The burst reaches the loopback file through the audio engine. For the microphone file, route the same signal into the input device (a virtual cable is cleaner than acoustic coupling; acoustic coupling adds ~3 ms/metre of unwanted-but-constant delay).

Emit a marker every **20 seconds**, not every 60 — see the resolution calculation below. Record for 4 hours. That is **720 marker events**.

**Measurement.** For each marker *k*:
- `t_v(k) = f_k / fps`, where `f_k` = index of the maximum-luminance frame
- `t_a(k) = s_k / R`, where `s_k` = burst onset sample from a matched filter
- `Δ(k) = t_a(k) − t_v(k)`

**Analysis.** Least-squares fit `Δ(k) = a + b·t_v(k)`.
- `a` is the **fixed offset** — the unsolvable acquisition-latency bias of §9. Report it; it becomes the default per-sink sync offset in config.
- `b` is the **residual drift**, in seconds per second. Multiply by 10⁶ for ppm.

**Criteria.**

| Metric | Target (§1.4 budget) | Hard fail |
|---|---|---|
| Residual drift `\|b\|` | ≤ 0.14 ppm (≤ 2 ms over 4 h) | > 1.16 ppm (> 1 frame at 60 fps over 4 h) |
| Max excursion `max\|Δ(k) − a − b·t_v(k)\|` | ≤ 2 ms | > 16.7 ms (one frame) |
| Fixed offset `\|a\|` | reported, not gated — it is §9 item 1 | — |

**Why the criteria are measurable.** Video quantisation gives each `Δ(k)` a uniform ±½-frame error, σ = 16.67/√12 ≈ 4.81 ms. With N markers spread over 4 h (σ_t = 14400/√12 ≈ 4157 s), the standard error on the fitted slope is `σ / (σ_t·√N)`:

| Marker interval | N | Slope SE | Equivalent error over 4 h |
|---|---:|---:|---:|
| 60 s | 240 | 0.075 ppm | 1.08 ms |
| **20 s** | **720** | **0.043 ppm** | **0.62 ms** |
| 10 s | 1440 | 0.030 ppm | 0.44 ms |

At a 20-second interval the test resolves drift about three times finer than the 0.14 ppm target, so a pass is a real pass rather than a measurement floor. Sixty-second markers are marginal against a 2 ms budget; that is why the interval is specified.

If tighter resolution is ever needed, replace the single white frame with a frame-indexed pattern (e.g. a binary-coded bar) so `f_k` can be recovered without ±½-frame ambiguity — but the regression above already has adequate margin and the simpler stimulus is easier to trust.

**Why "max excursion" matters separately from slope.** A step correction — an inserted 70 ms silence, as OBS would produce — shows up as a small slope but a large excursion. The excursion criterion is what distinguishes "continuously locked" from "periodically re-synced".

### 8.4 Torture variants

Run the 4-hour test at least once with each of: a mid-session default-audio-device change; unplugging and replugging the capture device; a resolution change on the recorded display; a fullscreen game alt-tab cycle; and a short sleep/resume. The pass criteria are unchanged — that is the point.

---

## 9. What is genuinely unsolved

Being explicit, because the ticket asks for it.

1. **The fixed acquisition-latency offset is not removable in software.** WASAPI's `QPCPosition` is documented as "the value of the performance counter at the time that the audio endpoint device recorded the device position" — but Microsoft does not define where in the analogue-to-driver path that instant sits, and no equivalent statement exists for the display path. So there is a constant, device-dependent bias between the two, of order milliseconds to tens of milliseconds. It is **not drift** and it does not grow. OBS's answer is a manual per-source **Sync Offset**, exposed in Advanced Audio Properties ([`frontend/components/OBSAdvAudioCtrl.cpp`](https://github.com/obsproject/obs-studio/blob/master/frontend/components/OBSAdvAudioCtrl.cpp) → `obs_source_set_sync_offset()`), applied in `source_output_audio_data()` as `in.timestamp += sync_offset`. A project as mature as OBS shipping a manual millisecond nudge for this is the strongest available evidence that it is not solvable automatically. ClipShift should ship the same: a per-sink offset in the config file, defaulting to 0, with §8.3's measured `a` as the recommended value for a given rig. Anyone claiming to have solved this in software without measuring it is guessing.

2. **Endpoint-loopback continuity during silence is undocumented.** See §4.3. The mitigation (silent render stream + gap-fill) is empirical, copied from OBS, and could in principle change with a Windows update. Process loopback has a written guarantee; endpoint loopback does not.

3. **`Direct3D11CaptureFrame.SystemRelativeTime`'s scaling is not documented.** `TimeSpan` implies 100 ns; Microsoft never says. Verify empirically before relying on it. (ClipShift may not need it at all — see §2.2; OBS ignores presentation timestamps entirely.)

4. **`GraphicsCaptureSession.MinUpdateInterval`** exists on Windows 11 build 26100+ and its [documentation page has no prose at all](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.minupdateinterval) — only language signatures. Do not rely on it to guarantee idle frames.

5. **A methodological note on the ITU figures in §1.4.** A first automated read of the BT.1359-1 PDF returned "detectability ±90 ms, acceptability ±45 ms" — symmetric, in the wrong order, and wrong. The real text is "+45 ms to –125 ms" and "+90 ms to –185 ms", asymmetric, which is materially different guidance. The figures now quoted come from a direct decompression of the PDF's content streams, not from a summary. **Any figure in this document that is not quoted verbatim from a source should be treated as suspect until it is.**

**None of this changes the shape of the project.** The separate-files alignment problem *does* have a clean answer, and it is §5.3. The residual uncertainties are a constant offset and a documentation gap, both manageable, neither structural.

---

## 10. The recommendation, stated to be implementable

### 10.1 Master clock

- `QueryPerformanceCounter`, normalised once to **nanoseconds** at the boundary (`ticks × 1e9 / QueryPerformanceFrequency`, 128-bit intermediate; cache the frequency at startup as Microsoft instructs).
- WASAPI hands 100 ns units — multiply by 100. DXGI hands raw ticks — normalise. Do not mix.
- Do **not** attempt to discipline QPC against wall time. Its own error is irrelevant to mutual alignment (§1.5).
- Detect suspend by comparing the QPC delta to the `QueryUnbiasedInterruptTimePrecise` delta.

### 10.2 The epoch, T0

1. Open all audio capture clients **first**, and begin buffering with QPC anchors. Discard nothing yet.
2. Acquire the first display surface, *then* start the video pacing clock. **`T0` = the first video grid tick's QPC value.** Do not start the grid before a surface exists, or the first frames are undefined; do not derive `T0` from the surface's own presentation timestamp (§2.2).
3. Every file's item 0 corresponds to `T0`.
4. `T0` is recorded once and never adjusted. Every subsequent length computation refers to it, so a bug in `T0` produces a uniform offset (visible immediately, in the first seconds of a test recording) rather than drift (visible only after hours). This is a deliberate property: it makes the failure mode cheap to detect.

Rationale: video is the coarsest-grained stream (16.67 ms per item vs 20.8 µs), so anchoring on video costs at most half a sample of audio precision, whereas anchoring on audio would cost up to half a video frame. This is also OBS's choice (§2.6).

### 10.3 The invariant (the whole design in one line)

> At every master-clock instant `t`, sink *i* has written exactly `round(rate_i × (t − T0))` items.

Assert it. It is not a goal, it is a postcondition.

### 10.4 Video path

- Own pacing clock: `frame_n` due at `T0 + n × (1e9 / fps)`, using an `os_sleepto`-style exact grid that advances by whole intervals on overrun (OBS's `video_sleep`, §2.2).
- At each tick, submit the most recently acquired surface. If capture has produced nothing new (static screen, `WAIT_TIMEOUT`, null frame), **submit the previous surface again**. Never skip a tick.
- If the tick overran by *k* intervals, submit *k* copies and increment a lagged-frame counter.
- Encoder PTS is an integer counter incremented by exactly one frame interval per submitted frame. Never derive PTS from a wall-clock timestamp.
- Mux with first PTS = 0 and **no edit list / initial-delay offset**.
- Ignore `LastPresentTime` / `SystemRelativeTime` for timing. (They remain useful as diagnostics — e.g. logging real capture cadence — but must not drive the output.)

### 10.5 Audio path, per sink

- Initialise shared-mode, event-driven, with `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY` so the client format is pinned regardless of the engine mix format. For loopback sinks, also open a render client and push a buffer of zeros (OBS's `ClearBuffer`) to keep the endpoint from idling.
- On each `GetBuffer`:
  - `AUDCLNT_BUFFERFLAGS_SILENT` → substitute zeros, **keep `frames`**.
  - `AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR` → do not re-anchor from this packet's `QPCPosition`.
  - `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY` → mark a gap; re-anchor from `QPCPosition`.
  - Otherwise push samples into a queue tagged with the packet's `QPCPosition`.
- Head alignment: discard (or silence-pad) exactly `round(R × (T0 − first_packet_qpc) / 1e9)` sample-frames, per OBS's `calc_offset_size`.
- Every 10 ms of master clock, compute `need = round(R × (t − T0)/1e9) − written_total` and pull exactly `need` sample-frames from a drift-locked resampler.
  - The resampler's ratio is adjusted slowly (a PI loop with a time constant of seconds) to hold input-queue occupancy at a ~50 ms setpoint.
  - If the input queue cannot satisfy `need` (a genuine gap), emit silence for the shortfall and log it.
  - If the accumulated positional error exceeds ~20 ms — beyond what a slow ratio change should ever be asked to absorb — hard-correct with silence insertion or truncation and log it as an incident, rather than letting the resampler ring.
- **Compensate the resampler's own group delay.** A polyphase resampler holds a filter's worth of samples, so its output lags its input by a fixed amount. That delay must be subtracted when mapping output sample index to master-clock time, or every audio file acquires a constant offset equal to the filter delay. OBS does exactly this — [`libobs/media-io/audio-resampler-ffmpeg.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/media-io/audio-resampler-ffmpeg.c), `audio_resampler_resample()`, sets `*ts_offset = (uint64_t)swr_get_delay(context, 1000000000);` (the resampler delay expressed in nanoseconds), and `obs-source.c` applies `in.timestamp -= source->resample_offset;`. This is an easy detail to miss and it shows up as a fixed lip-sync error, not as drift.
- Continuously compute and log `measured_device_ppm` from (`DevicePosition`, `QPCPosition`) pairs (§8.1).
- Handle `OnSessionDisconnected` / `OnDefaultDeviceChanged` by re-initialising the client and silence-filling the gap on the master clock. Remember `DevicePosition` resets to 0.

### 10.6 Output files

- Audio: **RF64** (or RIFF with a `JUNK` placeholder sized for a later `ds64`), because float32 stereo overflows RIFF at 3 h 6 m (§6.3). Write a BWF `bext` chunk with a `TimeReference` derived from `T0` so Resolve's *Auto Sync Audio → Based on Timecode* can recover alignment if the files are separated.
- Video: MP4/MOV, CFR, first PTS 0, **no edit list**, `tmcd` timecode track matching the audio `TimeReference` origin. An empty edit at the head of the video file is exactly the "manual nudging" the brief forbids (§3.6).
  - *Cross-ticket note:* fragmented MP4 happens to satisfy both this and the crash-survivability constraint at once — FFmpeg's `use_editlist` auto-resolves to **off** for fragmented output without `+delay_moov`, and a fragmented file is playable after a kill because there is no trailing `moov` to write. The coupled flip of `avoid_negative_ts` to `make_zero` is a no-op for us because our first PTS is already 0. This belongs to the container/crash-survivability ticket, but the sync requirement points the same way.
- Do not rely on the timecode for the primary use case. The primary guarantee is that all three files start at the same instant and have exactly proportional lengths, so dropping all three at a common playhead is correct with no further action (§6.1).
- Filenames share a session prefix, per the standing constraint in the project map.

### 10.7 What to copy from OBS and what not to

**Copy:** the QPC master clock; the synthesised constant-rate video grid with duplication on overrun; the integer-counter encoder PTS; the sample-accurate head trim to the video start timestamp; the `SILENT` zero-fill that preserves frame count; the silent-render loopback keepalive; the per-source manual sync offset; the lagged-frame/skipped-frame ledger.

**Do not copy:** the 70 ms / 2 s insert-drop drift correction (§2.4). It is right for a live stream and wrong for an editor's source material. Replace it with the drift-locked resampler of §5.3, which achieves the same invariant continuously and inaudibly.
