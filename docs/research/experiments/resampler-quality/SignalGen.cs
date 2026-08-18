using System;

namespace ClipShift.ResamplerQuality
{
    /// <summary>
    /// C# port of gen_windowed_sines from libsamplerate's tests/util.c.
    ///
    ///   Copyright (c) 2002-2016, Erik de Castro Lopo &lt;erikd@mega-nerd.com&gt;, 2-clause BSD.
    ///
    /// Frequencies are in cycles per sample, so they must be &lt; 0.5. A Hanning window is
    /// applied over the whole buffer, which is what keeps the tone's own spectral leakage
    /// below the aliasing products the test is trying to see.
    /// </summary>
    internal static class SignalGen
    {
        public static void WindowedSines(double[] freqs, double max, float[] output, int outputLen)
        {
            int freqCount = freqs.Length;
            double amplitude = max / freqCount;

            for (int k = 0; k < outputLen; k++)
                output[k] = 0.0f;

            for (int freq = 0; freq < freqCount; freq++)
            {
                double phase = 0.9 * Math.PI / freqCount;

                if (freqs[freq] <= 0.0 || freqs[freq] >= 0.5)
                    throw new ArgumentException($"freq[{freq}] == {freqs[freq]} is out of range. Should be < 0.5.");

                for (int k = 0; k < outputLen; k++)
                    output[k] = (float)(output[k] + amplitude * Math.Sin(freqs[freq] * (2 * k) * Math.PI + phase));
            }

            // Apply Hanning window.
            for (int k = 0; k < outputLen; k++)
                output[k] = (float)(output[k] * (0.5 - 0.5 * Math.Cos((2 * k) * Math.PI / (outputLen - 1))));
        }

        /// <summary>Interleave a mono signal up to <paramref name="channels"/> identical channels.</summary>
        public static float[] Interleave(float[] mono, int frames, int channels)
        {
            if (channels == 1) return mono;

            var interleaved = new float[frames * channels];
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++)
                    interleaved[f * channels + c] = mono[f];
            return interleaved;
        }

        public static double FindPeak(float[] data, int len)
        {
            double peak = 0.0;
            for (int k = 0; k < len; k++)
                if (Math.Abs(data[k]) > peak)
                    peak = Math.Abs(data[k]);
            return peak;
        }
    }
}
