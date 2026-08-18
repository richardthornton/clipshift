using System;
using ClipShift.Audio.Resampling;

namespace ClipShift.ResamplerQuality
{
    internal enum ResamplerMode
    {
        Sinc,
        Linear,
        ZeroOrderHold,
    }

    /// <summary>A WDL configuration: mode, filter length in taps, and phase-table oversampling.</summary>
    internal readonly struct SincSetting
    {
        public readonly ResamplerMode Mode;
        public readonly int Taps;
        public readonly int Interp;

        public SincSetting(int taps, int interp)
        {
            Mode = ResamplerMode.Sinc; Taps = taps; Interp = interp;
        }

        public SincSetting(ResamplerMode mode)
        {
            Mode = mode; Taps = 0; Interp = 0;
        }

        /// <summary>Head padding WDL inserts, in input frames -- 0 outside sinc mode.</summary>
        public int HeadPad => Mode == ResamplerMode.Sinc ? Taps / 2 : 0;

        public override string ToString() => Mode switch
        {
            ResamplerMode.Sinc => $"sinc {Taps}/{Interp}",
            ResamplerMode.Linear => "linear",
            _ => "zero-order hold",
        };
    }

    /// <summary>
    /// An operating point stated the way ClipShift meets it: a capture device rate feeding
    /// the fixed 48 kHz output. WDL's own ratio is inRate/outRate, libsamplerate's is the
    /// reciprocal, and the two conventions have caused enough confusion already -- so
    /// nothing in this harness is labelled with a bare "ratio".
    /// </summary>
    internal readonly struct OperatingPoint
    {
        public readonly string Name;
        public readonly double InRate;
        public readonly double OutRate;
        public readonly bool IsGate;
        public readonly double BarDb;

        public OperatingPoint(string name, double inRate, double outRate, bool isGate = false, double barDb = 0)
        {
            Name = name; InRate = inRate; OutRate = outRate; IsGate = isGate; BarDb = barDb;
        }

        public double WdlRatio => InRate / OutRate;

        public override string ToString() => Name;
    }

    internal static class Driver
    {
        public static WdlResampler Create(SincSetting setting, double inRate, double outRate, double rebuildEpsilon = 1e-4)
        {
            var r = new WdlResampler();
            // filtercnt is 0 throughout: in sinc mode SetMode zeroes it anyway, and in the
            // linear/ZOH modes used for harness validation it keeps WDL's optional IIR
            // pre/post filters out of the comparison against libsamplerate, which has no
            // equivalent stage. (It also sidesteps a real bug in the vendored code: the
            // output-side IIR pass writes from index 0 rather than from outBufferIndex.)
            switch (setting.Mode)
            {
                case ResamplerMode.Sinc:
                    r.SetMode(true, 0, true, setting.Taps, setting.Interp);
                    break;
                case ResamplerMode.Linear:
                    r.SetMode(true, 0, false);
                    break;
                default:
                    r.SetMode(false, 0, false);
                    break;
            }
            r.SetFeedMode(false);           // output-driven pull, which is ClipShift's shape
            r.FilterRebuildEpsilon = rebuildEpsilon;
            r.SetRates(inRate, outRate);
            return r;
        }

        /// <summary>
        /// Pull <paramref name="outFrames"/> output frames, feeding from a finite input
        /// buffer. Returns the number of output frames actually produced. When the input
        /// runs out the resampler is fed short, which is its flush path.
        /// </summary>
        public static int Run(WdlResampler r, float[] input, int inFrames, float[] output, int outFrames,
                              int nch, int chunk = 1024)
        {
            int inPos = 0, outPos = 0;

            while (outPos < outFrames)
            {
                int want = Math.Min(chunk, outFrames - outPos);
                int need = r.ResamplePrepare(want, nch, out float[] inbuf, out int inoff);

                int avail = Math.Min(need, inFrames - inPos);
                if (avail > 0)
                {
                    Array.Copy(input, inPos * nch, inbuf, inoff, avail * nch);
                    inPos += avail;
                }

                int got = r.ResampleOut(output, outPos * nch, avail, want, nch);
                outPos += got;

                if (got == 0 && avail == 0)
                    break;      // input exhausted and nothing left to flush
            }

            return outPos;
        }

        /// <summary>
        /// Group delay D, in output frames -- the number ClipShift pre-rolls by and then
        /// discards, per the resolution of #16. Not knowable from documentation for any
        /// candidate; only measured.
        ///
        /// The impulse is placed well inside the stream rather than at sample 0, so the
        /// test measures the thing that actually matters: whether input frame n lands on
        /// output frame n/ratio, which is #5's invariant restated. An impulse at sample 0
        /// would answer a weaker question and would hide any offset behind the head of the
        /// buffer.
        /// </summary>
        public static double MeasureDelay(SincSetting setting, double inRate, double outRate, out double peakValue,
                                          out double symmetryError, out int peakIndex)
        {
            const int inFrames = 40000;
            const int impulseAt = 1024;

            var r = Create(setting, inRate, outRate);

            var input = new float[inFrames];
            input[impulseAt] = 1.0f;

            int outFrames = (int)(inFrames * outRate / inRate) - 8;
            var output = new float[outFrames];

            int got = Run(r, input, inFrames, output, outFrames, 1);

            peakIndex = 0;
            peakValue = 0.0;
            for (int k = 0; k < got; k++)
            {
                if (Math.Abs(output[k]) > peakValue)
                {
                    peakValue = Math.Abs(output[k]);
                    peakIndex = k;
                }
            }

            // Sub-sample peak position by parabolic interpolation, so D is not quantised to
            // the output grid the ratio generally does not land on.
            double refined = peakIndex;
            if (peakIndex > 0 && peakIndex < got - 1)
            {
                double ym = Math.Abs(output[peakIndex - 1]);
                double y0 = Math.Abs(output[peakIndex]);
                double yp = Math.Abs(output[peakIndex + 1]);
                double denom = ym - 2 * y0 + yp;
                if (Math.Abs(denom) > 1e-30)
                    refined = peakIndex + 0.5 * (ym - yp) / denom;
            }

            // How far the peak stands above everything around it. A clean main lobe leaves
            // this small; a value near 1 would mean the "peak" found was one of a pair and
            // the delay reading is arbitrary.
            double sidelobe = 0.0;
            for (int k = 0; k < got; k++)
            {
                if (Math.Abs(k - peakIndex) <= 2) continue;
                sidelobe = Math.Max(sidelobe, Math.Abs(output[k]));
            }
            symmetryError = peakValue > 0 ? sidelobe / peakValue : double.NaN;

            // Where the impulse would land if the resampler introduced no delay at all.
            double expected = impulseAt * outRate / inRate;
            return refined - expected;
        }
    }
}
