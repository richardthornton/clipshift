using System;
using System.Collections.Generic;

namespace ClipShift.ResamplerQuality
{
    /// <summary>
    /// C# port of libsamplerate's tests/calc_snr.c.
    ///
    ///   Copyright (c) 2002-2016, Erik de Castro Lopo &lt;erikd@mega-nerd.com&gt;
    ///   Released under the 2-clause BSD licence:
    ///   https://github.com/libsndfile/libsamplerate/blob/master/COPYING
    ///
    /// Ported rather than reimplemented, deliberately: the measurement's credibility comes
    /// from being the same measurement libsamplerate publishes its own numbers with. The
    /// one substitution is FFTW -> the radix-2 FFT in Fft.cs.
    ///
    /// The trap this file exists to avoid is documented in the original and reproduced
    /// here: the side lobes of the windowed FFT look exactly like aliasing peaks, so the
    /// magnitude spectrum must first be smoothed by wiping out the troughs between
    /// adjacent peaks. Skip that and every converter looks far worse than it is.
    /// </summary>
    internal static class SnrCalculator
    {
        private const int MaxPeaks = 10;

        private struct PeakData
        {
            public double Peak;
            public int Index;
        }

        /// <summary>
        /// Signal-to-noise ratio, in dB, of a converted signal. <paramref name="expectedPeaks"/>
        /// is the number of tones expected in the pass band.
        /// </summary>
        public static double Calculate(float[] data, int len, int expectedPeaks)
        {
            var datacopy = new double[NextPow2(len)];
            for (int k = 0; k < len; k++)
                datacopy[k] = data[k];

            // The original pads to a multiple of 32 to speed FFTW up. The radix-2 FFT
            // needs a power of two, which is a strictly stronger requirement; every call
            // site here already hands over a power-of-two length, so this only ever pads
            // zero samples in practice.
            int fftLen = datacopy.Length;

            var magnitude = new double[fftLen];
            LogMagSpectrum(datacopy, fftLen, magnitude);
            SmoothMagSpectrum(magnitude, fftLen / 2);

            return FindSnr(magnitude, fftLen, expectedPeaks);
        }

        private static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        /// <summary>
        /// Smooth the magnitude spectrum by wiping out troughs between adjacent peaks.
        /// This removes side lobe peaks without affecting noise/aliasing peaks.
        /// </summary>
        private static void SmoothMagSpectrum(double[] mag, int len)
        {
            var peaks = new PeakData[2];

            // Find first peak.
            for (int k = 1; k < len - 1; k++)
            {
                if (mag[k - 1] < mag[k] && mag[k] >= mag[k + 1])
                {
                    peaks[0].Peak = mag[k];
                    peaks[0].Index = k;
                    break;
                }
            }

            // Find subsequent peaks and smooth between peaks.
            for (int k = peaks[0].Index + 1; k < len - 1; k++)
            {
                if (mag[k - 1] < mag[k] && mag[k] >= mag[k + 1])
                {
                    peaks[1].Peak = mag[k];
                    peaks[1].Index = k;

                    if (peaks[1].Peak > peaks[0].Peak)
                        LinearSmooth(mag, peaks[1], peaks[0]);
                    else
                        LinearSmooth(mag, peaks[0], peaks[1]);

                    peaks[0] = peaks[1];
                }
            }
        }

        private static void LinearSmooth(double[] mag, PeakData larger, PeakData smaller)
        {
            if (smaller.Index < larger.Index)
            {
                for (int k = smaller.Index + 1; k < larger.Index; k++)
                    mag[k] = (mag[k] < mag[k - 1]) ? 0.999 * mag[k - 1] : mag[k];
            }
            else
            {
                for (int k = smaller.Index - 1; k >= larger.Index; k--)
                    mag[k] = (mag[k] < mag[k + 1]) ? 0.999 * mag[k + 1] : mag[k];
            }
        }

        private static double FindSnr(double[] magnitude, int len, int expectedPeaks)
        {
            var peaks = new List<PeakData>(MaxPeaks + 1);

            // Find the MaxPeaks largest peaks, kept sorted descending.
            for (int k = 1; k < len - 1; k++)
            {
                if (magnitude[k - 1] < magnitude[k] && magnitude[k] >= magnitude[k + 1])
                {
                    var p = new PeakData { Peak = magnitude[k], Index = k };
                    if (peaks.Count < MaxPeaks)
                    {
                        peaks.Add(p);
                        peaks.Sort(static (a, b) => a.Peak < b.Peak ? 1 : -1);
                    }
                    else if (magnitude[k] > peaks[MaxPeaks - 1].Peak)
                    {
                        peaks[MaxPeaks - 1] = p;
                        peaks.Sort(static (a, b) => a.Peak < b.Peak ? 1 : -1);
                    }
                }
            }

            if (peaks.Count < expectedPeaks)
            {
                Console.WriteLine($"    !! bad peak count ({peaks.Count}), expected {expectedPeaks}.");
                return -1.0;
            }

            peaks.Sort(static (a, b) => a.Peak < b.Peak ? 1 : -1);

            double snr = peaks[0].Peak;
            for (int k = 1; k < peaks.Count; k++)
                if (Math.Abs(snr - peaks[k].Peak) > 10.0)
                    return Math.Abs(peaks[k].Peak);

            return snr;
        }

        private static void LogMagSpectrum(double[] input, int len, double[] magnitude)
        {
            Fft.RealMagnitude(input, len, magnitude);

            double maxval = 0.0;
            for (int k = 1; k < len / 2; k++)
                maxval = (maxval < magnitude[k]) ? magnitude[k] : maxval;

            Array.Clear(magnitude, len / 2, len / 2);

            // Don't care about the DC component. Make it zero.
            magnitude[0] = 0.0;

            for (int k = 0; k < len; k++)
            {
                magnitude[k] = magnitude[k] / maxval;
                magnitude[k] = (magnitude[k] < 1e-15) ? -200.0 : 20.0 * Math.Log10(magnitude[k]);
            }
        }
    }
}
