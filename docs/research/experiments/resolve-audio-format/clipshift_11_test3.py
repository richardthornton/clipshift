"""ClipShift #11, pass 3 - the 4 GiB decode test, with the render format discovered.

Pass 2 failed at SetCurrentRenderFormatAndCodec because the format identifier was
guessed. GetRenderFormats() returns {display name: extension} - 'Wave' maps to an
extension, and GetRenderCodecs() wants the extension, not the display name. This pass
enumerates both and tries candidates until AddRenderJob returns a job id.

The question is unchanged: rf64-big-24-stereo.wav crosses 4 GiB at 04:08:33, and pass 1
proved only that Resolve reads its ds64 duration. This renders the last 120 frames
(15298 s in, past the boundary), the first 120 as a within-file control, and the small
control file as a harness control.
"""
import os
import sys
import json
import time

SCRATCH = r"C:\Users\richa\AppData\Local\Temp\claude\C--Users-richa-Projects-clipshift\f432389a-ac0c-4604-94d7-23b055086df6\scratchpad"
OUT = os.path.join(SCRATCH, "resolve-out11")
REPORT = os.path.join(OUT, "report11c.json")

BIG_FRAMES = 15300 * 60

report = {"steps": [], "formats": {}, "codecs": {}, "jobs": {}, "renders": {}, "errors": []}


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

    res.OpenPage("deliver")
    fmts = proj.GetRenderFormats() or {}
    report["formats"] = fmts
    step("formats: %s" % fmts)

    # Build candidate (extension, codec) pairs: every codec of the Wave format first,
    # then MP4/H264 as the fallback that is known to work from #15.
    candidates = []
    for display, ext in fmts.items():
        if "wav" in str(display).lower() or "wav" in str(ext).lower():
            codecs = proj.GetRenderCodecs(ext) or {}
            report["codecs"][str(ext)] = codecs
            step("codecs for %s (%s): %s" % (display, ext, codecs))
            for cdisplay, ckey in codecs.items():
                candidates.append((ext, ckey, "%s/%s" % (display, cdisplay)))
    for display, ext in fmts.items():
        if str(ext).lower() == "mp4":
            codecs = proj.GetRenderCodecs(ext) or {}
            report["codecs"][str(ext)] = codecs
            for cdisplay, ckey in codecs.items():
                if "264" in str(ckey) or "264" in str(cdisplay):
                    candidates.append((ext, ckey, "%s/%s" % (display, cdisplay)))
    step("candidates: %s" % [c[2] for c in candidates])

    chosen = []
    for ext, ckey, label in candidates:
        if proj.SetCurrentRenderFormatAndCodec(ext, ckey):
            chosen = [ext, ckey, label]
            step("using render format %s (%s, %s)" % (label, ext, ckey))
            break
    if not chosen:
        fail("format", RuntimeError("no render format accepted: %s" % candidates))
        save()
        return
    report["chosen"] = chosen
    save()

    mp = proj.GetMediaPool()
    root = mp.GetRootFolder()
    by_file = {}
    for c in root.GetClipList():
        try:
            by_file[c.GetClipProperty("File Name")] = c
        except Exception:
            pass

    def render(label, filename, mark_in_frame, count):
        clip = by_file.get(filename)
        if clip is None:
            fail(label, RuntimeError("clip not in pool: " + filename))
            return
        tl_name = "tl3_" + label
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

            res.OpenPage("deliver")
            proj.SetCurrentRenderFormatAndCodec(chosen[0], chosen[1])
            ok_set = proj.SetRenderSettings({
                "TargetDir": OUT,
                "CustomName": "r3_" + label,
                "SelectAllFrames": False,
                "MarkIn": start + mark_in_frame,
                "MarkOut": start + mark_in_frame + count - 1,
            })
            job = proj.AddRenderJob()
            report["jobs"][label] = {"timeline": [start, end], "settings": ok_set,
                                     "job": job, "MarkIn": start + mark_in_frame}
            step("%s: frames %s..%s settings=%s job=%s" % (
                label, start, end, ok_set, job))
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

    render("ctl_head", "ctl-riff-16-stereo.wav", 0, 120)
    render("big_head", "rf64-big-24-stereo.wav", 0, 120)
    render("big_tail", "rf64-big-24-stereo.wav", BIG_FRAMES - 120, 120)

    save()
    step("report written to " + REPORT)


try:
    main()
except Exception as e:
    fail("main", e)
    save()
    raise
