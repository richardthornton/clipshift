using System.Diagnostics;

namespace ClipShiftSpike;

/// <summary>
/// The capture-to-encode spike for issue #19 — the instrument issue #14 measures.
///
/// It is a measurement instrument, not the beginnings of the app: no muxer, no UI, no config, no
/// audio. It captures one display, converts to NV12 on the GPU, feeds NVENC directly, and writes a
/// raw H.264 elementary stream, on the 60.000 fps grid of #12.
/// </summary>
internal static unsafe class Program
{
    private static volatile bool _stop;

    private static int Main(string[] argv)
    {
        try
        {
            var opts = Options.Parse(argv);
            if (opts.ShowHelp) { Options.PrintHelp(); return 0; }
            if (opts.List) return ListDisplays();
            return Run(opts);
        }
        catch (SpikeException e)
        {
            Console.Error.WriteLine("spike failed: " + e.Message);
            return 2;
        }
    }

    private static void* OpenFactory()
    {
        void* factory;
        Guid iid = Iid.IDXGIFactory1;
        Com.Check(Native.CreateDXGIFactory1(&iid, &factory), "CreateDXGIFactory1");
        return factory;
    }

    /// <summary>Returns true to stop the walk, keeping the adapter and output referenced.</summary>
    private delegate bool OutputVisitor(int index, void* adapter, void* output,
        DXGI_ADAPTER_DESC1 adapterDesc, DXGI_OUTPUT_DESC outputDesc);

    /// <summary>Walks adapters and their attached outputs, in the order the display index refers to.</summary>
    private static void ForEachOutput(void* factory, OutputVisitor visit)
    {
        int index = 0;
        for (uint a = 0; ; a++)
        {
            void* adapter;
            int hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)
                Com.Vtbl(factory)[V.Factory1_EnumAdapters1])(factory, a, &adapter);
            if (hr == Hr.DXGI_ERROR_NOT_FOUND) break;
            Com.Check(hr, "EnumAdapters1");

            DXGI_ADAPTER_DESC1 adesc;
            ((delegate* unmanaged[Stdcall]<void*, DXGI_ADAPTER_DESC1*, int>)
                Com.Vtbl(adapter)[V.Adapter1_GetDesc1])(adapter, &adesc);

            bool keepAdapter = false;
            for (uint o = 0; ; o++)
            {
                void* output;
                int ohr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)
                    Com.Vtbl(adapter)[V.Adapter_EnumOutputs])(adapter, o, &output);
                if (ohr == Hr.DXGI_ERROR_NOT_FOUND) break;
                Com.Check(ohr, "EnumOutputs");

                DXGI_OUTPUT_DESC odesc;
                ((delegate* unmanaged[Stdcall]<void*, DXGI_OUTPUT_DESC*, int>)
                    Com.Vtbl(output)[V.Output_GetDesc])(output, &odesc);

                if (odesc.AttachedToDesktop != 0)
                {
                    if (visit(index, adapter, output, adesc, odesc)) { keepAdapter = true; return; }
                    index++;
                }
                Com.Release(output);
            }
            if (!keepAdapter) Com.Release(adapter);
        }
    }

    private static int ListDisplays()
    {
        void* factory = OpenFactory();
        Console.WriteLine("attached displays:");
        ForEachOutput(factory, (i, adapter, output, adesc, odesc) =>
        {
            string name = new string((char*)odesc.DeviceName, 0, 32).TrimEnd('\0');
            string gpu = new string((char*)adesc.Description, 0, 128).TrimEnd('\0');
            long pt = ((long)(uint)(odesc.Top + 4) << 32) | (uint)(odesc.Left + 4);
            nint fromPoint = Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
            Console.WriteLine($"  [{i}] {name,-14} {odesc.Right - odesc.Left}x{odesc.Bottom - odesc.Top} " +
                              $"at ({odesc.Left},{odesc.Top})  on {gpu}");
            Console.WriteLine($"       HMONITOR from DXGI 0x{odesc.Monitor:X}, from MonitorFromPoint 0x{fromPoint:X}" +
                              (odesc.Monitor == fromPoint ? "  (agree)" : "  (DISAGREE)"));
            return false;
        });
        Com.Release(factory);
        return 0;
    }

    private static int Run(Options o)
    {
        void* factory = OpenFactory();
        void* chosenAdapter = null;
        void* chosenOutput = null;
        string adapterName = "";
        string outputName = "";
        nint monitor = 0;
        int displayWidth = 0, displayHeight = 0;

        ForEachOutput(factory, (i, adapter, output, adesc, odesc) =>
        {
            if (i != o.Display) return false;
            Com.AddRef(adapter);
            Com.AddRef(output);
            chosenAdapter = adapter;
            chosenOutput = output;
            adapterName = new string((char*)adesc.Description, 0, 128).TrimEnd('\0');
            outputName = new string((char*)odesc.DeviceName, 0, 32).TrimEnd('\0');
            return true;
        });

        if (chosenOutput == null) throw new SpikeException($"no attached display at index {o.Display}; try --list");

        using var gpu = new Gpu(chosenAdapter);

        Console.WriteLine($"display  : [{o.Display}] {outputName} on {adapterName}");
        Console.WriteLine($"variant  : {(o.Wgc ? "WGC" : o.Ownership == Ownership.Hold ? "DDA hold-the-frame" : "DDA release-immediately")}, " +
                          $"{(o.Source == ConvertSource.SrvDirect ? "SRV-direct" : "CopyResource")}, preset p{o.Preset}");
        Console.WriteLine($"nv12 RT  : {(gpu.Nv12IsRenderTargetable() ? "supported" : "NOT SUPPORTED — the convert will fail")}");

        DdaCapture? dda = null;
        WgcCapture? wgc = null;
        ICapture capture;
        if (o.Wgc)
        {
            wgc = new WgcCapture(gpu, monitor, o.Source);
            capture = wgc;
            Console.WriteLine($"capture  : {capture.Width}x{capture.Height} via Windows.Graphics.Capture, item \"{wgc.ItemDisplayName}\"");
            Console.WriteLine($"  border : {wgc.BorderProbe}");
            if (capture.Width != displayWidth || capture.Height != displayHeight)
            {
                Console.WriteLine($"  WARNING: WGC handed back a {capture.Width}x{capture.Height} item where DXGI reports");
                Console.WriteLine($"           this display as {displayWidth}x{displayHeight}. The two arms are NOT");
                Console.WriteLine($"           encoding the same thing and must not be compared until this is settled.");
            }
        }
        else
        {
            dda = new DdaCapture(gpu, chosenOutput, o.Ownership, o.Source, o.ProbeCompositedUiOnly);
            capture = dda;
            Console.WriteLine($"capture  : {capture.Width}x{capture.Height} via " +
                              $"{(dda.UsedDuplicateOutput1 ? "DuplicateOutput1" : "DuplicateOutput")}" +
                              (dda.UsedDuplicateOutput1 || dda.DuplicateOutput1Hr == 1
                                  ? "" : $" (DuplicateOutput1 returned 0x{dda.DuplicateOutput1Hr:X8})"));
        }
        using var captureLifetime = capture;

        if (capture.Width % 2 != 0 || capture.Height % 2 != 0)
            throw new SpikeException("odd display dimensions; NV12 needs even width and height");

        using var file = new FileStream(o.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        using var encoder = new Encoder(gpu, file, capture.Width, capture.Height, o.Preset, o.Qp, o.Gop);

        void* blackSrv = gpu.CreateBlackSrv();
        var counters = new Counters();

        long capacity = (long)(o.Seconds * 60.0) + 240;
        var lateness = new long[capacity];          // wake accuracy: when the tick actually started
        var tickWork = new long[capacity];          // cost of the tick itself, once started
        long ticks = 0;                             // both pre-sized: the loop must not allocate

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _stop = true; };

        // Steady-state allocation accounting starts after everything is warm.
        for (int i = 0; i < 3; i++) GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        long gen0Before = GC.CollectionCount(0);
        long gen1Before = GC.CollectionCount(1);
        long gen2Before = GC.CollectionCount(2);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long wsBefore = Environment.WorkingSet;

        // T0 is the record instant, per #12 — not the first surface, which would hang forever on a
        // static screen.
        long t0 = Clock.NowNs();
        using var grid = new FrameGrid(t0, 60.0);
        Console.WriteLine($"timer    : {(grid.UsingHighResolutionTimer ? "high-resolution waitable timer" : "COARSE waitable timer (pre-1803)")}");
        Console.WriteLine($"recording: {o.Seconds:F0}s to {o.OutputPath} (ctrl-c to stop early)");

        long deadlineNs = t0 + (long)(o.Seconds * 1_000_000_000.0);
        long frame = 0;

        // Allocated once, outside the loop: the convert runs at acquire time in every variant.
        ConvertCallback convert = srv =>
        {
            encoder.GetInputTargets(out void* luma, out void* chroma);
            gpu.ConvertToNv12(srv, luma, chroma, capture.Width, capture.Height);
        };

        var sw = Stopwatch.StartNew();
        bool inRecovery = false;
        long allocSteadyStart = -1;
        long framesSteadyStart = 0;

        while (!_stop && Clock.NowNs() < deadlineNs)
        {
            long due = grid.DueNs(frame);
            grid.WaitUntil(due);
            long woke = Clock.NowNs();

            int images = capture.Drain(convert, out bool accessLost);
            if (images > 1) counters.Superseded += images - 1;

            // #12: the grid never pauses through recovery — a pause would break #5's invariant by
            // exactly the recovery time. The spike counts it rather than rebuilding, because a
            // rebuild is app behaviour and this is an instrument.
            if (accessLost) inRecovery = true;

            if (encoder.RingFull)
            {
                counters.DuplicatedBackpressure++;
                encoder.NoteBackpressure();
                encoder.DrainBlocking();     // counted, never silently dropped
            }

            if (images == 0)
            {
                if (!capture.HasAnySurface)
                {
                    encoder.GetInputTargets(out void* luma, out void* chroma);
                    gpu.ConvertToNv12(blackSrv, luma, chroma, capture.Width, capture.Height);
                    counters.BlackLeadIn++;
                }
                else
                {
                    encoder.CopyPreviousInto(gpu);
                    if (inRecovery) counters.DuplicatedRecovery++; else counters.DuplicatedIdle++;
                }
            }

            encoder.Submit(frame);
            if (ticks < lateness.Length)
            {
                lateness[ticks] = woke - due;
                tickWork[ticks] = Clock.NowNs() - woke;
            }
            ticks++;
            frame++;
            if (frame == 60) { allocSteadyStart = GC.GetAllocatedBytesForCurrentThread(); framesSteadyStart = frame; }

            // Catch up if the loop itself overran: emit the ticks we owe rather than skipping them.
            long shouldHave = (Clock.NowNs() - t0) / grid.PeriodNs + 1;
            while (frame < shouldHave && !_stop)
            {
                if (encoder.RingFull) { encoder.NoteBackpressure(); encoder.DrainBlocking(); }
                encoder.CopyPreviousInto(gpu);
                encoder.Submit(frame);
                counters.DuplicatedLagged++;
                frame++;
                ticks++;
            }
        }

        double elapsed = sw.Elapsed.TotalSeconds;
        encoder.Finish();

        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        long wsAfter = Environment.WorkingSet;
        counters.CaptureMissed = dda?.AccumulatedFramesLost ?? 0;

        Com.Release(blackSrv);
        Com.Release(chosenOutput);
        Com.Release(chosenAdapter);
        Com.Release(factory);

        Report(o, dda, wgc, encoder, counters, grid, elapsed, frame, lateness, tickWork, ticks,
               gen0Before, gen1Before, gen2Before, allocBefore, allocAfter, wsBefore, wsAfter,
               allocSteadyStart, framesSteadyStart);
        return 0;
    }

    private static void Distribution(TextWriter w, string label, long[] values, int n, long periodNs)
    {
        var sorted = values.AsSpan(0, n).ToArray();
        Array.Sort(sorted);
        double mean = 0;
        for (int i = 0; i < n; i++) mean += sorted[i];
        mean /= n;
        long overPeriod = 0;
        for (int i = 0; i < n; i++) if (sorted[i] > periodNs) overPeriod++;
        w.WriteLine($"    {label,-14} mean {mean / 1000.0,8:F1}  p50 {sorted[n / 2] / 1000.0,8:F1}  " +
                    $"p99 {sorted[(int)(n * 0.99)] / 1000.0,8:F1}  max {sorted[n - 1] / 1000.0,8:F1}" +
                    $"   over one interval: {overPeriod}");
    }

    private static void Report(
        Options o, DdaCapture? capture, WgcCapture? wgc, Encoder encoder, Counters counters, FrameGrid grid,
        double elapsed, long frames, long[] lateness, long[] tickWork, long ticks,
        long gen0Before, long gen1Before, long gen2Before,
        long allocBefore, long allocAfter, long wsBefore, long wsAfter,
        long allocSteadyStart, long framesSteadyStart)
    {
        var w = Console.Out;
        w.WriteLine();
        w.WriteLine("=== spike result ===");
        w.WriteLine($"  elapsed              {elapsed:F2} s");
        w.WriteLine($"  frames submitted     {frames}  ({frames / elapsed:F3} fps against a 60.000 grid)");
        w.WriteLine($"  bytes written        {encoder.BytesWritten:N0}  ({encoder.BytesWritten * 8.0 / elapsed / 1e6:F1} Mb/s)");
        w.WriteLine($"  keyframes            {encoder.KeyFrames}  (expected ~{Math.Round(frames / (double)o.Gop)})");
        w.WriteLine($"  projected 4h size    {encoder.BytesWritten / elapsed * 14400 / 1e9:F1} GB");
        w.WriteLine();

        counters.Report(w, frames);
        w.WriteLine();

        int n = (int)Math.Min(ticks, lateness.Length);
        if (n > 0)
        {
            w.WriteLine("  pacing, in microseconds:");
            Distribution(w, "wake lateness", lateness, n, grid.PeriodNs);
            Distribution(w, "tick work", tickWork, n, grid.PeriodNs);
            w.WriteLine("    wake lateness is how late the grid started the tick; tick work is what the");
            w.WriteLine("    tick then cost. Only the sum exceeding one interval loses a frame.");
        }
        w.WriteLine();

        w.WriteLine("  zero-copy confirmation (properties, not inference):");
        w.WriteLine($"    encoder input is a D3D11 NV12 texture registered via NvEncRegisterResource");
        w.WriteLine($"      (NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX, pitch 0) and mapped per frame — the");
        w.WriteLine($"      external-resource route from the Programming Guide.");
        w.WriteLine($"    NV12 textures are Usage=DEFAULT, CPUAccessFlags=0: no Map, no staging texture,");
        w.WriteLine($"      and the spike never creates one.");
        w.WriteLine($"    the only CPU-visible byte flow is the bitstream copy out of NvEncLockBitstream,");
        w.WriteLine($"      which is unavoidable — that is the output, not a round-trip.");
        w.WriteLine($"    convert path        {(o.Source == ConvertSource.SrvDirect ? "SRV directly on the captured surface — no full-frame copy" : "CopyResource into a staging BGRA texture, then convert")}");
        w.WriteLine();

        w.WriteLine("  managed allocation on the hot path:");
        w.WriteLine($"    gen0 collections     {GC.CollectionCount(0) - gen0Before}");
        w.WriteLine($"    gen1 collections     {GC.CollectionCount(1) - gen1Before}");
        w.WriteLine($"    gen2 collections     {GC.CollectionCount(2) - gen2Before}");
        w.WriteLine($"    bytes allocated      {allocAfter - allocBefore:N0} total over {frames} frames, " +
                    "including one-time lazy buffers (the FileStream's 1 MB among them)");
        if (allocSteadyStart >= 0 && frames > framesSteadyStart)
        {
            long steady = allocAfter - allocSteadyStart;
            long steadyFrames = frames - framesSteadyStart;
            w.WriteLine($"    steady state         {steady:N0} bytes over {steadyFrames} frames " +
                        $"({steady / (double)steadyFrames:F2} B/frame) — measured from frame 60 on");
        }
        w.WriteLine($"    working set delta    {(wsAfter - wsBefore) / 1024.0 / 1024.0:F1} MB");
        w.WriteLine();

        if (wgc is not null)
        {
            w.WriteLine("  WGC probes:");
            w.WriteLine($"    frames delivered     {wgc.FramesDelivered} (empty polls {wgc.NullPolls})");
            w.WriteLine($"    capture border       {wgc.BorderProbe}   (§6)");
            w.WriteLine($"    IsCursorCaptureEnabled settable: {wgc.CursorCaptureDisabled}");
            if (wgc.TimeAgeSamples > 0)
            {
                double meanAge = wgc.TimeAgeSumNs / wgc.TimeAgeSamples / 1000.0;
                w.WriteLine($"    SystemRelativeTime age  mean {meanAge:F1} us, min {wgc.TimeAgeMinNs / 1000.0:F1} us, " +
                            $"max {wgc.TimeAgeMaxNs / 1000.0:F1} us");
            }
            w.WriteLine("    the managed-allocation figure above is the answer to §11.6: whether the CsWinRT");
            w.WriteLine("    projection allocates per TryGetNextFrame. Compare it against a DDA run.");
            return;
        }

        w.WriteLine("  DDA probes:");
        if (capture is null) return;
        w.WriteLine($"    acquires             {capture.AcquireCount} (timeouts {capture.TimeoutCount}, pointer-only {capture.PointerOnlyUpdates})");
        w.WriteLine($"    AccumulatedFrames>1  {capture.AccumulatedFramesLost} presents lost upstream");
        if (capture.PresentAgeSamples > 0)
        {
            double meanAge = capture.PresentAgeSumNs / capture.PresentAgeSamples / 1000.0;
            double spread = (capture.PresentAgeMaxNs - capture.PresentAgeMinNs) / 1000.0;
            w.WriteLine($"    LastPresentTime age  mean {meanAge:F1} us, min {capture.PresentAgeMinNs / 1000.0:F1} us, " +
                        $"max {capture.PresentAgeMaxNs / 1000.0:F1} us, spread {spread:F1} us  (§11.2 probe)");
            w.WriteLine($"      a constant offset is harmless to the sync design; the spread is what would not be.");
        }
        if (capture.CompositedUiOnlyHr != 1)
        {
            w.WriteLine(capture.CompositedUiOnlyHr >= 0
                ? "    DXGI_OUTDUPL_COMPOSITED_UI_CAPTURE_ONLY: accepted by DuplicateOutput1 (§11.9 probe)"
                : $"    DXGI_OUTDUPL_COMPOSITED_UI_CAPTURE_ONLY: rejected, 0x{capture.CompositedUiOnlyHr:X8} (§11.9 probe)");
        }
    }
}

internal sealed class Options
{
    public bool ShowHelp, List, ProbeCompositedUiOnly, Wgc;
    public int Display, Preset = 5, Qp = 20, Gop = 60;
    public double Seconds = 20;
    public string OutputPath = "spike.h264";
    public Ownership Ownership = Ownership.Hold;
    public ConvertSource Source = ConvertSource.SrvDirect;

    public static Options Parse(string[] argv)
    {
        var o = new Options();
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            string Next() => ++i < argv.Length ? argv[i] : throw new SpikeException($"{a} needs a value");
            switch (a)
            {
                case "-h" or "--help": o.ShowHelp = true; break;
                case "--list": o.List = true; break;
                case "--display": o.Display = int.Parse(Next()); break;
                case "--seconds": o.Seconds = double.Parse(Next()); break;
                case "--out": o.OutputPath = Next(); break;
                case "--preset": o.Preset = int.Parse(Next()); break;
                case "--qp": o.Qp = int.Parse(Next()); break;
                case "--gop": o.Gop = int.Parse(Next()); break;
                case "--hold": o.Ownership = Ownership.Hold; break;
                case "--release": o.Ownership = Ownership.Release; break;
                case "--srv-direct": o.Source = ConvertSource.SrvDirect; break;
                case "--copy": o.Source = ConvertSource.CopyResource; break;
                case "--probe-composited-ui": o.ProbeCompositedUiOnly = true; break;
                case "--wgc": o.Wgc = true; break;
                case "--wgc-slot": WgcCapture.MonitorSlot = int.Parse(Next()); break;
                case "--diag": Encoder.Diagnostics = true; break;
                default: throw new SpikeException($"unknown argument {a}");
            }
        }
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            ClipShiftSpike — the capture-to-encode instrument for issue #14.

              --list                  enumerate attached displays and exit
              --display N             which display to capture (default 0)
              --seconds N             how long to record (default 20)
              --out PATH              output elementary stream (default spike.h264)

            variant flags, which is the whole point of the spike:
              --wgc                   use Windows.Graphics.Capture instead of DDA — the
                                      comparison arm; --hold/--release do not apply to it
              --hold | --release      DDA frame ownership (default --hold)
              --srv-direct | --copy   convert from the captured surface, or from a CopyResource
                                      of it (default --srv-direct)
              --preset N              NVENC preset p4..p7 (default 5, per issue #10)
              --qp N                  CONSTQP value (default 20, per issue #10)
              --gop N                 keyframe interval in frames (default 60 = 1 s)

              --probe-composited-ui   probe DXGI_OUTDUPL_COMPOSITED_UI_CAPTURE_ONLY (§11.9)

            The output is a raw H.264 elementary stream, not an MP4: the muxer is deliberately out of
            scope here. Play it with `ffplay spike.h264` or wrap it with
            `ffmpeg -r 60 -i spike.h264 -c copy spike.mp4`.
            """);
    }
}
