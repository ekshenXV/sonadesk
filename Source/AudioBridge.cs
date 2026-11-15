using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace MicEffects
{
    internal sealed unsafe class AudioBridge : IDisposable
    {
        private const int RingSize = 16384;
        private const int BufferOffset = 64;
        private const int BridgeSize = BufferOffset + 2 * RingSize * 2 * sizeof(float);
        internal const int ProtocolVersion = 2;
        internal const int CapturedFramesOffset = 40;
        private enum Field
        {
            Version, SampleRate, Channels, InputEnd, OutputEnd, Enabled, OutputStart, WorkerSession, OutputSession, WetPosition
        }

        private readonly EffectChain chain;
        private readonly Thread thread;
        private readonly float[] samples = new float[VstEffect.BlockSize * 2];
        private readonly float[] processed = new float[VstEffect.BlockSize * 2];
        private byte* data;
        private int stopping;
        private int visibleRate;
        private int passingAudio;
        private float peak;
        private Exception error;
        private AudioSample recording;

        internal int SampleRate => Volatile.Read(ref visibleRate);
        internal bool IsProcessing => Volatile.Read(ref passingAudio) != 0;
        internal float Peak => Volatile.Read(ref peak);
        internal Exception Error => Volatile.Read(ref error);
        internal void RecordSample(AudioSample sample) => Volatile.Write(ref recording, sample);

        internal static string MappingName(Guid endpoint) => @"Global\MicEffects-" + endpoint.ToString("B").ToUpperInvariant();

        internal AudioBridge(Guid endpoint, EffectChain chain)
            : this(MappingName(endpoint), chain) { }

        internal AudioBridge(string mappingName, EffectChain chain)
        {
            this.chain = chain;
            thread = new Thread(() => Work(mappingName)) { IsBackground = true, Name = "SonaDesk audio", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        private int Read(Field field) => Volatile.Read(ref ((int*)data)[(int)field]);
        private void Write(Field field, int value) => Volatile.Write(ref ((int*)data)[(int)field], value);

        private void Work(string mappingName)
        {
            try
            {
                while (Volatile.Read(ref stopping) == 0)
                {
                    MemoryMappedFile mapping;
                    try { mapping = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.ReadWrite); }
                    catch (FileNotFoundException) { Thread.Sleep(40); continue; }

                    using (mapping)
                    using (var view = mapping.CreateViewAccessor(0, BridgeSize, MemoryMappedFileAccess.ReadWrite))
                    {
                        view.SafeMemoryMappedViewHandle.AcquirePointer(ref data);
                        try { ProcessStream(); }
                        finally
                        {
                            Write(Field.Enabled, 0);
                            Write(Field.OutputSession, 0);
                            view.SafeMemoryMappedViewHandle.ReleasePointer();
                            data = null;
                            Volatile.Write(ref visibleRate, 0);
                            Volatile.Write(ref passingAudio, 0);
                            Volatile.Write(ref peak, 0);
                        }
                    }
                    Thread.Sleep(10);
                }
            }
            catch (Exception exception)
            {
                Volatile.Write(ref error, exception);
            }
        }

        private void ProcessStream()
        {
            if (Read(Field.Version) != ProtocolVersion)
                return;
            int sampleRate = Read(Field.SampleRate);
            if (sampleRate <= 0) return;
            Volatile.Write(ref visibleRate, sampleRate);
            while (Volatile.Read(ref stopping) == 0 && Read(Field.Version) == ProtocolVersion)
            {
                if (!NeedsInput(sampleRate))
                {
                    Thread.Sleep(10);
                    continue;
                }
                ProcessEnabledEffects(sampleRate);
                Write(Field.Enabled, 0);
                Write(Field.OutputSession, 0);
                Volatile.Write(ref passingAudio, 0);
                Volatile.Write(ref peak, 0);
            }
        }

        private bool NeedsInput(int rate) => Volatile.Read(ref recording) != null ||
            (chain.HasEnabledEffects && chain.SampleRate == rate);

        private void ProcessEnabledEffects(int sampleRate)
        {
            Write(Field.OutputSession, 0);
            int session = unchecked(Read(Field.WorkerSession) + 1);
            if (session == 0) session = 1;
            Write(Field.WorkerSession, session);
            Write(Field.Enabled, 1);
            uint cursor = unchecked((uint)Read(Field.InputEnd));
            Write(Field.OutputStart, unchecked((int)cursor));
            float* input = (float*)(data + BufferOffset);
            float* output = input + RingSize * 2;
            int idleSince = Environment.TickCount;
            while (Volatile.Read(ref stopping) == 0 && Read(Field.Version) == ProtocolVersion && NeedsInput(sampleRate))
            {
                uint end = unchecked((uint)Read(Field.InputEnd));
                uint available = end - cursor;
                if (available == 0)
                {
                    if (unchecked(Environment.TickCount - idleSince) > 250)
                    {
                        Volatile.Write(ref passingAudio, 0);
                        Volatile.Write(ref peak, 0);
                    }
                    Thread.Sleep(1);
                    continue;
                }
                idleSince = Environment.TickCount;
                if (available > RingSize / 2)
                {
                    Write(Field.OutputSession, 0);
                    cursor = end;
                    Write(Field.OutputStart, unchecked((int)cursor));
                    continue;
                }
                int frames = Math.Min(VstEffect.BlockSize, (int)available);
                float blockPeak = 0;
                for (int frame = 0; frame < frames; frame++)
                {
                    int slot = (int)((cursor + (uint)frame) & (RingSize - 1)) * 2;
                    float left = input[slot];
                    float right = input[slot + 1];
                    samples[frame * 2] = left;
                    samples[frame * 2 + 1] = right;
                    blockPeak = Math.Max(blockPeak, Math.Max(Math.Abs(left), Math.Abs(right)));
                }
                AudioSample sample = Volatile.Read(ref recording);
                if (sample != null && sample.Rate == sampleRate) sample.Append(samples, frames);
                if (!chain.Process(samples, processed, frames, sampleRate))
                {
                    if (sample == null) return;
                    Write(Field.OutputSession, 0);
                    cursor += (uint)frames;
                    continue;
                }
                if (Read(Field.Version) != ProtocolVersion)
                    return;

                for (int frame = 0; frame < frames; frame++)
                {
                    int slot = (int)((cursor + (uint)frame) & (RingSize - 1)) * 2;
                    output[slot] = processed[frame * 2];
                    output[slot + 1] = processed[frame * 2 + 1];
                }
                cursor += (uint)frames;
                Write(Field.OutputEnd, unchecked((int)cursor));
                Write(Field.OutputSession, session);
                Volatile.Write(ref peak, blockPeak);
                Volatile.Write(ref passingAudio, Read(Field.WetPosition) != 0 ? 1 : 0);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0)
                return;
            thread.Join();
        }
    }
}
