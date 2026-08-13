"""ClipShift #11, pass 4 - the 4 GiB decode test, on a timeline Resolve will render.

Passes 2 and 3 failed in the harness, not the media. Diagnosis: GetRenderCodecs('wav')
returns {} - audio-only export is a render SETTING (ExportVideo: False), not a
format+codec pair - and MP4/H.264 will not accept a render job for a timeline with no
video track. Every AddRenderJob returned '', control included.

The fix: give the timeline a 2-second video bed and append the audio as a SOURCE RANGE,
so the region of interest sits at timeline position 0 and only 120 frames need rendering.
That is what makes testing the tail of a 4h15m file cheap.

  big_tail  source frames 917880..917999 - 15298 s in, past the 4 GiB boundary at 04:08:33
  big_head  source frames 0..119         - within-file control, before the boundary
  ctl       the 30 s control file        - harness control

rf64-big-24-stereo.wav is a continuous 1 kHz sine, so the rendered audio is checked with
ffprobe afterwards rather than trusted: a real decode past 4 GiB gives a clean tone, a
32-bit offset bug gives silence or garbage.
"""
import os
import sys
import json
import time

SCRATCH = r"C:\Users\richa\AppData\Local\Temp\claude\C--Users-richa-Projects-clipshift\f432389a-ac0c-4604-94d7-23b055086df6\scratchpad"
ART = os.path.join(SCRATCH, "artifacts11")
OUT = os.path.join(SCRATCH, "resolve-out11")
REPORT = os.path.join(OUT, "report11d.json")

BIG_FRAMES = 15300 * 60
VIDEO = "vid-120.mp4"

CASES = [
    ("big_tail", "rf64-big-24-stereo.wav", BIG_FRAMES - 120),
    ("big_head", "rf64-big-24-stereo.wav", 0),
    ("ctl", "ctl-riff-16-stereo.wav", 0),
]

report = {"steps": [], "timelines": {}, "jobs": {}, "renders": {}, "errors": []}


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
        proj = pm.LoadProject("ClipShift11")
    if proj is None:
        fail("project", RuntimeError("ClipShift11 not found - run pass 1 first"))
        save()
        return

    mp = proj.GetMediaPool()
    root = mp.GetRootFolder()
    ms = res.GetMediaStorage()
    mp.SetCurrentFolder(root)

    def pool_by_name():
        d = {}
        for c in root.GetClipList():
            try:
                d[c.GetClipProperty("File Name")] = c
            except Exception:
                pass
        return d

    by_file = pool_by_name()
    if VIDEO not in by_file:
        items = ms.AddItemListToMediaPool([os.path.join(ART, VIDEO)])
        if not items:
            fail("import video", RuntimeError("Resolve refused " + VIDEO))
            save()
            return
        by_file = pool_by_name()
    step("video bed: %s" % by_file[VIDEO].GetClipProperty("Duration"))

    def render(label, filename, src_start):
        clip = by_file.get(filename)
        if clip is None:
            fail(label, RuntimeError("clip not in pool: " + filename))
            return
        tl_name = "tl4_" + label
        try:
            tl = None
            for i in range(proj.GetTimelineCount()):
                t = proj.GetTimelineByIndex(i + 1)
                if t.GetName() == tl_name:
                    tl = t
            if tl is None:
                tl = mp.CreateTimelineFromClips(tl_name, [by_file[VIDEO]])
                if tl is None:
                    fail(label, RuntimeError("CreateTimelineFromClips returned None"))
                    return
                proj.SetCurrentTimeline(tl)
                added = mp.AppendToTimeline([{
                    "mediaPoolItem": clip,
                    "startFrame": src_start,
                    "endFrame": src_start + 119,
                    "mediaType": 2,
                    "trackIndex": 1,
                }])
                step("%s: appended audio source %s..%s -> %s" % (
                    label, src_start, src_start + 119, bool(added)))
            proj.SetCurrentTimeline(tl)
            start = tl.GetStartFrame()

            info = {"items": {}}
            for kind in ("video", "audio"):
                for t in range(1, tl.GetTrackCount(kind) + 1):
                    for it in tl.GetItemListInTrack(kind, t) or []:
                        info["items"]["%s%s:%s" % (kind, t, it.GetName())] = {
                            "start": it.GetStart() - start,
                            "duration": it.GetDuration(),
                            "source_start": it.GetSourceStartFrame(),
                        }
            report["timelines"][label] = info
            step("%s timeline: %s" % (label, info))

            res.OpenPage("deliver")
            proj.SetCurrentRenderFormatAndCodec("mp4", "H264")
            ok_set = proj.SetRenderSettings({
                "TargetDir": OUT,
                "CustomName": "r4_" + label,
                "SelectAllFrames": False,
                "MarkIn": start,
                "MarkOut": start + 119,
                "FormatWidth": 1920,
                "FormatHeight": 1080,
            })
            job = proj.AddRenderJob()
            report["jobs"][label] = {"settings": ok_set, "job": job}
            step("%s: settings=%s job=%s" % (label, ok_set, job))
            if not job:
                save()
                return

            proj.StartRendering([job], isInteractiveMode=False)
            waited = 0
            while proj.IsRenderingInProgress() and waited < 900:
                time.sleep(2)
                waited += 2
            st = proj.GetRenderJobStatus(job) or {}
            report["renders"][label] = {k: st[k] for k in st}
            step("render %s: %s" % (label, report["renders"][label]))
            save()
        except Exception as e:
            fail(label, e)

    for label, filename, src_start in CASES:
        render(label, filename, src_start)

    save()
    step("report written to " + REPORT)


try:
    main()
except Exception as e:
    fail("main", e)
    save()
    raise
