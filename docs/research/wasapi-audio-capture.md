# WASAPI audio capture: mechanism and source model

Research for [issue #4](https://github.com/richardthornton/clipshift/issues/4). Investigated against primary sources only —
Microsoft Learn Core Audio reference, Windows SDK headers, Microsoft's own ApplicationLoopback sample, and the
shipping source of OBS Studio and NAudio. Every claim below carries the URL it came from. Claims that primary
sources do **not** settle are marked **UNSETTLED** and say what would be needed to settle them.

---

## Recommendation in brief

**Capture mechanism.** Raw WASAPI interop, driving a shared-mode, event-driven `IAudioClient` per sink, reading
`IAudioCaptureClient::GetBuffer` with **both** the device-position and QPC-position out-parameters requested.
Specifically **not** NAudio's idiomatic `WasapiCapture`/`WasapiLoopbackCapture` API, which silently discards both
timestamps (§10). NAudio's *low-level* `AudioCaptureClient` wrapper does expose them and is a legitimate option;
the choice between it and generated interop is an ordinary engineering call, not a correctness one.

**Source model.** Model an audio source as a discriminated union of `Endpoint` and `Process`, keyed on a
persisted *selector* rather than a live handle, resolved to a live `IAudioClient` at record time. The two arms
differ only up to `Initialize`; every byte downstream is identical.

**The three findings that most change the design:**

1. **QPC is already the common timebase for audio and video** — no clock-correlation step is needed (§1.2). Better
   than the brief assumed.
2. **But the audio endpoint runs on its own crystal and no API reports its real rate** (§9). Uncorrected, a 4-hour
   session drifts ~0.5–1.5 s. Rate must be *measured* from the per-packet timestamps.
3. **Silence is a first-class concern.** The `SILENT` flag means "ignore these values", not "no data" — the frames
   are real and must be written (§1.4) — and endpoint loopback appears to stop delivering entirely when the system
   is idle (§2.2).

Full recommendation in §11; everything I could not settle is collected in §12. Detail and citations follow.

---

## 1. Timestamp semantics — the load-bearing finding

This section is the raw material for the multi-hour A/V sync ticket. It is the part worth reading closely.

### 1.1 What each captured buffer is stamped with

`IAudioCaptureClient::GetBuffer` returns five out-parameters, two of which are timestamps
([GetBuffer reference](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)):

```cpp
HRESULT GetBuffer(
  [out] BYTE   **ppData,
  [out] UINT32 *pNumFramesToRead,
  [out] DWORD  *pdwFlags,
  [out] UINT64 *pu64DevicePosition,
  [out] UINT64 *pu64QPCPosition
);
```

**`pu64DevicePosition`** — "the device position of the first audio frame in the data packet. The device position
is expressed as the number of audio frames from the start of the stream." It is a *frame counter*, not a time.
It advances in the stream's own sample-rate domain.

**`pu64QPCPosition`** — "the value of the performance counter at the time that the audio endpoint device recorded
the device position of the first audio frame in the data packet. The method converts the counter value to
100-nanosecond units before writing it to `*pu64QPCPosition`."

The conversion is documented exactly. Given raw counter `t` and `QueryPerformanceFrequency` `f`:

```
*pu64QPCPosition = 10,000,000 * t / f
```

So the value is **QPC, expressed in 100 ns units** — not raw QPC ticks. To compare it against your own
`QueryPerformanceCounter()` reading you must apply the same conversion to yours, which the
[`IAudioClock2::GetDevicePosition` remarks](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock2-getdeviceposition)
spell out step by step: "Multiply the raw counter value by 10,000,000. Divide the result by the counter frequency
obtained from `QueryPerformanceFrequency`."

Both timestamps describe **the first frame in the packet**, not the moment `GetBuffer` was called. That
distinction is what makes them usable: they are insensitive to how late your capture thread was scheduled.

The doc is explicit that the pair is a unit: "These values provide a time stamp for the first audio frame in the
data packet. Through the *pdwFlags* output parameter, the method indicates whether the reported device position is
valid."

### 1.2 The video path is stamped against the same clock

This is the single most useful fact for the sync ticket. Both candidate video capture paths stamp frames with QPC:

- **Windows.Graphics.Capture** — `Direct3D11CaptureFrame.SystemRelativeTime` is documented as
  "The QPC (Query Performance Counter) time at which the compositor rendered the frame."
  ([reference](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime))
- **DXGI Desktop Duplication** — `DXGI_OUTDUPL_FRAME_INFO.LastPresentTime` is "The time stamp of the last update
  of the desktop image. The operating system calls the `QueryPerformanceCounter` function to obtain the value."
  ([reference](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info))

So **video frames and audio packets land in one common, monotonic timebase with no correlation step required.**
ClipShift does not need to establish a cross-clock mapping between the audio and video paths; it needs to convert
units (QPC ticks vs. 100 ns) and pick a common `t=0`.

Caveat on units: `SystemRelativeTime` is a `TimeSpan`, i.e. already 100 ns units, matching WASAPI's convention.
`LastPresentTime` is a raw `LARGE_INTEGER` QPC tick count, which needs the `* 10,000,000 / f` conversion to match.

### 1.3 QPC's own guarantees over a 4-hour session

From [Acquiring high-resolution time stamps](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps):

- **Monotonic.** "Is the performance counter monotonic (non-decreasing)? **Yes. QPC does not go backward.**"
- **Fixed frequency.** "The frequency of the performance counter is fixed at system boot and is consistent across
  all processors so you only need to query the frequency from `QueryPerformanceFrequency` as the application
  initializes, and then cache the result."
- **Survives sleep.** QPC "returns the total number of ticks that have occurred since the Windows operating system
  was started, **including the time when the machine was in a sleep state** such as standby, hibernate, or
  connected standby." A suspend mid-recording therefore shows up as a large forward jump, not a discontinuity.
- **Immune to power management.** "Is QPC accuracy affected by processor frequency changes caused by power
  management or Turbo Boost technology? **No.**"
- **Unaffected by clock changes.** "Is QPC affected by daylight savings time, leap seconds, time zones, or system
  time changes made by the administrator? **No.** QPC is completely independent of the system time and UTC."
- **Cross-thread ordering caveat.** "when comparing performance counter results that are acquired from different
  threads, values that differ by ± 1 tick have an ambiguous ordering." Irrelevant at audio/video timescales.

**QPC is itself a crystal and has its own frequency offset.** The same doc: consumer crystals are "typically
manufactured with a frequency tolerance of ± 30 to 50 parts per million", and it gives the worked example that
±50 ppm yields ±4.3 s of error over 24 hours — roughly **±0.7 s over a 4-hour session**.

This matters less than it looks, and the distinction is worth being precise about:

- QPC's own offset is a *common-mode* error. Video and audio are both stamped against it, so it **cancels
  entirely for A/V sync purposes**. It only makes the recording's absolute wall-clock duration slightly wrong.
- The error that does **not** cancel is the audio endpoint's own crystal versus QPC. That is a genuine
  differential drift and is the thing the sync ticket has to correct for. See §9.

### 1.4 The three buffer flags, precisely

From the [`_AUDCLNT_BUFFERFLAGS` reference](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags),
quoted verbatim:

| Flag | Documented meaning |
| --- | --- |
| `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY` | "The data in the packet is not correlated with the previous packet's device position; this is possibly due to a stream state transition or timing glitch." |
| `AUDCLNT_BUFFERFLAGS_SILENT` | "Treat all of the data in the packet as silence and ignore the actual data values." |
| `AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR` | "The time at which the device's stream position was recorded is uncertain. Thus, the client might be unable to accurately set the time stamp for the current data packet." |

Three consequences ClipShift must design around:

**`SILENT` means the buffer contents are undefined, not zero.** The doc says "ignore the actual data values" — it
does not promise they are zeroes. Microsoft's own [Capturing a Stream](https://learn.microsoft.com/en-us/windows/win32/coreaudio/capturing-a-stream)
example handles it by nulling the pointer and having the sink synthesise silence:

```c
if (flags & AUDCLNT_BUFFERFLAGS_SILENT)
{
    pData = NULL;  // Tell CopyData to write silence.
}
```

Note this is *not* a signal to drop the packet. The frames are real and counted; they must be written, as
silence, to keep the file's frame count aligned with elapsed time. Dropping them is a direct source of drift.

**`DATA_DISCONTINUITY` is the glitch signal, and is Windows 7+.** The `GetBuffer` doc: "The
`AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY` flag is not supported in Windows Vista. In Windows 7 and later OS
releases, **this flag can be used for glitch detection**." Its meaning is specifically that device position is no
longer correlated with the previous packet's — which is exactly the condition under which a naive
"frames written so far == elapsed time" assumption breaks. This is the flag that tells you a gap happened.

**`TIMESTAMP_ERROR` invalidates `pu64QPCPosition` for that packet only.** It does not invalidate the audio. The
right response is to fall back to extrapolating the timestamp from device position for that packet, not to drop
it.

### 1.5 What OBS actually does with these — and what it reveals

OBS Studio's `plugins/win-wasapi/win-wasapi.cpp` is the reference real-world implementation.
([source](https://github.com/obsproject/obs-studio/blob/master/plugins/win-wasapi/win-wasapi.cpp))

It reads both timestamps (`pos`, `ts`) and converts QPC 100 ns units to nanoseconds by multiplying by 100:

```cpp
res = capture->GetBuffer(&buffer, &frames, &flags, &pos, &ts);
...
if (sourceType == SourceType::ProcessOutput) {
        data.timestamp = ts * 100;
} else {
        data.timestamp = useDeviceTiming ? ts * 100 : os_gettime_ns();
        if (!useDeviceTiming) {
                data.timestamp -= util_mul_div64(frames, UINT64_C(1000000000), sampleRate);
        }
}
```

Three things are worth extracting from this, because they are hard-won operational knowledge encoded in shipping code:

1. **OBS defaults device timing ON for loopback and OFF for microphone input.** From the same file:

   ```cpp
   static void GetWASAPIDefaultsInput(obs_data_t *settings) {
           obs_data_set_default_bool(settings, OPT_USE_DEVICE_TIMING, false);
   }
   static void GetWASAPIDefaultsDeviceOutput(obs_data_t *settings) {
           obs_data_set_default_bool(settings, OPT_USE_DEVICE_TIMING, true);
   }
   ```

   Loopback trusts the device QPC stamp; microphone input does not, and instead uses arrival wall-clock minus
   the packet duration. Process loopback uses `ts * 100` unconditionally, with no opt-out. I could not find a
   primary source explaining *why* input distrusts the device stamp — see **UNSETTLED** below.

2. **It handles `SILENT` by substituting a pre-allocated zero buffer**, keeping the frame count intact rather
   than dropping the packet:

   ```cpp
   if (flags & AUDCLNT_BUFFERFLAGS_SILENT) {
           uint32_t requiredBufSize = get_audio_channels(speakers) * frames * 4;
           if (silence.size() < requiredBufSize) { silence.resize(requiredBufSize); }
           buffer = silence.data();
   }
   ```

3. **It logs `TIMESTAMP_ERROR` once and ignores `DATA_DISCONTINUITY` entirely.** OBS does not check
   `DATA_DISCONTINUITY` anywhere in this file. For OBS's use case (live streaming, where a fixed-latency
   resampler absorbs error) that is defensible. For ClipShift's — a 4-hour file that must still be in sync at
   the end — it is not. **ClipShift should check `DATA_DISCONTINUITY` even though OBS does not.**

**UNSETTLED:** why OBS distrusts the device QPC timestamp for microphone input but trusts it for loopback. The
code carries no comment and I found no primary source explaining it. Two plausible readings — that some capture
drivers stamp badly, or that it is a legacy default nobody revisited — and I cannot distinguish them from
sources. This is worth a measurement on the reference hardware before committing to device timing for the mic
sink: log `pu64QPCPosition` deltas against `pu64DevicePosition` deltas for an hour and see whether they track.

---

## 2. Loopback capture and the silence problem

### 2.1 Initialisation

Loopback is a flag on a **render** endpoint's capture stream, not a separate device. From
[Loopback Recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording), the delta from
an ordinary capture stream is exactly two lines: pass `eRender` instead of `eCapture` to
`GetDefaultAudioEndpoint`, and pass `AUDCLNT_STREAMFLAGS_LOOPBACK` instead of `0` as `Initialize`'s `StreamFlags`.

Hard constraints, from the [`IAudioClient::Initialize` reference](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize):

- **Shared mode only.** "A client can enable audio loopback only on a rendering endpoint with a shared-mode
  stream." Passing `AUDCLNT_SHAREMODE_EXCLUSIVE` with the loopback flag returns `E_INVALIDARG`. Loopback Recording
  restates it: "Exclusive-mode streams cannot operate in loopback mode."
- **Render endpoints only.** Setting the flag on a capture device returns `AUDCLNT_E_WRONG_ENDPOINT_TYPE`.
- **Event-driven loopback works on Windows 10 1703+.** "In versions of Windows prior to Windows 10 1703,
  pull-mode capture client does not receive any events when a stream is initialized with event-driven buffering
  and is loopback-enabled... In Windows 10 versions 1703 and higher, event-driven loopback clients are supported,
  and no longer need the workaround involving the render stream." ClipShift targets Windows 11, so the ugly
  render-stream-pump workaround is not needed.

There is a **documentation inconsistency** worth flagging. `IAudioClient::Initialize` states: "The loopback data
in the capture buffer is in the device format, which the client can obtain by querying the device's
`PKEY_AudioEngine_DeviceFormat` property." But every real implementation — OBS
([`InitClient`](https://github.com/obsproject/obs-studio/blob/master/plugins/win-wasapi/win-wasapi.cpp)) and NAudio
([`WasapiLoopbackCapture`](https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/WasapiLoopbackCapture.cs)) —
uses `GetMixFormat` and works. **UNSETTLED:** whether `PKEY_AudioEngine_DeviceFormat` and `GetMixFormat` can
actually differ for a shared-mode loopback stream. The safe implementation is to use `GetMixFormat` (matching the
field) and assert the returned format matches what you were handed, rather than trusting either doc.

### 2.2 Silence delivery when nothing is playing — read this one carefully

This was called out in the ticket as mattering enormously for sync, and it is the place where the docs are
weakest. The honest answer has three parts.

**For process loopback, Microsoft documents it explicitly and the answer is "you get silence".** From the
[ApplicationLoopback sample page](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/):

> If the processes whose audio will be captured does not have any audio rendering streams, then the capturing
> process receives silence.

**For whole-system (endpoint) loopback, Microsoft does not document it at all.** Neither Loopback Recording nor
`IAudioClient::Initialize` says what happens to an endpoint loopback stream when the audio engine has no active
render clients. This is a genuine documentation gap, not something I overlooked.

**OBS's shipping code says the stream stops, and works around it.** `win-wasapi.cpp` contains a function
`WASAPISource::ClearBuffer` whose comment is unambiguous:

```cpp
/* Silent loopback fix. Prevents audio stream from stopping and */
/* messing up timestamps and other weird glitches during silence */
/* by playing a silent sample all over again. */
```

The implementation activates a *second*, ordinary render `IAudioClient` on the same device (note `StreamFlags = 0`
— no loopback), grabs an `IAudioRenderClient`, and writes one zeroed buffer:

```cpp
res = client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, BUFFER_TIME_100NS, 0, wfex, nullptr);
...
ComPtr<IAudioRenderClient> render;
res = client->GetService(IID_PPV_ARGS(render.Assign()));
res = render->GetBuffer(frames, &buffer);
memset(buffer, 0, (size_t)frames * (size_t)wfex->nBlockAlign);
render->ReleaseBuffer(frames, 0);
```

And it is called **only** for endpoint loopback, not for input and not for process loopback — which independently
corroborates that the problem is specific to endpoint loopback:

```cpp
ComPtr<IAudioClient> temp_client = InitClient(device, sourceType, process_id, ...);
if (sourceType == SourceType::DeviceOutput) {
        ClearBuffer(device);
}
```

**Be precise about what this workaround does and does not do.** Read the code, not just the comment. Despite
the comment saying "all over again", `ClearBuffer` runs **once, at stream initialisation**. It never calls
`Start()` on the render client, and the client is a local `ComPtr` released when the function returns. It is a
one-shot kick to spin the audio engine up at record start — it is **not** a continuous silence render that would
hold the engine open through a long quiet stretch mid-session.

So the state of knowledge is:

| Question | Status |
| --- | --- |
| Process loopback delivers silence when the target is quiet | **Settled** — documented by Microsoft |
| Endpoint loopback stops delivering when the system is fully idle | **Strongly evidenced** by OBS's code and comment; **not documented by Microsoft** |
| Whether OBS's one-shot `ClearBuffer` is sufficient for a 4-hour session | **UNSETTLED** — the code only kicks the engine at init |

**Recommendation and what to test.** ClipShift should not rely on OBS's one-shot trick. Render a continuous
stream of silence to the target endpoint for the lifetime of the recording — a second `IAudioClient` on the same
device in ordinary render mode, `Start()`ed, fed zeroes from the same event-driven loop. That converts "the
engine might go idle" from a hazard into an invariant, at the cost of one extra shared-mode render client.

The measurement that settles it: start an endpoint loopback capture on an otherwise silent machine and log
whether `GetNextPacketSize` returns non-zero over several minutes, with and without a silence renderer running.
That test is cheap and should be the first thing the implementation ticket does. In practice ClipShift's
"perfectly synced across 4 hours" requirement means a gap here is the difference between a usable file and a
catastrophic one, so it deserves an explicit test rather than an assumption.

Note the belt-and-braces alternative: because packets carry `pu64QPCPosition`, ClipShift can *detect* a gap
regardless of cause — if the QPC delta between consecutive packets exceeds the frame count converted to time,
frames are missing and can be padded with silence. **This should be implemented in any case**, since it also
covers `DATA_DISCONTINUITY` and device glitches. The silence renderer prevents the gap; the QPC-delta check
catches whatever slips through. Together they make the audio timeline continuous by construction.

---

## 3. What this implies for the sync ticket

Stated as a recipe, because the sibling ticket depends on it directly. Every step traces to a citation above.

**Establish `t=0` once, in QPC.** At record start, take a single `QueryPerformanceCounter` reading, convert to
100 ns units (`* 10,000,000 / f`), and store it as the session epoch. Cache `f` once — it is
[fixed at boot](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps).

**Stamp every stream against that one epoch.** Audio packet presentation time is
`pu64QPCPosition - epoch`. Video frame presentation time is `SystemRelativeTime - epoch` (already 100 ns) or
`LastPresentTime * 10,000,000 / f - epoch` (raw ticks). No cross-clock correlation is needed because
[both paths are QPC](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime).

**Treat the audio file's frame count as the authority, and QPC as the corrector.** Each sink writes frames
contiguously; the file has no timestamps, only an implied timeline of `frames / sampleRate`. Drift shows up as a
growing divergence between that implied timeline and `pu64QPCPosition - epoch`. Measure it continuously; do not
assume it is zero.

**Correct against a *measured rate*, not against instantaneous error.** §9 establishes that the endpoint runs on
its own crystal, that `GetFrequency` refuses to report the real rate, and that no API will tell you. So regress
`pu64DevicePosition` against `pu64QPCPosition` over a few minutes to recover the endpoint's true rate — this
converges to well under 1 ppm, far tighter than the ~50 ppm being corrected — then apply that constant ratio.
A feedback loop chasing instantaneous error is the thing that accumulates jitter over four hours; a measured
constant does not. Restart the regression segment on `DATA_DISCONTINUITY` and after every device-invalidated
recovery.

**Do the residual correction by inserting or dropping silence at packet boundaries.** Once rate is handled,
whatever remains is small and bursty. Pad (or drop) whole frames at a packet boundary rather than resampling
continuously — it keeps the writer allocation-free and the correction auditable in the log.

**Pad, never drop, on `SILENT`.** The frames are real and counted; write them as zeroes. See §1.4.

**Detect gaps two ways.** `DATA_DISCONTINUITY` on the packet, and independently a QPC delta larger than the
packet's frame duration. Either means frames are missing and silence must be inserted to preserve the timeline.
OBS checks neither, which is fine for a live stream and not fine for a 4-hour file.

**Ignore `pu64QPCPosition` for a packet flagged `TIMESTAMP_ERROR`**, and extrapolate from device position instead
— but do not drop the packet.

**Expect a forward jump if the machine sleeps.** QPC
[counts through sleep](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps),
so a suspend appears as a large gap rather than a discontinuity. Whether ClipShift pads hours of silence or ends
the recording is a policy question for the mid-recording-failure ticket, not a technical one.

**Do not try to correct QPC's own frequency offset.** It is common-mode across audio and video and cancels for
sync purposes (§1.3). Correcting it would only affect absolute duration, which nobody is checking.

## 4. Device enumeration, identity, and lifecycle

### 4.1 Enumeration

`IMMDeviceEnumerator::EnumAudioEndpoints(EDataFlow, DWORD dwStateMask, IMMDeviceCollection**)` takes `eRender`,
`eCapture` or `eAll`; `dwStateMask` is an OR of `DEVICE_STATE_*`, and "To include all endpoints, regardless of
state, set dwStateMask = DEVICE_STATEMASK_ALL"
([reference](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-enumaudioendpoints)).
For ClipShift: enumerate `eRender` for the loopback slot, `eCapture` for the input slot.

One trap, documented in [Device Properties](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties):
`GetValue` "succeeds and returns S_OK if PKEY_Device_FriendlyName is not found. In this case varName.vt is set to
VT_EMPTY." Check `vt`, not just the `HRESULT`.

### 4.2 Endpoint ID stability — documented precisely

`IMMDevice::GetId` itself only says the string is opaque: "Clients should treat the contents of the endpoint ID
string as opaque… the string format is undefined and might change from one implementation of the MMDevice API
system module to the next"
([reference](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-getid)).

The persistence contract lives on [Endpoint ID Strings](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-id-strings),
and it answers the ticket's question directly:

> The lifetime of an endpoint ID string is tied to the device installation. The endpoint ID string of a device
> changes if the user upgrades the device driver, or if the user uninstalls the device, and installs it again.
> However, the endpoint ID string remains unchanged across system restarts, and the endpoint ID string of a USB
> audio device remains unchanged if the user unplugs the device and plugs it back in.

| Event | ID survives? |
| --- | --- |
| Reboot | **Yes** — documented |
| USB unplug / replug | **Yes** — documented, explicitly for USB audio |
| Driver upgrade | **No** — documented to change |
| Uninstall / reinstall | **No** — documented to change |

The same page gives uniqueness: the ID "uniquely identifies the device among all audio endpoint devices in the
system… If a system contains two or more identical audio adapter devices, the corresponding audio endpoint devices
will have identical friendly names, but each endpoint device will have a unique endpoint ID string."

That last clause is why friendly name cannot be the key: two identical capture cards collide.

### 4.3 Which key to persist

- **`PKEY_Device_FriendlyName`** — display only, explicitly non-unique across identical adapters.
- **`PKEY_AudioEndpoint_GUID`** — documented unique, but its stated purpose is DirectSound interop, and
  **no Microsoft page documents any persistence guarantee for it**
  ([reference](https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-guid)).
- **`PKEY_Device_InstanceId`** — "The value can also be acquired via IMMDevice::GetId method"
  ([Device Properties](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties)), i.e. the
  same value, not an independent stabler handle.

**Persist `GetId()`** as the primary key — it is what `IMMDeviceEnumerator::GetDevice` consumes and what the
notification callbacks hand you, and it is the only value with a documented persistence contract. Persist the
friendly name alongside it as a display label and as a soft re-match hint for the driver-upgrade case that
rotates the ID. Treat name re-matching as explicitly ambiguous.

### 4.4 Re-resolving an absent device

The states, from [DEVICE_STATE_XXX Constants](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-state-xxx-constants):
`ACTIVE` (present and, if jack-detected, plugged in), `DISABLED` (disabled in Mmsys.cpl), `NOTPRESENT` (adapter
removed or disabled in Device Manager), `UNPLUGGED` ("the audio adapter that contains the jack… is present and
enabled, but the endpoint device is not plugged into the jack").

The load-bearing rule from the same page: **"a client can open a stream… only on a device that is in the
DEVICE_STATE_ACTIVE state."**

Crucially, `GetDevice(storedId)` **succeeds for non-active devices** — it returns `E_NOTFOUND` only when "The
device ID does not identify an audio device that is in this system"
([reference](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-getdevice)).
So resolution succeeding is not the same as the device being usable. **ClipShift must call `IMMDevice::GetState()`
and check for `ACTIVE` before treating a stored selection as available**, otherwise the failure surfaces later as
an obscure `Activate`/`Initialize` error.

Recommended ladder: `GetDevice(storedId)` → on `E_NOTFOUND`, the device was uninstalled or its driver upgraded, so
fall back to friendly-name match over a `DEVICE_STATEMASK_ALL` enumeration, then to the default endpoint → on
`S_OK` but non-`ACTIVE` state, **keep** the stored selection (it is still the user's intent, and a Mmsys.cpl
disable is reversible) but surface it as unavailable.

### 4.5 Defaults and roles

`GetDefaultAudioEndpoint` takes an `ERole`: `eConsole` ("Games, system notification sounds, and voice commands"),
`eMultimedia` ("Music, movies, narration, and live music recording"), `eCommunications` ("Voice communications")
([ERole](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/ne-mmdeviceapi-erole)).

**Store "Default" as a sentinel, never as a resolved ID.** Resolving at pick-time would silently pin the
selection and break default-follows behaviour. OBS encodes this as the literal string `"default"` and branches on
it, choosing `eCommunications` for input and `eConsole` for output. ClipShift should copy that.

**UNSETTLED:** the [GetDefaultAudioEndpoint](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-getdefaultaudioendpoint)
Remarks still carry Vista-era text claiming "the system assigns all three device roles… to that device. Thus,
GetDefaultAudioEndpoint always selects the default rendering or capture device, regardless of which role is
indicated by the role parameter." Modern Windows *does* expose a separate Communications default in Mmsys.cpl, so
this text appears stale, but Microsoft has not updated it. Settle by setting a distinct Communications default on
Windows 11 and comparing the two role queries.

### 4.6 Device change notification — the threading rules matter

`IMMNotificationClient` has five callbacks: `OnDefaultDeviceChanged`, `OnDeviceAdded`, `OnDeviceRemoved`,
`OnDeviceStateChanged`, `OnPropertyValueChanged`
([reference](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient)).
You receive **all** events for **all** devices and must filter yourself
([Device Events](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-events)). Adding or removing an
adapter "generates device events for all of the audio endpoint devices that connect to the adapter" — expect
bursts.

The documented restrictions, verbatim:

> - The methods of the interface must be nonblocking. The client should never wait on a synchronization object
>   during an event callback.
> - To avoid dead locks, the client should never call IMMDeviceEnumerator::RegisterEndpointNotificationCallback or
>   IMMDeviceEnumerator::UnregisterEndpointNotificationCallback in its implementation of IMMNotificationClient
>   methods.
> - The client should never release the final reference on an MMDevice API object during an event callback.

In .NET terms the first rule bans `lock`, `SemaphoreSlim.Wait`, `Task.Wait`/`.Result`, and any blocked `async`
continuation inside a callback.

There is a second .NET-specific hazard. Registration **does not take a reference**:
"These methods do not call the client's IMMNotificationClient::AddRef and IMMNotificationClient::Release
implementations. The client is responsible for maintaining the reference count"
([RegisterEndpointNotificationCallback](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-registerendpointnotificationcallback)).
A managed callback object must be **kept rooted for the entire registration window** or the GC will collect it out
from under the audio service. This is a real crash, not a theoretical one.

**Correct shape:** copy the ID string, cheap non-blocking filter, signal an event, return `S_OK`. Do all
re-resolution and stream rebuilding on your own worker thread. This is exactly what OBS does — its handler ends in
`SetEvent(restartSignal)` and nothing else, with the actual work on a dedicated reconnect thread.

Two notes on OBS as a model here. It ignores `OnDeviceStateChanged`/`Added`/`Removed` entirely and recovers from
device loss purely by treating `AUDCLNT_E_DEVICE_INVALIDATED` from the capture loop as a reconnect trigger.
ClipShift needs **both**: notifications to keep the UI device list fresh, and `DEVICE_INVALIDATED` handling for
capture-loop resilience. And OBS's own notifier takes a `std::mutex` inside the callback, which contradicts the
documented "never wait on a synchronization object" rule — it gets away with it because contention is
near-zero. Do not copy that part; use an immutable subscriber snapshot instead.

**UNSETTLED:** whether calling `GetDevice` from inside a callback is genuinely safe. Microsoft's own
`CMMNotificationClient` sample in [Device Events](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-events)
does exactly that (and even `CoInitialize`s inside the callback path, implying the callback thread is not
guaranteed COM-initialised), which sits badly against the nonblocking rule. The safe design does not need the
answer — copy the ID and get out.

---

## 5. Coexistence with OBS

This is a hard requirement, and the primary sources answer it favourably.

### 5.1 Shared mode is many-clients, many-processes

"In shared mode, several clients can share the captured stream from an audio hardware device. In exclusive mode,
one client has exclusive access to the captured stream from the device."
([User-Mode Audio Components](https://learn.microsoft.com/en-us/windows/win32/coreaudio/user-mode-audio-components))
That settles **the microphone case outright**: ClipShift and OBS can both capture the same mic concurrently, so
long as both use `AUDCLNT_SHAREMODE_SHARED`.

The same page is explicit that this spans processes: "the client shares the audio hardware with other applications
running in other processes."

The admission rule, from [IAudioClient::Initialize](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize):
**"An attempt to create a shared-mode stream can succeed only if the audio device is already operating in shared
mode or the device is currently unused. An attempt to create a shared-mode stream fails if the device is already
operating in exclusive mode."** OBS holding a shared-mode stream *is* the condition under which ClipShift's
shared-mode open is documented to succeed.

### 5.2 Loopback is shared-mode-only, and copied per client

Loopback cannot be exclusive (§2.1), so the exclusive-mode contention path does not exist for it. And the
mechanism is a per-client copy, not a single-consumer tap:
"When the hardware does not support a loopback pin, WASAPI copies the output stream from the audio engine into the
loopback application's capture buffer, in addition to copying the audio data to the hardware's render pin."
([Loopback Recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording))

**UNSETTLED, and worth being honest about:** no Microsoft page states in so many words that *N* simultaneous
loopback clients on one render endpoint are supported. The quoted sentence says "the loopback application's" —
singular. The inference from shared-mode semantics plus copy-per-client is strong, and OBS plus any of the dozens
of shipping loopback recorders coexist in practice, but it is an inference.

The same page notes the implementation "depend[s] on the capabilities of the hardware", and the
hardware-loopback-pin path is the one where a single physical pin could plausibly be contended.

**This is the highest-value empirical test in this document.** Run OBS with a Desktop Audio source on endpoint X
while ClipShift opens a second shared-mode loopback stream on the same X; confirm both receive non-silent,
non-degraded data. Ideally on a device with a hardware loopback pin and one without. It is roughly a 30-minute
test and it de-risks the whole architecture.

### 5.3 What actually breaks it — and it is not OBS

OBS **never uses exclusive mode**. Both `Initialize` call sites in `win-wasapi.cpp` pass
`AUDCLNT_SHAREMODE_SHARED`. So OBS can never lock ClipShift out. The requirement is symmetric: **ClipShift must
likewise never request `AUDCLNT_SHAREMODE_EXCLUSIVE`**, or it becomes the app that preempts OBS mid-stream.

The real risk is a *third* application. From [Exclusive-Mode Streams](https://learn.microsoft.com/en-us/windows/win32/coreaudio/exclusive-mode-streams),
on the "Allow applications to take exclusive control of this device" / "Give exclusive mode applications priority"
checkboxes:

> If preemption is enabled, a request by an application to take exclusive control of the device succeeds if the
> device is currently not in use, or if the device is being used in shared mode, but the request fails if another
> application already has exclusive control of the device.

Windows' default posture is exclusive-allowed with preemption enabled. So any app that opens the endpoint
exclusively — some DAWs, ASIO wrappers, certain "audiophile" players — will preempt **both** ClipShift and OBS
mid-recording. The symptom is `AUDCLNT_E_DEVICE_INVALIDATED` or `AUDCLNT_E_RESOURCES_INVALIDATED` from the capture
loop.

Relevant error codes, from the [Initialize](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)
return table:

| Code | Meaning | Reachable by a shared-mode-only ClipShift? |
| --- | --- | --- |
| `AUDCLNT_E_DEVICE_IN_USE` | "the device is being used in exclusive mode, or… the caller asked to use the device in exclusive mode" | Only if a third app holds it exclusively |
| `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED` | User disabled exclusive mode | No — requires asking for exclusive |
| `AUDCLNT_E_DEVICE_INVALIDATED` | Unplugged / reconfigured / disabled / removed | **Yes — expect this in the field** |
| `AUDCLNT_E_RESOURCES_INVALIDATED` | Stream suspended, or an exclusive stream disconnected | **Yes** |
| `AUDCLNT_E_SERVICE_NOT_RUNNING` | Windows audio service not running | Yes — worth a friendly message |
| `AUDCLNT_E_UNSUPPORTED_FORMAT` | Format not supported by the engine | No, if driven from `GetMixFormat` |

Note that a shared-mode caller meeting a shared-mode-occupied device is **not** in that table at all. That
absence is itself meaningful.

Treat `DEVICE_INVALIDATED` as a routine reconnect trigger rather than a fatal error — which is what OBS does,
silently routing it to reconnect without even logging a warning.

---

## 6. Per-process loopback — not the MVP, but it shapes the model

The MVP ships whole-system loopback. This section exists so the source model is not painted into a corner.

Primary sources: the four `audioclientactivationparams.h` reference pages, the SDK header as vendored in
[microsoft/win32metadata](https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/audioclientactivationparams.h),
and Microsoft's [ApplicationLoopback sample](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/ApplicationLoopback/cpp)
(note: the real source is under `Samples/ApplicationLoopback/cpp/`).

### 6.1 The mechanism

Activation goes through `ActivateAudioInterfaceAsync` against a magic pseudo-device path, not through `IMMDevice`:

```c
#define VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK L"VAD\\Process_Loopback"
typedef enum PROCESS_LOOPBACK_MODE {
    PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0,
    PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1
} PROCESS_LOOPBACK_MODE;
```

`AUDIOCLIENT_ACTIVATION_PARAMS` carries an `ActivationType` plus a union whose only current member is
`AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS { DWORD TargetProcessId; PROCESS_LOOPBACK_MODE ProcessLoopbackMode; }`
([reference](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_activation_params)).
It is passed as a `PROPVARIANT` of type **`VT_BLOB`** — the interop detail most likely to be got wrong from .NET,
since the struct must be pinned in native memory for the duration of the call.

**The pseudo-device is not an endpoint.** There is no `IMMDevice`, no endpoint ID, no friendly name, no
`IMMNotificationClient`. Microsoft states the upside directly: "the capture is not tied to a specific audio
endpoint, eliminating the need to create a separate IAudioClient to capture from each physical audio endpoint"
([sample page](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)).
A consequence worth noting: a process-loopback capture is **immune** to the user switching output device
mid-recording, where a device-loopback capture pinned to the old endpoint would not be.

**`GetMixFormat` does not work on this client — you must hardcode a format.** The sample hardcodes 44100/16/2 and
carries a comment claiming `GetMixFormat` would also work. That comment is a known defect: a Microsoft engineer
has stated on the record that the process-loopback `IAudioClient` is internally `CMixerClient`, on which
`GetMixFormat()` and `IsFormatSupported()` both return `E_NOTIMPL`
([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/1125409/loopbackcapture-(-activateaudiointerfaceasync-with)).
Source-tier caveat: that is a Microsoft-employee answer, not reference documentation — but it is the only
Microsoft-authored statement on the question and it matches the sample's actual behaviour.

The format works anyway because the sample passes **`AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM`**, which inserts "a
channel matrixer and a sample rate converter… as necessary to convert between the uncompressed format supplied to
IAudioClient::Initialize and the audio engine mix format"
([flags reference](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants)).
The sample's exact flag set:

```cpp
m_AudioClient->Initialize(AUDCLNT_SHAREMODE_SHARED,
    AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
    /* | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY // for better resampling quality */,
    0, 0, &m_CaptureFormat, nullptr);
```

Note `AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY` is commented out in the sample but documented as: "a sample rate
converter with better quality than the default conversion but with a higher performance cost… This should be used
if the audio is ultimately intended to be heard by humans as opposed to other scenarios such as pumping silence or
populating a meter." **ClipShift is exactly the "heard by humans" case — enable it.**

Both duration parameters must be `0`: "For a shared-mode stream that uses event-driven buffering, the caller must
set both hnsPeriodicity and hnsBufferDuration to 0"
([Initialize](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)).

**Apartment matters for .NET.** The completion handler must be agile — `ActivateAudioInterfaceAsync` docs say it
"needs to implement IAgileObject to ensure that there is no deadlock when the completionHandler is called from the
MTA. Otherwise, an E_ILLEGAL_METHOD_CALL will occur"
([reference](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync)) —
and the sample derives from `FtmBase` accordingly. Mixing STA `CoInitialize` with this API is reported to produce
intermittent `RPC_E_CHANGED_MODE`
([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/1120985/failed-to-call-activateaudiointerfaceasync-hresult)).
Since ClipShift's UI thread is STA, **activation must happen off the UI thread on an MTA thread.**

Errors arrive at **two** levels and both must be checked independently: the synchronous `HRESULT` from
`ActivateAudioInterfaceAsync`, and the separate `hrActivateResult` out-parameter from
`IActivateAudioInterfaceAsyncOperation::GetActivateResult`. The sample checks both.

### 6.2 Version floor — the headline, and it is not what "Windows 10 build 20348" suggests

All four reference pages carry an identical requirements table: **Minimum supported client: Windows 10 Build
20348**, with *Minimum supported server* blank. The SDK header gates on `#if (NTDDI_VERSION >= NTDDI_WIN10_FE)`,
and `NTDDI_WIN10_FE` is `0x0A00000A` — the 20348 branch.

The arithmetic that matters:

- **Windows 10 client topped out at build 19045** (22H2, the final client release)
  ([release information](https://learn.microsoft.com/en-us/windows/release-health/release-information)).
- **Build 20348 is Windows Server 2022**
  ([server release info](https://learn.microsoft.com/en-us/windows/release-health/windows-server-release-info)).

So **no shipping Windows 10 client can ever run process loopback** — Microsoft never released a Windows 10 client
at or above 20348. Windows 10 2004/20H1 (19041) is well below the floor. In practice the feature is
**Windows 11 and newer** (plus Server 2022).

Gate on `Build >= 20348` — the literal documented contract, which correctly admits Server 2022 — rather than on an
"is Windows 11" check. Read the build via `RtlGetVersion` or a manifested path so compatibility shims do not lie.

**Documentation inconsistency, recorded for completeness:** the `ActivateAudioInterfaceAsync` page says "Starting
with Windows 10 Build **20438**", against 20348 on four struct pages, the sample, the README, and the header gate.
Almost certainly a digit transposition. It does not change the conclusion — both are above every Windows 10 client
build.

### 6.3 Process tree semantics

`TargetProcessId` is "The ID of the process for which the render streams, and the render streams of its child
processes, will be included or excluded"
([reference](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params)).

For a browser this is the whole point: Chrome, Edge and Firefox render audio in child renderer or audio-service
processes, not the main process. Targeting the **top-level browser PID with INCLUDE** is correct; targeting a
single renderer PID would capture one tab.

**UNSETTLED, and this is the most important gap:** no Microsoft source specifies whether the tree is evaluated
once at activation or continuously (i.e. whether a child spawned *after* `Initialize` is picked up), whether
"child" means direct children or the full transitive closure, or how re-parenting and PID reuse are handled. The
wording says "its child processes" — singular generation on its face — while the sample and its docs consistently
say "process tree", implying transitive. Chrome's audio service is typically a *grandchild*, so this distinction
is load-bearing. Must be tested before any per-app feature ships.

### 6.4 Failure modes — mostly undocumented

| Question | Status |
| --- | --- |
| Silent target delivers silence | **Settled** — "the capturing process receives silence" |
| Invalid / nonexistent PID | **UNSETTLED** — no documented behaviour |
| Target exits mid-capture | **UNSETTLED** — no documented event or HRESULT |
| Elevated target, non-elevated capturer | **UNSETTLED** — integrity levels never mentioned |
| Capability / manifest / privacy toggle required | **UNSETTLED**, leaning no — consent prompt is documented only for microphones |
| Is `AUDCLNT_BUFFERFLAGS_SILENT` set during silent stretches | **UNSETTLED** — the sample ignores `dwCaptureFlags` entirely |

Two of these deserve emphasis. First, given the documented "no render streams → silence" rule, there is a real
possibility that **an invalid or already-exited PID activates successfully and yields an infinite silent stream**
rather than an error — the worst possible UX (a 40-minute recording of nothing). ClipShift must validate the PID
itself before activation rather than relying on the API to reject it.

Second, the documented recovery path for device errors does not apply at all here.
[Recovering from an Invalid-Device Error](https://learn.microsoft.com/en-us/windows/win32/coreaudio/recovering-from-an-invalid-device-error)
is entirely about re-enumerating via `IMMDeviceEnumerator`, which does not exist on this path.
**ClipShift will need its own process-exit watcher as the authoritative signal**, because WASAPI probably will not
tell you.

Also worth not inheriting: a reported reproducible crash in the sample on Windows 11 22H2 where `GetBuffer`'s
returned pointer is invalidated when an exclusive-fullscreen game is Alt-Tabbed away from, plus a genuine
start/stop race in the sample's state guard
([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/1036316/bug-in-activateaudiointerfaceasync-loopback-captur)).
Alt-Tabbing out of a fullscreen game is precisely the ClipShift use case.

### 6.5 The one piece of very good news

**Downstream of `Initialize`, the two paths are byte-for-byte identical.** The sample's post-`Initialize` sequence
is `GetBufferSize` → `GetService(IID_IAudioCaptureClient)` → `SetEventHandle` → `Start` → drain loop on
`GetNextPacketSize`/`GetBuffer`/`ReleaseBuffer` → `Stop`. There is not one process-loopback-specific line in its
capture loop.

That is what makes the additive design genuinely feasible, and it tells you exactly where the seam goes.

---

## 7. The audio source model

Everything above converges on this. The recommendation is a discriminated union over a **selector**, resolved
through a fallible step into an **activator**, with the seam at "produce an initialised `IAudioClient`".

### 7.1 Identity

```
AudioSource =
  | Endpoint(endpointId: string, friendlyName: string)   // friendlyName is display + soft re-match only
  | Process(selector: ProcessSelector, mode: LoopbackMode)
```

**`mode` is part of the identity, not a setting.** `{PID 1234, INCLUDE}` and `{PID 1234, EXCLUDE}` are entirely
different sources — one narrow, one system-wide-minus-one. Do not flatten it into a capture option.

**Never persist a raw PID.** PIDs are aggressively reused by Windows, so a stale PID is not merely invalid — it
may resolve to a different, live process. Persist instead, in priority order: normalised main module path
(what a user means by "record Spotify"), executable base name (survives app updates that relocate under versioned
directories — Discord and Spotify both do this), and AUMID / package family name for Store apps.

There is also no enumeration API for process sources; Microsoft's guidance is literally "use Task Manager or the
tlist program to get this ID". ClipShift would need its own picker, ideally filtered to processes that actually
have render streams via `IAudioSessionManager2` / `IAudioSessionControl2::GetProcessId`.

### 7.2 The four things to build into the MVP even though it ships device-only

Each of these costs nothing for the device path and saves a redesign for the process path.

**1. Resolution is a separate, fallible step from activation.** `AudioSource → Resolve() → ResolvedTarget` must be
able to return *not found*, *unique*, or **ambiguous** (three top-level `chrome.exe` processes — which tree?).
The device path makes this look trivial, but note it is fallible there too: §4.4 established that
`GetDevice` succeeding does not mean the device is `ACTIVE`. If the MVP conflates "the user's saved source" with
"the thing I can capture right now", the process path has nowhere to put not-running and ambiguous.

**2. Format is an input to the source, not an output of it.** The device path *discovers* its format via
`GetMixFormat`; the process path has no such call and must be *told*. Give each implementation
`NegotiateFormat(desired) → actual`. The natural device-first design — "ask the source what format it is, then
build the pipeline around it" — inverts a dependency that then has to be un-inverted everywhere downstream.
Invert it now.

**3. Activation returns a `Task`.** The device path is synchronous; the process path is fundamentally
callback-driven. The Microsoft sample papers over this by blocking on an event, but a .NET app with a UI should
not block a thread waiting on an MTA callback. A synchronous MVP `Activate` forces every caller to change later.

**4. Invalidation is an event with a reason, not an HRESULT.** Define
`SourceInvalidated(reason: DeviceRemoved | DefaultChanged | ProcessExited | Unknown)`. Devices raise it from
`IMMNotificationClient` plus HRESULT inspection; processes raise it from a wait on the process handle. Do not
couple invalidation detection to `AUDCLNT_E_*` codes — the process path may never produce one (§6.4).

### 7.3 The seam

```
interface IAudioSourceActivator {
    WaveFormat        NegotiateFormat(WaveFormat desired);
    Task<Activation>  ActivateAsync(WaveFormat format);   // -> IAudioClient + IAudioCaptureClient
    event SourceInvalidated;
}
```

`DeviceLoopbackActivator` and `DeviceInputActivator` are the MVP implementations, both via
`IMMDeviceEnumerator` → `IMMDevice::Activate`. `ProcessLoopbackActivator` slots in later via
`ActivateAudioInterfaceAsync` with **zero change below the seam**.

Everything below the seam — the MMCSS work queue, the drain loop, buffer-flag handling, QPC timestamping,
silence padding, encoder feed, file writing — is written once and shared verbatim. That is the whole point, and
§6.5 is the evidence it holds.

This also fits the standing constraint from the map ("Internally the model is N audio sinks, each to its own
file; the UI exposes two named slots") cleanly: a *sink* is a source plus a file writer, and the MVP simply never
constructs a `Process` source.

---

## 8. Input (microphone) capture

### 8.1 Shared-mode format negotiation

Two things `GetMixFormat` genuinely guarantees
([reference](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getmixformat),
[Device Formats](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-formats)):

1. **Shape.** It "always uses a WAVEFORMATEXTENSIBLE structure, instead of a stand-alone WAVEFORMATEX structure".
   You may reinterpret the pointer — but check `cbSize >= 22` anyway, since that guarantee is prose.
2. **Acceptance.** "the Initialize method always accepts the stream format obtained from a GetMixFormat call on
   the same device." Round-tripping `GetMixFormat` → `Initialize` never fails on format. Build on this.

**UNSETTLED: the mix format is not documented to be float32.** Every relevant sentence is hedged or scoped to the
engine's *internals* — "the audio engine **might** use a mix format that represents samples as floating-point
values"; "The audio engine represents sample values **internally** as floating-point numbers". No primary source
states that the returned `SubFormat` is always `KSDATAFORMAT_SUBTYPE_IEEE_FLOAT` at 32 bits. Float32 is
overwhelmingly typical and is what OBS assumes. **ClipShift must branch on the actual `SubFormat` and
`wBitsPerSample` at runtime, not assert.**

Sample rate and channel count come from the *user*, not from you — Device Formats documents that the shared-mode
format is what the user picked in `mmsys.cpl` → Properties → Advanced → "Default Format". A mic can hand you
44100 mono or 48000 stereo depending on OEM defaults. Do not hardcode.

**`IsFormatSupported` in shared mode** has three outcomes: `S_OK` (exact, `*ppClosestMatch` NULL), `S_FALSE`
(closest match returned — **you must `CoTaskMemFree` it**), `AUDCLNT_E_UNSUPPORTED_FORMAT` (nothing close)
([reference](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-isformatsupported)).
The return-value *table* on that page is misleading — it describes `AUDCLNT_E_UNSUPPORTED_FORMAT` in exclusive-mode
terms. Trust the Remarks.

**Only sample representation is converted for you — not rate, not channel count.** Device Formats: an app "can
rely on the audio engine to perform **only limited format conversions**… the format for an application stream
typically must have the same number of channels and the same sample rate as the stream format used by the
device." **Assume no free sample-rate conversion.** Take the mix format as-is and convert in managed code where
you control quality and can log it.

**`hnsBufferDuration`:** `hnsPeriodicity` must be 0 in shared mode. For **event-driven** shared mode, "the caller
must set **both** hnsPeriodicity and hnsBufferDuration to 0"
([Initialize](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)).
Note **OBS violates this** — it passes a 5-second `BUFFER_TIME_100NS` with `EVENTCALLBACK`. It evidently works,
but ClipShift should follow the doc and pass 0. A larger capture buffer costs no latency anyway: "For a capture
stream, the latency through the buffer is determined solely by the separation between the engine's write pointer
and the client's read pointer." Always call `GetBufferSize` afterwards — the requested size is a floor.

**Avoid `AUTOCONVERTPCM` / `SRC_DEFAULT_QUALITY` on the capture path.** Their capture behaviour is **UNSETTLED** —
the constants page restricts `LOOPBACK` and `RATEADJUST` to rendering devices explicitly but says nothing either
way about these two, and `Initialize` never mentions them. More importantly, using them puts an undocumented
in-engine resampler between the hardware and your file for four hours, which destroys your ability to reason about
drift because the SRC absorbs it invisibly. **Capture at the mix format and resample yourself.** (They are
correct and necessary for *process loopback* — §6.1 — where there is no mix format to query.)

**Event-driven vs polling. UNSETTLED, and the docs cut against the recommendation.** Microsoft's canonical
capture sample *polls* with `Sleep(duration/2)`
([Capturing a Stream](https://learn.microsoft.com/en-us/windows/win32/coreaudio/capturing-a-stream)), and no
primary sentence says "prefer events for capture". OBS uses events. **Use events** — a `Sleep` loop is at the
mercy of timer resolution and degrades badly under load over four hours.

### 8.2 Stream teardown is a scheduled event, not an edge case

A running stream's format is **fixed at `Initialize`**; a format change tears the stream down.
`IAudioSessionEvents::OnSessionDisconnected` has a dedicated reason, `DisconnectReasonFormatChanged`
([reference](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nf-audiopolicy-iaudiosessionevents-onsessiondisconnected)),
after which "many of the methods in the WASAPI interfaces… return error code AUDCLNT_E_DEVICE_INVALIDATED".

The full documented cause list, from
[Recovering from an Invalid-Device Error](https://learn.microsoft.com/en-us/windows/win32/coreaudio/recovering-from-an-invalid-device-error):
device removed; audio service shut down; preferred stream format changed; user logged off the WTS session; WTS
session disconnected; the shared-mode session was disconnected to free the device for an exclusive-mode
connection.

**Over a 4-hour session this is a scheduled event, not a rare edge case.** A user changing the sample rate in
Sound control panel, unplugging a USB mic, or another app grabbing the device exclusively all kill the stream.

Prescribed recovery: release `IAudioClient` and every interface acquired from it → re-activate on the same device
→ if that fails, prompt for another device. **Re-call `GetMixFormat` after re-activation** — the whole point of
`DisconnectReasonFormatChanged` is that the format is now different.

Register `IAudioSessionEvents` rather than polling, because "the notifications arrive asynchronously… even when
the stream is not running, the application will still receive timely notification". You need **both**
`IMMNotificationClient` and `IAudioSessionEvents`, and
[Stream Routing](https://learn.microsoft.com/en-us/windows/win32/coreaudio/stream-routing-implementation-considerations)
warns: "the order in which the application receives device-change and session-disconnect notifications cannot be
predicted. The application must implement notification handling to receive these notifications in any order."

That same page contains the single most relevant paragraph in the doc set for a recorder:

> When an existing audio stream is interrupted and opened on the new device, rendering on the new device must
> start at the position at which the stream was stopped on the old device. To do this, the application must have
> the last known device position, to calculate the start position on the new device… During the transition, the
> application must ensure that the clock does not get out of synchronization, resulting in out-of-sync audio and
> video streams.

**Cache the last known device position and QPC before teardown, and splice on the QPC master timeline, not on
frame counts** — device position resets to 0 on the new stream.

### 8.3 Getting a genuinely raw microphone

By default a shared-mode capture stream runs through the OEM's APO chain. The available effects are exactly the
ones ClipShift wants gone: Acoustic Echo Cancellation, Noise Suppression, Deep Noise Suppression, Automatic Gain
Control, Beam Forming, Constant Tone Removal, Dynamic Range Compression
([Audio Signal Processing Modes](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-signal-processing-modes)).

Three constraints, stated flatly on that page:

> Applications do not have the option to change the mapping between an audio category and a signal processing
> mode. Applications have no awareness of the concept of an 'audio processing mode'. They cannot find out what
> mode is used for each of their streams.
>
> Applications have no visibility into how many modes are present, with the exception of RAW/non-RAW.

**The category is your only lever.** Valid categories for a *capture* stream are restricted to
`AudioCategory_Communications`, `AudioCategory_Speech`, `AudioCategory_Other`
([AUDIO_STREAM_CATEGORY](https://learn.microsoft.com/en-us/windows/win32/api/audiosessiontypes/ne-audiosessiontypes-audio_stream_category));
loopback streams may only use `AudioCategory_Other`.

**Use `AudioCategory_Other`.** Never tag a mic capture `AudioCategory_Communications` — that is the documented
route into AEC/NS/AGC.

**`AUDCLNT_STREAMOPTIONS_RAW`**, verbatim
([reference](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-audclnt_streamoptions)):

> The audio stream is a "raw" stream that bypasses all signal processing except for endpoint specific, always-on
> processing in the Audio Processing Object (APO), driver, and hardware.

Note the carve-out — raw is **not** a hardware bypass. But the decisive guarantee is a *driver requirement*,
flagged Important on the signal-processing-modes page:

> **Raw capture streams must not include any time varying or adaptive processing, such as echo control, automatic
> gain control, or noise suppression. The only audio processing permitted in raw capture is linear equalization to
> flatten frequency response.**

That is exactly what ClipShift needs: whatever survives raw mode is guaranteed LTI, so it cannot pump, duck or
gate. It may still be an EQ curve you did not ask for.

Caveats, all documented:

- **Not always available.** "If `System.Devices.AudioDevice.RawProcessingSupported` is **false**, applications
  cannot set the 'use RAW' flag." ClipShift needs a fallback and should surface whether raw was actually obtained.
- **Microsoft discourages it**, and one reason is a genuine risk:
  "**The capture signal might come in a format that the application can't understand.**"
  ([Low Latency Audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)) On a
  device whose non-raw APO was downmixing or normalising, raw mode can hand you a different channel count.
  **Call `GetMixFormat` *after* `SetClientProperties`, not before.**
- **Ordering is load-bearing.** `SetClientProperties` must be called "after activation completes, but before the
  call to `Initialize`". A bad category surfaces as `E_INVALIDARG` from `Initialize`.

`AudioClientProperties` needs `cbSize = sizeof(props)`; `bIsOffload` is a render concept and irrelevant here
([reference](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ns-audioclient-audioclientproperties-r1)).

**Verify at runtime with `IAudioEffectsManager` (Windows 11, build 22000+).** It "allow[s] applications to get the
current list of effects, set the state of effects, and to register for notifications when the list of effects or
effect states change", obtained via `IAudioClient::GetService`
([reference](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-iaudioeffectsmanager)).
`GetAudioEffects` returns `AUDIO_EFFECT` entries with `id`, `state` and `canSetState`. **ClipShift should log the
effect list at the start of every recording** — that is the documented, supported way to prove the mic stream is
clean rather than hoping.

The legacy `PKEY_AudioEndpoint_Disable_SysFx` is a diagnostic breadcrumb only: it applies "only to the
local-effects and global-effects APOs that were installed by the .inf file", it is endpoint-wide rather than
per-stream, and Audio Endpoint Properties says "Clients can read these properties, but **should not set them**".
**UNSETTLED:** nothing formally deprecates it, and no primary source maps the Sound control panel's "Enable audio
enhancements" checkbox to any API. Do not build logic claiming to reflect that checkbox.

One aside that matters if ClipShift ever wants post-volume desktop audio:
`AUDCLNT_STREAMOPTIONS_POST_VOLUME_LOOPBACK` exists because "The default behavior is for the loopback stream to be
tapped **before** volume and/or mute". **By default, loopback ignores the system volume slider** — usually what a
recorder wants, but worth knowing it is a choice.

**OBS does none of this.** There are zero occurrences of `IAudioClient2`, `IAudioClient3`,
`SetClientProperties`, `AudioClientProperties`, `AUDCLNT_STREAMOPTIONS` or `AUDIO_STREAM_CATEGORY` in
`win-wasapi.cpp` or `libobs/audio-monitoring/win32/wasapi-output.c`. **OBS mic capture is subject to whatever the
OEM APO chain does.** That is a genuine differentiator for ClipShift, not a reason to copy OBS.

---

## 9. Device clock and drift — the honest answer

### 9.1 The audio device clock is NOT the QPC clock

This is the paragraph that settles the ticket's drift question, from
[`IAudioClock::GetFrequency`](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getfrequency):

> The device frequency is **the frequency generated by the hardware clock in the audio device**…
>
> **If the clock generated by an audio device runs at a nominally constant frequency, the frequency might still
> vary slightly over time due to drift or jitter with respect to a reference clock. The reference clock might be a
> wall clock or the system clock used by the QueryPerformanceCounter function. The GetFrequency method ignores
> such variations and simply reports a constant frequency. However, the position reported by the
> IAudioClient::GetPosition method takes all such variations into account to report an accurate position value
> each time it is called.**

Three consequences, stated plainly:

1. **The audio device clock is a separate hardware oscillator**, and Microsoft explicitly names QPC as a
   *reference clock it drifts against*.
2. **`GetFrequency` is a nominal constant and is therefore a lie about the real rate.** It returns 48000 forever
   even if the hardware runs at 48000.4 Hz.
3. **`GetPosition` is the truth** — it "takes all such variations into account".

So over 4 hours, `frameCount / nSamplesPerSec` **will** diverge from QPC-elapsed, monotonically and by design.
This is not a bug that can be configured away.

Discard `S_FALSE` readings from any drift estimator — `GetPosition` "can return S_FALSE instead of S_OK if the
method succeeds but the duration of the call is long enough to detract from the accuracy of the reported
position."

### 9.2 How big is the drift

Microsoft quantifies crystal behaviour in the
[QPC doc](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps), and
audio endpoints use the same class of part: "Crystal oscillators that are used in personal computers and servers
are typically manufactured with a frequency tolerance of **± 30 to 50 parts per million**, and rarely, crystals
can be off by as much as 500 ppm."

At ±50 ppm, **a 4-hour session accumulates roughly ±0.72 s per oscillator pair**. Two devices can drift in
opposite directions — the doc's own Example 3 makes exactly this point at 24-hour scale. A worst-case USB mic vs.
motherboard render pair over 4 hours can plausibly reach **~1.5 s of desync with zero correction**, entirely
within spec. For ClipShift's brief that is catastrophic.

Note that Windows partially calibrates *its own* timer at boot ("recent versions of Windows… use multiple hardware
timers to detect the frequency offset and compensate for it"). **No equivalent statement exists anywhere for audio
endpoint clocks.**

### 9.3 Two endpoints do not share a clock

No primary source says they do, and Microsoft's own code corrects for the fact that they don't. From the
[AEC System Filter](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/aec-system-filter) docs:

> The AEC system filter correctly handles **mismatches between the clocks for the capture and render streams**,
> and separate devices can be used for capture and rendering.

That is Microsoft stating in a shipping-component doc that capture and render endpoints have mismatched clocks and
that correction had to be implemented. Corroborated by
[`KSPROPERTY_RTAUDIO_CLOCKREGISTER`](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-clockregister):
"Software uses clock registers to synchronize between two or more controller devices by **measuring the relative
drift between the hardware clocks** of the device."

**A USB mic and the motherboard render device are independent clock domains and will drift relative to each other
over hours.**

**UNSETTLED:** WASAPI exposes no concept of "clock domain" and no way to ask whether two endpoints are
clock-locked — even though two endpoints on the same HD Audio codec very likely share an oscillator. Treat every
endpoint as its own domain and measure. If two turn out to be locked, the measured rates simply agree and nothing
is lost.

### 9.4 No API reports the real rate — you must measure it

From user mode via WASAPI, **there is none**:

- `IAudioClock::GetFrequency` explicitly refuses ("ignores such variations").
- `IAudioClockAdjustment::SetSampleRate` only *sets*, and requires `AUDCLNT_STREAMFLAGS_RATEADJUST`, which
  "is valid **only for a rendering device**. Otherwise the GetService call fails with the error code
  AUDCLNT_E_WRONG_ENDPOINT_TYPE"
  ([flags](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants)).
- `KSPROPERTY_RTAUDIO_CLOCKREGISTER` exists but is a kernel-streaming property on a KS pin, not reachable from
  WASAPI, and fails "if the audio hardware does not support a clock register".

**So ClipShift must measure drift itself, from the `(pu64DevicePosition, pu64QPCPosition)` pairs it already
receives on every packet.** That is the only supported path, and it is sufficient: a linear regression of device
frames against QPC over a few minutes yields the endpoint's true rate to well under 1 ppm — far tighter than the
~50 ppm being corrected.

Gate the regression on the flags from §1.4: fit only over clean packets, treat `DATA_DISCONTINUITY` as a **segment
boundary** (it is precisely the flag saying frames were lost and the frame-count-to-wall-clock mapping just
stepped), and drop `TIMESTAMP_ERROR` packets from the fit.

One honest caveat about how much to trust individual timestamps, from the driver side
([Low Latency Audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)):

> In devices that have complex DSP pipelines and signal processing, **calculating an accurate timestamp may be
> challenging and should be done thoughtfully**… Between the driver and DSP, calculate a correlation between the
> Windows performance counter and the DSP wall clock. Procedures for this can range from simple (but less
> precise) to fairly complex or novel (but more precise).

`pu64QPCPosition` is itself a *driver-computed estimate* correlating two oscillators, and its quality varies by
vendor. **Accurate enough to regress a rate from over minutes; jittery enough that individual packet timestamps
should be filtered, not trusted point-wise.** This is very likely the real reason OBS distrusts device timing on
mic input (§1.5) — and it argues for regression over per-packet correction regardless.

### 9.5 `IAudioClock2` and `IAudioClient3`

`IAudioClock2::GetDevicePosition` gets position "in frames, **directly from the hardware**", but note "The
sampling rate of the device endpoint may be different from the sampling rate of the mix format used by the
client." It counts the *endpoint's* frames, not yours. For ClipShift's timeline you want the client-side view, so
the per-packet `GetBuffer` values are the right primitive; `GetDevicePosition` is a useful **diagnostic for
detecting hidden in-engine resampling** (if the two disagree in rate, something is converting).

`IAudioClient3` changes *how often you are woken*, not *what clock ticks* — irrelevant to drift. There is a
positive reason to **avoid** requesting a small period: "if one application requests the usage of small buffers…
**all applications that use the same endpoint and mode will automatically switch to that small buffer size**"
([Low Latency Audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)).
ClipShift would degrade every other app's power consumption for four hours in exchange for latency it does not
need. Use plain `IAudioClient::Initialize` with `hnsBufferDuration = 0`. `IAudioClient2` — for
`SetClientProperties` — is the interface you actually want.

### 9.6 Audio vs. the video clock

**UNSETTLED:** no Microsoft document describes any locking or shared derivation between the audio engine clock and
the display/GPU/compositor clock, and there is no API to query such a relationship.

The practical resolution is §1.2: **QPC is the only clock both paths speak.** Video hands you QPC natively; audio
hands you QPC alongside every packet. QPC is ClipShift's master timeline, and **audio is the stream that must be
rate-corrected onto it.**

---

## 10. .NET reachability

I verified this against NAudio's actual source rather than its documentation, because the answer turned out to
differ from the obvious assumption in a way that matters.

### 10.1 NAudio: the idiomatic API discards the timestamps; the low-level one does not

**The low-level wrapper is complete.** `NAudio.Wasapi/CoreAudioApi/AudioCaptureClient.cs` exposes a five-out
overload that surfaces everything ClipShift needs, and returns a raw `IntPtr` with no copy:

```csharp
public IntPtr GetBuffer(
    out int numFramesToRead,
    out AudioClientBufferFlags bufferFlags,
    out long devicePosition,
    out long qpcPosition)
```

([source](https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/CoreAudioApi/AudioCaptureClient.cs)). The
underlying `[ComImport]` interface declaration is a faithful 1:1 mapping of the native signature
([source](https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/CoreAudioApi/Interfaces/IAudioCaptureClient.cs)).

**The idiomatic API throws them away.** `WasapiCapture.ReadNextPacket` calls the *two*-argument overload:

```csharp
IntPtr buffer = capture.GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags);
```

([`WasapiCapture.cs` line 297](https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/WasapiCapture.cs)) — and
that overload passes `out _, out _` for device position and QPC position, discarding both. Since
`WasapiLoopbackCapture` is a four-method subclass of `WasapiCapture` that only overrides the stream flags
([source](https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/WasapiLoopbackCapture.cs)), **the entire
`DataAvailable` event surface — the thing you would naturally reach for — is unusable for ClipShift's sync
requirement.** A `WaveInEventArgs` carries a byte buffer and a length. There is nowhere for a timestamp to go.

**Allocation, precisely.** Better than I expected in one respect and worse in another:

- The capture buffer is allocated **once** at initialisation (`recordBuffer = new byte[bufferFrameCount *
  bytesPerFrame]`, line 168), not per packet. Good.
- But `DataAvailable?.Invoke(this, new WaveInEventArgs(recordBuffer, recordBufferOffset))` (lines 306 and 323)
  allocates a **`WaveInEventArgs` per packet**, and `WaveInEventArgs` is a `class`
  ([source](https://github.com/naudio/NAudio/blob/master/NAudio.Core/Wave/WaveInputs/WaveInEventArgs.cs)). At a
  10 ms engine period that is ~100 small gen-0 allocations per second per sink, or ~1.4 million over a 4-hour
  session with two sinks. Not catastrophic — they die in gen 0 — but it violates the map's standing constraint
  that "the capture and encode hot path must avoid per-frame managed allocation", and it is entirely avoidable.
- It also does a `Marshal.Copy` from the WASAPI buffer into `recordBuffer` (line 313), an extra copy ClipShift
  does not need if it feeds the encoder or file writer from the native pointer directly.

Credit where due: `WasapiCapture` **does** handle `AUDCLNT_BUFFERFLAGS_SILENT` correctly, zero-filling via
`Array.Clear` (line 317) rather than dropping the packet — the right behaviour per §1.4.

**Conclusion on NAudio.** The honest framing is not "NAudio can't do this". It is: **using NAudio correctly for
ClipShift means bypassing `WasapiCapture` entirely and driving `AudioClient` / `AudioCaptureClient` yourself** —
at which point NAudio is functioning purely as a hand-maintained interop layer, and should be compared against
generated interop on interop-quality grounds, not on convenience. That is a real and defensible option; it is
just not the option most people think they are choosing when they pick NAudio.

### 10.2 What the interop layer has to cover

Whichever route is chosen, the surface ClipShift needs is larger than `IAudioCaptureClient`. Collected from the
sections above:

| Interface / API | Needed for | Section |
| --- | --- | --- |
| `IMMDeviceEnumerator`, `IMMDevice`, `IMMDeviceCollection`, `IPropertyStore` | enumeration, IDs, friendly names, state | §4.1–4.4 |
| `IMMNotificationClient` | device add/remove/default-change | §4.6 |
| `IAudioClient` | shared-mode init, event handle, start/stop | §2.1, §8.1 |
| `IAudioClient2::SetClientProperties` | raw mode, stream category | §8.3 |
| `IAudioCaptureClient` | the capture loop and both timestamps | §1.1 |
| `IAudioSessionEvents` | `DisconnectReasonFormatChanged`, teardown notice | §8.2 |
| `IAudioEffectsManager` (Win11 22000+) | verifying no AGC/NS/AEC on the mic | §8.3 |
| `ActivateAudioInterfaceAsync`, `AUDIOCLIENT_ACTIVATION_PARAMS`, `IActivateAudioInterfaceCompletionHandler` | per-process loopback, later | §6.1 |
| `IAudioClock` / `IAudioClock2` | drift diagnostics | §9.5 |

Two of these — `IAudioEffectsManager` and the process-loopback activation types — are recent enough that a
hand-maintained library is unlikely to have them. NAudio does not appear to expose either.

### 10.3 Constraints on the interop itself

A prior finding in this project records that **.NET 8 CsWinRT COM interop must use vtable calls or
`MarshalInterface`, never a cast of `__ComObject`**. That constraint is specific to the CsWinRT/WinRT projection
path. The Core Audio interfaces here are classic COM, not WinRT, so the natural routes are:

- **Source-generated COM** via `[GeneratedComInterface]` in `System.Runtime.InteropServices.Marshalling`, which
  produces `ComWrappers`-based marshalling with no built-in COM interop and is AOT-friendly.
- **Classic `[ComImport]`** interfaces, which is what NAudio uses.

**UNSETTLED:** I did not independently verify how completely `Microsoft.Windows.CsWin32` covers these specific
Core Audio interfaces, nor confirm from primary sources whether the `__ComObject` constraint has an analogue on
the `ComWrappers`/`GeneratedComInterface` path. Both are worth a short spike before committing, and neither
changes the architectural recommendation — they change only which of two interop mechanisms is used behind the
same seam (§7.3).

I also did not evaluate **CSCore**; it was assigned but the finding did not arrive in time. Its maintenance
status in particular is unverified here, and given the recency of `IAudioEffectsManager` and process-loopback
activation I would not expect it to help. **UNSETTLED.**

### 10.4 A zero-allocation capture loop is achievable, on any of these routes

Nothing in WASAPI forces managed allocation in the hot path. `GetBuffer` hands back a native pointer into the
endpoint buffer; the loop can read from it via `Span<byte>`/`ReadOnlySpan<byte>` over that pointer and hand it
straight to the encoder or file writer without a copy and without an event-args object. The allocations in
NAudio's `WasapiCapture` are a design choice of that wrapper, not a property of the API.

The constraint from `GetBuffer`'s Remarks that shapes the loop is timing, not allocation: "Clients should avoid
excessive delays between the GetBuffer call that acquires a packet and the ReleaseBuffer call… Clients that delay
releasing a packet for more than one period risk losing sample data." So the loop must not do file I/O
synchronously between `GetBuffer` and `ReleaseBuffer` — copy or hand off to a pre-allocated ring buffer, release,
then write. This matters more than allocation does, and it is also what makes the crash-survivability requirement
tractable.

---

## 11. Recommendation

### 11.1 Capture mechanism

**Raw WASAPI interop, one shared-mode event-driven `IAudioClient` per sink.** Concretely, per audio sink:

1. Resolve the persisted selector to an `IMMDevice`; **verify `GetState() == DEVICE_STATE_ACTIVE`** (§4.4).
2. `IMMDevice::Activate` → `IAudioClient`, then QI `IAudioClient2`.
3. `SetClientProperties` **before** `Initialize`: `eCategory = AudioCategory_Other`; for the mic sink set
   `Options |= AUDCLNT_STREAMOPTIONS_RAW` **if** `RawProcessingSupported` is true, else proceed without and record
   that fact (§8.3).
4. `GetMixFormat` — **after** `SetClientProperties`, since raw mode can change the format (§8.3). Branch on the
   actual `SubFormat` and `wBitsPerSample`; do not assume float32 (§8.1).
5. `Initialize(AUDCLNT_SHAREMODE_SHARED, EVENTCALLBACK [| LOOPBACK], 0, 0, mixFormat, null)`. Both durations
   **0** for shared event-driven mode (§8.1). Never exclusive — it is forbidden for loopback and would preempt
   OBS (§5.3).
6. `SetEventHandle`, `GetService(IAudioCaptureClient)`, `Start`.
7. Drain loop per event: `GetNextPacketSize` / `GetBuffer(..., &flags, &devicePos, &qpcPos)` / hand off /
   `ReleaseBuffer`. **Always request both timestamps.** Never do file I/O between `GetBuffer` and `ReleaseBuffer`
   (§10.4).
8. Register `IMMNotificationClient` and `IAudioSessionEvents`; treat `AUDCLNT_E_DEVICE_INVALIDATED` as a routine
   reconnect trigger (§5.3, §8.2).

**Additionally, for the loopback sink:** run a continuous silence renderer on the same endpoint for the lifetime
of the recording (§2.2), and implement QPC-delta gap detection regardless (§3).

**On the library question:** NAudio's low-level `AudioClient`/`AudioCaptureClient` wrappers are sufficient and
correct; its idiomatic `WasapiCapture`/`WasapiLoopbackCapture` API is not, because it discards both timestamps
(§10.1). Either drive NAudio's low-level types directly or generate the interop — the seam in §7.3 makes this
swappable, so it need not be settled to start.

### 11.2 Source model

**A discriminated union over a persisted selector, resolved through a fallible step, activated behind a seam at
"produce an initialised `IAudioClient`"** (§7). The four things to build now, all free for the device path:

1. Resolution is a **separate, fallible step** returning not-found / unique / ambiguous.
2. Format is an **input** to the source (`NegotiateFormat(desired)`), not an output of it.
3. Activation returns a **`Task`**.
4. Invalidation is an **event with a reason enum**, not an HRESULT.

Per-process loopback then becomes a new class behind the existing interface, because everything downstream of
`Initialize` is byte-identical between the two paths (§6.5). Gate it on **build ≥ 20348**, which in practice means
Windows 11 — no Windows 10 client can ever run it (§6.2).

### 11.3 The three findings that most change the design

**QPC is the common timebase and no clock correlation step is needed.** Audio packets carry `pu64QPCPosition`;
both candidate video paths stamp frames with QPC. This is a much better starting position than the brief assumed
(§1.2).

**But the audio endpoint runs on its own crystal, and no API will tell you its real rate.** `GetFrequency`
explicitly "ignores such variations and simply reports a constant frequency" (§9.1). At typical ±30–50 ppm, an
uncorrected 4-hour session drifts on the order of half a second to a second and a half per endpoint pair (§9.2),
and two endpoints are independent clock domains (§9.3). **ClipShift must measure rate from the per-packet
`(devicePosition, qpcPosition)` pairs and correct.** This is the single most important consequence for the sync
ticket.

**Silence is a first-class concern, not an edge case.** `AUDCLNT_BUFFERFLAGS_SILENT` means "ignore the data
values", not "no data" — those frames are real, counted, and must be written as silence or the timeline shifts
(§1.4). And endpoint loopback appears to stop delivering entirely when the system is idle — evidenced by OBS's
workaround, undocumented by Microsoft (§2.2).

---

## 12. What I could not settle from primary sources

Listed in rough order of how much it would hurt to be wrong, with the test that would settle each.

### High stakes — settle before locking the architecture

**1. Two processes loopback-capturing the same render endpoint simultaneously.** (§5.2) Microsoft documents
shared mode as multi-client/multi-process and loopback as shared-mode-only and copy-per-client, but never states
outright that *N* loopback clients on one endpoint are supported — and the one relevant sentence says "the
loopback application's capture buffer", singular. The inference is strong and matches universal practice, but it
is an inference, and the doc warns the implementation "depend[s] on the capabilities of the hardware".
*Test:* run OBS with Desktop Audio on endpoint X while ClipShift opens a second shared-mode loopback stream on X;
confirm both get non-silent, non-degraded data. Ideally on a device with a hardware loopback pin and one without.
~30 minutes, and it de-risks the entire brief.

**2. Whether endpoint loopback stops delivering during a long silent stretch.** (§2.2) Microsoft documents this
for *process* loopback ("the capturing process receives silence") and says **nothing at all** for endpoint
loopback. The only evidence is OBS's `ClearBuffer` workaround and its comment, and that workaround runs *once at
init* — it is not proof that a continuous silence renderer is unnecessary, nor that it is sufficient.
*Test:* start endpoint loopback on a silent machine, log whether `GetNextPacketSize` returns non-zero over
several minutes, with and without a silence renderer running. This should be the implementation ticket's first
task. The mitigation in §3 (QPC-delta gap detection) makes ClipShift correct either way, which is why it is
recommended unconditionally.

**3. Whether the mic's device QPC timestamp is trustworthy.** (§1.5) OBS defaults device timing **on** for
loopback and **off** for input, with no comment and no primary source explaining why. Either some capture drivers
stamp badly, or it is a legacy default nobody revisited — I cannot distinguish these from sources.
*Test:* log `pu64QPCPosition` deltas against `pu64DevicePosition` deltas on the reference mic for an hour and see
whether they track. This directly determines whether the mic sink can use the same sync strategy as the loopback
sink.

### Medium stakes — settle before the per-app feature, not before the MVP

**4. Process-tree depth and dynamism.** (§6.3) Transitive closure or direct children only? Are children spawned
*after* `Initialize` picked up? The docs say "its child processes" (singular generation) while the sample and its
page say "process tree". Chrome's audio service is typically a grandchild, so this decides whether browser
capture works at all.

**5. Behaviour on an invalid or exited target PID.** (§6.4) Undocumented. Given the documented silence rule, a
bad PID may well *activate successfully and stream silence forever* rather than erroring — the worst UX outcome.
ClipShift should validate PIDs independently regardless of how this resolves.

**6. Elevated target from a non-elevated capturer.** (§6.4) Completely undocumented — integrity levels are never
mentioned. An elevated game or Discord is an ordinary streamer situation.

**7. Whether `AUDCLNT_BUFFERFLAGS_SILENT` is set during process-loopback silent stretches.** (§6.4) The
Microsoft sample ignores `dwCaptureFlags` entirely and is no guide.

### Not settled because I ran out of scope, not because sources disagree

**7a. CSCore was not evaluated.** (§10.3) Its per-buffer allocation behaviour, whether it surfaces the
`GetBuffer` timestamps, and its maintenance status are all unverified here. Given how recent
`IAudioEffectsManager` and the process-loopback activation types are, I would not expect it to close any gap
NAudio leaves, but that is an expectation, not a finding.

**7b. `Microsoft.Windows.CsWin32` coverage of the Core Audio interfaces was not verified.** (§10.3) Nor did I
confirm from primary sources whether this project's recorded CsWinRT `__ComObject` constraint has an analogue on
the `ComWrappers` / `[GeneratedComInterface]` path. Worth a short spike; it changes only which interop mechanism
sits behind the §7.3 seam, not the architecture.

**7c. The mix format is not documented to be float32.** (§8.1) Every relevant sentence is hedged or scoped to the
engine's internals. Branch on `SubFormat`/`wBitsPerSample` at runtime rather than asserting.

**7d. `AUTOCONVERTPCM` / `SRC_DEFAULT_QUALITY` behaviour on *capture* streams.** (§8.1) The docs restrict
`LOOPBACK` and `RATEADJUST` to render devices explicitly but say nothing either way about these two. Recommended
avoided on the capture path for independent reasons.

**7e. Whether Microsoft recommends event-driven over polling for capture.** (§8.1) No primary sentence says so,
and Microsoft's own canonical sample polls. Events recommended on engineering grounds, not documentary ones.

**7f. `AUDCLNT_STREAMOPTIONS_AMBISONICS` is undocumented** — the constants table lists the value with an empty
description cell. Irrelevant here; recorded for completeness.

### Low stakes — documentation staleness, unlikely to bite

**8. Whether `ERole` is still collapsed on Windows 11.** (§4.5) `GetDefaultAudioEndpoint`'s Remarks carry
Vista-era text claiming all three roles always move together, but modern Windows exposes a separate Communications
default in Mmsys.cpl. The text appears stale and un-updated.

**9. Whether calling `GetDevice` inside an `IMMNotificationClient` callback is safe.** (§4.6) The documented rules
ban blocking; a cross-apartment COM call can block; Microsoft's own sample does it anyway. The recommended design
(copy the ID and get out) does not need the answer.

**10. Whether `PKEY_AudioEngine_DeviceFormat` and `GetMixFormat` can differ for a shared-mode loopback stream.**
(§2.1) `IAudioClient::Initialize` says loopback data is in the former; every real implementation uses the latter
and works.

**11. Whether `PKEY_AudioEndpoint_GUID` persists across a driver update.** (§4.3) Documented unique, but no page
documents any persistence guarantee. Do not assume it outlives a driver update just because the endpoint ID
does not either.

**12. Whether Windows 11-era privacy UI intercepts process loopback.** (§6.4) The relevant docs predate that UI
(dated 2021-01-21) and mention consent only for microphones.

### Recorded documentation defects

- `ActivateAudioInterfaceAsync` says build **20438**; four struct pages, the sample, the README and the SDK
  header's `NTDDI_WIN10_FE` gate all say **20348**. Digit transposition; immaterial to the conclusion. (§6.2)
- The ApplicationLoopback sample's comment claiming `GetMixFormat` works on a process-loopback client is wrong
  per a Microsoft engineer's own statement. (§6.1)
- `GetDefaultAudioEndpoint` documents the same failure as both `E_NOTFOUND` (return table) and `ERROR_NOT_FOUND`
  (Remarks prose). Check `FAILED(hr)`, not equality against one constant. (§4.5)
