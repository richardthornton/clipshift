# Making the recording indicator invisible to capture

Research for [issue #7](https://github.com/richardthornton/clipshift/issues/7).
Question: how does ClipShift show an on-screen recording indicator that the user can see
but the recording cannot — and does that choice constrain the display capture API?

Investigated 2026-08-11 against Microsoft Learn, Windows SDK metadata, first-party
Microsoft source (PowerToys, Windows-classic-samples), OBS Studio source, and direct
measurement on the reference machine.

---

## Verdict

**`SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` excludes the window from BOTH
DXGI Desktop Duplication and Windows.Graphics.Capture, identically.**

**The indicator therefore does NOT constrain the display capture API choice.** The
assumption made during charting — that the indicator forces a WGC-based architecture — is
**false**. The sibling ticket choosing between DXGI Desktop Duplication and
Windows.Graphics.Capture is free to decide on its own merits (adapter affinity, HDR,
cursor handling, latency, per-monitor vs per-window). The indicator is orthogonal.

This was verified two ways: from Microsoft's own shipping source, and by direct
measurement of both APIs capturing the same window at the same instant (§3).

---

## 1. What the API actually is

`SetWindowDisplayAffinity(HWND hWnd, DWORD dwAffinity)` — [Microsoft Learn][swda].

| Value | Numeric | Documented meaning |
| --- | --- | --- |
| `WDA_NONE` | `0x00000000` | "Imposes no restrictions on where the window can be displayed." |
| `WDA_MONITOR` | `0x00000001` | "The window content is displayed only on a monitor. Everywhere else, **the window appears with no content**." |
| `WDA_EXCLUDEFROMCAPTURE` | `0x00000011` | "The window is displayed only on a monitor. Everywhere else, **the window does not appear at all**. One use for this affinity is for windows that show video recording controls, so that the controls are not included in the capture. Introduced in Windows 10 Version 2004." |

Numeric values confirmed independently in Microsoft's Win32 metadata:
[`microsoft/windows-rs`, `metadata/win32/winuser.rdl`][rdl] — `WDA_EXCLUDEFROMCAPTURE = 17`
(`0x11`), `WDA_MONITOR = 1`, `WDA_NONE = 0`. Note `0x11` has the `WDA_MONITOR` bit set,
which is why the down-level degradation in §2 is to `WDA_MONITOR` rather than to nothing.

Two remarks from the same page matter:

> "This feature enables applications to protect their own onscreen window content from
> being captured or copied through **a specific set of public operating system features
> and APIs**. However, **it works only when the Desktop Window Manager (DWM) is composing
> the desktop**."

> "It is important to note that unlike a security feature or an implementation of Digital
> Rights Management (DRM), there is no guarantee that using SetWindowDisplayAffinity …
> will strictly protect windowed content, for example where someone takes a photograph of
> the screen."

The docs deliberately do **not** enumerate which APIs are in that "specific set" — which
is exactly why the question needed settling empirically rather than by reading. The
important structural point is that **enforcement lives in DWM**, not in either capture
API. That is the mechanism by which both capture paths inherit the same behaviour.

Note also the signature constraint: *"A handle to the **top-level** window. The window
must belong to the **current process**."* The indicator must be a top-level HWND owned by
ClipShift. `GetWindowDisplayAffinity` additionally documents that it "succeeds only when
the window is layered and Desktop Window Manager is composing the desktop"
([Learn][gwda]) — a layered overlay satisfies this.

---

## 2. Version floor

Documented: **Windows 10 version 2004**, i.e. build **19041** ([Learn][swda]).

> "Starting in Windows 10 Version 2004, WDA_EXCLUDEFROMCAPTURE is a supported value.
> Setting the display affinity to WDA_EXCLUDEFROMCAPTURE on previous version of Windows
> will behave as if **WDA_MONITOR** is applied."

This is a silent, dangerous degradation: on older builds the call **succeeds** and the
window becomes a **black rectangle** in every capture instead of being absent. OBS treats
this as a hard version gate rather than trusting the call —
[`frontend/utility/platform-windows.cpp`][obs-gate]:

```cpp
/* this has to be version gated as setting WDA_EXCLUDEFROMCAPTURE on
   older Windows builds behaves like WDA_MONITOR (black box) */

if (GetWindowsVersion() > 0x0A00 || GetWindowsVersion() == 0x0A00 && GetWindowsBuild() >= 19041)
        supported = true;
```

For ClipShift (Windows 11 only) this is moot in practice, but the check is cheap and the
failure mode — a black box burned into a 4-hour recording — is severe enough to justify
keeping it. **Gate on build ≥ 19041 and, below that, do not show the on-screen indicator
at all.**

---

## 3. The central question, settled by measurement

### 3.1 Method

A purpose-built .NET 8 harness on the reference machine:

- **OS:** Windows 11 25H2, build **10.0.26200**
- **GPU / display:** NVIDIA GeForce RTX 5060 Ti, `\\.\DISPLAY1`, desktop rect (0,0)–(1920,1080)

It creates a top-level overlay HWND with
`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE`,
painted solid magenta `#FF00FF` via `UpdateLayeredWindow` (`ULW_ALPHA`, `AC_SRC_ALPHA`),
at a fixed rect. It then captures the **same monitor** through **both** APIs concurrently
from the same D3D11 device — `IDXGIOutputDuplication::AcquireNextFrame` (DDA) and
`Direct3D11CaptureFramePool` + `GraphicsCaptureSession` created via
`IGraphicsCaptureItemInterop::CreateForMonitor` (WGC) — and reads back the pixel at the
centre of the overlay rect while stepping the affinity value.

A baseline sample with the window hidden establishes the true desktop colour underneath,
so "absent" can be distinguished from "black".

### 3.2 Result — same process

```
Duplicating output '\\.\DISPLAY1' on adapter 'NVIDIA GeForce RTX 5060 Ti'
Windows.Graphics.Capture session started on the same monitor.

   window HIDDEN (baseline background) -> #945A36

1. affinity=none      DDA=#945A36  WGC=#FF00FF   (DDA one sample behind)
2. affinity=excl      DDA=#945A36  WGC=#945A36   window ABSENT in both
3. affinity=none      DDA=#FF00FF  WGC=#FF00FF   window VISIBLE in both
4. affinity=monitor   DDA=#000000  WGC=#000000   BLACK RECTANGLE in both
5. affinity=none      DDA=#FF00FF  WGC=#FF00FF   window VISIBLE in both
6. affinity=excl      DDA=#945A36  WGC=#945A36   window ABSENT in both
```

### 3.3 Result — cross-process (the OBS case)

Display affinity is documented in terms of an app protecting *its own* window, so the
same-process test above could in principle have been lenient. Repeating it with the
overlay owned by **one** process and both captures running in a **separate** process
(different PID, no relationship to the overlay's owner):

```
[captor] separate process, PID 29236
  x18  owner-state=hidden  DDA=#7F7F7F WGC=#7F7F7F
  x49  owner-state=none    DDA=#FF00FF WGC=#FF00FF     visible to both, cross-process
  x42  owner-state=excl    DDA=#7F7F7F WGC=#7F7F7F     absent from both, cross-process
  x50  owner-state=hidden  DDA=#7F7F7F WGC=#7F7F7F
```

(Intermediate rows where the sampled background changed value were tracked identically by
both APIs — a useful consistency check that the two samplers agree pixel-for-pixel.)

### 3.4 Conclusion

| Affinity | DXGI Desktop Duplication | Windows.Graphics.Capture |
| --- | --- | --- |
| `WDA_NONE` | window visible | window visible |
| `WDA_MONITOR` | **black rectangle** | **black rectangle** |
| `WDA_EXCLUDEFROMCAPTURE` | **absent — background shows through** | **absent — background shows through** |

Both APIs, both same-process and cross-process, behave identically. **The exclusion is a
DWM composition property of the window, not a feature of either capture API.**

### 3.5 Corroboration from first-party source

Microsoft ships a recorder that does exactly this. **PowerToys ZoomIt** records video via
**Windows.Graphics.Capture** (`CaptureFrameWait.h` includes
`winrt/Windows.Graphics.Capture.h`; `Direct3D11CaptureFramePool` is used in
`CaptureFrameWait.cpp`, and the header notes the code is *"derived from
https://github.com/robmikh/capturevideosample"*) — and marks its on-screen furniture
`WDA_EXCLUDEFROMCAPTURE`:

- [`WebcamPreviewWindow.h`][pt-webcam] — *"Shows a live on-screen preview of the webcam
  overlay while recording. The window is marked WDA_EXCLUDEFROMCAPTURE **so it never
  appears in the recorded video**."*
- [`SelectRectangle.h`][pt-selrect] — `SetExcludeFromCapture(bool)` toggling between
  `WDA_EXCLUDEFROMCAPTURE` and `WDA_NONE`, on a border window whose comment reads
  *"Signal that recording is actively capturing frames. Changes the border to a thick red
  frame so the user can clearly [see]"* — i.e. **literally ClipShift's requirement**,
  implemented by Microsoft, this way.
- [`MirrorWindow.cpp`][pt-mirror] and [`MeasureToolCore/OverlayUI.cpp`][pt-measure] do the
  same for their overlays.
- [`ColorPickerUI/Helpers/WindowCaptureExclusionHelper.cs`][pt-cs] is a **C# / .NET**
  reference P/Invoke for this exact call, with `WDA_EXCLUDEFROMCAPTURE = 0x00000011`
  declared in [`NativeMethods.cs`][pt-nm].

And OBS applies `WDA_EXCLUDEFROMCAPTURE` to its windows **once, globally**, with no
branch on which capture backend is in use ([`OBSBasic.cpp`][obs-affinity]) — while
shipping *both* a DXGI-duplication and a WGC monitor-capture path
([`duplicator-monitor-capture.c`][obs-choose]). If the flag only worked for one of them,
that feature would be visibly broken for half its users.

---

## 4. Interaction with the capture APIs' own exclusion features

**Windows.Graphics.Capture has no public per-window or per-display exclusion list.** The
documented surface of [`GraphicsCaptureSession`][wgc-session] is:
`IsCursorCaptureEnabled` (added in 19041), `IsBorderRequired`, `IncludeSecondaryWindows`,
`MinUpdateInterval`, `DirtyRegionMode`, `ConfigurationIteration`, plus
`StartCapture`/`Close`/`IsSupported`. None of these exclude a window. `IsBorderRequired`
suppresses the *yellow capture border*, not content, and requires
`GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)` first —
OBS does exactly that ordering in [`winrt-capture.cpp`][obs-wgc].

**Desktop Duplication has no exclusion API at all.** [`IDXGIOutputDuplication`][ddapi]
duplicates a whole output; there is no filter.

OBS uses **no** WGC exclusion mechanism anywhere — a sweep of `libobs-winrt/` for
`Exclude`/`TryRemove`/`AddWindow`/`RemoveWindow` returns nothing. Its answer to "hide my
own windows" is `SetWindowDisplayAffinity`, full stop.

**So there is nothing to compose.** `SetWindowDisplayAffinity` is the only mechanism, it
sits below both APIs, and it is the same mechanism regardless of which one ClipShift
picks.

---

## 5. Side effects

### 5.1 Does it show as a black rectangle to OBS? — No.

This is the practically important one, since OBS will be capturing the same screen
simultaneously. Measured in §3.3 from a genuinely separate process: with
`WDA_EXCLUDEFROMCAPTURE` the captured pixels are the **desktop content behind the
overlay**, not black. That is the documented distinction — `WDA_MONITOR` makes the window
"appear with no content" (black), `WDA_EXCLUDEFROMCAPTURE` makes it "not appear at all".

**Consequence for ClipShift: the indicator will be invisible in OBS's capture too**, and
in Discord screenshares, Teams, Steam, and anything else scraping the desktop. It is
invisible to *all* capture, not selectively to ClipShift's own. That is almost certainly
what the user wants (the indicator is for the person at the desk, not the audience) — but
it should be a deliberate, stated design position, not a surprise. There is no mechanism
to be visible to *other* capture consumers while hidden from ClipShift's own.

### 5.2 New finding: switching directly between `WDA_MONITOR` and `WDA_EXCLUDEFROMCAPTURE` silently does nothing

Not documented anywhere I could find, and reproducible on this machine in **both** capture
APIs:

```
1. affinity=none      DDA=#945A36  WGC=#FF00FF
2. affinity=monitor   DDA=#000000  WGC=#000000   black, as expected
3. affinity=excl      DDA=#000000  WGC=#000000   STILL BLACK
4. affinity=excl      DDA=#000000  WGC=#000000   STILL BLACK
5. affinity=none      DDA=#FF00FF  WGC=#FF00FF   reset
6. affinity=excl      DDA=#945A36  WGC=#945A36   now correctly absent
```

`SetWindowDisplayAffinity` returns `TRUE` and `GetWindowDisplayAffinity` reads back the
new value — the API reports success while the composition behaviour does not change. It is
stuck in **either** direction (`excl` → `monitor` likewise stays "absent" rather than
becoming black).

Characterised further:

- Destroying and recreating the **duplication session** does *not* clear it → it is not a
  capture-session cache.
- Destroying and recreating the **window** does clear it → the stale state is bound to the
  HWND's DWM composition state.
- Setting `WDA_NONE` in between does clear it.

**Rule for ClipShift: never set `WDA_MONITOR` on the indicator window, and if affinity is
ever toggled, always pass through `WDA_NONE` between values.** The natural implementation
(set `WDA_EXCLUDEFROMCAPTURE` once at window creation, never change it) is unaffected.

### 5.3 Requirements and other costs

- **Top-level, own-process HWND** — required by the API signature ([Learn][swda]).
- **DWM composition required.** Always true on Windows 8+; DWM cannot be disabled.
- **Transparency and click-through are unaffected.** The measured overlay was
  `WS_EX_LAYERED | WS_EX_TRANSPARENT` with per-pixel alpha via `UpdateLayeredWindow` and
  remained fully functional with the affinity set.
- **No evidence of a forced composition-path change or loss of hardware acceleration.**
  I found no Microsoft documentation of such a cost, and none was observable in this test.
  Stated as absence of evidence, not evidence of absence (§8).
- **Possible spurious capture wakeups.** A third-party report against Microsoft engineer
  robmikh's `Win32CaptureSample` ([issue #83][robmikh83]) says that from Windows **24H2**,
  `AcquireNextFrame` returns when an *excluded* window updates, even though its content is
  still not captured — a behaviour change from 23H2. If ClipShift drives its encoder off
  DDA frame arrivals, an animating indicator could generate redundant identical frames.
  Mitigation: keep the indicator **static** (no pulsing/animation) while recording, or
  de-duplicate on `LastPresentTime`/dirty rects. This is a third-party bug report, not a
  Microsoft statement, and I did not reproduce it — treat as a flag for the encode ticket,
  not a settled fact.

---

## 6. The overlay window itself

### 6.1 Click-through, always-on-top, per-pixel alpha in .NET

Documented recipe, all from Microsoft Learn:

- **Per-pixel alpha requires `UpdateLayeredWindow`**, not `SetLayeredWindowAttributes`.
  [Window Features][winfeat]: *"For faster and more efficient animation or if per-pixel
  alpha is needed, call UpdateLayeredWindow… after SetLayeredWindowAttributes has been
  called, subsequent UpdateLayeredWindow calls will fail until the layering style bit is
  cleared and set again."* Do not mix the two.
- **Click-through** is `WS_EX_LAYERED | WS_EX_TRANSPARENT`. [Window Features][winfeat]:
  *"Hit testing of a layered window is based on the shape and transparency of the window…
  However, if the layered window has the WS_EX_TRANSPARENT extended window style, the
  shape of the layered window will be ignored and the mouse events will be passed to other
  windows underneath."* Note the [Extended Window Styles][exstyles] reference defines
  `WS_EX_TRANSPARENT` only in terms of paint order — the input behaviour is documented
  solely in the layered-windows text above.
- **`WM_NCHITTEST` → `HTTRANSPARENT` is NOT an equivalent.** [Learn][nchittest] scopes it
  to *"a window currently covered by another window **in the same thread**"*. It will not
  pass clicks through to a game in another process. Use `WS_EX_TRANSPARENT`.
- **`WS_EX_NOACTIVATE`** (does not become foreground on click) and **`WS_EX_TOOLWINDOW`**
  (absent from taskbar and Alt+Tab) — [Extended Window Styles][exstyles]. Both wanted, so
  the indicator can never steal focus from a game.
- **Topmost**: `SetWindowPos(..., HWND_TOPMOST, ..., SWP_NOACTIVATE)`.
  [Learn][setwindowpos]: *"Places the window above all non-topmost windows. The window
  maintains its topmost position even when it is deactivated."*
- **.NET framework choice**: WinForms cannot do per-pixel alpha — `Form.TransparencyKey`
  is [colour-key only][transparencykey]. WPF can: [Technology Regions][techregions] states
  *"WPF supports non-rectangular windows by using Win32 APIs… **layered windows for a
  per-pixel alpha**"* and *"**WPF supports hardware accelerated layered windows**"*, with
  `AllowsTransparency=true` requiring `WindowStyle=None` ([Learn][allowstransparency]).
  The widely repeated claim that `AllowsTransparency` forces software rendering is **not**
  supported by current Microsoft documentation. Either WPF with the extended styles
  P/Invoked onto its `HwndSource`, or a raw Win32 `UpdateLayeredWindow` window, is viable.

Given the standing constraint that the hot path avoid per-frame managed allocation, and
that the indicator is a tiny static element, a **raw Win32 `UpdateLayeredWindow` window
built by P/Invoke** is the lower-risk option: no XAML/airspace concerns, no WPF render
thread, one DIB uploaded once.

### 6.2 Fullscreen-exclusive vs borderless

**Borderless / DWM-composed: yes, the overlay composites normally.** This is the ordinary
topmost-window case.

**True fullscreen-exclusive: Microsoft does not document it working, and the structural
evidence says it does not.** From the DirectX team's [Demystifying Fullscreen
Optimizations][fso]:

> "Fullscreen Exclusive mode gives your game complete ownership of the display and
> allocation of resources of your graphics card."

> "In order to create an overlay, the outside application would have to step into and
> intercept the rendering process… This process of intercepting the render and
> presentation process can cause problems including performance regressions, instability
> and issues with anti-cheat."

That is Microsoft stating that overlays under FSE historically required render-pipeline
hooking — a plain topmost HWND does not suffice. Corroborating, the [DXGI
overview][dxgiov] notes a swap chain *"will relinquish full-screen mode whenever its
output window is occluded by another window"*, and
[`SetFullscreenState`][setfullscreenstate] fails with `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE`
when *"The output window is occluded."* The documented interaction is that a covering
window **breaks** FSE rather than composing over it.

**But on Windows 11 this case is increasingly rare**, for two documented reasons:

1. **D3D12 has no FSE at all.** [Swap Chains (Direct3D 12)][d3d12swap]: *"Direct3D 12
   doesn't support full-screen exclusive mode (FSE)… in Direct3D 12
   IDXGISwapChain::SetFullscreenState doesn't enter full-screen exclusive mode, and simply
   changes resolutions and refresh rates to allow full-screen optimisations."*
2. **Fullscreen Optimizations intercepts older FSE.** [FSO blog][fso]: *"Fullscreen
   Optimizations takes full screen exclusive games and runs them instead in a highly
   optimized borderless windowed format… your game believes that it is running in
   Fullscreen Exclusive, but behind the scenes, Windows has the game running in borderless
   windowed mode."*

So the overlay works over: any D3D12 title, any borderless/flip-model title, and any
legacy FSE title with FSO left on (the default). It is **not** documented to work over a
pre-D3D12 title with FSO explicitly disabled in the compatibility tab plus a real
`SetFullscreenState(TRUE)`.

**Note this is not an indicator-specific problem** — it is the same constraint that
governs the Xbox Game Bar and every other overlay, and the same constraint that makes true
FSE awkward for the *capture* side too. It should be handled as a known, documented
limitation with a graceful degradation (the unconditional tray icon remains), not designed
around.

### 6.3 Cost

**Microsoft acknowledges a cost but never quantifies it.** [FSO blog][fso]:

> "When an overlay such as the Game Bar is present, the DWM reassumes control of the
> display, and **a slight performance overhead is incurred** so that the overlay can be
> composited on top of the game in a safe and stable way."

The mechanism is documented in [For best performance, use DXGI flip model][flipmodel]: a
game in Independent Flip has its frames sent *"directly to the screen, independently, with
the same efficiency as fullscreen exclusive"*, and when *"other desktop contents come on
top, the DWM can either seamlessly transition back to composed mode, efficiently 'reverse
compose' the contents on top of the application before flipping it, or **leverage MPO to
maintain the independent flip mode**."*

On the reference hardware (RTX 5060 Ti) MPO support is likely, which is the cheapest of
those three. But **no Microsoft source gives a selection rule or a number.** Microsoft's
own recommended way to find out is empirical — the flip-model page points at PresentMon.
**This is not settled and should be measured, not assumed** (§8).

Mitigating factor specific to ClipShift: the indicator is small, static, and already
competing with OBS, which is itself an overlay-adjacent consumer. The marginal cost of one
more small topmost layered window is plausibly negligible, but that is an expectation, not
a finding.

---

## 7. Recommended approach

1. **Single top-level Win32 overlay window**, created by ClipShift, one per… (see below),
   with extended styles
   `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE`
   and `WS_POPUP`. Content drawn once into a 32-bpp premultiplied-BGRA DIB and pushed with
   `UpdateLayeredWindow(..., ULW_ALPHA)` with `AC_SRC_ALPHA`. Raw P/Invoke, not WPF.
2. **Call `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` once, immediately after
   `CreateWindowEx` and before `ShowWindow`.** Never set `WDA_MONITOR`. If the value is
   ever changed, pass through `WDA_NONE` (§5.2).
3. **Gate on build ≥ 19041.** Below it, suppress the on-screen indicator entirely and rely
   on the tray icon — never risk the black-box degradation (§2).
4. **Verify, don't trust.** `SetWindowDisplayAffinity` returns `BOOL`; check it, and read
   back with `GetWindowDisplayAffinity`. Note that success does not guarantee correct
   behaviour (§5.2), so also treat this as a smoke-test target.
5. **Keep the indicator static while recording** — no animation or pulsing — to sidestep
   the 24H2 spurious-frame report (§5.3).
6. **Show it with `SW_SHOWNA`** so it never activates, and re-assert `HWND_TOPMOST` with
   `SWP_NOACTIVATE` when the recorded display's topology changes.
7. **Which display?** Not settled by this ticket. Placing it on the *recorded* display is
   the honest signal; placing it on a different display makes it visible to the user even
   over a true-FSE game. Worth a deliberate decision — recommend the recorded display,
   since exclusion works there and the indicator's purpose is to say "this screen is being
   recorded."
8. **Accept that the indicator is invisible to OBS too** (§5.1) and state it in the design.

---

## 8. What could not be settled from primary sources

Being explicit, because these are the places where the write-up above is weaker.

- **Whether a topmost layered window can appear over a true fullscreen-exclusive swap
  chain.** No Microsoft document states this outright either way. The conclusion "it
  cannot" is *inferred* from FSE being defined as complete display ownership, from the
  documented statement that overlays under FSE required render-pipeline interception, and
  from the documented behaviour that occlusion breaks FSE. I could not test it — no
  fullscreen-exclusive game was run.
- **The frame-rate cost of the overlay.** Microsoft's ceiling of specificity is *"a slight
  performance overhead is incurred."* No number, no threshold, and no documented rule for
  which of the three Independent-Flip fallbacks DWM picks. **Unmeasured — this needs a
  PresentMon run against a real game on the reference hardware before any claim is made.**
- **Whether excluding a window changes its composition path or disables hardware
  acceleration for it.** No Microsoft documentation found either way; nothing observable in
  my test. Absence of evidence only.
- **The 24H2 spurious-`AcquireNextFrame` behaviour (§5.3).** Sourced to a third-party issue
  report on a Microsoft engineer's sample repo, not to Microsoft. Not reproduced here.
- **Discord's overlay.** **No primary source exists.** The Discord client and its overlay
  are closed source; a sweep of the `github.com/discord` org found no overlay-related
  repository. Any claim about Discord's technique would have to come from
  reverse-engineering write-ups, which this investigation excluded. Stating plainly: *not
  known*.
- **Ordering among multiple topmost windows.** Not documented on Microsoft Learn. Two
  competing overlays race with last-writer-wins. The internal window-band mechanism that
  actually governs this is undocumented; do not rely on it.
- **Generality of the measurements.** All measurement was on one machine (Windows 11 25H2
  / build 26200, RTX 5060 Ti, one 1920x1080 output). The behaviour is DWM-level and very
  unlikely to be GPU-specific, but the mixed NVIDIA-dGPU / Ryzen-iGPU multi-display
  configuration in the project brief was **not** exercised — in particular an overlay on a
  display driven by a *different adapter* than the one being duplicated was not tested.

---

## Sources

Microsoft Learn / Microsoft-official:

- [`SetWindowDisplayAffinity`][swda] · [`GetWindowDisplayAffinity`][gwda]
- [Desktop Duplication API][ddapi-conc] · [Desktop Duplication (driver docs)][ddapi-drv] · [`IDXGIOutputDuplication`][ddapi]
- [`GraphicsCaptureSession`][wgc-session] · [Screen capture (Windows.Graphics.Capture)][wgc-doc]
- [Window Features (Layered Windows, Z-Order)][winfeat] · [Extended Window Styles][exstyles] · [`UpdateLayeredWindow`][ulw] · [`SetLayeredWindowAttributes`][slwa] · [`WM_NCHITTEST`][nchittest] · [`SetWindowPos`][setwindowpos]
- [DXGI overview][dxgiov] · [`IDXGISwapChain::SetFullscreenState`][setfullscreenstate] · [Swap Chains (Direct3D 12)][d3d12swap] · [For best performance, use DXGI flip model][flipmodel] · [Multiplane overlay support][mpo]
- [Demystifying Fullscreen Optimizations (DirectX dev blog)][fso]
- [`Window.AllowsTransparency`][allowstransparency] · [Technology Regions Overview][techregions] · [`Form.TransparencyKey`][transparencykey]
- [`microsoft/windows-rs` Win32 metadata][rdl]

Microsoft first-party source (PowerToys, pinned to `d2c53bf`):

- [`ZoomIt/WebcamPreviewWindow.h`][pt-webcam] · [`ZoomIt/SelectRectangle.h`][pt-selrect] · [`ZoomIt/MirrorWindow.cpp`][pt-mirror] · [`ZoomIt/CaptureFrameWait.h`][pt-cfw] · [`MeasureToolCore/OverlayUI.cpp`][pt-measure] · [`ColorPickerUI/WindowCaptureExclusionHelper.cs`][pt-cs] · [`ColorPickerUI/NativeMethods.cs`][pt-nm]

OBS Studio source (pinned to `14e3dae`):

- [`frontend/widgets/OBSBasic.cpp` — `SetDisplayAffinity`][obs-affinity] · [`frontend/utility/platform-windows.cpp` — version gate][obs-gate] · [`plugins/win-capture/duplicator-monitor-capture.c` — `choose_method`][obs-choose] · [`libobs-winrt/winrt-capture.cpp`][obs-wgc]

Third-party, explicitly flagged as such:

- [robmikh/Win32CaptureSample issue #83 — Desktop Duplication behaviour change in 24H2][robmikh83]

Direct measurement:

- Harness written for this ticket; run on Windows 11 25H2 build 10.0.26200, RTX 5060 Ti.
  Results inline in §3 and §5.2.

[swda]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity
[gwda]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowdisplayaffinity
[ddapi-conc]: https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api
[ddapi-drv]: https://learn.microsoft.com/en-us/windows-hardware/drivers/display/desktop-duplication-api
[ddapi]: https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nn-dxgi1_2-idxgioutputduplication
[wgc-session]: https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession
[wgc-doc]: https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture
[winfeat]: https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features
[exstyles]: https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles
[ulw]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow
[slwa]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes
[nchittest]: https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest
[setwindowpos]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
[dxgiov]: https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/d3d10-graphics-programming-guide-dxgi
[setfullscreenstate]: https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-setfullscreenstate
[d3d12swap]: https://learn.microsoft.com/en-us/windows/win32/direct3d12/swap-chains
[flipmodel]: https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model
[mpo]: https://learn.microsoft.com/en-us/windows-hardware/drivers/display/multiplane-overlay-support
[fso]: https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/
[allowstransparency]: https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency
[techregions]: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/technology-regions-overview
[transparencykey]: https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.form.transparencykey
[rdl]: https://github.com/microsoft/windows-rs/blob/master/metadata/win32/winuser.rdl
[pt-webcam]: https://github.com/microsoft/PowerToys/blob/d2c53bf3861ed2688a1c30aafd66ea0fc0186399/src/modules/ZoomIt/ZoomIt/WebcamPreviewWindow.h
[pt-selrect]: https://github.com/microsoft/PowerToys/blob/d2c53bf3861ed2688a1c30aafd66ea0fc0186399/src/modules/ZoomIt/ZoomIt/SelectRectangle.h
[pt-mirror]: https://github.com/microsoft/PowerToys/blob/d2c53bf3861ed2688a1c30aafd66ea0fc0186399/src/modules/ZoomIt/ZoomIt/MirrorWindow.cpp
[pt-cfw]: https://github.com/microsoft/PowerToys/blob/d2c53bf3861ed2688a1c30aafd66ea0fc0186399/src/modules/ZoomIt/ZoomIt/CaptureFrameWait.h
[pt-measure]: https://github.com/microsoft/PowerToys/blob/d2c53bf3861ed2688a1c30aafd66ea0fc0186399/src/modules/MeasureTool/MeasureToolCore/OverlayUI.cpp
[pt-cs]: https://github.com/microsoft/PowerToys/blob/d2c53bf3861ed2688a1c30aafd66ea0fc0186399/src/modules/colorPicker/ColorPickerUI/Helpers/WindowCaptureExclusionHelper.cs
[pt-nm]: https://github.com/microsoft/PowerToys/blob/d2c53bf3861ed2688a1c30aafd66ea0fc0186399/src/modules/colorPicker/ColorPickerUI/NativeMethods.cs
[obs-affinity]: https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/frontend/widgets/OBSBasic.cpp#L2162-L2192
[obs-gate]: https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/frontend/utility/platform-windows.cpp#L217-L236
[obs-choose]: https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/plugins/win-capture/duplicator-monitor-capture.c#L250-L278
[obs-wgc]: https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs-winrt/winrt-capture.cpp#L382-L387
[robmikh83]: https://github.com/robmikh/Win32CaptureSample/issues/83
