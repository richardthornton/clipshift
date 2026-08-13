"""ClipShift #11, pass 2 - does Resolve decode past the 4 GiB boundary?

Pass 1 established that Resolve imports a 4.10 GiB RF64 file and reports 04:15:00:00.
But that duration is read out of the ds64 chunk; it does not prove Resolve can address
a sample that lives past byte 4,294,967,296. A 32-bit file-offset bug would look
exactly like pass 1's result and only show up on playback of the tail.

The 4 GiB boundary in rf64-big-24-stereo.wav falls at 04:08:33. This renders the last
120 frames - 15298 s in, comfortably past it - plus the first 120 as a within-file
control, plus the small control file as a harness control.

The content is a continuous 1 kHz sine, so a successful decode gives a strong tone and
a failed one gives silence or noise. Checked afterwards with check-renders.sh, not by ear.

Pass 1's renders all returned None including the control, so this also dumps the render
format list and every intermediate return value to diagnose that.
"""
import os
import sys
import json
import time

SCRATCH = r"C:\Users\richa\AppData\Local\Temp\claude\C--Users-richa-Projects-clipshift\f432389a-ac0c-4604-94d7-23b055086df6\scratchpad"
OUT = os.path.join(SCRATCH, "resolve-out11")
REPORT = os.path.join(OUT, "report11b.json")

BIG_FRAMES = 15300 * 60  # 4h15m at 60 fps

report = {"steps": [], "formats": {}, "jobs": {}, "renders": {}, "errors": []}


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

    try:
        report["formats"] = proj.GetRenderFormats() or {}
        step("render formats: %s" % sorted(report["formats"].keys()))
        for fmt in ("wav", "mp4"):
            if fmt in (report["formats"] or {}).values() or fmt in report["formats"]:
                report["formats"]["codecs_" + fmt] = proj.GetRenderCodecs(fmt) or {}
    except Exception as e:
        fail("formats", e)
    save()

    mp = proj.GetMediaPool()
    root = mp.GetRootFolder()
    by_file = {}
    for c in root.GetClipList():
        by_file[c.GetClipProperty("File Name")] = c
    step("media pool: %s" % sorted(by_file.keys()))

    def render(label, filename, mark_in_frame, count, fmt, codec):
        clip = by_file.get(filename)
        if clip is None:
            fail(label, RuntimeError("clip not in pool: " + filename))
            return
        tl_name = "tl2_" + label
        try:
            tl = None
            for i in range(proj.GetTimelineCount()):
                t = proj.GetTimelineByIndex(i + 1)
                if t.GetName() == tl_name:
                    tl = t
            if tl is None:
                tl = mp.CreateTimelineFromClips(tl_name, [clip])
            if tl is None:
                fail(label, RuntimeError("CreateTimelineFromClips returned None"))
                return
            proj.SetCurrentTimeline(tl)
            start, end = tl.GetStartFrame(), tl.GetEndFrame()
            step("%s timeline frames %s..%s" % (label, start, end))

            res.OpenPage("deliver")
            ok_fmt = proj.SetCurrentRenderFormatAndCodec(fmt, codec)
            ok_set = proj.SetRenderSettings({
                "TargetDir": OUT,
                "CustomName": "r2_" + label,
                "SelectAllFrames": False,
                "MarkIn": start + mark_in_frame,
                "MarkOut": start + mark_in_frame + count - 1,
            })
            job = proj.AddRenderJob()
            report["jobs"][label] = {"SetCurrentRenderFormatAndCodec": ok_fmt,
                                     "SetRenderSettings": ok_set,
                                     "AddRenderJob": job,
                                     "MarkIn": start + mark_in_frame}
            step("%s: fmt=%s settings=%s job=%s" % (label, ok_fmt, ok_set, job))
            if not job:
                save()
                return

            proj.StartRendering([job], isInteractiveMode=False)
            waited = 0
            while proj.IsRenderingInProgress() and waited < 600:
                time.sleep(2)
                waited += 2
            st = proj.GetRenderJobStatus(job) or {}
            report["renders"][label] = {k: st[k] for k in st}
            step("render %s: %s" % (label, report["renders"][label]))
            save()
        except Exception as e:
            fail(label, e)

    # Harness control first - if this does not render, nothing below means anything.
    render("ctl_head", "ctl-riff-16-stereo.wav", 0, 120, "wav", "lpcm")
    # Within-file control, before the boundary.
    render("big_head", "rf64-big-24-stereo.wav", 0, 120, "wav", "lpcm")
    # The actual question: 15298 s in, past the 4 GiB boundary at 04:08:33.
    render("big_tail", "rf64-big-24-stereo.wav", BIG_FRAMES - 120, 120, "wav", "lpcm")

    save()
    step("report written to " + REPORT)


try:
    main()
except Exception as e:
    fail("main", e)
    save()
    raise
