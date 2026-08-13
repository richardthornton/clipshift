"""ClipShift #15, pass 4 - full-length audio alignment.

Pass 3 measured alignment over the first two seconds and found it sample-exact.
This renders the whole 29s of the repaired video against the patched WAV, plus a
clean never-truncated control, so drift across the length of a clip can be measured
rather than assumed.

Run from Workspace > Scripts > ClipShift 15 Test 4.
"""
import os, sys, json, time

SCRATCH = r"C:\Users\richa\AppData\Local\Temp\claude\C--Users-richa-Projects-clipshift\ce076736-4f5c-4a53-810c-1ac845e03654\scratchpad"
ART = os.path.join(SCRATCH, "artifacts")
OUT = os.path.join(SCRATCH, "resolve-out4")
REPORT = os.path.join(OUT, "report4.json")

CLIPS = {"repaired": "killed-repaired.mp4", "patched_wav": "killed-patched.wav",
         "clean": "clean.mp4", "clean_wav": "clean.wav"}

report = {"steps": [], "timelines": {}, "renders": {}, "errors": []}


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
    if proj is None or proj.GetName() != "ClipShift15d":
        proj = pm.LoadProject("ClipShift15d") or pm.CreateProject("ClipShift15d")
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
                step(f"imported {name}")
        except Exception as e:
            fail("import " + name, e)

    def run(tl_name, video, audio, audio_frames, out_name):
        try:
            tl = None
            for i in range(proj.GetTimelineCount()):
                t = proj.GetTimelineByIndex(i + 1)
                if t.GetName() == tl_name:
                    tl = t
            if tl is None:
                tl = mp.CreateTimelineFromClips(tl_name, [pool[video]])
                proj.SetCurrentTimeline(tl)
                mp.AppendToTimeline([{"mediaPoolItem": pool[audio], "startFrame": 0,
                                      "endFrame": audio_frames, "mediaType": 2,
                                      "trackIndex": 1}])
            proj.SetCurrentTimeline(tl)
            info = {"frames": tl.GetEndFrame() - tl.GetStartFrame(), "items": {}}
            for kind in ("video", "audio"):
                for t in range(1, tl.GetTrackCount(kind) + 1):
                    for it in tl.GetItemListInTrack(kind, t) or []:
                        info["items"][f"{kind}{t}:{it.GetName()}"] = {
                            "start": it.GetStart() - tl.GetStartFrame(),
                            "duration": it.GetDuration()}
            report["timelines"][tl_name] = info
            step(f"timeline {tl_name}: {info}")

            res.OpenPage("deliver")
            proj.SetRenderSettings({"TargetDir": OUT, "CustomName": out_name,
                                    "SelectAllFrames": True,
                                    "FormatWidth": 1920, "FormatHeight": 1080})
            proj.SetCurrentRenderFormatAndCodec("mp4", "H264")
            job = proj.AddRenderJob()
            proj.StartRendering([job], isInteractiveMode=False)
            waited = 0
            while proj.IsRenderingInProgress() and waited < 600:
                time.sleep(2)
                waited += 2
            st = proj.GetRenderJobStatus(job) or {}
            report["renders"][out_name] = {k: st[k] for k in st}
            step(f"render {out_name}: {report['renders'][out_name].get('JobStatus')}")
            save()
        except Exception as e:
            fail(tl_name, e)

    run("tl4_control", "clean", "clean_wav", 599, "r4_control")
    run("tl4_full", "repaired", "patched_wav", 1800, "r4_full")
    save()
    step("report written")


try:
    main()
except Exception as e:
    fail("main", e)
    save()
    raise
