"""ClipShift #15 - does DaVinci Resolve (free) import a crash-truncated fragmented MP4?

Runs either from Resolve's Workspace > Scripts menu (in-process, works on the free
edition) or externally (needs Preferences > System > General > External scripting: Local).

Writes a JSON report and exports stills whose burned-in frame numbers make a
start-offset error visible rather than inferred.
"""
import os, sys, json, time

SCRATCH = r"C:\Users\richa\AppData\Local\Temp\claude\C--Users-richa-Projects-clipshift\ce076736-4f5c-4a53-810c-1ac845e03654\scratchpad"
ART = os.path.join(SCRATCH, "artifacts")
OUT = os.path.join(SCRATCH, "resolve-out")
REPORT = os.path.join(OUT, "report.json")

CLIPS = {
    "killed": os.path.join(ART, "killed.mp4"),                      # truncated fMP4, starts at 0
    "killed_tfdt_offset": os.path.join(ART, "killed-tfdt-offset.mp4"),  # same, first sample at 0.25s
    "killed_wav": os.path.join(ART, "killed.wav"),                  # truncated WAV, sizes still 0xffffffff
    "killed_patched_wav": os.path.join(ART, "killed-patched.wav"),  # sizes patched by recovery
}

PROPS = ["File Name", "Type", "Duration", "Frames", "FPS", "Format", "Video Codec",
         "Resolution", "Start TC", "End TC", "Start", "End", "Audio Ch", "Sample Rate",
         "Bit Depth", "Data Level", "Color Space", "Frame Rate"]

report = {"steps": [], "clips": {}, "timelines": {}, "stills": [], "errors": []}


def step(msg):
    print(msg)
    report["steps"].append(msg)


def fail(where, e):
    msg = f"{where}: {type(e).__name__}: {e}"
    print("ERROR " + msg)
    report["errors"].append(msg)


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


def save():
    os.makedirs(OUT, exist_ok=True)
    with open(REPORT, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)


def main():
    os.makedirs(OUT, exist_ok=True)
    res = get_resolve()
    if res is None:
        fail("connect", RuntimeError("scriptapp returned None"))
        save()
        return
    report["product"] = f"{res.GetProductName()} {res.GetVersionString()}"
    step("connected: " + report["product"])

    pm = res.GetProjectManager()
    proj = pm.GetCurrentProject()
    if proj is None or proj.GetName() != "ClipShift15":
        proj = pm.LoadProject("ClipShift15") or pm.CreateProject("ClipShift15")
    step("project: " + proj.GetName())

    for k, v in [("timelineFrameRate", "60"), ("timelineResolutionWidth", "1920"),
                 ("timelineResolutionHeight", "1080"), ("timelinePlaybackFrameRate", "60"),
                 ("videoMonitorUseRec601For422SDI", "0")]:
        proj.SetSetting(k, v)

    mp = proj.GetMediaPool()
    root = mp.GetRootFolder()
    ms = res.GetMediaStorage()

    # ---- import -------------------------------------------------------------
    mp.SetCurrentFolder(root)
    added = {}
    for name, path in CLIPS.items():
        try:
            items = ms.AddItemListToMediaPool([path])
            if not items:
                # already in the pool from an earlier run?
                items = [c for c in root.GetClipList()
                         if c.GetClipProperty("File Path") == path]
            if items:
                added[name] = items[0]
                step(f"imported {name}")
            else:
                fail("import " + name, RuntimeError("Resolve refused the file"))
                report["clips"][name] = {"imported": False}
        except Exception as e:
            fail("import " + name, e)

    for name, item in added.items():
        try:
            props = {p: item.GetClipProperty(p) for p in PROPS}
            props["imported"] = True
            report["clips"][name] = props
        except Exception as e:
            fail("props " + name, e)
    save()

    # ---- timelines ----------------------------------------------------------
    # One timeline per video clip, plus the patched WAV laid alongside the
    # offset clip so audio alignment can be measured off a render.
    def make_timeline(tl_name, clip_names):
        try:
            existing = {proj.GetTimelineByIndex(i + 1).GetName(): proj.GetTimelineByIndex(i + 1)
                        for i in range(proj.GetTimelineCount())}
            if tl_name in existing:
                tl = existing[tl_name]
            else:
                clips = [added[c] for c in clip_names if c in added]
                if not clips:
                    return None
                tl = mp.CreateTimelineFromClips(tl_name, clips)
            if tl is None:
                fail("timeline " + tl_name, RuntimeError("CreateTimelineFromClips returned None"))
                return None
            proj.SetCurrentTimeline(tl)
            info = {
                "start_frame": tl.GetStartFrame(),
                "end_frame": tl.GetEndFrame(),
                "frames": tl.GetEndFrame() - tl.GetStartFrame(),
                "start_tc": tl.GetStartTimecode(),
                "fps": tl.GetSetting("timelineFrameRate"),
                "video_tracks": tl.GetTrackCount("video"),
                "audio_tracks": tl.GetTrackCount("audio"),
                "items": {},
            }
            for kind in ("video", "audio"):
                for t in range(1, tl.GetTrackCount(kind) + 1):
                    for it in tl.GetItemListInTrack(kind, t) or []:
                        info["items"][f"{kind}{t}:{it.GetName()}"] = {
                            "start": it.GetStart(), "end": it.GetEnd(),
                            "duration": it.GetDuration(),
                            "left_offset": it.GetLeftOffset(),
                            "source_start": it.GetSourceStartFrame(),
                            "source_end": it.GetSourceEndFrame(),
                        }
            report["timelines"][tl_name] = info
            step(f"timeline {tl_name}: {info['frames']} frames")
            return tl
        except Exception as e:
            fail("timeline " + tl_name, e)
            return None

    tl_plain = make_timeline("tl_killed", ["killed"])
    tl_offset = make_timeline("tl_offset_plus_wav", ["killed_tfdt_offset", "killed_patched_wav"])
    save()

    # ---- scrub test: jump to timecodes and grab stills -----------------------
    # If Resolve can seek a fragmented file, the burned frame number in each
    # still must match the timecode we asked for.
    try:
        res.OpenPage("color")
        gallery = proj.GetGallery()
        album = gallery.GetCurrentStillAlbum()
        for tl, tag in ((tl_plain, "plain"), (tl_offset, "offset")):
            if tl is None:
                continue
            proj.SetCurrentTimeline(tl)
            for tc in ("01:00:00:00", "01:00:00:15", "01:00:05:00", "01:00:20:30", "01:00:29:00"):
                try:
                    ok = tl.SetCurrentTimecode(tc)
                    time.sleep(0.6)
                    got = tl.GetCurrentTimecode()
                    still = tl.GrabStill()
                    if still:
                        album.ExportStills([still], OUT, f"{tag}_{tc.replace(':', '-')}_", "png")
                    report["stills"].append({"timeline": tag, "asked": tc, "got": got,
                                             "set_ok": bool(ok), "grabbed": bool(still)})
                except Exception as e:
                    fail(f"still {tag} {tc}", e)
        save()
    except Exception as e:
        fail("scrub", e)

    # ---- render the offset+wav timeline so alignment can be measured ---------
    try:
        if tl_offset is not None:
            proj.SetCurrentTimeline(tl_offset)
            res.OpenPage("deliver")
            proj.SetRenderSettings({
                "TargetDir": OUT,
                "CustomName": "render_offset_plus_wav",
                "SelectAllFrames": True,
                "FormatWidth": 1920,
                "FormatHeight": 1080,
            })
            proj.SetCurrentRenderFormatAndCodec("mp4", "H264")
            job = proj.AddRenderJob()
            report["render_job"] = job
            proj.StartRendering([job], isInteractiveMode=False)
            waited = 0
            while proj.IsRenderingInProgress() and waited < 300:
                time.sleep(2)
                waited += 2
            status = proj.GetRenderJobStatus(job) or {}
            report["render_status"] = {k: status[k] for k in status}
            step("render finished: " + json.dumps(report["render_status"]))
    except Exception as e:
        fail("render", e)

    save()
    step("report written to " + REPORT)


try:
    main()
except Exception as e:
    fail("main", e)
    save()
    raise
