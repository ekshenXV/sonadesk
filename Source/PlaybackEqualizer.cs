using System;
using System.Linq;

namespace MicEffects
{
    public sealed class EqualizerBand
    {
        public double Frequency { get; set; }
        public double Gain { get; set; }
        public double Q { get; set; }

        internal bool IsValid => Frequency >= 20 && Frequency <= 20000 &&
            Gain >= -12 && Gain <= 12 && Q >= 0.1 && Q <= 10;
    }

    public sealed class PlaybackPreset
    {
        public string DeviceId { get; set; }
        public bool Enabled { get; set; }
        public EqualizerBand[] Bands { get; set; }

        internal static EqualizerBand[] DefaultBands() => new[] { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 }
            .Select(frequency => new EqualizerBand { Frequency = frequency, Q = 1 }).ToArray();
    }

    internal sealed class PlaybackEqualizer : IAudioEffect
    {
        private readonly object gate = new object();
        private readonly Biquad[] filters = Enumerable.Range(0, 10).Select(_ => new Biquad()).ToArray();
        private EqualizerBand[] bands = PlaybackPreset.DefaultBands();
        private int rate;

        internal void SetBands(EqualizerBand[] next)
        {
            lock (gate)
            {
                bands = next;
                if (rate != 0) Apply(reset: false);
            }
        }

        public void StartAudio(int sampleRate)
        {
            lock (gate) { rate = sampleRate; Apply(reset: true); }
        }

        private void Apply(bool reset)
        {
            for (int index = 0; index < filters.Length; index++)
                filters[index].Set(bands[index], rate, reset);
        }

        public void StopAudio() { lock (gate) rate = 0; }

        public void Process(float[] input, int channels, float[] output, int frames)
        {
            lock (gate)
            {
                for (int frame = 0; frame < frames; frame++)
                    for (int channel = 0; channel < channels; channel++)
                    {
                        int index = frame * channels + channel;
                        double sample = input[index];
                        foreach (Biquad filter in filters) sample = filter.Process(sample, channel);
                        output[index] = (float)sample;
                    }
            }
        }

        public bool HasNativeEditor => false;
        public void OpenEditor(IntPtr parent) { }
        public void CloseEditor() { }
        public void IdleEditor() { }
        public System.Drawing.Size EditorSize() => System.Drawing.Size.Empty;
        public void SaveState(EffectSettings state) { }
        public void Dispose() => StopAudio();

        private sealed class Biquad
        {
            private readonly double[] z1 = new double[2];
            private readonly double[] z2 = new double[2];
            private double b0 = 1, b1, b2, a1, a2;
            private double targetB0 = 1, targetB1, targetB2, targetA1, targetA2;
            private double smoothing;

            internal void Set(EqualizerBand band, int sampleRate, bool reset)
            {
                // RBJ peaking EQ coefficients. Frequencies above Nyquist are bypassed.
                if (band.Gain == 0 || band.Frequency >= sampleRate / 2.0)
                {
                    targetB0 = 1;
                    targetB1 = targetB2 = targetA1 = targetA2 = 0;
                }
                else
                {
                    double amplitude = Math.Pow(10, band.Gain / 40);
                    double omega = 2 * Math.PI * band.Frequency / sampleRate;
                    double alpha = Math.Sin(omega) / (2 * band.Q);
                    double a0 = 1 + alpha / amplitude;
                    targetB0 = (1 + alpha * amplitude) / a0;
                    targetB1 = targetA1 = -2 * Math.Cos(omega) / a0;
                    targetB2 = (1 - alpha * amplitude) / a0;
                    targetA2 = (1 - alpha / amplitude) / a0;
                }
                smoothing = 1 - Math.Exp(-1.0 / (0.012 * sampleRate));
                if (!reset) return;
                b0 = targetB0; b1 = targetB1; b2 = targetB2; a1 = targetA1; a2 = targetA2;
                Array.Clear(z1, 0, z1.Length);
                Array.Clear(z2, 0, z2.Length);
            }

            internal double Process(double input, int channel)
            {
                if (channel == 0)
                {
                    b0 += (targetB0 - b0) * smoothing;
                    b1 += (targetB1 - b1) * smoothing;
                    b2 += (targetB2 - b2) * smoothing;
                    a1 += (targetA1 - a1) * smoothing;
                    a2 += (targetA2 - a2) * smoothing;
                }
                double output = b0 * input + z1[channel];
                double first = b1 * input - a1 * output + z2[channel];
                double second = b2 * input - a2 * output;
                z1[channel] = Math.Abs(first) < 1e-20 ? 0 : first;
                z2[channel] = Math.Abs(second) < 1e-20 ? 0 : second;
                return output;
            }
        }
    }
}
