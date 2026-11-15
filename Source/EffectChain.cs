using System;
using System.Linq;
using System.Threading;

namespace MicEffects
{
    internal sealed class EffectChain
    {
        private readonly object gate = new object();
        private readonly float[] intermediate = new float[VstEffect.BlockSize * 2];
        private IAudioEffect[] effects = Array.Empty<IAudioEffect>();
        private int sampleRate;

        internal bool HasEnabledEffects => Volatile.Read(ref effects).Length != 0;
        internal int SampleRate => Volatile.Read(ref sampleRate);

        internal void SetEffects(IAudioEffect[] next)
        {
            lock (gate)
            {
                if (sampleRate != 0)
                {
                    var added = next.Where(effect => !effects.Contains(effect)).ToArray();
                    try
                    {
                        foreach (var effect in added) effect.StartAudio(sampleRate);
                    }
                    catch
                    {
                        foreach (var effect in added) effect.StopAudio();
                        throw;
                    }
                    foreach (var effect in effects)
                        if (!next.Contains(effect)) effect.StopAudio();
                }
                Volatile.Write(ref effects, next);
            }
        }

        internal void StartAudio(int rate)
        {
            lock (gate)
            {
                if (sampleRate == rate) return;
                if (sampleRate != 0)
                    foreach (var effect in effects) effect.StopAudio();
                sampleRate = 0;
                try
                {
                    foreach (var effect in effects) effect.StartAudio(rate);
                    sampleRate = rate;
                }
                catch
                {
                    foreach (var effect in effects) effect.StopAudio();
                    throw;
                }
            }
        }

        internal void StopAudio()
        {
            lock (gate)
            {
                foreach (var effect in effects)
                    effect.StopAudio();
                sampleRate = 0;
            }
        }

        internal bool Process(float[] input, float[] output, int frames, int rate = 0)
        {
            lock (gate)
            {
                if (effects.Length == 0 || (rate != 0 && rate != sampleRate))
                    return false;
                // Alternate buffers and write the last effect directly to output for odd and even lengths.
                float[] source = input;
                float[] destination = effects.Length % 2 == 0 ? intermediate : output;
                foreach (var effect in effects)
                {
                    effect.Process(source, 2, destination, frames);
                    source = destination;
                    destination = destination == output ? intermediate : output;
                }
                for (int i = 0; i < frames * 2; i++)
                    output[i] = Math.Max(-1, Math.Min(1, output[i]));
                return true;
            }
        }
    }
}
