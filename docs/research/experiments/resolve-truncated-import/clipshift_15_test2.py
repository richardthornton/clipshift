"""ClipShift #15, pass 2 - render-based measurement.

Pass 1 established that Resolve imports and seeks the truncated file. This pass
answers the two questions that need pixels and samples rather than properties:

  1. does a start offset carried in tfdt move the picture, and
  2. does audio stay aligned to video across a truncated pair,

by rendering timelines whose video carries burned-in frame numbers and whose audio
carries a click on every second boundary. A clean, never-truncated pair is rendered
too, so any constant delay in Resolve's own render path can be subtracted out.

Run from Workspace > Scripts > ClipShift 15 Test 2.
"""
import os, sys, json, time

SCRATCH = r"C:\Users\richa\AppData\Local\Temp\claude\C--Users-richa-Projects-clipshift\ce076736-4f5c-4a53-810c-1ac845e03654\scratchpad"
ART = os.path.join(SCRATCH, "artifacts")
OUT = os.path.join(SCRATCH, "resolve-out2")
REPORT = os.path.join(OUT, "report2.json")

CLIPS = {
    "clean": "clean.mp4",                                    # never truncated, control
    "clean_wav": "clean.wav",
    "repaired": "killed-repaired.mp4",                       # partial tail dropped
    "repaired_offset": "killed-tfdt-offset-repaired.mp4",    # same + 0.25s tfdt offset
    "killed": "killed.mp4",                                  # partial tail left in place
    "patched_wav": "killed-patched.wav",
}

report = {"steps": [], "clips": {}, "timelines": {}, "renders": {}, "errors": []}


def step(m):
    print(m)
    report["steps"].append(m)


def fail(where, e):
    m = f"{where}: {type(e).__name__}: {e}"
    print("ERROR " + m)
    report["errors"].append(m)


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
    if proj is None or proj.GetName() != "ClipShift15b":
        proj = pm.LoadProject("ClipShift15b") or pm.CreateProject("ClipShift15b")
    for k, v in [("timelineFrameRate", "60"), ("timelineResolutionWidth", "1920"),
                 ("timelineResolutionHeight", "1080"), ("timelinePlaybackFrameRate", "60")]:
        proj.SetSetting(k, v)
    step("project: " + proj.GetName())

    mp = proj.GetMediaPool()
    root = mp.GetRootFolder()
    ms = res.GetMediaStorage()
    mp.SetCurrentFolder(root)

    pool = {}
    for name, fn in CLIPS.items():
        path = os.path.join(ART, fn)
        try:
            existing = [c for c in root.GetClipList() if c.GetClipProperty("File Name") == fn]
            items = existing or ms.AddItemListToMediaPool([path])
            if items:
                pool[name] = items[0]
                report["clips"][name] = {
                    "Frames": items[0].GetClipProperty("Frames"),
                    "Duration": items[0].GetClipProperty("Duration"),
                    "Start TC": items[0].GetClipProperty("Start TC"),
                    "FPS": items[0].GetClipProperty("FPS"),
                    "Video Codec": items[0].GetClipProperty("Video Codec"),
                }
                step(f"imported {name}: {report['clips'][name]['Frames']} frames")
            else:
                fail("import " + name, RuntimeError("refused"))
        except Exception as e:
            fail("import " + name, e)
    save()

    def build(tl_name, video, audio=None):
        """Video on V1 at 0, audio (if any) on A1 at 0."""
        try:
            for i in range(proj.GetTimelineCount()):
                t = proj.GetTimelineByIndex(i + 1)
                if t.GetName() == tl_name:
                    proj.SetCurrentTimeline(t)
                    return t
            if video not in pool:
                return None
            tl = mp.CreateTimelineFromClips(tl_name, [pool[video]])
            if tl is None:
                fail("timeline " + tl_name, RuntimeError("CreateTimelineFromClips returned None"))
                return None
            proj.SetCurrentTimeline(tl)
            if audio and audio in pool:
                # A1 is empty, so an append lands at frame 0.
                mp.AppendToTimeline([{"mediaPoolItem": pool[audio], "startFrame": 0,
                                      "endFrame": int(pool[audio].GetClipProperty("Frames") or 1) - 1,
                                      "mediaType": 2, "trackIndex": 1}])
            info = {"frames": tl.GetEndFrame() - tl.GetStartFrame(),
                    "start_frame": tl.GetStartFrame(), "items": {}}
            for kind in ("video", "audio"):
                for t in range(1, tl.GetTrackCount(kind) + 1):
                    for it in tl.GetItemListInTrack(kind, t) or []:
                        info["items"][f"{kind}{t}:{it.GetName()}"] = {
                            "start": it.GetStart() - tl.GetStartFrame(),
                            "duration": it.GetDuration(),
                            "source_start": it.GetSourceStartFrame(),
                        }
            report["timelines"][tl_name] = info
            step(f"timeline {tl_name}: {info['frames']} frames, "
                 f"{len(info['items'])} items at {[v['start'] for v in info['items'].values()]}")
            return tl
        except Exception as e:
            fail("timeline " + tl_name, e)
            return None

    def render(tl, name, mark_in=None, mark_out=None):
        try:
            if tl is None:
                return
            proj.SetCurrentTimeline(tl)
            res.OpenPage("deliver")
            settings = {"TargetDir": OUT, "CustomName": name,
                        "FormatWidth": 1920, "FormatHeight": 1080}
            if mark_in is None:
                settings["SelectAllFrames"] = True
            else:
                settings["SelectAllFrames"] = False
                settings["MarkIn"] = tl.GetStartFrame() + mark_in
                settings["MarkOut"] = tl.GetStartFrame() + mark_out
            proj.SetRenderSettings(settings)
            proj.SetCurrentRenderFormatAndCodec("mp4", "H264")
            job = proj.AddRenderJob()
            proj.StartRendering([job], isInteractiveMode=False)
            waited = 0
            while proj.IsRenderingInProgress() and waited < 600:
                time.sleep(2)
                waited += 2
            st = proj.GetRenderJobStatus(job) or {}
            report["renders"][name] = {k: st[k] for k in st}
            step(f"render {name}: {report['renders'][name].get('JobStatus')}")
            save()
        except Exception as e:
            fail("render " + name, e)

    # 1. control: a pair that was never truncated, to expose any constant delay
    render(build("tl_control", "clean", "clean_wav"), "r_control")

    # 2. the real case: repaired video against the patched WAV, full length
    render(build("tl_repaired", "repaired", "patched_wav"), "r_repaired")

    # 3. does Resolve honour a 0.25s (15 frame) start offset carried in tfdt?
    render(build("tl_offset", "repaired_offset", "patched_wav"), "r_offset", 0, 119)

    # 4. the unrepaired tail: render only the last two seconds
    tl_tail = build("tl_tail", "killed")
    render(tl_tail, "r_tail_last2s", 1680, 1799)
    # ... and a stretch that ends before the damage, which should succeed
    render(tl_tail, "r_tail_safe", 1620, 1739)

    save()
    step("report written")


try:
    main()
except Exception as e:
    fail("main", e)
    save()
    raise
