using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClipShift.Audio.Resampling;

namespace ClipShift.ResamplerQuality
{
    internal sealed class SnrResult
    {
        public SincSetting Setting;
        public OperatingPoint Point;
        public double Freq;
        public double Snr;
        public double Peak;
        public int Delay;
        public int Generated;
        public int Expected;
        public bool Pass;
    }

    internal static class Tests
    {
        // Frequencies in cycles per sample, both from libsamplerate's own snr_bw_test.c.
        //
        // The low tone is what its ratio tests use: at 48 kHz output that is 533 Hz,
        // deliberately low so every aliasing product of it lands somewhere the FFT can see.
        //
        // The high tone is what its 1.33-ratio test uses, and it is the one that matters
        // here. Near unity the fractional phase advances by ~5e-5 per output sample, so a
        // low tone's interpolation error is a very slowly varying modulation that lands in
        // the bins immediately either side of the carrier -- exactly where calc_snr's side
        // lobe smoothing wipes it out. A tone up at 0.35 puts the error somewhere the
        // measurement can actually see it.
        private const double LowFreq = 0.01111111111;
        private const double HighFreq = 0.3511111111;

        // ------------------------------------------------------------------------------
        // 1. Group delay D, by impulse.
        // ------------------------------------------------------------------------------

        public static Dictionary<(int, int, string), int> DelayTable(
            IReadOnlyList<SincSetting> settings, IReadOnlyList<OperatingPoint> points)
        {
            var table = new Dictionary<(int, int, string), int>();

            Console.WriteLine();
            Console.WriteLine("  1. GROUP DELAY (impulse test)");
            Console.WriteLine("     An impulse at input frame 1024 should emerge at output frame 1024/ratio.");
            Console.WriteLine("     D is how far past that it actually lands: the number ClipShift pre-rolls");
            Console.WriteLine("     and discards at init, per #16. 'side' is the largest sample outside the main lobe,");
            Console.WriteLine("     relative to the peak -- a check that the peak found is the main lobe.");
            Console.WriteLine();
            Console.WriteLine("     setting          operating point            peak idx        D    peak val      side");
            Console.WriteLine("     ---------------  ------------------------  ----------  -------  ----------  --------");

            foreach (var s in settings)
            {
                foreach (var p in points)
                {
                    double d = Driver.MeasureDelay(s, p.InRate, p.OutRate, out double peak, out double asym, out int idx);
                    table[(s.Taps, s.Interp, p.Name)] = (int)Math.Ceiling(Math.Max(d, 0));
                    Console.WriteLine($"     {s,-15}  {p.Name,-24}  {idx,10}  {d,7:F3}  {peak,10:F4}  {asym,8:F5}");
                }
            }

            return table;
        }

        // ------------------------------------------------------------------------------
        // 2. SNR, by libsamplerate's own method.
        // ------------------------------------------------------------------------------

        public static SnrResult Snr(SincSetting setting, OperatingPoint point, double[] freqs, int expectedPeaks,
                                    int delay, bool verbose)
        {
            // The analysed block must contain the WHOLE converted signal with zeros at both
            // ends. libsamplerate fills its analysis block edge to edge, which it can do
            // because it never has to leave room for a group delay; leaving the tail short
            // by D would truncate the Hanning window partway up its skirt and swamp a
            // 110 dB measurement with the resulting step. So the input is shortened instead
            // and the conversion is allowed to finish inside the block.
            int outFrames = setting.Taps > 1024 ? 1 << 16 : 1 << 15;
            int slack = delay + setting.Taps + 128;
            int inFrames = (int)((outFrames - slack) * point.WdlRatio);

            if (inFrames < 4096)
                throw new InvalidOperationException($"{setting} leaves too little room at {point.Name}.");

            var input = new float[inFrames];
            SignalGen.WindowedSines(freqs, 1.0, input, inFrames);

            var output = new float[outFrames];
            var r = Driver.Create(setting, point.InRate, point.OutRate);
            int got = Driver.Run(r, input, inFrames, output, outFrames, 1);

            double peak = SignalGen.FindPeak(output, outFrames);
            double snr = SnrCalculator.Calculate(output, outFrames, expectedPeaks);

            var result = new SnrResult
            {
                Setting = setting,
                Point = point,
                Freq = freqs[0],
                Snr = snr,
                Peak = peak,
                Delay = delay,
                Generated = got,
                Expected = (int)(inFrames / point.WdlRatio),
                Pass = !point.IsGate || snr >= point.BarDb,
            };

            if (verbose)
                Console.WriteLine($"       in {inFrames} -> out {got}/{outFrames} (expected >= {result.Expected}), "
                                  + $"peak {peak:F4}, SNR {snr:F2} dB");

            return result;
        }

        /// <summary>
        /// Before any WDL number is worth reading, the harness has to be shown to measure
        /// the same thing libsamplerate's does. WDL's linear and point-sampling modes are
        /// the same algorithms as SRC_LINEAR and SRC_ZERO_ORDER_HOLD, and libsamplerate
        /// publishes the SNR floors its own suite asserts against for both. If this table
        /// lands on those floors, the port is measuring what it claims to.
        /// </summary>
        public static bool ValidateHarness()
        {
            Console.WriteLine();
            Console.WriteLine("  0. HARNESS VALIDATION");
            Console.WriteLine("     WDL's linear and point-sampling modes are the same algorithms as");
            Console.WriteLine("     libsamplerate's SRC_LINEAR and SRC_ZERO_ORDER_HOLD, at the same ratios,");
            Console.WriteLine("     with the same tone. 'floor' is the value snr_bw_test.c asserts against.");
            Console.WriteLine();
            Console.WriteLine("     mode             src_ratio (out/in)   measured    floor    ");
            Console.WriteLine("     ---------------  ------------------   ---------   ---------");

            // (src_ratio, ZOH floor, linear floor) straight out of snr_bw_test.c.
            (double ratio, double zoh, double linear)[] cases =
            {
                (3.0,   28.0, 73.0),
                (0.6,   36.0, 73.0),
                (0.3,   36.0, 73.0),
                (1.001, 38.0, 77.0),
            };

            bool ok = true;

            foreach (var (mode, name) in new[] { (ResamplerMode.ZeroOrderHold, "zero-order hold"), (ResamplerMode.Linear, "linear") })
            {
                foreach (var c in cases)
                {
                    var setting = new SincSetting(mode);
                    var point = new OperatingPoint($"ratio {c.ratio}", 48000.0, 48000.0 * c.ratio);
                    var res = Snr(setting, point, new[] { LowFreq }, 1, 0, verbose: false);

                    double floor = mode == ResamplerMode.ZeroOrderHold ? c.zoh : c.linear;
                    bool pass = res.Snr >= floor;
                    ok &= pass;

                    Console.WriteLine($"     {name,-15}  {c.ratio,18}   {res.Snr,6:F2} dB   {floor,6:F2} dB  {(pass ? "ok" : "MISMATCH")}");
                }
            }

            Console.WriteLine();
            Console.WriteLine(ok
                ? "     PASS - the port reproduces libsamplerate's own floors on the two algorithms"
                : "     FAIL - the port does not reproduce libsamplerate's floors; no WDL number below is trustworthy");

            return ok;
        }

        public static List<SnrResult> SnrSweep(IReadOnlyList<SincSetting> settings, IReadOnlyList<OperatingPoint> points,
                                               Dictionary<(int, int, string), int> delays)
        {
            var results = new List<SnrResult>();

            Console.WriteLine();
            Console.WriteLine("  2. SIGNAL-TO-NOISE RATIO (port of libsamplerate snr_bw_test.c + calc_snr.c)");
            Console.WriteLine("     Two Hanning-windowed tones per point, reported worst case: 0.0111 and");
            Console.WriteLine("     0.3511 cycles/sample. The high tone is skipped where it would sit above");
            Console.WriteLine("     the 24 kHz output Nyquist, since removing it is then correct behaviour.");
            Console.WriteLine("     Gate: >= 110 dB near unity, >= 100 dB at 44.1 -> 48 (#16).");
            Console.WriteLine();

            Console.Write($"     {"setting",-15}");
            foreach (var p in points) Console.Write($"  {p.Name,24}");
            Console.WriteLine();
            Console.Write($"     {new string('-', 15)}");
            foreach (var _ in points) Console.Write($"  {new string('-', 24)}");
            Console.WriteLine();

            foreach (var s in settings)
            {
                Console.Write($"     {s,-15}");
                foreach (var p in points)
                {
                    int d = delays[(s.Taps, s.Interp, p.Name)];

                    var low = Snr(s, p, new[] { LowFreq }, 1, d, verbose: false);
                    results.Add(low);

                    SnrResult worst = low;

                    // The high tone is only a fair test when it survives the conversion:
                    // above the output Nyquist the resampler is supposed to remove it, and
                    // what is left to measure is the noise floor on its own.
                    if (HighFreq * p.InRate < p.OutRate / 2.0)
                    {
                        var high = Snr(s, p, new[] { HighFreq }, 1, d, verbose: false);
                        results.Add(high);
                        if (high.Snr < worst.Snr) worst = high;
                    }

                    string mark = p.IsGate ? (worst.Pass ? " PASS" : " FAIL") : "     ";
                    Console.Write($"  {worst.Snr,15:F2} dB{mark,5}");
                }
                Console.WriteLine();
            }

            return results;
        }

        // ------------------------------------------------------------------------------
        // 3. Steady-state allocation on the hot path.
        // ------------------------------------------------------------------------------

        public static bool AllocationTest(SincSetting setting)
        {
            Console.WriteLine();
            Console.WriteLine("  3. STEADY-STATE ALLOCATION ON THE HOT PATH");
            Console.WriteLine("     10 ms pulls at 48 kHz stereo, ratio re-set every pull with +/-50 ppm of");
            Console.WriteLine("     jitter -- what #16's feed-forward loop does at a 100 Hz control rate.");
            Console.WriteLine();

            const int nch = 2;
            const int chunkFrames = 480;          // 10 ms at 48 kHz
            const int warmPulls = 500;
            const int measuredPulls = 20000;      // 200 s of audio

            var source = new float[chunkFrames * 4 * nch];
            for (int i = 0; i < source.Length; i++)
                source[i] = (float)(0.25 * Math.Sin(i * 0.01));
            var outbuf = new float[chunkFrames * nch];

            // --- patched ---
            var r = Driver.Create(setting, 48000.0, 48000.0);
            long patchedBytes = DrivePatched(r, source, outbuf, chunkFrames, nch, warmPulls, measuredPulls, out int growths, out int rebuilds);

            // --- pristine baseline, same work ---
            var b = new Audio.Resampling.Baseline.WdlResampler();
            b.SetMode(true, 0, true, setting.Taps, setting.Interp);
            b.SetFeedMode(false);
            b.SetRates(48000.0, 48000.0);
            long baselineBytes = DriveBaseline(b, source, outbuf, chunkFrames, nch, warmPulls, measuredPulls);

            double hours = measuredPulls * chunkFrames / 48000.0 / 3600.0;
            double perFourHours = baselineBytes / hours * 4.0;

            Console.WriteLine($"     patched   : {patchedBytes,12:N0} bytes over {measuredPulls:N0} pulls   "
                              + $"({growths} buffer growths, {rebuilds} filter rebuilds)");
            Console.WriteLine($"     baseline  : {baselineBytes,12:N0} bytes over {measuredPulls:N0} pulls   "
                              + $"= {baselineBytes / (double)measuredPulls:F0} bytes/pull");
            Console.WriteLine($"                 baseline extrapolates to {perFourHours / (1024.0 * 1024.0):N0} MB of garbage "
                              + "per four-hour session, per sink");
            Console.WriteLine();
            Console.WriteLine(patchedBytes == 0
                ? "     PASS - zero managed bytes allocated in the steady state."
                : $"     FAIL - {patchedBytes} bytes allocated in the steady state.");

            return patchedBytes == 0;
        }

        private static long DrivePatched(WdlResampler r, float[] source, float[] outbuf, int chunkFrames, int nch,
                                         int warmPulls, int measuredPulls, out int growths, out int rebuilds)
        {
            int srcFrames = source.Length / nch;

            for (int i = 0; i < warmPulls; i++)
                OnePullPatched(r, source, srcFrames, outbuf, chunkFrames, nch, i);

            int g0 = r.RsinbufGrowths, f0 = r.FilterRebuilds;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < measuredPulls; i++)
                OnePullPatched(r, source, srcFrames, outbuf, chunkFrames, nch, i);

            long after = GC.GetAllocatedBytesForCurrentThread();

            growths = r.RsinbufGrowths - g0;
            rebuilds = r.FilterRebuilds - f0;
            return after - before;
        }

        private static void OnePullPatched(WdlResampler r, float[] source, int srcFrames, float[] outbuf,
                                           int chunkFrames, int nch, int i)
        {
            // +/-50 ppm of device drift, re-fed every pull, exactly as #16's feed-forward
            // control loop will. This is what makes the requested input length jitter, and
            // the jitter is what made the original allocate.
            double ppm = 50.0 * Math.Sin(i * 0.017);
            r.SetRates(48000.0 * (1.0 + ppm * 1e-6), 48000.0);

            int need = r.ResamplePrepare(chunkFrames, nch, out float[] inbuf, out int inoff);
            int avail = Math.Min(need, srcFrames);
            Array.Copy(source, 0, inbuf, inoff, avail * nch);
            r.ResampleOut(outbuf, 0, avail, chunkFrames, nch);
        }

        private static long DriveBaseline(Audio.Resampling.Baseline.WdlResampler r, float[] source, float[] outbuf,
                                          int chunkFrames, int nch, int warmPulls, int measuredPulls)
        {
            int srcFrames = source.Length / nch;

            for (int i = 0; i < warmPulls; i++)
                OnePullBaseline(r, source, srcFrames, outbuf, chunkFrames, nch, i);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < measuredPulls; i++)
                OnePullBaseline(r, source, srcFrames, outbuf, chunkFrames, nch, i);

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private static void OnePullBaseline(Audio.Resampling.Baseline.WdlResampler r, float[] source, int srcFrames,
                                            float[] outbuf, int chunkFrames, int nch, int i)
        {
            double ppm = 50.0 * Math.Sin(i * 0.017);
            r.SetRates(48000.0 * (1.0 + ppm * 1e-6), 48000.0);

            int need = r.ResamplePrepare(chunkFrames, nch, out float[] inbuf, out int inoff);
            int avail = Math.Min(need, srcFrames);
            Array.Copy(source, 0, inbuf, inoff, avail * nch);
            r.ResampleOut(outbuf, 0, avail, chunkFrames, nch);
        }

        // ------------------------------------------------------------------------------
        // 4. Varispeed bounds sweep (port of varispeed_test.c's set_ratio_test).
        // ------------------------------------------------------------------------------

        public static bool VarispeedBounds(SincSetting setting)
        {
            Console.WriteLine();
            Console.WriteLine("  4. VARISPEED BOUNDS SWEEP (port of libsamplerate varispeed_test.c)");
            Console.WriteLine("     Hard ratio switches mid-stream across 1..9 channels. Asserts termination,");
            Console.WriteLine("     non-empty output and no NaNs. #17's lesson: a ratio change is a buffer");
            Console.WriteLine("     management bug generator before it is a quality problem.");
            Console.WriteLine();

            double[] ratios = { 0.1, 0.01, 20 };
            int cases = 0, failures = 0;

            for (int chan = 1; chan <= 9; chan++)
            {
                for (int r1 = 0; r1 < ratios.Length; r1++)
                {
                    for (int r2 = 0; r2 < ratios.Length; r2++)
                    {
                        if (r1 == r2) continue;
                        cases++;
                        string why = SetRatioCase(setting, chan, ratios[r1], ratios[r2]);
                        if (why != null)
                        {
                            failures++;
                            Console.WriteLine($"     FAIL  {chan} ch, {ratios[r1]} -> {ratios[r2]}: {why}");
                        }
                    }
                }
            }

            Console.WriteLine(failures == 0
                ? $"     PASS - {cases} cases, all terminated with finite output."
                : $"     FAIL - {failures} of {cases} cases.");

            return failures == 0;
        }

        /// <summary>Returns null on success, or a description of the failure.</summary>
        private static string SetRatioCase(SincSetting setting, int channels, double initialRatio, double secondRatio)
        {
            const int bufferLen = 1 << 14;
            const int maxLoopCount = 100000;
            const int chunkSize = 128;

            int totalInputFrames = bufferLen;
            int totalOutputFrames = 25 * bufferLen;   // max upsample ratio is 20; leave room

            // Interested in array boundary conditions, so all-zero input is fine -- except
            // that all-zero output would make the NaN check vacuous, so the input carries a
            // tone instead. Zeros would pass a NaN check that a real signal fails.
            var input = new float[totalInputFrames * channels];
            for (int i = 0; i < input.Length; i++)
                input[i] = (float)(0.5 * Math.Sin(i * 0.03));
            var output = new float[totalOutputFrames * channels];

            // libsamplerate's ratio is out/in; WDL's SetRates takes the rates themselves.
            var r = Driver.Create(setting, 48000.0, 48000.0 * initialRatio);

            int totalUsed = 0, totalGen = 0, k;

            for (k = 0; k < maxLoopCount; k++)
            {
                if (k == 1)
                    r.SetRates(48000.0, 48000.0 * secondRatio);

                int want = Math.Min(chunkSize, totalOutputFrames - totalGen);
                if (want <= 0) break;

                int need = r.ResamplePrepare(want, channels, out float[] inbuf, out int inoff);
                int avail = Math.Min(need, totalInputFrames - totalUsed);
                if (avail > 0)
                    Array.Copy(input, totalUsed * channels, inbuf, inoff, avail * channels);

                int got = r.ResampleOut(output, totalGen * channels, avail, want, channels);

                totalUsed += avail;
                totalGen += got;

                if (avail == 0 && got == 0)
                    break;
            }

            if (k >= maxLoopCount) return "did not terminate";
            if (totalGen <= 0) return "produced no output";

            for (int i = 0; i < totalGen * channels; i++)
                if (float.IsNaN(output[i]) || float.IsInfinity(output[i]))
                    return $"non-finite output at index {i}";

            return null;
        }

        // ------------------------------------------------------------------------------
        // 5. Throughput, and the cost of the sinc-table rebuild.
        // ------------------------------------------------------------------------------

        public static void Throughput(IReadOnlyList<SincSetting> settings)
        {
            Console.WriteLine();
            Console.WriteLine("  5. THROUGHPUT AT THE OPERATING POINT");
            Console.WriteLine("     48 kHz stereo, 10 ms pulls, ratio re-set every pull. 'core' is the fraction");
            Console.WriteLine("     of one core one sink costs in real time. 'rebuild' repeats the run with");
            Console.WriteLine("     FilterRebuildEpsilon = 0, i.e. the unpatched sinc-table rebuild policy.");
            Console.WriteLine();
            Console.WriteLine("     'table' is the sinc coefficient table, which is what the phase count costs:");
            Console.WriteLine("     (taps + 1) * phases floats, allocated once at init.");
            Console.WriteLine();
            Console.WriteLine("     setting          table       ns/frame    core     rebuild ns/frame   rebuild core   x");
            Console.WriteLine("     ---------------  ---------   ---------   ------   ----------------   ------------   -----");

            foreach (var s in settings)
            {
                double gated = TimeOne(s, 1e-4);
                double ungated = TimeOne(s, 0.0);

                double coreGated = gated * 48000.0 * 1e-9;
                double coreUngated = ungated * 48000.0 * 1e-9;
                double tableKb = (s.Taps + 1L) * s.Interp * sizeof(float) / 1024.0;

                Console.WriteLine($"     {s,-15}  {tableKb,6:N0} KB   {gated,9:F1}   {coreGated,6:P1}   {ungated,16:F1}   {coreUngated,12:P1}   {ungated / gated,4:F1}x");
            }
        }

        private static double TimeOne(SincSetting setting, double epsilon)
        {
            const int nch = 2;
            const int chunkFrames = 480;
            int pulls = setting.Taps >= 4096 ? 300 : 3000;

            var r = Driver.Create(setting, 48000.0, 48000.0, epsilon);
            var source = new float[chunkFrames * 4 * nch];
            for (int i = 0; i < source.Length; i++)
                source[i] = (float)(0.25 * Math.Sin(i * 0.01));
            var outbuf = new float[chunkFrames * nch];
            int srcFrames = source.Length / nch;

            for (int i = 0; i < 100; i++)
                OnePullPatched(r, source, srcFrames, outbuf, chunkFrames, nch, i);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < pulls; i++)
                OnePullPatched(r, source, srcFrames, outbuf, chunkFrames, nch, i);
            sw.Stop();

            return sw.Elapsed.TotalMilliseconds * 1e6 / (pulls * (double)chunkFrames);
        }

        // ------------------------------------------------------------------------------
        // 6. Long-block SNR, and the audibility of the filter-rebuild patch.
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Near unity the phase advances by ~5e-5 per output frame, so a 32768-sample block
        /// only sweeps the phase table 1.6 times. If that is too short, the measurement
        /// flatters the resampler. Re-run the gate at 2^18 samples and see whether the
        /// number moves.
        /// </summary>
        public static void LongBlock(IReadOnlyList<SincSetting> candidates, IReadOnlyList<OperatingPoint> points)
        {
            Console.WriteLine();
            Console.WriteLine("  6. LONG-BLOCK CHECK AT THE GATE POINTS");
            Console.WriteLine("     Same measurement over 2^18 output frames (5.5 s) instead of 2^15, to show");
            Console.WriteLine("     the short block is not flattering the near-unity result.");
            Console.WriteLine();
            Console.WriteLine("     setting          operating point            short block   long block   delta");
            Console.WriteLine("     ---------------  ------------------------   -----------   ----------   ------");

            foreach (var s in candidates)
            {
                foreach (var p in points)
                {
                    if (!p.IsGate) continue;

                    double shortSnr = SnrAt(s, p, HighFreq, 1 << 15);
                    double longSnr = SnrAt(s, p, HighFreq, 1 << 18);

                    Console.WriteLine($"     {s,-15}  {p.Name,-24}   {shortSnr,8:F2} dB   {longSnr,7:F2} dB   {longSnr - shortSnr,+6:F2}");
                }
            }
        }

        private static double SnrAt(SincSetting setting, OperatingPoint point, double freq, int outFrames)
        {
            int slack = setting.Taps + 256;
            int inFrames = (int)((outFrames - slack) * point.WdlRatio);

            var input = new float[inFrames];
            SignalGen.WindowedSines(new[] { freq }, 1.0, input, inFrames);

            var output = new float[outFrames];
            var r = Driver.Create(setting, point.InRate, point.OutRate);
            Driver.Run(r, input, inFrames, output, outFrames, 1);

            return SnrCalculator.Calculate(output, outFrames, 1);
        }

        /// <summary>
        /// The sinc-table rebuild gate is the one ClipShift patch that changes what the
        /// resampler computes rather than only how it allocates. This measures the
        /// difference directly: identical input, identical ratio schedule, gated against
        /// ungated, sample for sample.
        /// </summary>
        public static void RebuildPatchEquivalence(SincSetting setting)
        {
            Console.WriteLine();
            Console.WriteLine("  7. AUDIBILITY OF THE FILTER-REBUILD PATCH");
            Console.WriteLine("     Identical 16.85 kHz input and identical ratio schedule through the gated");
            Console.WriteLine("     and ungated code, sample for sample. The ratio sweeps +/-50 ppm and");
            Console.WriteLine("     crosses 1.0, so WDL's 3% guard-band low-pass switches in and out during");
            Console.WriteLine("     the run -- the one moment a coefficient change could be heard.");
            Console.WriteLine();

            const int outFrames = 1 << 18;
            const int chunk = 480;

            int inFrames = (int)(outFrames * 1.001) + setting.Taps + 1024;
            var input = new float[inFrames];
            SignalGen.WindowedSines(new[] { HighFreq }, 1.0, input, inFrames);

            var gated = new float[outFrames];
            var ungated = new float[outFrames];

            RunSwept(Driver.Create(setting, 48000.0, 48000.0, 1e-4), input, inFrames, gated, outFrames, chunk, out int rebuildsGated);
            RunSwept(Driver.Create(setting, 48000.0, 48000.0, 0.0), input, inFrames, ungated, outFrames, chunk, out int rebuildsUngated);

            double sigSq = 0.0, diffSq = 0.0, maxDiff = 0.0;
            for (int i = 0; i < outFrames; i++)
            {
                double d = gated[i] - (double)ungated[i];
                sigSq += ungated[i] * (double)ungated[i];
                diffSq += d * d;
                maxDiff = Math.Max(maxDiff, Math.Abs(d));
            }

            double db = diffSq > 0 ? 10.0 * Math.Log10(diffSq / sigSq) : double.NegativeInfinity;

            Console.WriteLine($"     rebuilds, gated   : {rebuildsGated}");
            Console.WriteLine($"     rebuilds, ungated : {rebuildsUngated}");
            Console.WriteLine($"     difference        : {db:F1} dB relative to signal, peak {maxDiff:E2}");
            Console.WriteLine();
            // The honest comparison is against the resampler's own error, not against zero.
            // At the candidate settings that error sits around 125 dB down, so a difference
            // 20 dB below it cannot be the thing anyone hears. (Peak difference is ~1.5 LSB
            // at 24-bit, so the claim is "far below the approximation error already
            // present", not "bit-identical".)
            Console.WriteLine(db < -140.0
                ? "     PASS - the gate moves the output ~20 dB below the resampler's own error floor."
                : "     REVIEW - the gate changes the output by more than expected.");
        }

        private static void RunSwept(WdlResampler r, float[] input, int inFrames, float[] output, int outFrames,
                                     int chunk, out int rebuilds)
        {
            int inPos = 0, outPos = 0, pull = 0;

            while (outPos < outFrames)
            {
                // One full +/-50 ppm sweep across the run, crossing 1.0 twice.
                double ppm = 50.0 * Math.Sin(2.0 * Math.PI * outPos / outFrames);
                r.SetRates(48000.0 * (1.0 + ppm * 1e-6), 48000.0);

                int want = Math.Min(chunk, outFrames - outPos);
                int need = r.ResamplePrepare(want, 1, out float[] inbuf, out int inoff);
                int avail = Math.Min(need, inFrames - inPos);
                if (avail > 0)
                {
                    Array.Copy(input, inPos, inbuf, inoff, avail);
                    inPos += avail;
                }

                int got = r.ResampleOut(output, outPos, avail, want, 1);
                outPos += got;
                pull++;

                if (got == 0 && avail == 0) break;
            }

            rebuilds = r.FilterRebuilds;
        }

        // ------------------------------------------------------------------------------

        public static string ToCsv(List<SnrResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("taps,interp,operating_point,freq_cycles_per_sample,in_rate,out_rate,wdl_ratio,"
                          + "delay_D,snr_db,peak,frames_generated,frames_expected,is_gate,bar_db,pass");
            foreach (var r in results)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    r.Setting.Taps.ToString(CultureInfo.InvariantCulture),
                    r.Setting.Interp.ToString(CultureInfo.InvariantCulture),
                    r.Point.Name,
                    r.Freq.ToString("R", CultureInfo.InvariantCulture),
                    r.Point.InRate.ToString(CultureInfo.InvariantCulture),
                    r.Point.OutRate.ToString(CultureInfo.InvariantCulture),
                    r.Point.WdlRatio.ToString("R", CultureInfo.InvariantCulture),
                    r.Delay.ToString(CultureInfo.InvariantCulture),
                    r.Snr.ToString("F3", CultureInfo.InvariantCulture),
                    r.Peak.ToString("F5", CultureInfo.InvariantCulture),
                    r.Generated.ToString(CultureInfo.InvariantCulture),
                    r.Expected.ToString(CultureInfo.InvariantCulture),
                    r.Point.IsGate ? "1" : "0",
                    r.Point.BarDb.ToString("F0", CultureInfo.InvariantCulture),
                    r.Pass ? "1" : "0",
                }));
            }
            return sb.ToString();
        }
    }
}
