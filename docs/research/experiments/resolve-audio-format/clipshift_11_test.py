"""ClipShift #11 - which WAV containers and formats does Resolve free actually read?

#15 verified a plain RIFF 16-bit stereo WAV. This covers the shapes the audio format
decision depends on and that nobody has verified:

  ctl-riff-16-stereo    plain RIFF                    the known-good control
  junk-riff-16-stereo   RIFF carrying a JUNK reservation for a later ds64
  rf64-small-16-stereo  RF64 + ds64 from byte zero, small file
  rf64-big-24-stereo    RF64, 24-bit stereo, 4h15m, 4.10 GiB - past the RIFF ceiling
  mic-24-mono           24-bit mono - the locked microphone format
  bext-tc-16-stereo     BWF bext, TimeReference = 2,505,600,000 (14:30:00 at 48 kHz)
  riff-stale-16-stereo  RIFF declaring 1 s less than it holds - the patch-cadence steady state
  rf64-stale-16-stereo  same, in ds64

Each is imported, its full property set dumped, and a short range rendered - because
#15 established that Resolve will happily import and scrub a file it then refuses to
export, so import success alone proves nothing.

Drop in %APPDATA%\\Blackmagic Design\\DaVinci Resolve\\Support\\Fusion\\Scripts\\Utility\\
and run from Workspace > Scripts. Edit SCRATCH below first.
"""
import os
import sys
import json
import time

SCRATCH = r"C:\Users\richa\AppData\Local\Temp\claude\C--Users-richa-Projects-clipshift\f432389a-ac0c-4604-94d7-23b055086df6\scratchpad"
ART = os.path.join(SCRATCH, "artifacts11")
OUT = os.path.join(SCRATCH, "resolve-out11")
REPORT = os.path.join(OUT, "report11.json")

CLIPS = [
    ("ctl_riff", "ctl-riff-16-stereo.wav"),
    ("junk_riff", "junk-riff-16-stereo.wav"),
    ("rf64_small", "rf64-small-16-stereo.wav"),
    ("rf64_big", "rf64-big-24-stereo.wav"),
    ("mic_24_mono", "mic-24-mono.wav"),
    ("bext_tc", "bext-tc-16-stereo.wav"),
    ("riff_stale", "riff-stale-16-stereo.wav"),
    ("rf64_stale", "rf64-stale-16-stereo.wav"),
]

report = {"steps": [], "clips": {}, "renders": {}, "errors": []}


def step(m):
    print(m)
    report["steps"].append(m)


def fail(where, e):
    report["errors"].append("%s: %s: %s" % (where, type(e).__name__, e))
    print("ERROR " + report["errors"][-1])


def save():
    os.makedirs(OUT, exist_ok=True)
    with open(REPORT, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)


def get_resolve():
    r = globals().get("resolve")
    if r is not None:
        return r
    api = r"C:\ProgramData\Blackmagic Design\DaVinci Resolve\Support\Developer\Scripting"
    os.environ["RESOLVE_SCRIPT_API"] = api
    os.environ["RESOLVE_SCRIPT_LIB"] = r"C:\Program Files\Blackmagic Design\DaVinci Resolve\fusionscript.dll"
    sys.path.append(os.path.join(api, "Modules"))
    import DaVinciResolveScript as dvr
    return dvr.scriptapp("Resolve")


def main():
    os.makedirs(OUT, exist_ok=True)
    res = get_resolve()
    if res is None:
        fail("connect", RuntimeError("scriptapp returned None"))
        save()
        return
    step("connected: %s %s" % (res.GetProductName(), res.GetVersionString()))

    pm = res.GetProjectManager()
    proj = pm.GetCurrentProject()
    if proj is None or proj.GetName() != "ClipShift11":
        proj = pm.LoadProject("ClipShift11") or pm.CreateProject("ClipShift11")
    for k, v in [("timelineFrameRate", "60"), ("timelineResolutionWidth", "1920"),
                 ("timelineResolutionHeight", "1080"), ("timelinePlaybackFrameRate", "60")]:
        proj.SetSetting(k, v)

    mp = proj.GetMediaPool()
    root = mp.GetRootFolder()
    ms = res.GetMediaStorage()
    mp.SetCurrentFolder(root)

    pool = {}
    for name, fn in CLIPS:
        path = os.path.join(ART, fn)
        if not os.path.exists(path):
            fail("import " + name, RuntimeError("artifact missing: " + path))
            continue
        try:
            existing = [c for c in root.GetClipList()
                        if c.GetClipProperty("File Name") == fn]
            items = existing or ms.AddItemListToMediaPool([path])
            if items:
                pool[name] = items[0]
                # No argument returns the whole property dict - avoids guessing key names.
                props = items[0].GetClipProperty()
                if not isinstance(props, dict):
                    props = {k: items[0].GetClipProperty(k) for k in
                             ("Frames", "Duration", "Start TC", "End TC", "FPS",
                              "Audio Ch", "Sample Rate", "Bit Depth", "Format")}
                report["clips"][name] = props
                step("imported %s: dur=%s frames=%s startTC=%s ch=%s rate=%s" % (
                    name, props.get("Duration"), props.get("Frames"),
                    props.get("Start TC"), props.get("Audio Ch"),
                    props.get("Sample Rate")))
            else:
                fail("import " + name, RuntimeError("Resolve refused the file"))
                report["clips"][name] = None
        except Exception as e:
            fail("import " + name, e)
    save()

    def render(name):
        """Render 120 frames of an audio-only timeline - the #15 lesson is that
        import success does not imply export success."""
        if name not in pool:
            return
        tl_name = "tl_" + name
        try:
            tl = None
            for i in range(proj.GetTimelineCount()):
                t = proj.GetTimelineByIndex(i + 1)
                if t.GetName() == tl_name:
                    tl = t
            if tl is None:
                tl = mp.CreateTimelineFromClips(tl_name, [pool[name]])
            if tl is None:
                fail(tl_name, RuntimeError("could not build timeline"))
                return
            proj.SetCurrentTimeline(tl)

            res.OpenPage("deliver")
            proj.SetRenderSettings({"TargetDir": OUT, "CustomName": "r_" + name,
                                    "SelectAllFrames": False,
                                    "MarkIn": tl.GetStartFrame(),
                                    "MarkOut": tl.GetStartFrame() + 119,
                                    "FormatWidth": 1920, "FormatHeight": 1080})
            proj.SetCurrentRenderFormatAndCodec("mp4", "H264")
            job = proj.AddRenderJob()
            proj.StartRendering([job], isInteractiveMode=False)
            waited = 0
            while proj.IsRenderingInProgress() and waited < 300:
                time.sleep(2)
                waited += 2
            st = proj.GetRenderJobStatus(job) or {}
            report["renders"][name] = {k: st[k] for k in st}
            step("render %s: %s" % (name, report["renders"][name].get("JobStatus")))
            save()
        except Exception as e:
            fail("render " + name, e)

    for name, _ in CLIPS:
        render(name)

    save()
    step("report written to " + REPORT)


try:
    main()
except Exception as e:
    fail("main", e)
    save()
    raise
