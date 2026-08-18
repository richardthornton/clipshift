using System;
using System.Collections.Generic;
using System.IO;

namespace ClipShift.ResamplerQuality
{
    /// <summary>
    /// ClipShift #20 -- does the vendored WdlResampler clear the quality bar #16 set, or
    /// does the libsamplerate fallback fire?
    ///
    /// Five measurements, in dependency order:
    ///   1. group delay D, by impulse -- needed by 2, and a deliverable in its own right
    ///   2. SNR against the bar, by libsamplerate's own published method
    ///   3. steady-state allocation on the hot path, patched vs pristine
    ///   4. varispeed bounds sweep -- hard ratio switches across channel counts
    ///   5. throughput, so "clears the bar" can be read against what it costs
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // The first pass through this sweep held the phase table near WDL's default and
            // varied the filter length, which is the obvious knob and the wrong one. SNR
            // turned out to track sinc_interpsize and to ignore sinc_size almost entirely,
            // so the sweep is laid out to show that: a tap sweep at fixed phase count, a
            // phase sweep at fixed taps, and enough crosses to confirm the two axes are
            // separable. WDL's stated maximum, 8192/4096, anchors the top.
            var settings = new List<SincSetting>
            {
                // phase table fixed at 128, taps varying
                new(64, 128),
                new(128, 128),
                new(512, 128),
                new(1024, 128),

                // taps fixed at 64, phase table varying
                new(64, 32),        // WDL's own defaults
                new(64, 64),
                new(64, 256),
                new(64, 512),
                new(64, 1024),
                new(64, 4096),

                // crosses
                new(128, 1024),
                new(128, 4096),
                new(256, 1024),
                new(384, 64),
                new(2048, 256),
                new(8192, 4096),    // WDL's maximum
            };

            var points = new List<OperatingPoint>
            {
                new("48000 -> 48000",   48000.0,  48000.0),
                new("48002.4 -> 48000 (+50ppm)", 48002.4, 48000.0, isGate: true, barDb: 110.0),
                new("47997.6 -> 48000 (-50ppm)", 47997.6, 48000.0, isGate: true, barDb: 110.0),
                new("44100 -> 48000",   44100.0,  48000.0, isGate: true, barDb: 100.0),
                new("32000 -> 48000",   32000.0,  48000.0),
                new("96000 -> 48000",   96000.0,  48000.0),
            };

            Console.WriteLine("================================================================================");
            Console.WriteLine(" ClipShift #20 : WdlResampler against #16's quality bar");
            Console.WriteLine("================================================================================");
            Console.WriteLine($" runtime      : {Environment.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")}");
            Console.WriteLine($" machine      : {Environment.ProcessorCount} logical processors");
            Console.WriteLine($" server GC    : {System.Runtime.GCSettings.IsServerGC}");

            bool harnessOk = Tests.ValidateHarness();

            var delays = Tests.DelayTable(settings, points);
            var snr = Tests.SnrSweep(settings, points, delays);

            // The allocation and bounds tests characterise the implementation, not the
            // setting, so they run once at a representative mid-size configuration.
            var repr = new SincSetting(384, 64);
            bool allocOk = Tests.AllocationTest(repr);
            bool boundsOk = Tests.VarispeedBounds(repr);

            Tests.Throughput(settings);

            var candidates = new List<SincSetting> { new(64, 256), new(64, 512), new(256, 1024) };
            Tests.LongBlock(candidates, points);
            Tests.RebuildPatchEquivalence(new SincSetting(256, 1024));

            // ---- verdict ------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("  VERDICT");
            Console.WriteLine();

            var clearing = new List<SincSetting>();
            foreach (var s in settings)
            {
                // Every gated measurement for this setting must pass, not just the best one.
                bool all = snr.TrueForAll(r => !r.Point.IsGate
                                               || r.Setting.Taps != s.Taps
                                               || r.Setting.Interp != s.Interp
                                               || r.Pass);
                if (all) clearing.Add(s);
            }

            if (clearing.Count == 0)
            {
                Console.WriteLine("     No WDL sinc setting clears every gate. Per #16 the fallback fires:");
                Console.WriteLine("     libsamplerate (BSD-2), same harness, different implementation behind it.");
            }
            else
            {
                Console.WriteLine($"     {clearing.Count} of {settings.Count} settings clear every gate. Cheapest:");
                foreach (var s in clearing)
                    Console.WriteLine($"       {s}");
            }

            Console.WriteLine();
            Console.WriteLine($"     harness validation           : {(harnessOk ? "PASS" : "FAIL")}");
            Console.WriteLine($"     zero steady-state allocation : {(allocOk ? "PASS" : "FAIL")}");
            Console.WriteLine($"     varispeed bounds sweep       : {(boundsOk ? "PASS" : "FAIL")}");

            string outDir = args.Length > 0 ? args[0] : "results";
            Directory.CreateDirectory(outDir);
            string csv = Path.Combine(outDir, "snr.csv");
            File.WriteAllText(csv, Tests.ToCsv(snr));
            Console.WriteLine();
            Console.WriteLine($"     SNR table written to {Path.GetFullPath(csv)}");

            return (harnessOk && allocOk && boundsOk) ? 0 : 1;
        }
    }
}
