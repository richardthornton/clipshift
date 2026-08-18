using System;

namespace ClipShift.ResamplerQuality
{
    /// <summary>
    /// Radix-2 Cooley-Tukey FFT, double precision, in place.
    ///
    /// libsamplerate's calc_snr.c uses FFTW's FFTW_R2HC real-to-halfcomplex transform.
    /// There is no FFTW here and no managed equivalent worth taking a dependency on, so
    /// this is a plain complex FFT with the imaginary part zeroed; the magnitudes it
    /// produces are the same numbers FFTW's halfcomplex layout is unpacked into, which is
    /// all calc_snr uses. Every transform length in this harness is a power of two by
    /// construction, so radix-2 is sufficient.
    /// </summary>
    internal static class Fft
    {
        /// <summary>
        /// Magnitude spectrum of a real input signal. Only bins 0..len/2 are meaningful;
        /// the caller (SnrCalculator) zeroes and normalises exactly as calc_snr.c does.
        /// </summary>
        public static void RealMagnitude(double[] input, int len, double[] magnitude)
        {
            if ((len & (len - 1)) != 0)
                throw new ArgumentException($"FFT length {len} is not a power of two.");

            var re = new double[len];
            var im = new double[len];
            Array.Copy(input, re, len);

            Transform(re, im, len);

            for (int k = 0; k < len; k++)
                magnitude[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
        }

        private static void Transform(double[] re, double[] im, int n)
        {
            // Bit-reversal permutation.
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;

                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int span = 2; span <= n; span <<= 1)
            {
                double angle = -2.0 * Math.PI / span;
                double wRe = Math.Cos(angle);
                double wIm = Math.Sin(angle);

                for (int start = 0; start < n; start += span)
                {
                    double curRe = 1.0, curIm = 0.0;
                    for (int k = 0; k < span / 2; k++)
                    {
                        int a = start + k;
                        int b = a + span / 2;

                        double xRe = re[a], xIm = im[a];
                        double yRe = re[b] * curRe - im[b] * curIm;
                        double yIm = re[b] * curIm + im[b] * curRe;

                        re[a] = xRe + yRe;
                        im[a] = xIm + yIm;
                        re[b] = xRe - yRe;
                        im[b] = xIm - yIm;

                        // Recurrence for the twiddle factor. Recomputed from scratch every
                        // 64 steps so the error does not accumulate across a 32768-point
                        // transform and raise the noise floor we are trying to measure.
                        if (((k + 1) & 63) == 0)
                        {
                            double a2 = angle * (k + 1);
                            curRe = Math.Cos(a2);
                            curIm = Math.Sin(a2);
                        }
                        else
                        {
                            double nextRe = curRe * wRe - curIm * wIm;
                            curIm = curRe * wIm + curIm * wRe;
                            curRe = nextRe;
                        }
                    }
                }
            }
        }
    }
}
