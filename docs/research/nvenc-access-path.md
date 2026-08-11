# NVENC access path from .NET

Research for [issue #3](https://github.com/richardthornton/clipshift/issues/3) — how ClipShift should drive NVENC on an RTX 5060 Ti (Blackwell) from .NET, encoding 1080p60 from a D3D11 texture, **while OBS is streaming with NVENC**.

Date: 2026-08-11. Sources are NVIDIA's own docs and support matrix, Microsoft Learn, FFmpeg's own documentation and source, and OBS Studio's source. Some claims are backed by measurements taken on the reference machine itself; those are marked **[measured]** and the method is given so they can be re-run.

Reference machine used for measurements: NVIDIA GeForce RTX 5060 Ti, driver **610.62** (`nvidia-smi --query-gpu=driver_version`), Windows 11 Pro 26200, alongside an AMD iGPU.

---

## Recommendation

**Drive NVENC directly through the NVIDIA Video Codec SDK API (`nvEncodeAPI64.dll`), from .NET, over unmanaged function pointers, feeding it NV12 D3D11 textures registered with `NvEncRegisterResource`. Encode H.264 High 4:2:0 8-bit, rate control CONSTQP.**

Reasoning, in order of weight:

1. **Coexistence with OBS is a non-issue, and the constraint that matters is engine throughput, not session count.** A single realtime 1080p60 H.264 p5 encode consumes ~20% of the 5060 Ti's one NVENC engine; two concurrent consume ~38% **[measured]**. Six concurrent sessions ran clean **[measured]**. The old "GeForce is limited to 2/3 NVENC sessions" folklore is contradicted by NVIDIA's current matrix (see [Concurrent sessions](#concurrent-sessions)).
2. **It is the only path that gives full control over the three things this project is actually hard about**: rate control (CQP for a 4-hour local recording), in-flight frame count, and a deterministic flush at stop that does not truncate the tail.
3. **It is what OBS itself does.** OBS ships a dedicated `obs-nvenc` plugin calling the NVENC API directly, and *dropped* its Media Foundation encoder plugin in 2022 ([`plugins: Drop win-mf`, 4ab9cd1](https://github.com/obsproject/obs-studio/commit/4ab9cd100594d16d179b31a909f837e44d178079)). There is a high-quality reference implementation to check our behaviour against.
4. **Licensing is trivially clean for MIT.** The only artifact needed is NVIDIA's header, which is MIT-licensed, and the runtime DLL, which ships with the user's driver and is never redistributed by us. Nothing LGPL, nothing GPL, no compliance checklist.
5. **The API is a flat C function table, not COM.** From .NET that is `delegate* unmanaged[Cdecl]<...>` calls into a struct of function pointers — no marshalling layer, no per-frame allocation, and none of this project's known CsWinRT/COM interop hazards.

### Tradeoffs of this choice, stated plainly

- **Most code of the three options.** We implement session setup, surface pooling, the encode/lock/unlock loop, DTS reconstruction if B-frames are on, and EOS drain ourselves. Roughly the surface area of OBS's `nvenc.c` (~1500 lines of C), less the features we do not need.
- **It gives us an elementary stream, not a file.** NVENC hands back an Annex-B/AVCC bitstream; muxing to a container is a separate problem. This ticket does not settle it.
- **What would change the answer:** if the container/muxer decision lands on **libavformat**, then FFmpeg is already a dependency, and `h264_nvenc` via libavcodec becomes the cheaper path — one dependency instead of two, with the D3D11 zero-copy input still available (see below). That is the runner-up and it is close. The licensing cost of that choice is real but bounded, and is spelled out in [Licensing](#licensing).
- **Media Foundation is rejected**, but not because it cannot work — the NVIDIA encoder MFTs are genuinely present (see below). It is rejected for weaker control and vendor-inconsistent rate-control surfaces.

---

## Concurrent sessions

This is the question that decides whether ClipShift can exist at all, and it is the one most polluted by outdated hearsay. Two NVIDIA primary sources disagree with each other, so both are given.

**NVIDIA's support matrix** has a `Max # of concurrent sessions` column. The row for our exact card, read from the live page on 2026-08-11:

```
GeForce RTX 5060 Ti | Blackwell | 9th Gen | Desktop | 1 chip | 1 NVENC | 12 | ...
```

All GeForce RTX 40- and 50-series rows show **12**. Professional cards (e.g. `NVIDIA RTX PRO 6000 Blackwell Workstation Edition`) show **Unrestricted**.
Source: <https://developer.nvidia.com/video-encode-and-decode-gpu-support-matrix-new>

**NVIDIA's NVENC Application Note** (Video Codec SDK 13.0), §3 "NVENC Licensing Policy", says something different, verbatim:

> As far as NVENC hardware encoding is concerned, NVIDIA GPUs are classified into two categories: "qualified" and "non-qualified". On qualified GPUs, the number of concurrent encode sessions is limited by available system resources (encoder capacity, system memory, video memory etc.). On non-qualified GPUs, the number of concurrent encode sessions is limited to **8 per system**. This limit of 8 concurrent sessions per system applies to the combined number of encoding sessions executed on all non-qualified cards present in the system.

Source: <https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-application-note/index.html>

**The discrepancy is unresolved.** Neither document carries a revision date, and no NVIDIA driver release note was found that ties a specific number to a specific driver branch. The matrix is the more frequently regenerated artifact and says 12; the App Note says 8. **Uncertainty stated: I could not establish from primary sources which of 8 or 12 is current, nor the exact driver version at which the limit last changed.**

**[measured] What the reference machine actually does.** Six concurrent 1080p60 `h264_nvenc` sessions were started with FFmpeg 8.0.1 and all ran to completion with empty stderr; `nvidia-smi` reported `encoder.stats.sessionCount = 6` and `utilization.encoder = 100%` while they ran. Method:

```
ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=60 -t 15 -c:v h264_nvenc -preset p5 -f null -   (x6 concurrently)
nvidia-smi --query-gpu=encoder.stats.sessionCount,encoder.stats.averageFps,utilization.encoder --format=csv
```

**Conclusion for ClipShift:** OBS streaming (1 session) + OBS recording if enabled (1) + ClipShift (1) is at most 3 sessions against a documented floor of 8. The session limit is not a design constraint. It should still be handled as a *failure mode* — see [Fallback](#fallback) — because the error it produces is misleading.

---

## Throughput: the constraint that actually matters

The 5060 Ti has **1 NVENC engine** (matrix: `# OF CHIPS = 1`, `TOTAL # OF NVENC = 1`). Every session shares it. OBS's `split_encode` option, which splits a frame across multiple NVENC engines, is therefore inapplicable on this card.

NVIDIA's App Note publishes engine throughput at 1080p, YUV 4:2:0, 8-bit. Blackwell column, H.264:

| Preset | RC / Tuning | Blackwell fps |
| --- | --- | --- |
| P1 | CBR / LL | 977 |
| P3 | VBR / HQ | 708 |
| P5 | CBR / LL | 323 |
| P5 | VBR / HQ | 317 |
| P7 | VBR / HQ | 227 |

Measurement conditions, quoted: *"Resolution/Input Format/Bit depth: 1920 × 1080/YUV 4:2:0/8-bit … All measurements are done at the highest video clocks as reported by nvidia-smi … The performance should scale according to the video clocks as reported by nvidia-smi for other GPUs of every individual family."* The named reference GPUs are GTX 1060 (Pascal), RTX 8000 (Turing), RTX 3090 (Ampere), RTX 4090 (Ada) — the Blackwell reference part is not named, so treat the Blackwell column as indicative.
Source: <https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-application-note/index.html>

**[measured] On the actual 5060 Ti**, feeding realtime 1080p60 (`-re`), H.264, preset p5, `-rc constqp -qp 20`:

| Load | `encoder.stats.sessionCount` | `utilization.encoder` | `utilization.gpu` |
| --- | --- | --- | --- |
| 1 realtime 1080p60 session | 1 | **20%** | 2% |
| 2 concurrent realtime 1080p60 | 2 | **38%** | 5% |
| 6 unthrottled 1080p60 | 6 | 100% (≈54 fps each, ≈324 fps aggregate) | — |

Two independent corroborations fall out of this: the ≈324 fps aggregate at saturation matches the App Note's 323 fps P5 figure almost exactly, and the ~19% duty cycle implied by 60/323 matches the observed 20%. **A ClipShift recording alongside an OBS stream should land around 40% of one NVENC engine, with the GPU's shader load essentially untouched (2–5%).** That is the number to defend against regressions.

Caveat: these were measured on an otherwise idle GPU. A running game changes SM and memory-bandwidth contention, and the RGBA→NV12 conversion and texture copies do consume shader/copy-engine time even though the encode itself does not. The 40% figure is an NVENC-engine budget, not a total-GPU-cost claim.

---

## Zero-copy input from a D3D11 texture

**All three candidate paths can take a D3D11 texture.** This is not a differentiator; the details are.

### NVENC direct

The Programming Guide describes the external-resource route: *"in scenarios where the client cannot or does not want to allocate input buffers through the NVIDIA Video Encoder Interface, it can use any externally allocated DirectX resource as an input buffer."* The sequence is:

1. Open the session with `NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS::device` set to the `IUnknown*` of the `ID3D11Device` (cast to `void*`), device type DirectX.
2. `NvEncRegisterResource` — once per surface, at init.
3. `NvEncMapInputResource` — per frame, to get the handle passed to `NvEncEncodePicture`.
4. `NvEncUnmapInputResource` / `NvEncUnregisterResource` at teardown.

Source: <https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html>

OBS's implementation is worth copying exactly, because it shows the parts the guide leaves implicit ([`plugins/obs-nvenc/nvenc-d3d11.c`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-nvenc/nvenc-d3d11.c)):

- It creates **its own** pool of encoder-side textures — `DXGI_FORMAT_NV12` (or `P010` for 10-bit), `MipLevels = 1`, `ArraySize = 1`, `BindFlags = D3D11_BIND_RENDER_TARGET`, and `SetEvictionPriority(DXGI_RESOURCE_PRIORITY_MAXIMUM)` — one per in-flight buffer, and registers each once with `bufferFormat = NV_ENC_BUFFER_FORMAT_NV12`.
- The captured frame arrives as a **shared handle**, is opened as an `ID3D11Texture2D`, and is synchronised with `IDXGIKeyedMutex` before being copied into a registered texture.

So "zero-copy" in practice means *no CPU round-trip*, not *no GPU copy*: there is still a GPU-side conversion into NV12 and a copy into an encoder-owned surface. Budget for that. It is cheap (see the 2–5% GPU utilisation above) but it is not free, and it means the capture path must hand us either an NV12 texture or something we convert on the GPU.

### FFmpeg / libavcodec

`h264_nvenc` accepts `AV_PIX_FMT_D3D11` and maps it to `NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX` — the same registration mechanism, wrapped ([`libavcodec/nvenc.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/nvenc.c), `nvenc_register_frame`). The registered-frame cache is bounded by `MAX_REGISTERED_FRAMES`. So the library path preserves zero-copy; only the **child-process** path does not.

### Media Foundation

An MFT advertises `MF_SA_D3D11_AWARE`; the client then sends `MFT_MESSAGE_SET_D3D_MANAGER` with an `IMFDXGIDeviceManager` before streaming starts. Verbatim: *"If the attribute is nonzero, the client can give the MFT a pointer to the IMFDXGIDeviceManager interface before streaming starts… The client is not required to send this message."*
Source: <https://learn.microsoft.com/en-us/windows/win32/medfound/mf-sa-d3d11-aware>

### The child-process path is disqualified

An `ffmpeg.exe` child process cannot be handed a D3D11 texture. Frames would have to cross to system memory and back through a pipe — a per-frame CPU copy of ~3 MB at 60 Hz plus IPC, against a brief whose only metric is in-game FPS impact. Rule it out on performance, before licensing even comes up.

---

## Media Foundation: viable, but rejected

Worth being accurate here, because the usual claim ("NVIDIA doesn't ship an MF encoder") is wrong.

**[measured] The NVIDIA encoder MFTs are registered on this machine.** Enumerating `MFT_CATEGORY_VIDEO_ENCODER` via `MFTEnumEx` with `MFT_ENUM_FLAG_HARDWARE`:

```
NVIDIA HEVC Encoder MFT
NVIDIA H.264 Encoder MFT
NVIDIA AV1 Encoder MFT
AMDh265Encoder / AMDh264Encoder   (the iGPU)
```

and with all flags, additionally the Windows-supplied software `H264 Encoder MFT`, `Microsoft AVC DX12 Encoder`, `HEVCVideoExtensionEncoder`, and others.

So the path exists. It is rejected because:

- **The protocol is heavier than the alternative, not lighter.** A hardware MFT must be asynchronous: it sets `MF_TRANSFORM_ASYNC`, must be unlocked via `MF_TRANSFORM_ASYNC_UNLOCK`, and is driven by out-of-band `METransformNeedInput` / `METransformHaveOutput` events through `IMFMediaEventGenerator`. Draining is `MFT_MESSAGE_COMMAND_DRAIN` → repeated `METransformHaveOutput` → `METransformDrainComplete`.
  Sources: <https://learn.microsoft.com/en-us/windows/win32/medfound/hardware-mfts>, <https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts>
- **It is entirely COM**, which for this project is the interop shape with known hazards, versus NVENC's flat C function table which has none.
- **Rate control is reached through `ICodecAPI` properties whose support is vendor-defined.** Whether the NVIDIA MFT exposes true constant-QP for a long local recording is **not established here — I did not probe its `ICodecAPI` for `CODECAPI_AVEncCommonRateControlMode` support.** That is a real gap in this analysis, but it is a gap that only matters if MF is otherwise attractive, and it is not.
- **OBS abandoned it.** `win-mf` was deprecated, disabled by default, and removed entirely in 2022; the project maintains a direct NVENC plugin instead.

MF's one enduring merit: it is the vendor-neutral route, and the same code would reach the AMD and Intel encoders. That matters for a future non-NVIDIA fallback, not for the MVP.

---

## Codec choice for NLE ingest

Blackwell (9th-gen NVENC) encode support, per the matrix row for the 5060 Ti: **H.264** 4:2:0, 4:2:2, 4:4:4 and lossless; **HEVC** 4K in all chroma formats, 8K, 10-bit, B-frames, lossless; **AV1**.

The binding constraint is the NLE, not the encoder. DaVinci Resolve 20's official codec list (July 2025), **Windows** section:

- **H.264** (mov/mkv/mp4), decode: *"8-bit OS-supported profiles. More profiles and GPU acceleration in Studio"*
- **H.265** (mov/mkv/mp4), decode: same wording
- **AV1** (mov/mp4/mkv), decode: *"Yes, GPU accelerated"*

Source: <https://documents.blackmagicdesign.com/SupportNotes/DaVinci_Resolve_20_Supported_Codec_List.pdf>

That is the argument for **H.264 High, 4:2:0, 8-bit** as the fixed MVP default: it is the one profile the *free* Resolve decodes on Windows without qualification, and it is universally supported by Premiere. HEVC and AV1 belong in the config file, not the UI, and should carry the caveat that HEVC on free Resolve is limited to OS-supported profiles.

I could not verify Adobe's import matrix: `helpx.adobe.com` timed out repeatedly during this research. **No claim is made here about Premiere's AV1 import support.**

Note also that long-GOP H.264 scrubs worse in an NLE than an intra-heavy stream. Keyframe interval is a real lever for the "files must scrub well" requirement and belongs to the encoder-settings decision, not this one. OBS's own nvenc default is `keyint_sec = 0` (encoder default, ~2s).

---

## Rate control

NVENC offers `NV_ENC_PARAMS_RC_CONSTQP`, `_VBR`, `_CBR`, and a target-quality mode (VBR plus `targetQuality`), with optional multipass (`NV_ENC_TWO_PASS_QUARTER_RESOLUTION` / full-resolution).
Source: <https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html>

**What OBS uses for local recording specifically** — the question as asked. Two different defaults exist in OBS and they are easy to confuse:

- The **encoder plugin's** defaults are streaming defaults: `rate_control = "cbr"`, `preset = "p5"`, `tune = "hq"`, `multipass = "qres"`, `cqp = 20`, `bf = 2`, adaptive quantisation on ([`plugins/obs-nvenc/nvenc-properties.c`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-nvenc/nvenc-properties.c)).
- The **recording path** overrides that. `SimpleOutput::UpdateRecordingSettings` computes `crf = CalcCRF(ultra_hq ? 16 : 23)` and calls `UpdateRecordingSettings_nvenc(crf)`, which sets `rate_control = "CQP"` and `cqp = crf`. `CalcCRF` reduces the value further for higher resolutions ([`frontend/utility/SimpleOutput.cpp`](https://github.com/obsproject/obs-studio/blob/master/frontend/utility/SimpleOutput.cpp)).

So: **OBS records locally at CQP**, around 23 for "High Quality" and 16 for "Indistinguishable" at 1080p, never CBR. CBR is for the stream, where the bitrate ceiling is the ingest's, not ours.

**Recommendation for ClipShift:** `NV_ENC_PARAMS_RC_CONSTQP`, qp ≈ 20 (OBS's own nvenc default cqp, and between its two recording tiers), preset **p5** or **p6**, tuning **HQ**. CQP is the right family for a local recording because it produces a predictable *quality* rather than a predictable *size*, degrades gracefully when the scene gets busy, and — crucially for a 4-hour session — has no VBV buffer model to drift or stall.

Disable **look-ahead** and keep B-frames low or zero. Both trade latency for compression, and both complicate the stop path (below). B-frames also force PTS/DTS reordering, which OBS handles with an explicit `dts_list` deque; skipping B-frames removes that whole class of bug from the MVP at a modest bitrate cost.

---

## Latency, buffering, and a clean stop

This is where a recorder gets silently wrong, so the numbers matter.

- The Programming Guide: *"The client should allocate at least (1 + NB) input and output buffers, where NB is the number of B frames between successive P frames."*
- With look-ahead enabled, *"frames are queued up in the encoder and hence `NvEncEncodePicture` will return `NV_ENC_ERR_NEED_MORE_INPUT` until the encoder has sufficient number of input frames to satisfy the look-ahead requirement."*
- End of stream is signalled by calling `NvEncEncodePicture` with `NV_ENC_PIC_FLAG_EOS`.

Source: <https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html>

OBS's concrete numbers ([`plugins/obs-nvenc/nvenc.c`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-nvenc/nvenc.c)):

```c
int buf_count = max(4, config->frameIntervalP * 2 * 2);
if (lookahead) buf_count = max(buf_count, config->frameIntervalP + rc_lookahead + EXTRA_BUFFERS);
buf_count = min(64, buf_count);
const int output_delay = buf_count - 1;
```

and on teardown it sends EOS and then drains before releasing anything:

```c
params.encodePicFlags = NV_ENC_PIC_FLAG_EOS;
nv.nvEncEncodePicture(enc->session, &params);
get_encoded_packet(enc, true);   /* finalize = true */
```

**Implication for ClipShift's stop sequence**, which must not truncate the tail: stop feeding → send `NV_ENC_PIC_FLAG_EOS` → keep locking/collecting bitstreams until the encoder reports it is drained → *then* finalise the container. With B-frames off and no look-ahead, in-flight depth is ~4 frames (OBS's floor), i.e. **~65 ms of tail at 60 fps**; with 2 B-frames it is ~8 frames, ~130 ms. Small — but a stop that skips the drain loses exactly that much video, silently, on every recording.

For comparison, the equivalent hazards on the other paths: FFmpeg's `delay` option defaults to `INT_MAX` (*"Delay frame output by the given amount of frames"*, [`libavcodec/nvenc_h264.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/nvenc_h264.c)) — maximum buffering unless explicitly lowered — and MF requires the full `MFT_MESSAGE_COMMAND_DRAIN` → `METransformDrainComplete` handshake.

---

## Licensing

MIT project. The relevant facts, from the licence texts themselves.

**FFmpeg's own split** ([`LICENSE.md`](https://github.com/FFmpeg/FFmpeg/blob/master/LICENSE.md)):

> Most files in FFmpeg are under the GNU Lesser General Public License version 2.1 or later (LGPL v2.1+)… Some optional parts of FFmpeg are licensed under the GNU General Public License version 2 or later (GPL v2+)… **None of these parts are used by default, you have to explicitly pass `--enable-gpl` to configure to activate them. In this case, FFmpeg's license changes to GPL v2+.**

**NVENC is not among the GPL parts.** The GPL list is a handful of x86 asm files, build/test tools, and ~30 libavfilter filters. And in `configure`: `h264_nvenc_encoder_deps="nvenc"`, `nvenc_deps="ffnvcodec"`, `nvenc_deps_any="libdl LoadLibrary"` — i.e. NVENC needs only the **ffnvcodec headers** and loads the driver library dynamically at runtime. No `--enable-gpl`, no `--enable-nonfree`, no link against an NVIDIA SDK.

**`libx264` and `libx265` are GPL v2.** Enabling either forces the whole FFmpeg build — and anything statically combined with it — to GPL. **A software x264 fallback would relicense ClipShift.** Do not ship one.

**nv-codec-headers** (`FFmpeg/nv-codec-headers`) carries NVIDIA's MIT-style grant: *"Permission is hereby granted, free of charge, to any person… to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies"*, with the notice-retention condition. The current master *"Corresponds to Video Codec SDK version 13.1.15. Minimum required driver versions: Windows: 610.0 or newer"* — the reference machine's 610.62 satisfies this. **The same headers are what a direct-NVENC implementation needs, so the direct path's entire licensing footprint is: retain an MIT notice.**

**If FFmpeg is linked as a library (LGPL build)**, FFmpeg's own compliance checklist applies: dynamic linking; provide the FFmpeg source matching the shipped binaries, hosted on the same server; state *"This software uses code of FFmpeg licensed under the LGPLv2.1"*; mention LGPLv2.1 in the about box and EULA. That is a real but tractable obligation — it does not threaten ClipShift's MIT licence, it just adds distribution work. Source: <https://www.ffmpeg.org/legal.html>

**Does invoking `ffmpeg.exe` as a child process change the analysis?** Per the FSF, who own the licence: pipes, sockets and command-line arguments are *"communication mechanisms normally used between two separate programs"*, and where *"the two programs remain well separated… you can treat them as two separate programs"* (`gpl-faq.html#GPLInProprietarySystem`); an aggregate of separate programs on the same medium leaves each under its own licence (`#MereAggregation`). Source: <https://www.gnu.org/licenses/gpl-faq.html>

So an MIT app that shells out to a GPL `ffmpeg.exe` does not itself become GPL — **but if we ship that binary in our installer, the binary must still be distributed in GPL compliance** (source availability and so on), and we inherit that obligation as the distributor. Combined with the fatal per-frame CPU round-trip, the child-process path is not worth it. Note that this is a summary of the FSF's stated position, not legal advice.

---

## .NET specifics

**Loading.** `nvEncodeAPI64.dll` lives in `C:\Windows\System32` (verified present on the reference machine, 1.07 MB, dated with the driver). Load it by name via `NativeLibrary.TryLoad` so that absence is a catchable condition rather than a `DllNotFoundException` at first use.

**Interop shape.** The API is not COM. `NvEncodeAPICreateInstance` fills an `NV_ENCODE_API_FUNCTION_LIST` struct with raw function pointers; every subsequent call goes through that table. In modern .NET this is `delegate* unmanaged[Cdecl]<...>` invoked directly off the struct — no marshalling stubs, no delegate allocation, no `Marshal.GetDelegateForFunctionPointer` per call. This is the single biggest reason the direct path is *easier* from .NET than Media Foundation, which is COM end-to-end and subject to this project's known CsWinRT interop constraints.

**Per-frame allocation.** The hot path must not allocate. Practical shape:
- Declare NVENC structs as blittable `[StructLayout(LayoutKind.Sequential)]` value types, each with its `version` field set from the SDK's versioning macros.
- Keep one reusable `NV_ENC_PIC_PARAMS` (and lock/map param structs) as fields, or in a pinned native block, and mutate them per frame — `stackalloc`/`ref` locals, never a fresh boxed object.
- Register the surface pool once at init (OBS: `buf_count` textures); per frame only map → encode → lock → copy out → unlock.

**Handing output to the muxer.** `NvEncLockBitstream` returns a pointer to encoder memory plus a length, valid until unlock. That means the muxer should either consume a `ReadOnlySpan<byte>` synchronously inside the lock, or copy into a pooled buffer (`ArrayPool<byte>` / a ring of pre-sized native buffers) and unlock immediately. Holding locks across a slow disk write will stall the encoder — with 4-hour sessions and a crash-survivability requirement, the write path must be decoupled from the encode path by a bounded queue.

References: <https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices>, <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code#function-pointers>

---

## Fallback

**Detection, in order:**

1. `nvEncodeAPI64.dll` fails to load → no NVIDIA driver / no NVIDIA GPU.
2. `NvEncodeAPICreateInstance` returns `NV_ENC_ERR_INVALID_VERSION` → driver older than the headers we built against (nv-codec-headers 13.1.15 requires **driver ≥ 610.0** on Windows).
3. Session open or `NvEncInitializeEncoder` fails → capability or resource problem. Note the trap: **the concurrent-session limit surfaces as `NV_ENC_ERR_OUT_OF_MEMORY`, not as a dedicated error code.** If ClipShift ever reports "out of memory" to a user who is running several encoders, that message will send them hunting for the wrong problem. Special-case it.
4. The recorded display is not on the NVIDIA adapter → out of MVP scope per the project map, but it must be detected and explained, not silently paid for with a cross-adapter copy every frame.

**Behaviour on failure:** refuse to start the recording and say precisely which check failed. Do **not** silently fall back to a CPU encoder — on this brief, a software encode during a stream is a worse outcome than not recording, because it degrades the thing the user is actually doing.

**If a non-NVIDIA fallback is ever wanted** (explicitly not MVP), the machine probe above shows the shape it would take: Media Foundation's hardware encoder MFTs cover AMD and Intel with one code path, and Windows ships a software `H264 Encoder MFT` as a last resort. That route stays MIT-safe, unlike bundling x264. This is another reason not to *dismiss* MF outright — just not to build on it now.

---

## Open questions and things not established

Stated plainly rather than papered over:

1. **The 8-vs-12 concurrent session discrepancy is unresolved.** NVIDIA's App Note says 8 per system for non-qualified GPUs; NVIDIA's support matrix says 12 for every RTX 40/50 row. No driver release note tying a number to a driver version was found. Empirically ≥6 works on driver 610.62. The decision does not depend on which is right — ClipShift needs 1 — but the exact ceiling is not known.
2. **The NVIDIA H.264 Encoder MFT's `ICodecAPI` surface was not probed.** Whether it exposes true constant-QP is unverified. Only matters if MF is revisited.
3. **Premiere Pro's import matrix was not verified** — Adobe's help pages timed out on every attempt. No claim is made about Premiere and AV1.
4. **Throughput was measured on an idle GPU.** The 20%/38% NVENC figures do not include contention from a running game, and the shader cost of RGBA→NV12 conversion under real load is unmeasured. The end-to-end "in-game FPS impact" number this project actually cares about can only come from a prototype on the real machine under a real game.
5. **The Blackwell reference GPU behind NVIDIA's published fps table is not named**, so those numbers are indicative for a 5060 Ti rather than exact.
6. **Muxing is out of scope here** and is the main thing that could tip the recommendation toward libavcodec/libavformat. See the tradeoffs section.

---

## Sources

- NVIDIA Video Encode and Decode GPU Support Matrix — <https://developer.nvidia.com/video-encode-and-decode-gpu-support-matrix-new>
- NVENC Application Note, Video Codec SDK 13.0 — <https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-application-note/index.html>
- NVENC Video Encoder API Programming Guide, SDK 13.0 — <https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html>
- FFmpeg `LICENSE.md` — <https://github.com/FFmpeg/FFmpeg/blob/master/LICENSE.md>
- FFmpeg legal / LGPL compliance checklist — <https://www.ffmpeg.org/legal.html>
- FFmpeg `configure` (nvenc dependencies) — <https://github.com/FFmpeg/FFmpeg/blob/master/configure>
- FFmpeg `libavcodec/nvenc.c`, `libavcodec/nvenc_h264.c` — <https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/nvenc.c>
- FFmpeg `nv-codec-headers` — <https://github.com/FFmpeg/nv-codec-headers>
- GNU GPL FAQ (aggregation, arms-length communication) — <https://www.gnu.org/licenses/gpl-faq.html>
- Microsoft Learn, Hardware MFTs — <https://learn.microsoft.com/en-us/windows/win32/medfound/hardware-mfts>
- Microsoft Learn, Asynchronous MFTs — <https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts>
- Microsoft Learn, `MF_SA_D3D11_AWARE` — <https://learn.microsoft.com/en-us/windows/win32/medfound/mf-sa-d3d11-aware>
- OBS Studio `plugins/obs-nvenc/` — <https://github.com/obsproject/obs-studio/tree/master/plugins/obs-nvenc>
- OBS Studio `frontend/utility/SimpleOutput.cpp` — <https://github.com/obsproject/obs-studio/blob/master/frontend/utility/SimpleOutput.cpp>
- OBS Studio, removal of the Media Foundation encoder plugin — <https://github.com/obsproject/obs-studio/commit/4ab9cd100594d16d179b31a909f837e44d178079>
- DaVinci Resolve 20 Supported Formats and Codecs (July 2025) — <https://documents.blackmagicdesign.com/SupportNotes/DaVinci_Resolve_20_Supported_Codec_List.pdf>
