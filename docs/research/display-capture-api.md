# Display capture API: Windows.Graphics.Capture vs DXGI Desktop Duplication

Research for [issue #2](https://github.com/richardthornton/clipshift/issues/2).

**Sources are primary only:** Microsoft Learn reference and conceptual pages, Windows SDK headers as
published in `microsoft/win32metadata`, Microsoft's own sample code, and the actual OBS Studio
source on GitHub. No blog posts except `blogs.windows.com`, which is the authoritative announcement
channel for these APIs. Every factual claim carries the URL of the source that owns it.

**Read [§11 What is not settled](#11-what-is-not-settled) before acting on this.** Several questions
the ticket asked cannot be answered from primary sources, including the one that matters most
(in-game FPS impact). Those are listed explicitly rather than papered over.

---

## 1. Recommendation

**Use DXGI Desktop Duplication (DDA) as the primary capture path. Treat Windows.Graphics.Capture
(WGC) as a fallback for the one case DDA structurally cannot serve — a display driven by an adapter
other than the one the app has a device on.**

This is the same split OBS Studio ships, chosen by the same test (§9) — and FFmpeg's only built-in
Windows display-capture source, `ddagrab`, is Desktop Duplication too, with no WGC equivalent at all.
Two independent, heavily-deployed pipelines converge on DDA for GPU-resident display capture.

Ranked reasons:

1. **WGC draws a system notification border around the captured display, and an unpackaged app has
   no documented way to remove it.** Microsoft: "a **yellow notification border is drawn by the
   system around the actively captured item**. In the case of multiple simultaneous capture sessions,
   a yellow border is drawn around each item being captured."
   ([Screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture))
   Removing it requires `IsBorderRequired = false`, which requires user consent via
   `GraphicsCaptureAccess.RequestAccessAsync(Borderless)`, which requires "the
   **graphicsCaptureWithoutBorder** capability in your app's **package manifest**"
   ([IsBorderRequired](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired)).
   And even then it is defeatable: "if the **IsBorderRequired** property is set to **true** for the
   same window or display by **other apps on the device**, the border will be displayed." ClipShift
   is specified to run beside OBS. A yellow border around the gaming display for four hours is a
   product defect, and it is one ClipShift cannot unilaterally prevent. DDA has no such UI at all.
2. **The hot path must avoid per-frame managed allocation** (standing constraint, issue #1). DDA is
   plain COM and CsWin32 has a documented, first-class zero-GC mode for exactly this
   (`allowMarshaling: false` + `preserveSigMethods`). WGC's supported .NET route hands back a
   projected `IDisposable` object per frame that Microsoft says you must dispose per frame. See §8.
3. **DDA's delivery model is documented; WGC's is not.** DDA's behaviour on a static desktop is
   specified down to the return code. For WGC, **no Microsoft document states whether frame delivery
   is change-driven or paced, or whether `FrameArrived` stops firing on a static desktop.** Building
   a fixed-cadence 4-hour recorder on an undocumented delivery model is the kind of bet that causes
   the rewrite this ticket exists to prevent. See §2 and §11.1.
4. **`DuplicateOutput1` has a documented performance advantage while a game is fullscreen** — it
   "allows directly receiving the original back buffer format used by a running fullscreen
   application", where plain `DuplicateOutput` "always converts the fullscreen surface to a 32-bit
   BGRA format… [which] incurs a performance penalty"
   ([DuplicateOutput1](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_5/nf-dxgi1_5-idxgioutput5-duplicateoutput1)).
   This is the only first-party performance statement found anywhere in this research that speaks
   directly to in-game cost, and it favours DDA.

**The honest cost of choosing DDA:** the cursor. ClipShift records the cursor unconditionally, and
on hardware-cursor systems DDA delivers the pointer *separately* and requires the app to decode
three shape formats and composite them. WGC does it with one boolean. This is real work — perhaps
the largest single chunk DDA adds — and OBS's implementation is GPLv2 so it cannot be copied into an
MIT project. But it is bounded, fully documented work with a licence-compatible Microsoft reference
implementation. It is a cost, not a risk. See §5.

### The fallback, precisely

`IDXGIOutput1::DuplicateOutput`'s device parameter "**must be created from the adapter to which the
output is connected**", and a device from the wrong adapter yields `E_INVALIDARG` — "was not created
on the correct adapter"
([Learn](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutput1-duplicateoutput)).

Issue #1 already scopes recording an iGPU-driven display out of the MVP and asks that the mismatch be
detected and the user warned plainly. That detection is mechanical and should be built regardless:
enumerate each `IDXGIAdapter`'s outputs, `IDXGIOutput::GetDesc`, and match `DXGI_OUTPUT_DESC.Monitor`
against the selected `HMONITOR` — exactly OBS's `device_duplicator_get_monitor_index`
([d3d11-duplicator.cpp](https://github.com/obsproject/obs-studio/blob/master/libobs-d3d11/d3d11-duplicator.cpp)).
No match on the encoding adapter ⇒ warn and refuse (MVP), or create a second device on the owning
adapter and pay a cross-adapter copy (post-MVP).

---

## 2. Frame delivery model

### DDA: change-driven delivery, app-owned pacing

> "**AcquireNextFrame** acquires a new desktop frame when the operating system either updates the
> desktop bitmap image or changes the shape or position of a hardware pointer. The new frame that
> **AcquireNextFrame** acquires might have only the desktop image updated, only the pointer shape or
> position updated, or both."
> — [AcquireNextFrame](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe)

Delivery is change-driven. There is no heartbeat frame. **On a genuinely static desktop with a
stationary cursor, `AcquireNextFrame` returns `DXGI_ERROR_WAIT_TIMEOUT` indefinitely and the app
receives nothing.** ClipShift must therefore synthesise repeat frames itself — this is true of both
APIs, and is not a reason to prefer one.

What DDA gives you that WGC does not is *explicit control of the wait*:

> "If the caller specifies a zero time-out interval in the *TimeoutInMilliseconds* parameter,
> **AcquireNextFrame** verifies whether there is a new desktop image available, returns immediately,
> and indicates its outcome with the return value. If the caller specifies an **INFINITE** time-out
> interval… the time-out interval never elapses."
>
> "**Note** You cannot cancel the wait that you specified in the *TimeoutInMilliseconds* parameter.
> Therefore, if you must periodically check for other conditions (for example, a terminate signal),
> you should specify a non-**INFINITE** time-out interval."

So `DXGI_ERROR_WAIT_TIMEOUT` *is* the "nothing changed, emit a repeat frame" signal, delivered on a
clock the app chooses. Microsoft's own sample treats timeout as success, not failure:
`if (hr == DXGI_ERROR_WAIT_TIMEOUT) { *Timeout = true; return DUPL_RETURN_SUCCESS; }`
([DuplicationManager.cpp](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/DXGIDesktopDuplication/cpp/DuplicationManager.cpp)).
It uses a 500 ms timeout; OBS uses 0 and polls on its own render tick (§9).

### The frame-ownership guidance — read it carefully

> "For performance reasons, we recommend that you **release the frame just before you call the
> IDXGIOutputDuplication::AcquireNextFrame method** to acquire the next frame. **When the client does
> not own the frame, the operating system copies all desktop updates to the surface.** This can
> result in wasted GPU cycles if the operating system updates the same region for each frame that
> occurs. […] **When the client acquires a frame, the client owns the surface; therefore, the
> operating system can track only the updated regions and cannot copy desktop updates to the
> surface.** Because of this behavior, we recommend that you **minimize the time between the call to
> release the current frame and the call to acquire the next frame.**"
> — [ReleaseFrame](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-releaseframe)

This passage is easy to misread as "hold frames for as short a time as possible". It says the
opposite. The expensive state is the **un-owned** state, in which the OS copies every desktop update
into the surface. Minimising "the time between release and acquire" means minimising time spent
un-owned — i.e. **hold the acquired frame, and release it only immediately before re-acquiring.**

Microsoft's own sample behaves consistently with that reading: when its shared destination surface is
busy it sets a `WaitToProcessCurrentFrame` latch and **keeps holding the acquired DDA frame** rather
than releasing and re-acquiring
([DesktopDuplication.cpp](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/DXGIDesktopDuplication/cpp/DesktopDuplication.cpp)).
OBS does the opposite — copies and releases immediately (§9). Since in-game FPS impact is the metric
that matters, this is worth measuring both ways in the prototype (§11.1); the documentation points at
hold-the-frame, but no first-party measurement exists.

### WGC: push-based, and the model itself is undocumented

`FrameArrived` is "An event raised when a captured frame is stored in the frame pool"
([Learn](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.framearrived)) —
that is the entire description; the page has no Remarks. The conceptual doc adds only that "this
event fires every time a new frame is available"
([Screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)).
Pull is also supported: "you can manually pull frames with the
**Direct3D11CaptureFramePool.TryGetNextFrame** method"; it returns null when the pool is empty.

**Whether WGC delivery is change-driven or compositor-paced, and whether `FrameArrived` stops firing
on a static desktop, is not stated in any Microsoft document.** The only hint is that
`SystemRelativeTime` is "the QPC time at which **the compositor rendered the frame**", tying frames
to compositor work rather than an app timer. That is inference. See §11.1.

Frame check-out semantics are documented and matter for a 4-hour run:

> "Each frame from the **Direct3D11CaptureFramePool** is checked out when calling
> **TryGetNextFrame**, and checked back in according to the lifetime of the
> **Direct3D11CaptureFrame** object. For managed applications, it's recommended to use the
> **Direct3D11CaptureFrame.Dispose** method… disposing the frame returns the buffer to the pool."

**What happens if the app under-drains the pool — drop, stall, or overwrite — is not documented.**

`GraphicsCaptureSession.MinUpdateInterval`, the only pacing knob, is worse than undocumented:

- Its Learn page has **no description, no remarks, and no Requirements table** — signature only.
  ([MinUpdateInterval](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.minupdateinterval))
- Its moniker range begins at `winrt-26100`, i.e. **Windows 11 24H2**. Sibling members added in the
  same drop carry `UniversalApiContract` v19.0 → 10.0.26100.0
  ([IncludeSecondaryWindows](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.includesecondarywindows)).

### Frame pool construction

`Create` (1803 / 10.0.17134.0) binds the pool to the calling thread's `DispatcherQueue`.
`CreateFreeThreaded` (1809 / 10.0.17763.0, contract v7.0) "**Creates a frame pool where the
dependency on the DispatcherQueue is removed** and the **FrameArrived** event is raised on **the
frame pool's internal worker thread**"
([Learn](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded)).
Microsoft's own C# recording walkthrough uses `CreateFreeThreaded` for exactly this reason; OBS uses
`Create` and stands up a `DispatcherQueueController` to serve it
([winrt-dispatch.cpp](https://github.com/obsproject/obs-studio/blob/master/libobs-winrt/winrt-dispatch.cpp)).
If WGC were chosen, `CreateFreeThreaded` is the correct call for a headless capture thread.

Buffer counts in first-party samples: Microsoft's C++ sample uses 2; Microsoft's C# video-recording
walkthrough uses 1; OBS uses 2.

---

## 3. Zero-copy to the encoder

**Both APIs hand back a GPU texture with no CPU round-trip. Neither has an advantage.** The
difference is in the lifetime rules.

### DDA

`AcquireNextFrame` returns an `IDXGIResource` that `QueryInterface`s to `ID3D11Texture2D` on the same
device passed to `DuplicateOutput`. Format is fixed with `DuplicateOutput`:

> "The format of the desktop image is always **DXGI_FORMAT_B8G8R8A8_UNORM** no matter what the
> current display mode is."
> — [Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api)

`IDXGIOutput5::DuplicateOutput1` lifts that, and its Remarks are directly relevant to in-game cost:

> "This method allows directly receiving the original back buffer format used by a running fullscreen
> application. For comparison, using the original **DuplicateOutput** function always converts the
> fullscreen surface to a 32-bit BGRA format. **In cases where the current fullscreen application is
> using a different buffer format, a conversion to 32-bit BGRA incurs a performance penalty.** […]
> The list of supported formats should always contain DXGI_FORMAT_B8G8R8A8_UNORM, as this is the most
> common format for the desktop."
> — [DuplicateOutput1](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_5/nf-dxgi1_5-idxgioutput5-duplicateoutput1)

`DuplicateOutput` itself says: "For improved performance, consider using **DuplicateOutput1**."
Microsoft's sample passes `{R8G8B8A8_UNORM, B8G8R8A8_UNORM, R16G16B16A16_FLOAT}`, commenting "On
supported OS versions, explicitly declare support for receiving FP16 surfaces for HDR mode"; OBS
passes `{R16G16B16A16_FLOAT, B8G8R8A8_UNORM}`.

**A full-frame copy is not required.** Microsoft's sample creates a shader resource view **directly
on the acquired duplication texture** and renders from it
([DisplayManager.cpp](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/DXGIDesktopDuplication/cpp/DisplayManager.cpp)),
and the interface doc says the operation "can be more complex. For example, the application can run
some pixel shaders on the updated regions of the image to encode those regions"
([IDXGIOutputDuplication](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nn-dxgi1_2-idxgioutputduplication)).
**So the ideal ClipShift path is: SRV on the acquired texture → BGRA→NV12 colour-convert shader
writing straight into the encoder input texture. No full-frame `CopyResource` at all.** OBS's
unconditional `CopyResource` is not required by the API.

The hard rule is lifetime:

> "The application must release the frame before it acquires the next frame. **After the frame is
> released, the surface that contains the desktop bitmap becomes invalid; you will not be able to use
> the surface in a DirectX graphics operation.**"
> — [ReleaseFrame](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-releaseframe)

Calling `AcquireNextFrame` without releasing returns `DXGI_ERROR_INVALID_CALL`.

**The acquired texture's `D3D11_USAGE`, `BindFlags`, `CPUAccessFlags` and `MiscFlags` are not
documented.** Microsoft's sample calls `GetDesc()` on it at runtime rather than assuming, and only
ever reads from it. Do the same. CPU access is separately gated: `MapDesktopSurface` works only when
`DXGI_OUTDUPL_DESC.DesktopImageInSystemMemory == TRUE`, otherwise `DXGI_ERROR_UNSUPPORTED` and "the
application must first transfer the image to a staging surface"
([MapDesktopSurface](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-mapdesktopsurface)).

### WGC

`Direct3D11CaptureFrame.Surface` is an `IDirect3DSurface`; the `ID3D11Texture2D` comes out through
`IDirect3DDxgiInterfaceAccess::GetInterface`, declared in
`windows.graphics.directx.direct3d11.interop.h`
([Learn](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/ns-windows-graphics-directx-direct3d11-interop-idirect3ddxgiinterfaceaccess)).
The device side is `CreateDirect3D11DeviceFromDXGIDevice`.

Lifetime, verbatim:

> "**Applications should not save references to Direct3D11CaptureFrame objects, nor should they save
> references to the underlying Direct3D surface after the frame has been checked back in.**"
> — [Screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)

Two WGC-specific traps that DDA does not have:

- **The surface is always the pool's size, not the content's.** "The underlying Direct3D surface is
  always the size specified when creating (or recreating) the Direct3D11CaptureFramePool. If content
  is larger than the frame, the contents are clipped… If the content is smaller than the frame, then
  **the rest of the frame contains undefined data.** It's recommended that applications **copy out a
  sub-rect using the ContentSize property**." OBS reaches for `GetDesc` instead, commenting
  `/* need GetDesc because ContentSize is not reliable */`.
- **The pool textures' bind/misc flags are undocumented.** Microsoft's C# sample reads the source
  description and then *overrides* `Usage`, `BindFlags`, `CpuAccessFlags` and `OptionFlags` before
  creating its copy target, implying the pool's description is not directly reusable. That is
  inference, not documentation.

OBS copies out of WGC frames and notes it would rather not:

```cpp
/* if they gave an SRV, we could avoid this copy */
context->CopyResource((ID3D11Texture2D *)gs_texture_get_obj(texture), frame_surface.get());
```
— [libobs-winrt/winrt-capture.cpp](https://github.com/obsproject/obs-studio/blob/master/libobs-winrt/winrt-capture.cpp)

**Net: DDA's acquired surface is documented as directly usable as a shader input by Microsoft's own
sample; WGC's is not documented at all and Microsoft's own sample copies out of it.** DDA is
marginally better here, and the gap is one full-frame copy per frame.

### The encoder hand-off

**Neither capture API constrains this** — both produce an `ID3D11Texture2D` that feeds the encoder
identically. Recorded here because the ticket asked, and because two findings bear on the capture
design.

NVENC accepts a D3D11 texture directly, no CUDA interop required. The documented protocol is
`NvEncRegisterResource` → `NvEncMapInputResource` → encode → `NvEncUnmapInputResource` →
`NvEncUnregisterResource`, with the session opened as `NV_ENC_DEVICE_TYPE_DIRECTX` passing an
`ID3D11Device` as `NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS::device`
([NVENC Programming Guide](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html)).
FFmpeg proves the shape: `libavcodec/nvenc.c` lists `AV_PIX_FMT_D3D11` in `ff_nvenc_pix_fmts` and
sets `reg.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX; reg.resourceToRegister = frame->data[0]`
— the raw `ID3D11Texture2D*`
([FFmpeg](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/nvenc.c)).

Two caveats, both honest gaps:

- **NVIDIA does not document whether the texture's creating device must be the same device passed to
  `NvEncOpenEncodeSessionEx`.** The words "adapter" and "same device" appear nowhere in the guide.
  FFmpeg structurally assumes one device for both; OBS deliberately uses two devices on the same
  adapter and bridges them with a shared handle plus `IDXGIKeyedMutex`. Either works in practice; the
  contract is unwritten.
- **Feeding BGRA to NVENC is not free.** The guide's "Encoder features using CUDA" section lists
  "Encoding of RGB contents" among features that "internally use CUDA for hardware acceleration". So
  a BGRA→NV12 shader in ClipShift's own pipeline is likely cheaper than letting NVENC convert, since
  it uses the 3D pipe rather than CUDA cores the game is also contending for. Worth measuring
  alongside §11.1.

OBS's arrangement, for reference (GPLv2 — do not copy):

- NVENC runs on **its own** `ID3D11Device`, created on `EnumAdapters(factory, 0, …)`.
- libobs passes a **shared texture handle**; the encoder device calls `OpenSharedResource` and
  `QueryInterface(IDXGIKeyedMutex)`.
- Per frame: `AcquireSync` → `CopyResource` into an NVENC-owned `DXGI_FORMAT_NV12`/`P010` texture →
  `ReleaseSync` → `nvEncMapInputResource`.

— [plugins/obs-nvenc/nvenc-d3d11.c](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-nvenc/nvenc-d3d11.c)

**One structural conclusion for the spec: ClipShift cannot shell out to `ffmpeg.exe` and keep frames
on the GPU.** `libavcodec` supports D3D11→NVENC in-process, but the `ffmpeg` CLI has no input that
accepts a shared DXGI handle from another process — CLI inputs are files, pipes and capture devices,
so handing frames to a child process is a CPU round-trip by construction. Encoding must be in-process
(NVENC directly, Media Foundation, or libavcodec as a library).

---

## 4. Timestamps

**Strongest finding in this document: both capture APIs and WASAPI stamp on QueryPerformanceCounter,
so video and audio share one timebase by construction.**

| Source | Field | Documented clock | Units |
| --- | --- | --- | --- |
| WGC | `Direct3D11CaptureFrame.SystemRelativeTime` | "The **QPC (Query Performance Counter)** time at which the compositor rendered the frame." ([Learn](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime)) | `TimeSpan` = 100 ns ticks |
| DDA | `DXGI_OUTDUPL_FRAME_INFO.LastPresentTime` | "The time stamp of the last update of the desktop image. **The operating system calls the QueryPerformanceCounter function to obtain the value.**" ([Learn](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info)) | raw QPC ticks |
| DDA | `LastMouseUpdateTime` | same wording, for pointer updates | raw QPC ticks |
| WASAPI | `IAudioCaptureClient::GetBuffer` → `pu64QPCPosition` | "the value of the performance counter at the time that the audio endpoint device recorded the device position of the first audio frame in the data packet"; `*pu64QPCPosition = 10,000,000 · t / f` ([Learn](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)) | 100 ns |
| WASAPI | `IAudioClock::GetPosition` → `pu64QPCPosition` | same conversion, paired with a device-clock position ([Learn](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition)) | 100 ns |

Consequences:

- **WGC's `SystemRelativeTime` and WASAPI's `pu64QPCPosition` are directly comparable integers** —
  both are QPC in 100 ns units, produced by the same conversion.
- **DDA's `LastPresentTime` is a raw QPC count** and needs one scale to match:
  `t_100ns = 10,000,000 · LastPresentTime / f`, where `f` comes from `QueryPerformanceFrequency` and
  "is determined during system initialization and doesn't change while the system is running"
  ([Learn](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps)).
  One multiply-divide per frame against a cached constant.
- **`LastPresentTime` is zero on a pointer-only update.** "If only the pointer was updated (that is,
  the desktop image was not updated), the **AccumulatedFrames**, **TotalMetadataBufferSize**, and
  **LastPresentTime** members are set to zero." The timeline must never consume a zero as a
  timestamp — and `AccumulatedFrames == 0` is the clean test for "cursor moved, pixels didn't".
- **`AccumulatedFrames > 1` is a free per-frame drop indicator:** "more desktop image updates have
  occurred while the application processed the last desktop update." WGC offers no equivalent.

### What "LastPresentTime" actually means is *not* fully pinned down

The doc says only "the time stamp of the last update of the desktop image". It does **not** say
whether that is compositor-present time, scanout, or vblank. Contrast `DXGI_FRAME_STATISTICS`, where
Microsoft names `SyncQPCTime` explicitly as a QPC value tied to a vblank. **The QPC domain is
settled; the semantic anchor is not.** WGC is marginally better specified here — "the time at which
the compositor rendered the frame" — but neither is anchored to scanout. For ClipShift this is
tolerable: a constant, unknown offset between the video timestamp origin and the audio timestamp
origin produces a fixed A/V offset, not drift, and a fixed offset is correctable once and stays
correct. **A varying offset would not be, and nothing in the documentation rules that out.** See
§11.2.

### QPC's own guarantees

All from
[Acquiring high-resolution time stamps](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps):

- Monotonic: "**Is the performance counter monotonic (non-decreasing)?** Yes. QPC does not go
  backward."
- Cross-process, cross-core: "the performance counter results are consistent across all processors in
  multi-core and multi-processor systems, even when measured on different threads or processes."
  Caveat: "values that differ by ± 1 tick have an ambiguous ordering."
- Survives sleep: it "returns the total number of ticks that have occurred since the Windows
  operating system was started, **including the time when the machine was in a sleep state** such as
  standby, hibernate, or connected standby."
- Frequency fixed at boot; unaffected by DST, system clock changes, Turbo Boost, or frequency
  scaling.

### Where the four-hour drift actually comes from

**Not from QPC vs QPC.** Video and audio timestamps ride the same counter and cannot drift relative
to each other. The drift is between the **audio device's own crystal** and QPC. Microsoft's own
figures: PC crystals are typically "±30 to 50 parts per million", and "a frequency error of 100 ppm
causes an error of 8.64 seconds after 24 hours" — so roughly ±0.5–0.7 s over a 4-hour session if the
audio device clock is left uncorrected.

The correction material is documented: `IAudioClock::GetPosition` returns a device position *and*
the QPC instant at which the device reached it, and `GetFrequency` gives the units — "the
stream-relative offset in seconds can always be calculated as *p/f*". Comparing `p/f` against elapsed
QPC over the session measures the device clock's true rate. That belongs to the sync ticket.
**The point for this ticket: the capture API choice does not constrain the sync design either way,
because both stamp on QPC.**

---

## 5. Cursor

### DDA: conditional, and in practice your problem

> "**Either the mouse pointer is already drawn onto the desktop image that AcquireNextFrame provides
> or the mouse pointer is separate from the desktop image.** If the mouse pointer is drawn onto the
> desktop image, the pointer position data that is reported by **AcquireNextFrame** […] indicates
> that a separate pointer isn't visible. **If the graphics adapter overlays the mouse pointer on top
> of the desktop image, AcquireNextFrame reports that a separate pointer is visible. So, your client
> app must draw the mouse pointer shape onto the desktop image** to accurately represent what the
> current user will see on their monitor."
> — [Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api)

`PointerPosition.Visible == TRUE` means a hardware overlay cursor that is **not** in the pixels. On
any modern GPU — both on the reference machine — that is the normal case, so **ClipShift must
composite the cursor itself.**

The parts, all documented:

- `DXGI_OUTDUPL_POINTER_POSITION` = `{POINT Position; BOOL Visible;}`. "The **Position** member is
  valid only if the **Visible** member's value is set to TRUE."
  ([Learn](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_pointer_position))
  Position is the top-left of the shape, "not the desktop position of the hot spot".
- `GetFramePointerShape` only when `PointerShapeBufferSize != 0`: "keep a copy of the last pointer
  image and use it to draw on the desktop unless the shape of the mouse pointer changes." Returns
  `DXGI_ERROR_MORE_DATA` on undersized buffer, `DXGI_ERROR_INVALID_CALL` if called without owning the
  frame.
- The hot spot is informational: "**An application does not use the hot spot when it determines where
  to draw the cursor shape.**"
  ([DXGI_OUTDUPL_POINTER_SHAPE_INFO](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_pointer_shape_info))
- Three shape types must all be handled
  ([DXGI_OUTDUPL_POINTER_SHAPE_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ne-dxgi1_2-dxgi_outdupl_pointer_shape_type)):
  - `MONOCHROME` — "a 1 bits per pixel (bpp) device independent bitmap (DIB) format **AND mask** that
    is followed by another 1 bpp DIB format **XOR mask** of the same size" (two masks stacked; the
    reported `Height` covers both).
  - `COLOR` — "32 bpp ARGB DIB format".
  - `MASKED_COLOR` — "32 bpp ARGB format bitmap with the mask value in the alpha bits. **The only
    allowed mask values are 0 and 0xFF. When the mask value is 0, the RGB value should replace the
    screen pixel. When the mask value is 0xFF, an XOR operation is performed** on the RGB value and
    the screen pixel."

Cost: a shape cache plus a composite shader handling three blend modes including an XOR path.
Bounded. **Reference the Microsoft `DXGIDesktopDuplication` sample, not OBS — OBS is GPLv2 and
ClipShift is MIT.**

### WGC: composited for you

`GraphicsCaptureSession.IsCursorCaptureEnabled` — "Gets or sets a value specifying whether the
capture session will **include the cursor in the captured content**". **Windows 10 version 2004
(10.0.19041.0), contract v10.0**
([Learn](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.iscursorcaptureenabled)).
The cursor is composited into the frame; there is **no cursor position or shape member anywhere in
the `Windows.Graphics.Capture` namespace**, so there is no way to get it separately. The property's
**default value is not documented**.

Worth noting that OBS, which supports both, deliberately disables WGC's cursor and draws its own:

```cpp
/* disable cursor capture if possible since ours performs better */
```
— [winrt-capture.cpp](https://github.com/obsproject/obs-studio/blob/master/libobs-winrt/winrt-capture.cpp)

The source does not say *why*. That is a signal, not evidence — but it weakens rather than reinforces
WGC's cursor advantage.

---

## 6. Window exclusion, the capture border, and consent

### The exclusion primitive is shared, not per-API

`SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` — "The window is displayed only on a
monitor. Everywhere else, the window does not appear at all. **One use for this affinity is for
windows that show video recording controls, so that the controls are not included in the capture.**"
Introduced in **Windows 10 version 2004**; on earlier builds it silently degrades to `WDA_MONITOR`
([Learn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)).

This is a property of the *excluded* window, set by its own process — not a feature of either capture
API. The page says the feature protects content "from being captured or copied through **a specific
set of public operating system features and APIs**", and **never enumerates that set**. It also warns
it is not a security boundary and "works only when the Desktop Window Manager (DWM) is composing the
desktop".

**Therefore: whether DDA or WGC specifically honours `WDA_EXCLUDEFROMCAPTURE` is NOT settled by this
research.** The sibling ticket on capture-invisible overlays owns that question. Do not cite this
document as having answered it.

Neither API offers exclusion of *another* process's window — with one 24H2-only exception:

- **`IDisplayGraphicsCaptureSession.SetWindowExclusionList(IEnumerable<WindowId>)`** and
  `GetWindowExclusionList()` exist in WGC as of contract v19.0 / build 26100. This is the only
  first-party "exclude these windows from my monitor capture" API found. **Both pages carry no
  description and no remarks whatsoever** — semantics unknown.
  ([Learn](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.idisplaygraphicscapturesession))
  Worth knowing it exists; not something to build on today.

### WGC's capture border is the decisive problem

The border is documented, not folklore:

> "With screen capture, developers invoke secure system UI for end users to pick the display or
> application window to be captured, and **a yellow notification border is drawn by the system around
> the actively captured item. In the case of multiple simultaneous capture sessions, a yellow border
> is drawn around each item being captured.**"
> — [Screen capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)

Suppressing it requires all of:

1. `GraphicsCaptureSession.IsBorderRequired = false` — **10.0.20348.0, contract v12.0**
   ([Learn](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired)).
   (The Requirements table labels this "Windows 10, version 2104"; 20348 is the Windows Server 2022
   build. In client terms these APIs reach consumers on Windows 11 21H2 / build 22000. Microsoft
   publishes no correction to the label — flagging the discrepancy rather than smoothing it over.)
2. User consent first: "your app must get consent from the user by calling
   `GraphicsCaptureAccess.RequestAccessAsync`, passing in the value
   `GraphicsCaptureAccessKind.Borderless`, **which displays a prompt to the user**."
3. A **package manifest capability**: "To call **RequestAccessAsync** with
   **GraphicsCaptureAccessKind.Borderless**, you must declare the **graphicsCaptureWithoutBorder**
   capability in your app's **package manifest**." Defined in the `uap11:Capability` element
   ([App capability declarations](https://learn.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations)).

And two failure modes worse than the requirement:

- **Silent failure:** "If the user denies access, setting this property to **false** will succeed,
  **but the value will be ignored and the border will be displayed** during subsequent captures."
- **Another app can force it back on:** "if the **IsBorderRequired** property is set to **true** for
  the same window or display by other apps on the device, the border will be displayed."

Since capabilities live in an app package manifest and Microsoft states that capabilities are
"relevant only to apps that have package identity", **an unpackaged .NET desktop app has no
documented path to suppress the border.** (Microsoft never says "unpackaged apps cannot"; the
mechanism simply has no unpackaged route. OBS does call `RequestAccessAsync(Borderless)` from an
unpackaged process, so it is evidently *reachable* in practice — but shipping on undocumented
tolerance is precisely the bet this ticket exists to avoid.)

### Consent on the capture path itself is not a differentiator

Three ways to get a `GraphicsCaptureItem`, with materially different documented requirements:

| Route | Min version | Documented consent / capability |
| --- | --- | --- |
| `GraphicsCapturePicker.PickSingleItemAsync` | 1803 / 17134 | System picker UI; `graphicsCapture` capability for packaged apps |
| `IGraphicsCaptureItemInterop::CreateForMonitor` / `CreateForWindow` | **1903 / build 18362** (Win32, `windows.graphics.capture.interop.h`) | **The reference pages say nothing about consent, a picker, or a capability** |
| `GraphicsCaptureItem.TryCreateFromDisplayId` / `TryCreateFromWindowId` | 20348 / v12.0 | Explicitly requires `RequestAccessAsync(Programmatic)` **and** the `graphicsCaptureProgrammatic` manifest capability |

So the only route viable for an unpackaged app capturing a chosen monitor without a picker is
`IGraphicsCaptureItemInterop::CreateForMonitor` — which is what OBS uses. Its silence on consent is
suggestive but is not a positive statement that no UI appears. DDA has no consent UI of any kind.

**Capture consent is therefore not the differentiator. The border is.**

---

## 7. Cross-adapter behaviour

### DDA: hard requirement, settled

`pDevice` — "A pointer to the Direct3D device interface that you can use to process the desktop
image. **This device must be created from the adapter to which the output is connected.**" A device
from the wrong adapter returns `E_INVALIDARG`: "The specified device (*pDevice*) is invalid, **was
not created on the correct adapter**, or was not created from `IDXGIFactory1`."
([Learn](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutput1-duplicateoutput))

Multi-output capture is done by one duplication per output, each on its own adapter's device: "If an
application wants to duplicate the entire desktop, it must create a desktop duplication interface on
each active output on the desktop. **This interface does not provide an explicit way to synchronize
the timing of each output image. Instead, the application must use the time stamp of each output.**"
ClipShift records one display, so this does not arise — but it is why the adapter check must be done
per-display, not per-machine.

**One caveat worth carrying into the spec.** A Microsoft support article states, for Microsoft Hybrid
(Optimus-style) systems: "**the DDA does not support being run against the discrete GPU on a Microsoft
Hybrid system. By design, the call fails together with error code DXGI_ERROR_UNSUPPORTED**", with the
resolution "run the application on the integrated GPU instead"
([KB](https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/error-when-dda-capable-app-is-against-gpu)).
That article is scoped **Applies to: Windows 8.1**, and whether it still holds on Windows 10/11 is
not documented. The reference machine is a desktop with displays natively split across two GPUs, not
a muxless hybrid laptop, so this most likely does not apply — but the design rule it implies is safe
and matches the API contract regardless: **always create the capture device on the adapter that
`EnumOutputs` reports owns the target monitor, never blindly on adapter 0 or "the fastest GPU".**

### WGC: not settled

The frame pool is created against an `IDirect3DDevice` of the app's choosing, and **no Microsoft page
states what happens when the captured display is driven by a different adapter** — whether the system
performs the copy transparently, at what cost, or whether it fails. OBS's `choose_method` switches to
WGC precisely when the monitor is not on OBS's own adapter (§9), which is circumstantial evidence
that it works. Inference, not contract. See §11.5.

### Whatever the mechanism, moving a frame between adapters is expensive — and D3D11 cannot do it

This is worth stating plainly, because it is easy to assume a shared-texture flag solves it:

**`D3D11_RESOURCE_MISC_SHARED_CROSS_ADAPTER` does not exist.** The
[`D3D11_RESOURCE_MISC_FLAG`](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_resource_misc_flag)
enum has no cross-adapter member. D3D11's sharing flags — `_SHARED`, `_SHARED_KEYEDMUTEX`,
`_SHARED_NTHANDLE` — are all documented as sharing "between two or more Direct3D **devices**", never
adapters. (The recommended modern combination is `_SHARED_NTHANDLE | _SHARED_KEYEDMUTEX`, opened with
`ID3D11Device1::OpenSharedResource1`; `_SHARED` and `_SHARED_KEYEDMUTEX` are mutually exclusive.)

Cross-adapter sharing is a **D3D12-only** concept, and Microsoft is blunt about its cost
([Shared heaps](https://learn.microsoft.com/en-us/windows/win32/direct3d12/shared-heaps)):

> "**Cross-adapter shared resources are only supported in system memory.**"
>
> "**Cross-adapter heaps are located in `D3D12_MEMORY_POOL_L0`… That memory pool is not efficient for
> discrete/NUMA adapter architectures.** And, the most efficient texture layouts are not always
> available."

Additional constraints: only row-major `TEXTURE2D` resources, `CreateReservedResource` unsupported,
and "Certain restrictions may apply to such textures, such as only supporting copying." (Microsoft
never uses the word "PCIe", so the exact transport is not documented — but "system memory" plus "not
efficient for discrete adapters" is unambiguous about the shape of the cost.)

**Consequence:** for a display on the iGPU, the frame has to reach the RTX 5060 Ti through system
memory, per frame, at 1080p60 — regardless of which capture API produced it. This is exactly the cost
issue #1 anticipated when it scoped iGPU-driven displays out of the MVP and asked for a warning
instead. **That decision is correct and this research reinforces it: the correct MVP behaviour is to
detect the mismatch and refuse with a clear explanation, not to silently pay it.**

---

## 8. .NET reachability and the allocation constraint

### DDA — clean, with a documented zero-GC mode

`IDXGIOutputDuplication` and all DDA types are in the official `Windows.Win32.Graphics.Dxgi`
namespace generated by `microsoft/win32metadata` (the Dxgi partition traverses `dxgi1_2.h` through
`dxgi1_6.h`), consumed from C# via CsWin32, which "provides P/Invoke and COM Interop projection
support for C#" ([CsWin32](https://github.com/microsoft/CsWin32)).

Critically, CsWin32 documents a mode built for exactly this requirement:

> `"allowMarshaling"` — "**Emit COM interfaces instead of structs, and allow generation of
> non-blittable structs for the sake of an easier to use API.**" (default `true`)
> — [settings.schema.json](https://github.com/microsoft/CsWin32/blob/main/src/Microsoft.Windows.CsWin32/settings.schema.json)

> "Force generation of blittable structs, **COM structs instead of interfaces (for super high
> performance with 0 GC pressure)**"
> — [getting-started](https://github.com/microsoft/CsWin32/blob/main/docfx/docs/getting-started.md)

**Recommended ClipShift configuration:** `allowMarshaling: false` plus
`comInterop.preserveSigMethods: ["*"]`. The first gives blittable structs and raw vtable calls; the
second returns raw `HRESULT`s instead of exception-throwing wrappers — essential when
`DXGI_ERROR_WAIT_TIMEOUT` is an *expected*, once-per-idle-frame return rather than an error. The
result is that `AcquireNextFrame`/`ReleaseFrame` is a pure indirect call with zero GC pressure and no
exception on the common path.

This also aligns with existing project memory — **.NET 8 CsWinRT COM interop must use vtable calls or
`MarshalInterface`, never a cast of `__ComObject`.** The `allowMarshaling: false` projection *is* the
vtable-call route.

Vtable slot order for `IDXGIOutputDuplication`, from `dxgi1_2.h` — note `ReleaseFrame` is **last**,
not adjacent to `AcquireNextFrame`:

```
0 QueryInterface   1 AddRef              2 Release                  (IUnknown)
3 SetPrivateData   4 SetPrivateDataInterface
5 GetPrivateData   6 GetParent                                      (IDXGIObject)
7 GetDesc          8 AcquireNextFrame    9 GetFrameDirtyRects
10 GetFrameMoveRects  11 GetFramePointerShape
12 MapDesktopSurface  13 UnMapDesktopSurface  14 ReleaseFrame
```

### WGC — friction, and a documented per-frame managed object

The supported route is the CsWinRT projection via a Windows-version-specific TFM (e.g.
`net8.0-windows10.0.19041.0`), which pulls in `Microsoft.Windows.SDK.NET.Ref`
([Learn](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps)).
Two frictions:

- **`IGraphicsCaptureItemInterop` has no first-party C# wrapper.** Unlike `IInitializeWithWindow`,
  it is not in the SDK projection's interop wrapper set, so it must be declared by hand and obtained
  off the activation factory. And the CsWinRT rule bites: "When casting an object to an interface
  that has the `ComImport` attribute, you'll need to use the **`.As<>` operator** instead of using an
  explicit cast expression"
  ([CsWinRT](https://learn.microsoft.com/en-us/windows/apps/develop/platform/csharp-winrt/)) — which
  is exactly the failure already recorded in project memory. Note that Microsoft's own C# screen-capture
  walkthrough uses the older `(IDirect3DDxgiInterfaceAccess)surface` cast, i.e. **the pattern that
  breaks under CsWinRT.**
- **A managed object per frame is required by the documentation, not merely likely.**
  "For managed applications, it's recommended to use the **Direct3D11CaptureFrame.Dispose**
  method… disposing the frame returns the buffer to the pool." At 60 fps × 4 h ≈ **864,000 frames**,
  deterministic disposal is mandatory. **Whether the CsWinRT projection allocates a fresh RCW per
  `TryGetNextFrame` is not documented** (§11.6) — but the projection's object model is disposable-
  per-frame by design, which is in direct tension with the standing constraint.

It can be dodged by calling the WinRT ABI by hand — activation factories, `IInspectable`, manual
vtables — but at that point WGC's ergonomic advantage from .NET, its main selling point, is gone.

**The API that is easier to reach from .NET is the one that fights the allocation constraint; the API
that is harder to reach is the one that satisfies it, and has a documented mode for doing so.
Choosing DDA resolves the tension instead of trading it.**

---

## 9. What OBS actually does — and why it matters here

ClipShift runs beside OBS, so OBS's choices are both a compatibility constraint and the closest thing
to a battle-tested reference.

### OBS picks DDA by default on desktops

`choose_method`, in
[plugins/win-capture/duplicator-monitor-capture.c](https://github.com/obsproject/obs-studio/blob/master/plugins/win-capture/duplicator-monitor-capture.c):

```c
if (!wgc_supported)
        method = METHOD_DXGI;

if (method == METHOD_AUTO) {
        method = METHOD_DXGI;

        obs_enter_graphics();
        const int dxgi_index = gs_duplicator_get_monitor_index(monitor);
        obs_leave_graphics();

        if (dxgi_index == -1) {
                method = METHOD_WGC;
        } else {
                SYSTEM_POWER_STATUS status;
                if (GetSystemPowerStatus(&status) && status.BatteryFlag < 128) {
                        obs_enter_graphics();
                        const uint32_t count = gs_get_adapter_count();
                        obs_leave_graphics();
                        if (count >= 2)
                                method = METHOD_WGC;
                }
        }
}
```

DDA is the default. WGC is selected only when (a) the monitor is not enumerable on OBS's own adapter
— exactly the cross-adapter case — or (b) the machine has a battery (`BatteryFlag < 128` means a
system battery is present) **and** two or more adapters, i.e. a hybrid-graphics laptop.

**The reference machine is a desktop with no battery, so OBS will use DXGI Desktop Duplication on the
display ClipShift is asked to record.** That is the coexistence case to design for.

### OBS's DDA loop

[libobs-d3d11/d3d11-duplicator.cpp](https://github.com/obsproject/obs-studio/blob/master/libobs-d3d11/d3d11-duplicator.cpp):

- `AcquireNextFrame(0, &info, res.Assign())` — **zero timeout**, once per OBS render tick. Pacing is
  entirely OBS's.
- `DXGI_ERROR_WAIT_TIMEOUT` → `return true`, keep the previously copied texture. That is the
  repeat-frame mechanism.
- `DXGI_ERROR_ACCESS_LOST` → `return false`; the source rebuilds the duplicator on a 3-second retry
  (`RESET_INTERVAL_SEC`).
- Always `CopyResource` into an OBS-owned texture, then `ReleaseFrame()` immediately — i.e. OBS does
  **not** follow the hold-the-frame guidance in §2, and does **not** take the SRV-direct path of §3.
- **`LastPresentTime` is never read.** OBS timestamps on its own compositor clock.
- Duplicators are refcounted per monitor index inside the process
  (`static std::unordered_map<int, gs_duplicator *> instances`) — necessary, since the documented
  limit is one duplication per process per output.

### OBS's WGC path

[libobs-winrt/winrt-capture.cpp](https://github.com/obsproject/obs-studio/blob/master/libobs-winrt/winrt-capture.cpp):

- Availability gate: `IsApiContractPresent(L"Windows.Foundation.UniversalApiContract", 8)` with the
  comment `/* no contract for IGraphicsCaptureItemInterop, verify 10.0.18362.0 */` — Windows 10 1903.
- `Direct3D11CaptureFramePool::Create(device, format, 2, size)` — 2 buffers, dispatcher-bound
  variant — with `FrameArrived` → `TryGetNextFrame()`.
- Feature detection by `ApiInformation::IsPropertyPresent` for `IsCursorCaptureEnabled` and
  `IsBorderRequired`. **`MinUpdateInterval`, `DirtyRegionMode` and `IncludeSecondaryWindows` are not
  used at all.**
- `RequestAccessAsync(Borderless).get()` then `session.IsBorderRequired(false)`.
- Item creation via `IGraphicsCaptureItemInterop::CreateForMonitor` / `CreateForWindow`, never the
  picker.
- On size change: `frame_pool.Recreate(device, format, 2, frame_content_size)`.
- **`SystemRelativeTime` never appears in the file** (verified: zero occurrences).

That last point deserves weight: **the most widely deployed WGC consumer on Windows discards WGC's
frame timestamp entirely.** OBS is a resampling compositor, not a synchronised recorder. ClipShift
*is* a synchronised recorder, so it will exercise `SystemRelativeTime` / `LastPresentTime` in a way
OBS's field-testing does not cover. Budget for verifying the timestamp path yourself (§11.2).

### FFmpeg is a second independent vote for DDA

FFmpeg's built-in Windows display-capture source is `ddagrab` —
[libavfilter/vsrc_ddagrab.c](https://github.com/FFmpeg/FFmpeg/blob/master/libavfilter/vsrc_ddagrab.c) —
which is Desktop Duplication, outputs `AV_PIX_FMT_D3D11` with `sw_format` BGRA / X2BGR10 / RGBAF16,
and stays GPU-resident all the way into `hevc_nvenc`. FFmpeg ships **no** WGC capture source at all.

So the two most widely deployed open-source Windows capture pipelines both default to DDA for display
capture, for a fully GPU-resident path. That is not proof of lower in-game cost — no measurement
exists (§11.1) — but it is meaningful convergent evidence that DDA is the well-trodden road and WGC
the exception case.

---

## 10. Coexistence with a second capture consumer

**DDA has a documented, generous, hard limit.** From
[DuplicateOutput](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutput1-duplicateoutput):

> "**By default, only four processes can use a `IDXGIOutputDuplication` interface at the same time
> within a single session. A process can have only one desktop duplication interface on a single
> desktop output; however, that process can have a desktop duplication interface for each output that
> is part of the desktop.**"

> "`DXGI_ERROR_NOT_CURRENTLY_AVAILABLE` if DXGI reached the limit on the maximum number of concurrent
> duplication applications (**default of four**). Therefore, the calling application cannot create any
> desktop duplication interfaces until the other applications close."

OBS + ClipShift = **2 of 4**. Comfortable, but the budget is shared with Discord, GeForce overlay,
Game Bar, and remote-support tools, and it is per **session**, not per output. The failure is a clean
HRESULT, not degraded output.

Microsoft treats this as terminal rather than transient: the official sample omits
`DXGI_ERROR_NOT_CURRENTLY_AVAILABLE` from every retry table and surfaces a message box — "There is
already the maximum number of applications using the Desktop Duplication API running, please close one
of those applications and then try again."
([DuplicationManager.cpp](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/DXGIDesktopDuplication/cpp/DuplicationManager.cpp))
ClipShift should do the same: a specific, actionable message, not a generic error.

The word "**default** of four" implies configurability, but **no documented mechanism for changing it
exists** — no Learn page, registry key, or policy.

A second `DuplicateOutput` from the *same* process on the *same* output returns `E_INVALIDARG` — "The
calling application is already duplicating this desktop output." If ClipShift grows a preview that
also wants the desktop image, it must share one duplication object, as OBS does.

**Duplicated work.** Both consumers independently receive and process the desktop image. Nothing in
the documentation suggests DXGI coalesces work between duplication clients, and the `ReleaseFrame`
remarks imply per-client surface handling. Whether two DDA clients cost measurably more in-game FPS
than one is a measurement question (§11.1).

**WGC coexistence is undocumented.** No Learn page states a limit on concurrent
`GraphicsCaptureSession`s. Absence of a documented limit is not a documented absence of a limit —
treat it as unknown. What *is* documented is the border interaction (§6), which is itself a
coexistence failure mode: "a yellow border is drawn around each item being captured", and another app
can force the border back on.

---

## 11. What is not settled

Stated plainly, because a confidently wrong answer here costs an architecture rewrite.

1. **In-game FPS impact — the metric that actually matters — cannot be settled from primary sources.**
   Neither Microsoft nor NVIDIA publishes a comparison of DDA vs WGC capture overhead, and no
   first-party document quantifies either. **Everything in this document about performance is
   mechanism-reasoning, not measurement**, with the single exception of the `DuplicateOutput1`
   format-conversion remark (§3). **This needs a prototype ticket on the reference machine**: OBS
   streaming + a game + ClipShift capturing, measuring in-game frame times across (a) DDA
   hold-the-frame, (b) DDA release-immediately, (c) DDA with SRV-direct vs full `CopyResource`,
   (d) WGC. Do not lock the spec's performance claims until that exists.
2. **What `LastPresentTime` is anchored to.** Documented as "the time stamp of the last update of the
   desktop image", obtained via QPC. Whether that is compositor-present, scanout, or vblank is not
   stated. The QPC *domain* is settled; the semantic anchor is not. A constant offset is harmless; a
   varying one would not be, and nothing rules that out. Verify empirically against a known-cadence
   source before the sync design is frozen.
3. **WGC frame delivery model.** Whether it is change-driven or paced, and whether `FrameArrived`
   stops firing on a static desktop, is not documented anywhere. Also undocumented: what happens when
   the app under-drains the frame pool (drop, stall, or overwrite).
4. **`GraphicsCaptureSession.MinUpdateInterval` semantics.** Its Learn page carries a signature and
   nothing else — no description, no remarks, no Requirements table. Whether it caps the rate or
   forces idle frames is unknown. Windows 11 24H2 (26100) and later regardless.
5. **WGC cross-adapter behaviour.** Undocumented — whether a capture item on adapter A with a frame
   pool on adapter B works, and at what cost. OBS's `choose_method` implies it works; that is
   inference.
6. **Whether the CsWinRT projection allocates a fresh RCW per `TryGetNextFrame`.** Not documented.
   Would need measurement if WGC were chosen.
7. **Whether either API honours `WDA_EXCLUDEFROMCAPTURE`.** The `SetWindowDisplayAffinity` page
   describes "a specific set of public operating system features and APIs" and never enumerates them.
   Neither the DDA nor the WGC documentation mentions it. **The sibling capture-invisible-overlay
   ticket owns this; this document has not answered it.**
8. **Texture descriptions on both sides.** DDA's acquired surface and WGC's pool textures both have
   undocumented `D3D11_TEXTURE2D_DESC` fields (usage, bind flags, misc flags, shareability). Query
   `GetDesc` at runtime; do not hard-code.
9. **`DXGI_OUTDUPL_COMPOSITED_UI_CAPTURE_ONLY`.** The sole member of `DXGI_OUTDUPL_FLAG` in
   `dxgi1_5.h`, with **no Learn page at all** (the URL 404s). Given the name, it could be relevant to
   excluding fullscreen game content or to overlay behaviour — worth a prototype probe, but nothing
   should be designed around it.
10. **How to change the "default of four" concurrent-duplication limit.** The word "default" implies
    configurability; no mechanism is documented.
11. **Whether the Microsoft Hybrid `DXGI_ERROR_UNSUPPORTED` restriction still applies on Windows
    10/11.** The only primary source is a Windows 8.1-scoped support article.
12. **`IsCursorCaptureEnabled`'s default value**, the complete list of pixel formats the WGC frame
    pool accepts, WGC behaviour under DPI changes, and `DirtyRegionMode` /
    `IDisplayGraphicsCaptureSession.SetWindowExclusionList` semantics — all published as signatures
    with no prose.
13. **Precise DRM/protected-content trigger conditions** behind `ProtectedContentMaskedOut`. The
    driver docs say only "The API provides protection against accessing protected video content."
14. **Whether NVENC requires the input texture's creating device to be the session device.** NVIDIA's
    guide never says. FFmpeg uses one device; OBS uses two on the same adapter bridged by a shared
    handle. Both work in the field; neither is contractual.
15. **WASAPI loopback behaviour during silence** is not addressed by the Learn "Loopback Recording"
    page. Whether packets stop arriving when nothing is playing matters a great deal for a 4-hour
    timeline. Flagged for the audio ticket; does not affect this decision.

---

## 11a. Adjacent findings — for sibling tickets, not decided here

Turned up while settling the zero-copy question. Recorded so the work isn't repeated; **not** part of
this ticket's decision.

- **NVENC concurrent-session limit: two NVIDIA sources disagree.** The
  [Video Encode and Decode GPU Support Matrix](https://developer.nvidia.com/video-encode-and-decode-gpu-support-matrix-new)
  lists **12** concurrent sessions for the RTX 5060 Ti (and every GeForce row from Ada onward); the
  [NVENC Application Note 13.0](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-application-note/index.html)
  still says "**8 per system**" for non-qualified GPUs. Unresolvable from primary sources. Either way
  the budget is **system-wide** and shared with OBS, and one ClipShift session is safe.
- **Crash survivability points hard at the fragmented MP4 sink.** `IMFSinkWriter::Finalize`: "**If you
  do not call Finalize, the output from the media sink might be incomplete or invalid. For example,
  required file headers might be missing.**"
  ([Learn](https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-imfsinkwriter-finalize))
  And the standard MP4 sink writes the index last: "**The default behavior of the mpeg4 media sink is
  to write 'moov' after 'mdat' box.**" `MF_MPEG4SINK_MOOV_BEFORE_MDAT` does **not** rescue this —
  "This feature involves an additional file copying/remuxing", i.e. it happens at finalize time.
  [`MFCreateFMPEG4MediaSink`](https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nf-mfidl-mfcreatefmpeg4mediasink)
  ("Creates a media sink for authoring fragmented MP4 files", Windows 8+) plus
  `MF_MPEG4SINK_MIN_FRAGMENT_DURATION` is the documented route to a file that survives a kill.
  **Caveat: the MPEG-4 File Sink page affirms >4 GB support for the *non-fragmented* sink and is
  silent about the fragmented one.** A 4-hour 1080p60 recording will exceed 4 GB — verify empirically.
- **Media Foundation's D3D11 protocol, if MF is chosen over NVENC-direct:** check `MF_SA_D3D11_AWARE`,
  `MFCreateDXGIDeviceManager` → `ResetDevice`, then
  `ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, …)` which "**Must be called before SetInputType or
  SetOutputType**", then feed textures via `MFCreateDXGISurfaceBuffer` (riid "must be
  `IID_ID3D11Texture2D` or `IID_ID3D12Resource`"). Read `MF_SA_D3D11_BINDFLAGS` off the input stream
  attributes to learn what bind flags the vendor MFT wants on capture textures. Hardware MFTs are
  always asynchronous and must be unlocked with `MF_TRANSFORM_ASYNC_UNLOCK`.
- **`MF_LOW_LATENCY` is probably wrong for ClipShift**: "you typically should not enable low-latency
  mode, **because it can affect quality**"
  ([Learn](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-low-latency)). ClipShift
  records to disk; it is not a real-time communication scenario.

---

## 12. Consequences for the spec

If this recommendation is accepted:

- **Capture:** `IDXGIOutput5::DuplicateOutput1` (falling back to `IDXGIOutput1::DuplicateOutput`),
  supported-format list `{B8G8R8A8_UNORM}` for SDR, on a D3D11 device created on the adapter that
  drives the selected display, from an `IDXGIFactory1` or later.
- **Set `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2` on the capture thread.** Microsoft's sample does
  this with the comment "Set per-monitor DPI awareness which is required for the latest
  DuplicateOutput1 API" — a requirement that appears nowhere on the Learn page.
- **Adapter check at record time:** match the selected `HMONITOR` against each adapter's outputs;
  if it is not on the encoding adapter, warn and refuse (MVP scope, per issue #1).
- **Pacing:** app-driven 60 Hz loop; `DXGI_ERROR_WAIT_TIMEOUT` means "repeat the last frame". Prefer
  holding the acquired frame and releasing immediately before the next acquire, pending §11.1.
- **Frame path:** SRV directly on the acquired texture → BGRA→NV12 convert shader → encoder input.
  Avoid a full-frame `CopyResource` unless measurement says otherwise, and prefer converting in your
  own shader over handing BGRA to NVENC (NVENC's RGB path burns CUDA, §3).
- **Encode in-process.** Shelling out to `ffmpeg.exe` forces a CPU round-trip; the CLI has no way to
  receive a shared DXGI handle. NVENC directly, Media Foundation, or libavcodec as a library.
- **Timestamps:** `LastPresentTime` scaled to 100 ns (`10,000,000 · t / QPF`) as the video PTS
  source; discard zeros (pointer-only updates, identified by `AccumulatedFrames == 0`); log
  `AccumulatedFrames > 1` as a drop indicator.
- **Cursor:** composite from `PointerPosition` plus a cached `GetFramePointerShape`, handling
  `MONOCHROME` (stacked AND/XOR masks), `COLOR`, and `MASKED_COLOR` (alpha-as-mask, 0 = replace,
  0xFF = XOR). Reference the Microsoft `DXGIDesktopDuplication` sample, **not** OBS (GPLv2 vs MIT).
- **Robustness loop:** `DXGI_ERROR_ACCESS_LOST` on any duplication method means tear down and
  re-create. Documented triggers: "Desktop switch / Mode change / Switch from DWM on, DWM off, or
  other full-screen application" — all routine during a 4-hour gaming session, not exceptional.
  A game entering or leaving fullscreen exclusive will invalidate the duplication object; once
  steady in FSE, duplication is documented to work ("even full screen DirectX applications can be
  duplicated", [driver docs](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/desktop-duplication-api)).
  Mirror the sample's back-off rather than hot-looping: 250 ms ×20, then 2 s ×60, then 5 s.
- **Secure desktop:** call `OpenInputDesktop(0, FALSE, GENERIC_ALL)` + `SetThreadDesktop` on the
  capture thread and treat failure as retryable — this is how the Microsoft sample survives UAC
  prompts and Ctrl+Alt+Del. Re-creation returns `E_ACCESSDENIED` until the user returns to the
  default desktop ("only an application that runs at LOCAL_SYSTEM can access the secure desktop").
- **Start-up failure surface, with distinct messages:** `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE` (too
  many capture apps — terminal, not retryable), `E_ACCESSDENIED` (secure desktop), and
  `DXGI_ERROR_UNSUPPORTED` (unsupported desktop mode; retry after `EVENT_SYSTEM_DESKTOPSWITCH` or
  `WM_DISPLAYCHANGE`).
- **.NET projection:** CsWin32 with `allowMarshaling: false` and `comInterop.preserveSigMethods:
  ["*"]`, so the per-frame path is a raw vtable call returning a raw `HRESULT`.
- **Rotation:** `AcquireNextFrame` always returns the un-rotated surface with the image rotated
  inside it; handle `DXGI_OUTPUT_DESC.Rotation`. Not an MVP concern on three landscape displays, but
  a one-line trap worth documenting.

Feeds directly into the still-open "mid-recording failure behaviour" and "error surfacing" items on
issue #1.

---

## Sources

All primary.

**Microsoft Learn — DXGI Desktop Duplication**

- [Desktop Duplication API (overview)](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api)
- [Desktop Duplication API (driver docs)](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/desktop-duplication-api)
- [IDXGIOutputDuplication](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nn-dxgi1_2-idxgioutputduplication)
- [IDXGIOutput1::DuplicateOutput](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutput1-duplicateoutput)
- [IDXGIOutput5::DuplicateOutput1](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_5/nf-dxgi1_5-idxgioutput5-duplicateoutput1)
- [AcquireNextFrame](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe)
- [ReleaseFrame](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-releaseframe)
- [MapDesktopSurface](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-mapdesktopsurface)
- [GetFramePointerShape](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-getframepointershape)
- [DXGI_OUTDUPL_FRAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info)
- [DXGI_OUTDUPL_POINTER_POSITION](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_pointer_position)
- [DXGI_OUTDUPL_POINTER_SHAPE_INFO](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_pointer_shape_info)
- [DXGI_OUTDUPL_POINTER_SHAPE_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ne-dxgi1_2-dxgi_outdupl_pointer_shape_type)
- [DXGI_ERROR codes](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-error)
- [Hybrid-system DDA restriction (KB, Windows 8.1)](https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/error-when-dda-capable-app-is-against-gpu)

**Microsoft Learn — Windows.Graphics.Capture**

- [Screen capture (conceptual)](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
- [Screen capture to video (C# walkthrough)](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture-video)
- [Direct3D11CaptureFramePool](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool) · [Create](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.create) · [CreateFreeThreaded](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded) · [Recreate](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.recreate) · [FrameArrived](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.framearrived) · [TryGetNextFrame](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.trygetnextframe)
- [Direct3D11CaptureFrame](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe) · [SystemRelativeTime](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime)
- [GraphicsCaptureSession](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession) · [IsCursorCaptureEnabled](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.iscursorcaptureenabled) · [IsBorderRequired](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired) · [MinUpdateInterval](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.minupdateinterval) · [IncludeSecondaryWindows](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.includesecondarywindows)
- [GraphicsCaptureAccess.RequestAccessAsync](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccess.requestaccessasync) · [GraphicsCaptureAccessKind](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccesskind)
- [GraphicsCaptureItem.TryCreateFromDisplayId](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureitem.trycreatefromdisplayid) · [GraphicsCapturePicker](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturepicker)
- [IGraphicsCaptureItemInterop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nn-windows-graphics-capture-interop-igraphicscaptureiteminterop)
- [IDirect3DDxgiInterfaceAccess](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/ns-windows-graphics-directx-direct3d11-interop-idirect3ddxgiinterfaceaccess) · [CreateDirect3D11DeviceFromDXGIDevice](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/nf-windows-graphics-directx-direct3d11-interop-createdirect3d11devicefromdxgidevice)
- [IDisplayGraphicsCaptureSession](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.idisplaygraphicscapturesession)
- [App capability declarations](https://learn.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations)
- [New ways to do screen capture (Windows Developer Blog, 2019)](https://blogs.windows.com/windowsdeveloper/2019/09/16/new-ways-to-do-screen-capture/)

**Microsoft Learn — clocks, timestamps, and window affinity**

- [Acquiring high-resolution time stamps](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps)
- [IAudioCaptureClient::GetBuffer](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)
- [IAudioClock::GetPosition](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition)
- [Loopback Recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
- [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)

**Microsoft .NET interop**

- [Call WinRT APIs in desktop apps](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps)
- [C#/WinRT](https://learn.microsoft.com/en-us/windows/apps/develop/platform/csharp-winrt/) · [CsWinRT interop guide](https://github.com/microsoft/CsWinRT/blob/master/docs/interop.md)
- [CsWin32](https://github.com/microsoft/CsWin32) · [settings.schema.json](https://github.com/microsoft/CsWin32/blob/main/src/Microsoft.Windows.CsWin32/settings.schema.json) · [getting started](https://github.com/microsoft/CsWin32/blob/main/docfx/docs/getting-started.md)
- [win32metadata (SDK headers, Dxgi partition)](https://github.com/microsoft/win32metadata)

**Microsoft sample code**

- [DXGIDesktopDuplication](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/DXGIDesktopDuplication/cpp) — `DesktopDuplication.cpp`, `DuplicationManager.cpp`, `DisplayManager.cpp`
- [Windows.UI.Composition-Win32-Samples — ScreenCaptureforHWND](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/blob/master/cpp/ScreenCaptureforHWND/ScreenCaptureforHWND/SimpleCapture.cpp)

**Direct3D sharing, NVENC, and Media Foundation** (for §3, §7 and §11a)

- [D3D11_RESOURCE_MISC_FLAG](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_resource_misc_flag) · [ID3D11Device1::OpenSharedResource1](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nf-d3d11_1-id3d11device1-opensharedresource1) · [IDXGIKeyedMutex](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nn-dxgi-idxgikeyedmutex)
- [D3D12 shared heaps (cross-adapter)](https://learn.microsoft.com/en-us/windows/win32/direct3d12/shared-heaps) · [D3D12_RESOURCE_FLAGS](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_resource_flags)
- [NVENC Video Encoder API Programming Guide 13.0](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html) · [NVENC Application Note 13.0](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-application-note/index.html) · [Video Encode and Decode GPU Support Matrix](https://developer.nvidia.com/video-encode-and-decode-gpu-support-matrix-new)
- [MF_SA_D3D11_AWARE](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-sa-d3d11-aware) · [MFT_MESSAGE_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/mftransform/ne-mftransform-mft_message_type) · [MFCreateDXGISurfaceBuffer](https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mfcreatedxgisurfacebuffer) · [MFTEnumEx](https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mftenumex) · [Asynchronous MFTs](https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts) · [MF_LOW_LATENCY](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-low-latency)
- [IMFSinkWriter::Finalize](https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-imfsinkwriter-finalize) · [MFCreateFMPEG4MediaSink](https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nf-mfidl-mfcreatefmpeg4mediasink) · [MF_MPEG4SINK_MOOV_BEFORE_MDAT](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-mpeg4sink-moov-before-mdat) · [MPEG-4 File Sink](https://learn.microsoft.com/en-us/windows/win32/medfound/mpeg-4-file-sink)

**FFmpeg source (github.com/FFmpeg/FFmpeg, `master`)**

- [libavcodec/nvenc.c](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/nvenc.c) — `AV_PIX_FMT_D3D11` accepted directly
- [libavfilter/vsrc_ddagrab.c](https://github.com/FFmpeg/FFmpeg/blob/master/libavfilter/vsrc_ddagrab.c) — FFmpeg's Desktop Duplication capture source
- [libavcodec/mfenc.c](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/mfenc.c) · [libavutil/hwcontext_d3d11va.c](https://github.com/FFmpeg/FFmpeg/blob/master/libavutil/hwcontext_d3d11va.c)

**OBS Studio source (github.com/obsproject/obs-studio, `master`)**

- [plugins/win-capture/duplicator-monitor-capture.c](https://github.com/obsproject/obs-studio/blob/master/plugins/win-capture/duplicator-monitor-capture.c)
- [libobs-d3d11/d3d11-duplicator.cpp](https://github.com/obsproject/obs-studio/blob/master/libobs-d3d11/d3d11-duplicator.cpp)
- [libobs-winrt/winrt-capture.cpp](https://github.com/obsproject/obs-studio/blob/master/libobs-winrt/winrt-capture.cpp)
- [libobs-winrt/winrt-dispatch.cpp](https://github.com/obsproject/obs-studio/blob/master/libobs-winrt/winrt-dispatch.cpp)
- [plugins/obs-nvenc/nvenc-d3d11.c](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-nvenc/nvenc-d3d11.c)
