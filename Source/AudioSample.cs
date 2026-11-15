using System;
using System.Threading;

namespace MicEffects
{
    internal sealed class AudioSample
    {
        internal int Rate { get; }
        internal float[] Samples { get; }
        private int length;
        private double energy;
        private float peak;
        private const int WaveBars = 96;
        private readonly float[] wave = new float[WaveBars];
        internal AudioSample(int rate, int seconds)
        {
            Rate = rate;
            Samples = new float[rate * seconds * 2];
        }

        internal int Progress => Volatile.Read(ref length) * 100 / Samples.Length;
        internal bool Complete => Volatile.Read(ref length) == Samples.Length;
        internal bool HasSignal => energy / Samples.Length >= 1e-12;
        internal bool IsClipping => peak >= .999f;

        internal void Append(float[] source, int frames)
        {
            int cursor = length;
            int count = Math.Min(Samples.Length - cursor, frames * 2);
            for (int index = 0; index < count; index++)
            {
                float value = source[index];
                if (float.IsNaN(value) || float.IsInfinity(value)) value = 0;
                Samples[cursor + index] = value;
                energy += (double)value * value;
                peak = Math.Max(peak, Math.Abs(value));
                int bar = (cursor + index) * WaveBars / Samples.Length;
                wave[bar] = Math.Max(wave[bar], Math.Abs(value));
            }
            Volatile.Write(ref length, cursor + count);
        }

        internal int[] Waveform() => Array.ConvertAll(wave, value => (int)(Math.Pow(Math.Min(value, 1), .42) * 100));
    }
}
