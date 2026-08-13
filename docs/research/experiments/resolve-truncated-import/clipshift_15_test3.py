"""ClipShift #15, pass 3 - does Resolve honour an edit list?

Pass 2 covers the tfdt route. This covers the two edit-list routes:
  offset-elst.mp4  - plain MP4 with an empty edit, what a soft-remuxed file looks like
  frag-elst.mp4    - fragmented MP4 with the same edit list in its init segment,
                     which is the only way a crash-truncated file could carry an offset

Both are the same media, offset by 250ms (15 frames at 60fps). FFmpeg reports
start_time=0.250000 for both. Each is rendered against the patched WAV; if Resolve
honours the edit, the first 15 rendered frames carry no picture and FRAME 0 lands at
rendered frame 15.

Run from Workspace > Scripts > ClipShift 15 Test 3.
"""
import os, sys, json, time

SCRATCH = r"C:\Users\richa\AppData\Local\Temp\claude\C--Users-richa-Projects-clipshift\ce076736-4f5c-4a53-810c-1ac845e03654\scratchpad"
ART = os.path.join(SCRATCH, "artifacts")
OUT = os.path.join(SCRATCH, "resolve-out3")
REPORT = os.path.join(OUT, "report3.json")

CLIPS = {"elst_plain": "offset-elst.mp4", "elst_frag": "frag-elst.mp4",
         "patched_wav": "killed-patched.wav"}

report = {"steps": [], "clips": {}, "timelines": {}, "renders": {}, "errors": []}


def step(m):
    print(m)
    report["steps"].append(m)


def fail(where, e):
    report["errors"].append(f"{where}: {type(e).__name__}: {e}")
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
    step("connected: " + res.GetProductName() + " " + res.GetVersionString())

    pm = res.GetProjectManager()
    proj = pm.GetCurrentProject()
    if proj is None or proj.GetName() != "ClipShift15c":
        proj = pm.LoadProject("ClipShift15c") or pm.CreateProject("ClipShift15c")
    for k, v in [("timelineFrameRate", "60"), ("timelineResolutionWidth", "1920"),
                 ("timelineResolutionHeight", "1080"), ("timelinePlaybackFrameRate", "60")]:
        proj.SetSetting(k, v)

    mp = proj.GetMediaPool()
    root = mp.GetRootFolder()
    ms = res.GetMediaStorage()
    mp.SetCurrentFolder(root)

    pool = {}
    for name, fn in CLIPS.items():
        try:
            existing = [c for c in root.GetClipList() if c.GetClipProperty("File Name") == fn]
            items = existing or ms.AddItemListToMediaPool([os.path.join(ART, fn)])
            if items:
                pool[name] = items[0]
                report["clips"][name] = {k: items[0].GetClipProperty(k) for k in
                                         ("Frames", "Duration", "Start TC", "End TC", "FPS")}
                step(f"imported {name}: {report['clips'][name]}")
            else:
                fail("import " + name, RuntimeError("refused"))
        except Exception as e:
            fail("import " + name, e)
    save()

    def build_and_render(tl_name, video, out_name):
        try:
            tl = None
            for i in range(proj.GetTimelineCount()):
                t = proj.GetTimelineByIndex(i + 1)
                if t.GetName() == tl_name:
                    tl = t
            if tl is None:
                if video not in pool:
                    return
                tl = mp.CreateTimelineFromClips(tl_name, [pool[video]])
                proj.SetCurrentTimeline(tl)
                if "patched_wav" in pool:
                    mp.AppendToTimeline([{"mediaPoolItem": pool["patched_wav"], "startFrame": 0,
                                          "endFrame": 1800, "mediaType": 2, "trackIndex": 1}])
            proj.SetCurrentTimeline(tl)
            info = {"frames": tl.GetEndFrame() - tl.GetStartFrame(), "items": {}}
            for kind in ("video", "audio"):
                for t in range(1, tl.GetTrackCount(kind) + 1):
                    for it in tl.GetItemListInTrack(kind, t) or []:
                        info["items"][f"{kind}{t}:{it.GetName()}"] = {
                            "start": it.GetStart() - tl.GetStartFrame(),
                            "duration": it.GetDuration(),
                            "source_start": it.GetSourceStartFrame()}
            report["timelines"][tl_name] = info
            step(f"timeline {tl_name}: {info}")

            res.OpenPage("deliver")
            proj.SetRenderSettings({"TargetDir": OUT, "CustomName": out_name,
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
            report["renders"][out_name] = {k: st[k] for k in st}
            step(f"render {out_name}: {report['renders'][out_name].get('JobStatus')}")
            save()
        except Exception as e:
            fail(tl_name, e)

    build_and_render("tl_elst_plain", "elst_plain", "r_elst_plain")
    build_and_render("tl_elst_frag", "elst_frag", "r_elst_frag")
    save()
    step("report written")


try:
    main()
except Exception as e:
    fail("main", e)
    save()
    raise
