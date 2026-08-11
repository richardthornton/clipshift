# Global hotkey under fullscreen — mechanism research

**Issue:** [#6](https://github.com/richardthornton/clipshift/issues/6)
**Status:** research complete, recommendation below
**Date:** 2026-08-11

ClipShift needs one global hotkey — a user-chosen chord of one or more keys — that toggles
recording and fires while a fullscreen game has focus, with conflicts surfaced at bind time
rather than discovered mid-session.

This document settles the mechanism against primary sources: Microsoft Learn reference
documentation, the Windows integrity-mechanism reference, and the source of shipping
applications that solve the same problem. Every claim carries the URL of the source that owns
it. Where the primary record is silent, that is stated rather than papered over.

---

## Recommendation

**Observe with Raw Input (`RIDEV_INPUTSINK`). Probe with `RegisterHotKey` at bind time. Do not
elevate. Do not install a low-level keyboard hook.**

Concretely:

1. **Detection** — a dedicated background thread owns a message-only window, registers the
   keyboard top-level collection with `RIDEV_INPUTSINK`, and pumps messages. It receives
   `WM_INPUT` for every key transition regardless of which application has focus, tracks the
   pressed-key set itself, and raises the toggle when the bound chord becomes fully held.
2. **Bind-time conflict detection** — when the user's chord is expressible in `RegisterHotKey`
   terms (zero or more of Alt/Ctrl/Shift/Win, plus exactly one other key), ClipShift attempts
   `RegisterHotKey` for it, then **unregisters immediately**. `ERROR_HOTKEY_ALREADY_REGISTERED`
   (1409) is an authoritative, system-owned answer that another process already claims the chord.
   This is the *only* mechanism on Windows that gives that answer, and it is exactly what
   Microsoft's own PowerToys does (§9). For chords outside that shape ClipShift **cannot** check,
   and must show that as a distinct third state rather than as a clean result.
3. **Elevation** — ClipShift ships unelevated. The hotkey will not fire while an *elevated*
   window has focus; games are not normally elevated, so this is a documented, acceptable
   limitation rather than a reason to ask for admin rights. OBS makes the same call (§7).
4. **Suppression** — the chord is passed through to the game. Raw Input cannot swallow it, and
   swallowing is not worth the cost of the mechanism that could.

The shape of the decision in one line: **observe input, never claim it — except for a moment at
bind time, purely to ask the system whether anyone else already has.**

A viable fallback for step 1 is polling `GetAsyncKeyState` on a ~25 ms timer, which is precisely
what OBS Studio ships (§7). It is in the same class — registers nothing, hooks nothing — and trades
edge accuracy for simplicity.

Rationale, tradeoffs and the losing options are below.

---

## The four mechanisms, head to head

| | `RegisterHotKey` | `WH_KEYBOARD_LL` hook | Raw Input + `RIDEV_INPUTSINK` | `GetAsyncKeyState` polling |
|---|---|---|---|---|
| Fires when another app has focus | Yes — system-wide match | Yes — system-wide hook | Yes — explicitly, that is what `RIDEV_INPUTSINK` is for | Yes |
| Fires under fullscreen game | Yes, **unless** the game registers Raw Input with `RIDEV_NOHOTKEYS` | Yes | Yes | Yes — this is what OBS ships |
| Fires over an *elevated* focused window | Not documented either way | No (UIPI) | No (UIPI) | No (UIPI — documented on the API) |
| Arbitrary multi-key chords | **No** — 1 virtual key + a 4-bit modifier mask | Yes | Yes | Yes |
| Modifier-only chords (e.g. Ctrl+Shift alone) | **No** | Yes | Yes | Yes |
| Conflict detection at bind time | **Yes — authoritative** (`ERROR_HOTKEY_ALREADY_REGISTERED`) | No | No | No |
| Can suppress the key from the game | Effectively yes (undocumented) | **Yes — documented** | **No** | **No** |
| Sits in the system input hot path | No | **Yes — hard timeout** | No | No |
| Injects anything into other processes | No | **No** (documented) | No | No |
| Needs a message pump | Yes | Yes | Yes | No |
| Can miss a very fast keypress | No | No | No | **Yes** — nothing queues edges between ticks |

---

## 1. `RegisterHotKey`

### What it does

> "When a key is pressed, the system looks for a match against all hot keys. Upon finding a
> match, the system posts the **WM_HOTKEY** message to the message queue of the window with
> which the hot key is associated. If the hot key is not associated with a window, then the
> **WM_HOTKEY** message is posted to the thread associated with the hot key."
> — [RegisterHotKey, Remarks](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)

The match is performed by the system against *all* registered hot keys on every key press. The
documentation states no condition about which window has focus. Delivery is a post to a message
queue:

> "Posted when the user presses a hot key registered by the **RegisterHotKey** function. The
> message is placed at the top of the message queue associated with the thread that registered
> the hot key."
> — [WM_HOTKEY](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-hotkey)

If `hWnd` is `NULL`, "**WM_HOTKEY** messages are posted to the message queue of the calling
thread and must be processed in the message loop" — so a window is not strictly required, but a
message pump is.

### Why it cannot express ClipShift's chords

The signature is `RegisterHotKey(HWND, int id, UINT fsModifiers, UINT vk)`. `vk` is documented as
"The virtual-key code of the hot key" — **singular**. `fsModifiers` is a bitmask limited to
exactly five documented values: `MOD_ALT` (0x0001), `MOD_CONTROL` (0x0002), `MOD_SHIFT` (0x0004),
`MOD_WIN` (0x0008) and `MOD_NOREPEAT` (0x4000).
([RegisterHotKey, Parameters](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey))

That is a hard structural limit, and it rules out two shapes the issue explicitly asks for:

- **Modifier-only chords.** There is no way to express "Ctrl+Shift, no other key". `vk` has no
  documented "none" value, and passing `VK_CONTROL` as `vk` is a different thing from a modifier.
- **Multiple non-modifier keys.** "F9 and F10 together" is inexpressible.

Two further constraints worth carrying into the spec:

- "The F12 key is reserved for use by the debugger at all times, so it should not be registered as
  a hot key."
- On `MOD_WIN`: "Keyboard shortcuts that involve the WINDOWS key are reserved for use by the
  operating system."

Both from [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey).

### The one thing it does better than anything else: bind-time conflict detection

> "Typically, **RegisterHotKey** also fails if the keystrokes specified for the hot key have
> already been registered for another hot key. However, some pre-existing, default hotkeys
> registered by the OS (such as PrintScreen, which launches the Snipping tool) may be overridden
> by another hot key registration when one of the app's windows is in the foreground."
> — [RegisterHotKey, Return value](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)

The failure surfaces through `GetLastError` as:

> **ERROR_HOTKEY_ALREADY_REGISTERED** — 1409 (0x581) — "Hot key is already registered."
> — [System Error Codes (1300-1699)](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1300-1699-)

This is the whole reason to keep `RegisterHotKey` in the design even though it cannot be the
detection mechanism. Nothing else on Windows will tell you, at bind time, that another process
owns a chord. A low-level hook sees the keystroke but has no idea whether some other application
also intends to act on it; Raw Input likewise.

Note the hedges in Microsoft's own wording — "**Typically**", and the carve-out for OS defaults
that *can* be overridden. Bind-time detection via this route is strong evidence of a conflict,
not a proof of its absence. The UI copy should reflect that.

### The documented way a game can break it

This is the finding that disqualifies `RegisterHotKey` as ClipShift's detection mechanism. An
application registering for Raw Input keyboard data may set:

> **RIDEV_NOHOTKEYS** 0x00000200 — "If set, the application-defined keyboard device hotkeys are
> not handled. However, the system hotkeys; for example, ALT+TAB and CTRL+ALT+DEL, are still
> handled. By default, all keyboard hotkeys are handled. **RIDEV_NOHOTKEYS** can be specified even
> if **RIDEV_NOLEGACY** is not specified and **hwndTarget** is **NULL**."
> — [RAWINPUTDEVICE](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice)

"Application-defined keyboard device hotkeys" is exactly what `RegisterHotKey` creates. A game
that registers Raw Input with this flag — a reasonable thing for a game to do, precisely so that
stray hotkeys do not fire mid-match — documentedly suppresses `RegisterHotKey` handling. Raw
Input registration by the game is *not* an exotic choice: Microsoft steers games toward it and
away from DirectInput (§4).

**Uncertainty, stated plainly:** the documentation does not say whether `RIDEV_NOHOTKEYS`
suppresses application hotkeys only while the registering application has focus, or globally for
as long as the registration stands. The flag's own text is silent on scope, and the note that it
works with `hwndTarget == NULL` (which is the *focus-following* configuration) muddies rather
than settles it. Either reading is bad for ClipShift's requirement, since the case that matters
is exactly "the game has focus". This was not settled from primary sources and would need a
runtime experiment against real titles.

---

## 2. Low-level keyboard hook (`WH_KEYBOARD_LL`)

### It does not inject anything — this is documented, and it matters

The single most repeated claim about low-level hooks on the internet is that they inject a DLL
into every process. For `WH_KEYBOARD_LL` that is **false**, and Microsoft says so directly:

> "This hook is called in the context of the thread that installed it. The call is made by sending
> a message to the thread that installed the hook. Therefore, the thread that installed the hook
> must have a message loop."
>
> "The keyboard input can come from the local keyboard driver or from calls to the **keybd_event**
> function. If the input comes from a call to **keybd_event**, the input was 'injected'. However,
> the **WH_KEYBOARD_LL** hook is not injected into another process. Instead, the context switches
> back to the process that installed the hook and it is called in its original context. Then the
> context switches back to the application that generated the event."
> — [LowLevelKeyboardProc, Remarks](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)

Contrast with the non-low-level `WH_KEYBOARD`, where injection is real: "**SetWindowsHookEx** can
be used to inject a DLL into another process... If the *dwThreadId* parameter is zero or specifies
the identifier of a thread created by a different process, the *lpfn* parameter must point to a
hook procedure in a DLL."
([SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw))

`WH_KEYBOARD_LL` is documented as "Global only" in the hook-scope table on the same page — you
cannot scope it to one thread — but global scope here means "sees all input", not "runs in every
process".

This distinction is load-bearing for the anti-cheat question in §6.

### The hot-path obligation is severe and the failure mode is silent

> "The hook procedure should process a message in less time than the data entry specified in the
> **LowLevelHooksTimeout** value in the following registry key: `HKEY_CURRENT_USER\Control
> Panel\Desktop`. The value is in milliseconds. If the hook procedure times out, the system passes
> the message to the next hook. However, on Windows 7 and later, the hook is silently removed
> without being called. **There is no way for the application to know whether the hook is
> removed.**"
>
> "**Windows 10 version 1709 and later** The maximum timeout value the system allows is 1000
> milliseconds (1 second). The system will default to using a 1000 millisecond timeout if the
> **LowLevelHooksTimeout** value is set to a value larger than 1000."
> — [LowLevelKeyboardProc, Remarks](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)

Quantifying the obligation for ClipShift: the hook callback would sit synchronously between every
keystroke system-wide and the application receiving it, on a thread that must also be pumping
messages. Exceeding the timeout does not raise an error, does not return a failure code, and does
not notify — the hotkey simply stops working forever, with no signal, until the process restarts.
For an application whose *entire job* is to run for four hours while a game is under load, and
whose managed runtime can introduce a GC pause at an arbitrary moment, that is an unacceptable
failure mode for the one control the user has.

Microsoft's own mitigation guidance concedes the point and then recommends against the mechanism
entirely:

> "If the application must use low level hooks, it should run the hooks on a dedicated thread that
> passes the work off to a worker thread and then immediately returns. **In most cases where the
> application needs to use low level hooks, it should monitor raw input instead.** This is because
> raw input can asynchronously monitor mouse and keyboard messages that are targeted for other
> threads more effectively than low level hooks can."
> — [LowLevelKeyboardProc, Remarks](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)

That is Microsoft, in the reference page for the hook itself, telling you to use Raw Input for
exactly ClipShift's use case.

The general hooks overview reinforces it: "Hooks tend to slow down the system because they
increase the amount of processing the system must perform for each message. You should install a
hook only when necessary, and remove it as soon as possible." and "You should use global hooks
only for debugging purposes; otherwise, you should avoid them. Global hooks hurt system
performance and cause conflicts with other applications that implement the same type of global
hook."
([Hooks Overview](https://learn.microsoft.com/en-us/windows/win32/winmsg/about-hooks))

### What it is uniquely good at: suppression

> "If the hook procedure processed the message, it may return a nonzero value to prevent the
> system from passing the message to the rest of the hook chain or the target window procedure."
> — [LowLevelKeyboardProc, Returns](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)

This is the only mechanism of the three that gives a documented, per-event choice to swallow the
key or pass it through. If ClipShift ever decides suppression is mandatory, this is the only door.

### Chords

The hook sees every `WM_KEYDOWN` / `WM_KEYUP` / `WM_SYSKEYDOWN` / `WM_SYSKEYUP` with a
`KBDLLHOOKSTRUCT`, so arbitrary chords — including modifier-only — are trivially expressible by
maintaining a pressed-key set. One trap is documented:

> "When this callback function is called in response to a change in the state of a key, the
> callback function is called before the asynchronous state of the key is updated. Consequently,
> the asynchronous state of the key cannot be determined by calling **GetAsyncKeyState** from
> within the callback function."
> — [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)

i.e. you must track state from the hook's own event stream, not by polling inside the callback.

---

## 3. Raw Input with `RIDEV_INPUTSINK` — the recommended mechanism

### Background delivery is the documented purpose of the flag

> **RIDEV_INPUTSINK** 0x00000100 — "If set, this enables the caller to receive the input even when
> the caller is not in the foreground. Note that **hwndTarget** must be specified."
> — [RAWINPUTDEVICE](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice)

> "An application can receive data when it is in the foreground and when it is in the background
> (if registered with **RIDEV_INPUTSINK**)."
> — [Raw Input Overview](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input)

Delivery is via `WM_INPUT` posted to the target window's queue. It is *not* synchronous with the
foreground application's input processing — ClipShift being slow cannot delay the game, and
ClipShift being slow cannot get its own input path silently torn down. That asymmetry versus the
hook is the core of the recommendation.

Do not confuse `RIDEV_INPUTSINK` with `RIDEV_EXINPUTSINK`, which is conditional and wrong here:
"this enables the caller to receive input in the background **only if the foreground application
does not process it**."

### Registration constraints to carry into the design

> "Only one window per raw input device class may be registered to receive raw input within a
> process (the window passed in the last call to RegisterRawInputDevices). Because of this,
> RegisterRawInputDevices should not be used from a library, as it may interfere with any raw
> input processing logic already present in applications that load it."
> — [RegisterRawInputDevices, Remarks](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices)

So ClipShift gets exactly one keyboard raw-input window per process. Owning it in a single
dedicated component is therefore mandatory, not stylistic — and any future third-party library
that registers raw input would silently steal it.

Also note: `hwndTarget` must be a real `HWND` that outlives the registration. A message-only
window is the right shape.

### It cannot suppress

Nothing in the Raw Input API blocks the keystroke from reaching the focused application.
`RIDEV_NOLEGACY` is sometimes mistaken for suppression, but the documentation scopes it to the
registering application: "If **RIDEV_NOLEGACY** is set for a mouse or a keyboard, the system does
not generate any legacy message for that device **for the application**."
([RAWINPUTDEVICE, Remarks](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice))

For a recording toggle, pass-through is acceptable and arguably correct — the user picks a chord
they do not use in-game, and a key that mysteriously vanishes is worse than one that does double
duty. This should be an explicit spec decision, not an accident.

### Reading efficiently

For a hotkey there is no volume problem, but the documented pattern is worth following: read the
current event with `GetRawInputData` on the `lParam` handle, then drain with `GetRawInputBuffer`,
because "`GetMessage` removes the current `WM_INPUT` from the raw input queue before returning. As
a result, `GetRawInputBuffer` will not see the current event — only events that arrived after it."
([Raw Input Overview](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input))

---

## 4. Does it fire under a fullscreen game?

Three sub-cases, and the honest answer differs in confidence across them.

### Borderless fullscreen

No special case exists. A borderless-fullscreen window is an ordinary top-level window; keyboard
routing is the ordinary Win32 path. All three mechanisms work.

### Exclusive fullscreen (DXGI `SetFullscreenState`)

Exclusive fullscreen is a **presentation and display-mode** concept, not an input concept. The
DXGI documentation describes full-screen mode entirely in terms of swap chains, display mode
switching, flipping versus blitting, and occlusion — "A full-screen mode swap chain can optimize
performance by switching the display resolution"; "The DXGI swap chain might change the display
mode of an output when making a full-screen transition." Nothing in DXGI's full-screen
documentation touches keyboard input routing at all.
([DXGI overview](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/d3d10-graphics-programming-guide-dxgi))

This is negative evidence rather than a positive guarantee: Microsoft does not document that
exclusive fullscreen changes keyboard delivery, and the mechanism it *does* document has nothing
to do with input. That is the strongest statement the primary record supports.

The historical mechanism people are remembering is **DirectInput exclusive-mode keyboard
acquisition**, not DXGI. Microsoft has deprecated that path for keyboard and mouse:

> "The use of DirectInput for keyboard and mouse input is not recommended. You should use Windows
> messages instead... Overall, using DirectInput offers no advantages when reading data from mouse
> or keyboard devices, and the use of DirectInput in these scenarios is discouraged."
> — [Introduction to DirectInput](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee418273(v=vs.85))
> and [DirectInput](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee416842(v=vs.85))

Modern titles are steered to Raw Input / `WM_INPUT`. Which brings back the one *documented*
fullscreen hazard: a game using Raw Input may set `RIDEV_NOHOTKEYS` and thereby stop
application-defined hotkeys from being handled (§1). That hazard applies to `RegisterHotKey`
only — it says nothing about hooks or about another application's own Raw Input registration.

**Net:** a low-level hook and Raw Input `RIDEV_INPUTSINK` have no documented fullscreen hazard at
all. `RegisterHotKey` has exactly one, and it is real.

### An elevated window has focus — see §5.

---

## 5. Elevation

### What the primary record says

UIPI is the relevant boundary. The Windows integrity-mechanism reference lists what a
lower-privilege process cannot do:

> "User Interface Privilege Isolation (UIPI) implements restrictions in the windows subsystem that
> prevents lower-privilege applications from sending window messages or installing hooks in
> higher-privilege processes."
>
> "A lower-privilege process cannot: Perform a window handle validation of a process running with
> higher rights. Use SendMessage or PostMessage to application windows running with higher rights.
> These APIs return success but silently drop the window message. **Use thread hooks to attach to
> a process running with higher rights. Use journal hooks to monitor a process running with higher
> rights. Perform dynamic link library (DLL) injection to a process running with higher rights.**"
> — [Windows Integrity Mechanism Design](https://learn.microsoft.com/en-us/previous-versions/dotnet/articles/bb625963(v=msdn.10))

That list names thread hooks, journal hooks and DLL injection — not low-level hooks and not Raw
Input. But the same document settles the question from the other direction, by enumerating what a
**UIAccess** process gains that an ordinary medium-integrity process lacks:

> "A process that is launched with UIAccess rights: ... **Has read input for all integrity levels
> using low-level hooks, raw input, GetKeyState, GetAsyncKeyState, and GetKeyboardInput.** Can set
> journal hooks. Uses AttachThreadInput to attach a thread to a higher integrity input queue"
> — [Windows Integrity Mechanism Design](https://learn.microsoft.com/en-us/previous-versions/dotnet/articles/bb625963(v=msdn.10))

If reading input for all integrity levels *via low-level hooks and raw input* is a privilege
granted by UIAccess, then a plain medium-integrity process does not have it. That is the answer
to the elevation question, from a first-party source, and it covers both of ClipShift's candidate
mechanisms identically.

It is corroborated directly in the `GetAsyncKeyState` reference, which lists among reasons the
call returns zero:

> "**UI Privilege Isolation (UIPI) prevents the calling thread from accessing the foreground
> thread.**" and "The foreground thread belongs to another process and the calling thread does not
> have **DESKTOP_HOOKCONTROL** or **DESKTOP_JOURNALRECORD** access to its desktop."
> — [GetAsyncKeyState, Return value](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate)

### Microsoft's own shipping app confirms the practical consequence

PowerToys is first-party, open-source, and documents precisely this:

> "PowerToys needs elevated administrator permission when writing protected system settings or
> when interacting with other applications that are running in administrator mode. If those
> applications are in focus, PowerToys may not function unless it's elevated as well.
> These are the two scenarios where PowerToys will not work: **Intercepting certain types of
> keyboard strokes**; Resizing / moving windows."
>
> Utilities listed as requiring admin mode include "Keyboard remapper — Key to key remapping,
> Global level shortcuts remapping" and "PowerToys Run — Use shortcut".
> — [Run PowerToys in Administrator Mode](https://learn.microsoft.com/en-us/windows/powertoys/administrator)

### The decision for ClipShift

**Do not elevate.** Reasons:

- Games are not normally elevated. Anti-cheat *drivers* run in kernel mode and their service
  components may be elevated, but the game process itself — the thing that has focus, and therefore
  the thing UIPI is evaluated against — is a medium-integrity process launched from a launcher that
  is itself medium-integrity. *This is an empirical claim about how games ship, not something the
  primary record states*; it is the assumption the recommendation rests on, and it is the one worth
  checking first if the hotkey ever misbehaves in a specific title. Note that OBS makes the same bet
  and requests `asInvoker` (§7).
- Elevation is a large ask for a recorder. Microsoft's own guidance on its own utility says "It's
  not recommended to always run an application as administrator unless absolutely necessary."
  ([PowerToys administrator mode](https://learn.microsoft.com/en-us/windows/powertoys/administrator))
- An elevated ClipShift would also inherit elevated file I/O for recordings and an elevated
  auto-start entry — both undesirable.

**But document the limitation.** "The hotkey will not fire while a window running as administrator
has focus" belongs in the ClipShift docs and, ideally, is detectable: if the toggle appears not to
work, ClipShift can check the foreground window's process elevation and say so, rather than
leaving the user to guess. (PowerToys does effectively this with its "Warnings for elevated apps"
setting — [PowerToys General settings](https://learn.microsoft.com/en-us/windows/powertoys/general).)

**UIAccess is not a viable escape hatch.** It requires "a digital signature that can be verified
using a digital certificate that chains up to a trusted root" and installation "in a local folder
application directory that is writeable only by administrators, such as the Program Files
directory" ([Windows Integrity Mechanism Design](https://learn.microsoft.com/en-us/previous-versions/dotnet/articles/bb625963(v=msdn.10))).
Code signing and a Program Files installer are explicitly out of scope for the MVP.

---

## 6. Anti-cheat

<!-- ANTICHEAT -->

---

## 7. What shipping applications actually do

### OBS Studio — polls `GetAsyncKeyState` on a 25 ms thread. That is the whole mechanism.

This is the most useful data point in the document, because OBS is the reference implementation of
"global hotkey that must work while a fullscreen game has focus", used by millions of streamers
across every anti-cheat-protected title on the market. Source citations below are pinned to master
commit `14e3dae77f9893a15d69c8b7bae57ac8ab961f59`.

OBS uses **no** `RegisterHotKey`, **no** `WH_KEYBOARD_LL`, and **no** Raw Input. The entire Windows
platform layer for hotkeys is a virtual-key lookup table plus a state query:

```c
struct obs_hotkeys_platform {
    int vk_codes[OBS_KEY_LAST_VALUE];
};

static bool vk_down(DWORD vk)
{
    short state = GetAsyncKeyState(vk);
    bool down = (state & 0x8000) != 0;
    return down;
}
```
— [libobs/obs-windows.c#L367](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-windows.c#L367)
and [#L986-L1001](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-windows.c#L986-L1001).
`obs_hotkeys_platform_init` fills the table and returns — it registers nothing with Windows
([#L970-L978](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-windows.c#L970-L978)).

The driver is a dedicated thread on a **25 ms** tick, created unconditionally at libobs init
([libobs/obs.c#L1161](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs.c#L1161)):

```c
while (os_event_timedwait(obs->hotkeys.stop_event, 25) == ETIMEDOUT) {
    ...
    query_hotkeys();
    ...
}
```
— [libobs/obs-hotkey.c#L1167](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-hotkey.c#L1167)

Per tick it calls `GetAsyncKeyState` once per modifier plus once per bound key
([#L1135-L1153](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-hotkey.c#L1135-L1153)).

**Consequences of that choice, visible in the source:**

- **Suppression: none, by construction.** A passive state query cannot consume a key. There is no
  source-level toggle for swallowing. (The one place OBS returns "handled" is a Qt event filter for
  its *own* windows —
  [frontend/OBSApp.cpp#L156-L262](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/frontend/OBSApp.cpp#L156-L262).)
- **Cross-application conflict detection: none, and architecturally impossible.** OBS never asks
  the OS for a hotkey, so there is nothing to fail. Its "duplicate hotkey" feature scans only its
  own settings rows and is advisory — a warning icon, never a rejection:
  `Basic.Settings.Hotkeys.DuplicateWarning="This hotkey is shared by one or more other actions,
  click to show conflicts"`
  ([en-US.ini#L1401](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/frontend/data/locale/en-US.ini#L1401),
  scanner at [OBSBasicSettings.cpp#L4585-L4635](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/frontend/settings/OBSBasicSettings.cpp#L4585-L4635)).
- **Elevation: not requested.** The manifest is `asInvoker` with `uiAccess="false"`
  ([obs.manifest](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/frontend/cmake/windows/obs.manifest)).
  Elevation status is logged for diagnostics only
  ([obs-windows.c#L163-L178](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-windows.c#L163-L178)).
  No locale string or UI warning connects hotkeys to elevation.
- **Latency ~25 ms worst case, and a press shorter than one tick can be missed** — nothing queues
  edges.
- **Chord model: single key + 4-bit modifier mask, with modifier-only supported.**
  ```c
  struct obs_key_combination {
      uint32_t modifiers;
      obs_key_t key;
  };
  ```
  ([obs-hotkey.h#L45-L49](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-hotkey.h#L45-L49))
  Modifier-only is expressed as `key == OBS_KEY_NONE` and explicitly special-cased
  ([obs-hotkey.c#L1035-L1055](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-hotkey.c#L1035-L1055));
  the settings editor emits it when you press only a modifier
  ([OBSHotkeyEdit.cpp#L37-L57](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/frontend/settings/OBSHotkeyEdit.cpp#L37-L57)).
  Left/right modifier variants are collapsed (`OBS_KEY_META` = LWIN **or** RWIN). Modifier matching
  is **strict** by default — extra held modifiers break the match
  ([obs-hotkey.c#L993-L1000](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-hotkey.c#L993-L1000),
  flag set at [obs.c#L1164](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs.c#L1164)).

**No rationale comment exists.** There is no comment in `obs-hotkey.c` or `obs-windows.c`
explaining the choice, no mention of fullscreen games or anti-cheat, and the originating commit
(`5ad553d06`, "libobs: Add global hotkey support") has no body text. Anyone claiming OBS chose
polling *because of* anti-cheat is inferring, not citing — this document does not make that claim.
What the source does establish is the outcome: a mechanism that registers nothing and hooks
nothing has shipped for a decade as the streaming world's default, alongside every major anti-cheat.

One cross-platform signal about how OBS conceives of the mechanism: on macOS it asks for the
*Input Monitoring* permission, with the string "This permission is required for hotkeys to work
while OBS is in the background."
([en-US.ini#L535](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/frontend/data/locale/en-US.ini#L535))
It treats global hotkeys as input *observation*, not hotkey *registration*, on every platform.

### Microsoft PowerToys — uses both a low-level hook *and* `RegisterHotKey`

The useful contrast. PowerToys is first-party, open source, and unlike OBS it does not have to
survive fullscreen games — so it makes the opposite tradeoff and pays the opposite costs.

- **Low-level keyboard hooks**, in a centralised runner hook plus several per-module hooks:
  `src/runner/centralized_kb_hook.cpp`, `src/common/interop/KeyboardHook.cpp`,
  `src/modules/keyboardmanager/KeyboardManagerEngineLibrary/KeyboardManager.cpp`,
  `src/modules/fancyzones/FancyZonesLib/GenericKeyHook.h`, and others (25 files match
  `WH_KEYBOARD_LL` repo-wide). Keyboard Manager *needs* the hook, because remapping requires
  suppression — the thing only `WH_KEYBOARD_LL` provides (§2).
- **`RegisterHotKey`**, in `src/runner/centralized_hotkeys.cpp`, `src/common/interop/HotkeyManager.cpp`
  and per-module hotkey services (31 files match repo-wide).
- And, as a direct consequence of using hooks over registration, it pays the elevation tax that
  Microsoft documents on the user-facing side (§5): PowerToys must be run as administrator for
  "Intercepting certain types of keyboard strokes" to work when an elevated app has focus.

The lesson for ClipShift is the shape of the tradeoff, not the choice: **you install a low-level
hook when you need to suppress. ClipShift does not need to suppress, so it should not install
one.**

Verification of the repo-wide counts above: GitHub code search on `microsoft/PowerToys` and
`obsproject/obs-studio`. OBS returns **0** hits for `WH_KEYBOARD_LL`, **0** for `RIDEV_INPUTSINK`
and **0** for `requireAdministrator`; the `GetAsyncKeyState` call sites and the 25 ms loop were
confirmed against the raw file at the pinned commit rather than taken from search alone.

### Discord, the NVIDIA App, and other overlays

Closed-source. This document makes no claim about their mechanisms, because none can be sourced.
Statements circulating about what they use are inference from behaviour or from disassembly write-ups,
neither of which is a primary source. See §12.

### What this changes about the recommendation

OBS validates a fourth option that §1–§3 did not consider: **polling `GetAsyncKeyState` on a timer**.
It shares Raw Input's key virtues — registers nothing, hooks nothing, injects nothing, sits outside
the system input hot path — and it is the option with by far the most field evidence in exactly
ClipShift's scenario.

Raw Input is still the recommendation, for three reasons:

1. ClipShift needs a message-pumping thread anyway for the `RegisterHotKey` conflict probe (§9), so
   Raw Input's only real overhead — a message-only window and a pump — is already paid for.
2. Raw Input is edge-driven. It cannot miss a fast tap, and it gives exact press/release ordering,
   which an arbitrary-key-set chord model (§8) genuinely wants and a 25 ms sampler does not provide.
3. Microsoft explicitly recommends raw input for this purpose in the hook's own reference page (§2).

Polling is a legitimate fallback if Raw Input proves troublesome in practice — the same UIPI
elevation ceiling applies to both
([GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate)),
so nothing is lost on that axis by switching. Whichever is chosen, OBS's design is the proof that
the *class* of mechanism — observe, never register, never hook — is the right one.

---

## 8. Representing chords

The data model that follows from §1–§3:

- A binding is **a set of virtual-key codes**, not a modifier mask plus a key. This is the only
  representation that covers modifier-only chords and multi-key chords, and it is what both the
  hook and Raw Input naturally produce.
- **Left/right distinction is available and should be a deliberate choice.** `VK_LSHIFT`,
  `VK_RSHIFT`, `VK_LCONTROL`, `VK_RCONTROL`, `VK_LMENU`, `VK_RMENU` exist as distinct codes
  ([GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate)),
  whereas `RegisterHotKey`'s `MOD_*` flags are explicitly side-agnostic ("Either ALT key must be
  held down"). Recommendation: normalise to side-agnostic for binding, since users do not think in
  sides and side-specific bindings produce baffling "my hotkey doesn't work" reports.
- **Fire on the transition into the full set being held**, not on every key event while held, and
  re-arm only after the set is broken. `RegisterHotKey` gets this for free via `MOD_NOREPEAT`
  ("Changes the hotkey behavior so that the keyboard auto-repeat does not yield multiple hotkey
  notifications" — [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey));
  with Raw Input, ClipShift must implement it. For a recording toggle this is not a nicety — key
  repeat would toggle recording dozens of times per second.
- Store scan codes alongside virtual keys if layout-independence is ever wanted;
  `RAWKEYBOARD`/`KBDLLHOOKSTRUCT` both carry them. Not needed for the MVP; noted so the model is
  not painted into a corner.

---

## 9. Conflict detection at bind time

This is the requirement that most constrains the design, because **only `RegisterHotKey` reports
conflicts at all**. Neither the hook nor Raw Input has any notion of another application claiming
a chord — they observe hardware events, and hardware events are not owned.

The workable design is a **probe**:

1. User picks a chord in the UI.
2. If the chord is `RegisterHotKey`-expressible (0..4 of Alt/Ctrl/Shift/Win + exactly one other
   key), call `RegisterHotKey`. On failure with `GetLastError() == ERROR_HOTKEY_ALREADY_REGISTERED`
   (1409), tell the user immediately: *"Something else on this PC already uses this shortcut."*
3. Whether or not the probe succeeds, detection at runtime is done by Raw Input.
4. If the chord is not expressible that way, ClipShift **cannot** check, and must say so rather
   than implying a clean bill of health.

**Probe and release — do not hold the registration.** This matters and is easy to get wrong.
Holding a successful `RegisterHotKey` would reserve the chord against later claimants, which sounds
attractive, but a held registration is generally observed to *consume* the keystroke — which would
silently contradict the pass-through decision in §3 and make the key vanish from the game. Register,
read the result, unregister immediately.

### Microsoft implements exactly this, in PowerToys

`src/runner/hotkey_conflict_detector.cpp` classifies conflicts as `InAppConflict` (between
PowerToys' own modules) or `SystemConflict` (another application or the OS), and detects the
latter with the probe:

```cpp
bool HotkeyConflictManager::HasConflictWithSystemHotkey(const Hotkey& hotkey)
{
    // ... build `modifiers` from hotkey.win/ctrl/alt/shift ...

    // No modifiers or no key is not a valid hotkey
    if (modifiers == 0 || hotkey.key == 0)
    {
        return false;
    }

    // Use a unique ID for this test registration
    const int hotkeyId = 0x0FFF; // Arbitrary ID for temporary registration

    // Try to register the hotkey with Windows, using nullptr instead of a window handle
    if (!RegisterHotKey(nullptr, hotkeyId, modifiers, hotkey.key))
    {
        // If registration fails with ERROR_HOTKEY_ALREADY_REGISTERED, it means the hotkey
        // is already in use by the system or another application
        if (GetLastError() == ERROR_HOTKEY_ALREADY_REGISTERED)
        {
            return true;
        }
    }
    else
    {
        // If registration succeeds, unregister it immediately
        UnregisterHotKey(nullptr, hotkeyId);
    }

    return false;
}
```
— [src/runner/hotkey_conflict_detector.cpp](https://github.com/microsoft/PowerToys/blob/main/src/runner/hotkey_conflict_detector.cpp)

Two things to take from it beyond the validation:

- It passes `nullptr` for `hWnd`, so the probe does not require a window — only that the calling
  thread can be given a hotkey. Convenient, since the probe happens on the settings path.
- **Its handling of inexpressible chords is the bug ClipShift should not copy.** When the chord has
  no modifiers or no key, PowerToys returns `false` — *no conflict* — which is indistinguishable
  from "checked and clean". ClipShift must instead surface a third state: **checked / conflict /
  cannot check**, and say which.

Note that Microsoft's user-facing documentation describes only the in-app half of this feature —
"If you have multiple PowerToys modules that use the same keyboard shortcut, the conflict detection
feature will alert you"
([PowerToys General settings](https://learn.microsoft.com/en-us/windows/powertoys/general)) — while
the source shows both. The docs understate what ships.

### Remaining honest caveats

- The probe answers "is this claimed *right now*". An application started later can claim it
  afterwards, and nothing will tell ClipShift. Re-probing whenever the settings window gains focus
  is cheap and worth doing.
- Microsoft's wording is "**Typically**, `RegisterHotKey` also fails if the keystrokes... have
  already been registered", with a documented exception for some OS default hotkeys
  ([RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)).
  **A failed probe is strong evidence of a conflict; a successful probe is weak evidence of its
  absence.** Word the UI accordingly.
- The probe detects conflicts with applications that used `RegisterHotKey`. It cannot see a
  conflict with an application that, like OBS, merely observes the same keys (§7) — that application
  will simply fire too, and neither app will ever know. This is an unfixable limit of the platform,
  not of the design.

---

## 10. Input latency and hot-path cost

- **Raw Input:** ClipShift is not in anyone's input path. `WM_INPUT` is posted to a queue and
  handled whenever the dedicated thread gets to it. A slow ClipShift delays only ClipShift. There
  is no documented timeout and no documented teardown. This matters enormously for a .NET
  application, where a GC pause is not under the developer's control.
- **Low-level hook:** ClipShift would be in the synchronous path of every keystroke system-wide,
  with a hard budget of `LowLevelHooksTimeout` (capped at 1000 ms since Windows 10 1709), and the
  penalty for exceeding it is silent, permanent, undetectable removal of the hook
  ([LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)).
  Meeting that budget reliably would mean the callback does nothing but copy the event to a
  lock-free queue and return — which is achievable, but it is a permanent, fragile constraint on
  a code path in a garbage-collected runtime, bought for a feature (suppression) ClipShift does
  not need.
- **`RegisterHotKey`:** no hot path at all; the match happens in the system.

Latency of detection itself is not a design constraint here. A recording toggle tolerates tens of
milliseconds without any user-perceptible difference; the requirement is *reliability*, not
sub-frame responsiveness.

---

## 11. .NET reachability

Both the hook and Raw Input approaches involve a native callback or a native window, and both have
the same two failure modes in .NET.

**Garbage collection.** Microsoft documents this on the hook API itself:

> "In .NET apps, you must ensure the callback is not moved around by the garbage collector
> (otherwise your app will crash with an ExecutionEngineException). One way to do this is by
> making the callback a static method of your class."
> — [SetWindowsHookExW, Remarks](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw)

And generally for any delegate handed to native code:

> "You must manually keep the delegate from being collected by the garbage collector from managed
> code. The garbage collector does not track references to unmanaged code... If native code stores
> the function pointer beyond the duration of the call, root the delegate for its entire lifetime
> — for example, by storing it in a `static` field."
> — [Marshal.GetFunctionPointerForDelegate](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getfunctionpointerfordelegate)

The same page gives the modern answer, which ClipShift should use: "It is recommended to use
function pointers and `UnmanagedCallersOnlyAttribute` instead. Function pointers are more
efficient, easier to use correctly, and supported in all environments." A `static`
`[UnmanagedCallersOnly]` window procedure has no delegate to root and no marshalling stub — which
also aligns with the project's standing constraint that hot paths avoid managed allocation.

**Message pumping.** Every mechanism here requires it:

- `RegisterHotKey` with `hWnd == NULL` posts `WM_HOTKEY` to the calling thread's queue, which
  "must be processed in the message loop"
  ([RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)).
- The low-level hook is delivered by "sending a message to the thread that installed the hook.
  Therefore, the thread that installed the hook must have a message loop."
  ([LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc))
- Raw Input arrives as `WM_INPUT` on the target window's queue
  ([Raw Input Overview](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input)).

**Design consequence:** one dedicated foreground thread, created explicitly (not a thread-pool
thread, which must not be blocked in a message loop), owning a message-only window, doing
`GetMessage`/`DispatchMessage` for its lifetime, and doing nothing else. It is the same thread
that calls `RegisterRawInputDevices` and `RegisterHotKey`, because both are bound to the thread or
window that made the call. Work triggered by the hotkey is handed off to the recording subsystem,
never done on this thread.

---

## 12. What could not be settled from primary sources

Stated plainly, because these are the places a runtime experiment is needed rather than more
reading:

1. **Whether `RegisterHotKey` fires while an elevated window has focus.** Microsoft documents the
   UIPI position for hooks, raw input and `GetAsyncKeyState`, but says nothing either way about
   `RegisterHotKey`, whose matching happens in the system rather than in the calling process. The
   architecture suggests it would work; there is no documentation to cite for that, so this
   document does not claim it. (It does not change the recommendation, since `RegisterHotKey`
   cannot express ClipShift's chords regardless.)
2. **The scope of `RIDEV_NOHOTKEYS`** — focus-conditional or global-while-registered. Documented
   text is silent (§1).
3. **Whether `RegisterHotKey` reliably suppresses the key from the focused application.** It is
   universally observed to do so; the documentation never states it. Not load-bearing for the
   recommendation.
4. **What proportion of shipping games actually set `RIDEV_NOHOTKEYS`.** Unknowable from
   documentation; it is a per-title implementation detail in closed-source binaries.
5. **What the NVIDIA App and Discord use internally.** Both are closed-source. Any claim about
   their mechanism is inference, and this document does not make one.

---

## 13. Sources

**Microsoft Learn — API reference**

- [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
- [WM_HOTKEY](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-hotkey)
- [SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw)
- [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)
- [Hooks Overview](https://learn.microsoft.com/en-us/windows/win32/winmsg/about-hooks)
- [Raw Input Overview](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input)
- [RAWINPUTDEVICE](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice)
- [RegisterRawInputDevices](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices)
- [GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate)
- [System Error Codes (1300-1699)](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1300-1699-)
- [Marshal.GetFunctionPointerForDelegate](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getfunctionpointerfordelegate)
- [DXGI overview](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/d3d10-graphics-programming-guide-dxgi)
- [Introduction to DirectInput](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee418273(v=vs.85))
- [DirectInput](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ee416842(v=vs.85))

**Microsoft Learn — security model**

- [Windows Integrity Mechanism Design (UIPI, UIAccess)](https://learn.microsoft.com/en-us/previous-versions/dotnet/articles/bb625963(v=msdn.10))
- [Mandatory Integrity Control](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control)

**Microsoft — first-party shipping application**

- [Run PowerToys in Administrator Mode](https://learn.microsoft.com/en-us/windows/powertoys/administrator)
- [PowerToys General settings (shortcut conflict detection)](https://learn.microsoft.com/en-us/windows/powertoys/general)

**OBS Studio source** (all pinned to commit `14e3dae77f9893a15d69c8b7bae57ac8ab961f59`)

- [libobs/obs-windows.c — Windows hotkey platform layer, `vk_down` / `obs_hotkeys_platform_is_pressed`](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-windows.c#L986-L1001)
- [libobs/obs-hotkey.c — the 25 ms polling thread](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-hotkey.c#L1157-L1179)
- [libobs/obs-hotkey.h — `obs_key_combination`](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/libobs/obs-hotkey.h#L45-L49)
- [frontend/settings/OBSBasicSettings.cpp — `ScanDuplicateHotkeys`](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/frontend/settings/OBSBasicSettings.cpp#L4585-L4635)
- [frontend/cmake/windows/obs.manifest — `asInvoker`, `uiAccess="false"`](https://github.com/obsproject/obs-studio/blob/14e3dae77f9893a15d69c8b7bae57ac8ab961f59/frontend/cmake/windows/obs.manifest)

**Microsoft PowerToys source**

- [src/runner/hotkey_conflict_detector.cpp — `HasConflictWithSystemHotkey`, the `RegisterHotKey` probe](https://github.com/microsoft/PowerToys/blob/main/src/runner/hotkey_conflict_detector.cpp)
- [src/runner/centralized_kb_hook.cpp — centralised low-level keyboard hook](https://github.com/microsoft/PowerToys/blob/main/src/runner/centralized_kb_hook.cpp)
- [src/runner/centralized_hotkeys.cpp — `RegisterHotKey` path](https://github.com/microsoft/PowerToys/blob/main/src/runner/centralized_hotkeys.cpp)

<!-- SOURCES_EXTRA -->
