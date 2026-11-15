using System;
using System.IO;
using System.Text;

namespace MicEffects
{
    internal sealed class RecordingComparison
    {
        private readonly AudioSample sample;
        private readonly float[] cleaned;
        internal int[] BeforeWave { get; }
        internal int[] AfterWave { get; }

        internal RecordingComparison(AudioSample sample, float[] cleaned)
        {
            this.sample = sample;
            this.cleaned = cleaned;
            BeforeWave = sample.Waveform();
            var view = new AudioSample(sample.Rate, sample.Samples.Length / (sample.Rate * 2));
            view.Append(cleaned, cleaned.Length / 2);
            AfterWave = view.Waveform();
        }

        internal string Encode(bool after)
        {
            // At most 48 kHz stereo PCM so the comparison fits in one host reply.
            int rate = Math.Min(sample.Rate, 48000);
            float[] source = after ? cleaned : sample.Samples;
            int frames = (int)((long)source.Length / 2 * rate / sample.Rate);
            using (var stream = new MemoryStream(44 + frames * 4))
            using (var wav = new BinaryWriter(stream, Encoding.ASCII))
            {
                void Tag(string tag) => wav.Write(Encoding.ASCII.GetBytes(tag));
                Tag("RIFF"); wav.Write(36 + frames * 4); Tag("WAVE");
                Tag("fmt "); wav.Write(16); wav.Write((short)1); wav.Write((short)2);
                wav.Write(rate); wav.Write(rate * 4); wav.Write((short)4); wav.Write((short)16);
                Tag("data"); wav.Write(frames * 4);
                for (int frame = 0; frame < frames; frame++)
                {
                    int first = (int)((long)frame * sample.Rate / rate);
                    int last = (int)((long)(frame + 1) * sample.Rate / rate);
                    for (int channel = 0; channel < 2; channel++)
                    {
                        double value = 0;
                        for (int index = first; index < last; index++) value += source[index * 2 + channel];
                        wav.Write((short)Math.Round(Math.Max(-1, Math.Min(1, value / (last - first))) * 32767));
                    }
                }
                return Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
            }
        }
    }
}
