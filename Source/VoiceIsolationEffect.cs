using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using NAudio.Dsp;

namespace MicEffects
{
    internal sealed class VoiceIsolationEffect : IAudioEffect
    {
        private const int ModelRate = 48000;
        private const int HopFrames = 480;
        private const int ModelLookAheadMilliseconds = 40;
        private readonly StreamingModel model;
        private readonly float[] modelOutput = new float[HopFrames * 2];
        private float[] input, output, resampled;
        private WdlResampler resampler;
        private int rate, filled, first, queued, priming, failed;

        internal VoiceIsolationEffect() => model = new StreamingModel();
        internal bool Failed => Volatile.Read(ref failed) != 0;
        // Two hops to collect and overlap input. Non-native rates also need resampling headroom.
        private int BufferFrames => rate * (rate == ModelRate ? 20 : 40) / 1000;
        internal int DelayFrames => BufferFrames + rate * ModelLookAheadMilliseconds / 1000;

        public bool HasNativeEditor => false;
        public void OpenEditor(IntPtr parent) => throw new NotSupportedException();
        public void CloseEditor() { }
        public System.Drawing.Size EditorSize() => System.Drawing.Size.Empty;
        public void IdleEditor() { }
        public void SaveState(EffectSettings state) { }

        public void StartAudio(int sampleRate)
        {
            rate = sampleRate;
            input = new float[rate / 100];
            output = new float[rate / 10];
            resampled = new float[rate / 20];
            resampler = null;
            if (rate != ModelRate)
            {
                resampler = new WdlResampler();
                resampler.SetMode(true, 0, true, 32, 32);
                resampler.SetFeedMode(true);
                resampler.SetRates(ModelRate, rate);
            }
            model.Reset();
            filled = first = queued = 0;
            priming = BufferFrames;
            Volatile.Write(ref failed, 0);
        }

        public void StopAudio() { }

        public void Process(float[] source, int channels, float[] destination, int frames)
        {
            if (!Failed)
            {
                try
                {
                    for (int frame = 0; frame < frames; frame++)
                    {
                        float value = 0;
                        if (priming > 0) priming--;
                        else
                        {
                            if (queued == 0) throw new InvalidOperationException("Voice isolation stream underrun.");
                            value = output[first];
                            first = (first + 1) % output.Length;
                            queued--;
                        }
                        int offset = frame * channels;
                        // The microphone bridge duplicates mono input. Keep voice centered on stereo inputs.
                        float sample = channels == 1 ? source[offset] : (source[offset] + source[offset + 1]) * .5f;
                        input[filled++] = float.IsNaN(sample) || float.IsInfinity(sample) ? 0 : sample;
                        for (int channel = 0; channel < channels; channel++) destination[offset + channel] = value;
                        if (filled == input.Length)
                        {
                            Feed();
                            filled = 0;
                        }
                    }
                    return;
                }
                catch (Exception)
                {
                    Volatile.Write(ref failed, 1);
                }
            }
            Array.Copy(source, destination, frames * channels);
        }

        private void Feed()
        {
            int count = model.Run(input, rate, modelOutput);
            if (count == 0) return;
            float[] samples = modelOutput;
            if (resampler != null)
            {
                int prepared = resampler.ResamplePrepare(count, 1, out var source, out int offset);
                Array.Copy(modelOutput, 0, source, offset, prepared);
                count = resampler.ResampleOut(resampled, 0, prepared, resampled.Length, 1);
                samples = resampled;
            }
            if (count > output.Length - queued) throw new InvalidOperationException("Voice isolation stream overflow.");
            int cursor = (first + queued) % output.Length;
            int leading = Math.Min(count, output.Length - cursor);
            Array.Copy(samples, 0, output, cursor, leading);
            Array.Copy(samples, leading, output, 0, count - leading);
            queued += count;
        }

        internal static RecordingComparison Compare(AudioSample sample)
        {
            using (var effect = new VoiceIsolationEffect())
            {
                effect.StartAudio(sample.Rate);
                int delay = effect.DelayFrames * 2;
                var cleaned = new float[sample.Samples.Length];
                var input = new float[VstEffect.BlockSize * 2];
                var output = new float[input.Length];
                for (int offset = 0; offset < cleaned.Length + delay; offset += input.Length)
                {
                    int count = Math.Max(0, Math.Min(input.Length, sample.Samples.Length - offset));
                    if (count > 0) Array.Copy(sample.Samples, offset, input, 0, count);
                    Array.Clear(input, count, input.Length - count);
                    effect.Process(input, 2, output, VstEffect.BlockSize);
                    if (effect.Failed) throw new InvalidOperationException("isolationUnavailable");
                    int start = Math.Max(0, delay - offset);
                    int end = Math.Min(output.Length, cleaned.Length + delay - offset);
                    if (end > start) Array.Copy(output, start, cleaned, offset + start - delay, end - start);
                }
                return new RecordingComparison(sample, cleaned);
            }
        }

        public void Dispose() => model.Dispose();

        private sealed class StreamingModel : SafeHandle
        {
            private const string LibraryName = "sherpa-onnx-c-api.dll";
            private const string ModelName = "dpdfnet2_48khz_hr.onnx";
            private const string ModelHash = "0B399F8A58DC4D70D8CD97541F5C39869406145193B957D00A03B66070944928";
            private static readonly Lazy<IntPtr> Library = new Lazy<IntPtr>(LoadLibrary);

            [StructLayout(LayoutKind.Sequential)]
            private struct Configuration
            {
                public IntPtr GtcrnModel;
                public int Threads, Debug;
                [MarshalAs(UnmanagedType.LPStr)] public string Provider;
                [MarshalAs(UnmanagedType.LPUTF8Str)] public string DpdfNetModel;
                public float AttenuationLimitDb;
                public IntPtr VoxLockEnroll, VoxLockChunk, VoxLockSample;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct Audio
            {
                public IntPtr Samples;
                public int Length, Rate;
            }

            private static string Assets => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voice-isolation");

            internal StreamingModel() : base(IntPtr.Zero, true)
            {
                string path = Path.Combine(Assets, ModelName);
                if (!File.Exists(path) || Library.Value == IntPtr.Zero)
                    throw new InvalidOperationException("isolationUnavailable");
                // The native runtime treats invalid model metadata as a fatal error.
                using (var file = File.OpenRead(path))
                using (var hash = SHA256.Create())
                    if (BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "") != ModelHash)
                        throw new InvalidOperationException("isolationUnavailable");
                SetHandle(Create(new Configuration { Threads = 1, Provider = "cpu", DpdfNetModel = path }));
                if (IsInvalid) throw new InvalidOperationException("isolationUnavailable");
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle()
            {
                Destroy(handle);
                return true;
            }

            internal void Reset() => Reset(this);

            internal int Run(float[] input, int rate, float[] output)
            {
                IntPtr pointer = Run(this, input, input.Length, rate);
                if (pointer == IntPtr.Zero) return 0;
                try
                {
                    Audio audio = Marshal.PtrToStructure<Audio>(pointer);
                    if (audio.Rate != ModelRate || audio.Length < 0 || audio.Length > output.Length)
                        throw new InvalidOperationException("Unexpected voice isolation output format.");
                    Marshal.Copy(audio.Samples, output, 0, audio.Length);
                    return audio.Length;
                }
                finally { DestroyAudio(pointer); }
            }

            private static IntPtr LoadLibrary() => LoadLibraryEx(Path.Combine(Assets, LibraryName), IntPtr.Zero, 0x1100);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr LoadLibraryEx(string filename, IntPtr reserved, uint flags);
            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxCreateOnlineSpeechDenoiser")]
            private static extern IntPtr Create(in Configuration configuration);
            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxDestroyOnlineSpeechDenoiser")]
            private static extern void Destroy(IntPtr model);
            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxOnlineSpeechDenoiserReset")]
            private static extern void Reset(StreamingModel model);
            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxOnlineSpeechDenoiserRun")]
            private static extern IntPtr Run(StreamingModel model, [In] float[] input, int count, int rate);
            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxDestroyDenoisedAudio")]
            private static extern void DestroyAudio(IntPtr audio);
        }
    }
}
