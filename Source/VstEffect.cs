using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MicEffects
{
    internal sealed class VstEffect : IAudioEffect
    {
        internal const int BlockSize = 512;
        private int sampleRate = 48000;
        private enum Command
        {
            Open = 0, Close = 1, SetProgram = 2, GetProgram = 3,
            SampleRate = 10, BlockSize = 11, MainsChanged = 12,
            EditorRect = 13, EditorOpen = 14, EditorClose = 15, EditorIdle = 19,
            GetChunk = 23, SetChunk = 24, StartProcess = 71, StopProcess = 72
        }

        private enum HostQuery
        {
            Version = 1, WantMidi = 6, SampleRate = 16, BlockSize = 17,
            ProcessLevel = 23, Vendor = 32, Product = 33, VendorVersion = 34,
            CanDo = 37, Language = 38
        }

        private const int HasEditor = 1;
        private const int UsesChunks = 1 << 5;
        private const int IsInstrument = 1 << 8;
        private const int MaxChunkBytes = 8 * 1024 * 1024;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr EntryPoint(Dispatch host);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr Dispatch(IntPtr effect, int opcode, int index, IntPtr value, IntPtr pointer, float option);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ProcessBlock(IntPtr effect, IntPtr inputs, IntPtr outputs, int frames);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetParameter(IntPtr effect, int index, float value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate float GetParameter(IntPtr effect, int index);

        [StructLayout(LayoutKind.Sequential)]
        private struct EffectHeader
        {
            internal int Magic;
            internal IntPtr Dispatcher;
            internal IntPtr LegacyProcess;
            internal IntPtr SetParameter;
            internal IntPtr GetParameter;
            internal int ProgramCount;
            internal int ParameterCount;
            internal int Inputs;
            internal int Outputs;
            internal int Flags;
            internal IntPtr Reserved1;
            internal IntPtr Reserved2;
            internal int InitialDelay;
            internal int RealQualities;
            internal int OffQualities;
            internal float IoRatio;
            internal IntPtr Object;
            internal IntPtr User;
            internal int UniqueId;
            internal int Version;
            internal IntPtr ProcessReplacing;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EditorBounds
        {
            internal short Top, Left, Bottom, Right;
        }

        private readonly object gate = new object();
        private readonly Dispatch host;
        private IntPtr module;
        private IntPtr effect;
        private Dispatch dispatcher;
        private ProcessBlock process;
        private EffectHeader header;
        private AudioChannels input;
        private AudioChannels output;
        private bool audioStarted;
        private bool editorOpened;
        private int processingThread;

        public bool HasNativeEditor => (header.Flags & HasEditor) != 0;

        internal VstEffect(string path, EffectSettings preset)
        {
            if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Выбери 64-битный эффект VST2 в формате .dll.");

            host = HandleHostQuery;
            module = LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00001000);
            if (module == IntPtr.Zero)
            {
                int code = Marshal.GetLastWin32Error();
                string message = code == 193
                    ? "Плагин должен быть 64-битным (x64)."
                    : "Не удалось загрузить плагин. Проверь, что он установлен вместе со своими файлами.";
                throw new InvalidOperationException(message, new Win32Exception(code));
            }

            try
            {
                IntPtr entry = GetProcAddress(module, "VSTPluginMain");
                if (entry == IntPtr.Zero)
                    entry = GetProcAddress(module, "main");
                if (entry == IntPtr.Zero)
                    throw new InvalidOperationException("В этом DLL нет эффекта VST2.");

                effect = Marshal.GetDelegateForFunctionPointer<EntryPoint>(entry)(host);
                if (effect == IntPtr.Zero || Marshal.ReadInt32(effect) != 0x56737450)
                    throw new InvalidOperationException("Плагин не открылся как VST2.");

                header = Marshal.PtrToStructure<EffectHeader>(effect);
                dispatcher = Marshal.GetDelegateForFunctionPointer<Dispatch>(header.Dispatcher);
                Call(Command.Open);
                if ((header.Flags & IsInstrument) != 0 || header.Inputs < 1 || header.Outputs < 1
                    || header.Inputs > 32 || header.Outputs > 32 || header.ProcessReplacing == IntPtr.Zero
                    || header.ParameterCount < 0 || header.ParameterCount > 16384)
                    throw new InvalidOperationException("Нужен VST2-эффект с аудиовходом и аудиовыходом.");

                process = Marshal.GetDelegateForFunctionPointer<ProcessBlock>(header.ProcessReplacing);
                input = new AudioChannels(header.Inputs);
                output = new AudioChannels(header.Outputs);
                Call(Command.SampleRate, option: sampleRate);
                Call(Command.BlockSize, value: BlockSize);
                Restore(preset);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void StartAudio(int rate)
        {
            lock (gate)
            {
                if (audioStarted)
                    return;

                sampleRate = rate;
                Call(Command.SampleRate, option: rate);
                Call(Command.BlockSize, value: BlockSize);
                Call(Command.MainsChanged, value: 1);
                Call(Command.StartProcess);
                audioStarted = true;
            }
        }

        public void StopAudio()
        {
            lock (gate)
            {
                if (!audioStarted)
                    return;

                Call(Command.StopProcess);
                Call(Command.MainsChanged);
                audioStarted = false;
            }
        }

        public void Process(float[] source, int channels, float[] destination, int frames)
        {
            for (int frame = 0; frame < frames; ++frame)
            {
                float left = source[frame * channels];
                float right = channels == 1 ? left : source[frame * channels + 1];
                input.Samples[0][frame] = header.Inputs == 1 ? (left + right) * 0.5f : left;
                if (header.Inputs > 1)
                    input.Samples[1][frame] = right;
            }

            lock (gate)
            {
                processingThread = Environment.CurrentManagedThreadId;
                try { process(effect, input.Pointers, output.Pointers, frames); }
                finally { processingThread = 0; }
            }

            float[] rightOutput = output.Samples[header.Outputs == 1 ? 0 : 1];
            for (int frame = 0; frame < frames; ++frame)
            {
                destination[frame * 2] = Finite(output.Samples[0][frame]);
                destination[frame * 2 + 1] = Finite(rightOutput[frame]);
            }
        }

        private static float Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0 : value;

        public void OpenEditor(IntPtr parent)
        {
            lock (gate)
            {
                Call(Command.EditorOpen, pointer: parent);
                editorOpened = true;
            }
        }

        public void CloseEditor()
        {
            lock (gate)
            {
                if (!editorOpened)
                    return;

                Call(Command.EditorClose);
                editorOpened = false;
            }
        }

        public void IdleEditor()
        {
            // A busy audio callback should never delay the UI timer.
            if (!System.Threading.Monitor.TryEnter(gate))
                return;

            try { if (editorOpened) Call(Command.EditorIdle); }
            finally { System.Threading.Monitor.Exit(gate); }
        }

        public System.Drawing.Size EditorSize()
        {
            lock (gate)
            {
                IntPtr result = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(result, IntPtr.Zero);
                    Call(Command.EditorRect, pointer: result);
                    IntPtr address = Marshal.ReadIntPtr(result);
                    if (address != IntPtr.Zero)
                    {
                        var rect = Marshal.PtrToStructure<EditorBounds>(address);
                        return new System.Drawing.Size(Math.Max(1, rect.Right - rect.Left),
                            Math.Max(1, rect.Bottom - rect.Top));
                    }
                    return new System.Drawing.Size(640, 440);
                }
                finally { Marshal.FreeHGlobal(result); }
            }
        }

        public void SaveState(EffectSettings preset)
        {
            lock (gate)
            {
                preset.ProgramIndex = Call(Command.GetProgram).ToInt32();
                preset.Parameters = null;
                preset.Chunk = null;
                if ((header.Flags & UsesChunks) != 0)
                {
                    IntPtr result = Marshal.AllocHGlobal(IntPtr.Size);
                    try
                    {
                        Marshal.WriteIntPtr(result, IntPtr.Zero);
                        long length = Call(Command.GetChunk, index: 1, pointer: result).ToInt64();
                        IntPtr address = Marshal.ReadIntPtr(result);
                        if (address != IntPtr.Zero && length > 0 && length <= MaxChunkBytes)
                        {
                            preset.Chunk = new byte[(int)length];
                            Marshal.Copy(address, preset.Chunk, 0, preset.Chunk.Length);
                            return;
                        }
                    }
                    finally { Marshal.FreeHGlobal(result); }
                }

                if (header.GetParameter == IntPtr.Zero)
                    return;

                var get = Marshal.GetDelegateForFunctionPointer<GetParameter>(header.GetParameter);
                preset.Parameters = new float[header.ParameterCount];
                for (int i = 0; i < header.ParameterCount; i++)
                    preset.Parameters[i] = get(effect, i);
            }
        }

        private void Restore(EffectSettings preset)
        {
            if (preset == null)
                return;

            if (preset.ProgramIndex >= 0 && preset.ProgramIndex < header.ProgramCount)
                Call(Command.SetProgram, value: preset.ProgramIndex);

            if (preset.Chunk?.Length > 0 && preset.Chunk.Length <= MaxChunkBytes && (header.Flags & UsesChunks) != 0)
            {
                var pinned = GCHandle.Alloc(preset.Chunk, GCHandleType.Pinned);
                try { Call(Command.SetChunk, index: 1, value: preset.Chunk.Length, pointer: pinned.AddrOfPinnedObject()); }
                finally { pinned.Free(); }
                return;
            }

            if (preset.Parameters == null || header.SetParameter == IntPtr.Zero)
                return;

            var set = Marshal.GetDelegateForFunctionPointer<SetParameter>(header.SetParameter);
            for (int i = 0; i < Math.Min(preset.Parameters.Length, header.ParameterCount); i++)
                set(effect, i, preset.Parameters[i]);
        }

        private IntPtr Call(Command command, int index = 0, int value = 0, IntPtr pointer = default, float option = 0) =>
            dispatcher(effect, (int)command, index, new IntPtr(value), pointer, option);

        private IntPtr HandleHostQuery(IntPtr plugin, int opcode, int index, IntPtr value, IntPtr pointer, float option)
        {
            switch ((HostQuery)opcode)
            {
                case HostQuery.Version: return new IntPtr(2400);
                case HostQuery.SampleRate: return new IntPtr(sampleRate);
                case HostQuery.BlockSize: return new IntPtr(BlockSize);
                case HostQuery.ProcessLevel: return new IntPtr(processingThread == Environment.CurrentManagedThreadId ? 2 : 1);
                case HostQuery.Vendor:
                case HostQuery.Product:
                    if (pointer == IntPtr.Zero) return IntPtr.Zero;
                    byte[] name = Encoding.ASCII.GetBytes("Mic Effects\0");
                    Marshal.Copy(name, 0, pointer, name.Length);
                    return new IntPtr(1);
                case HostQuery.VendorVersion: return new IntPtr(100);
                case HostQuery.Language: return new IntPtr(1);
                case HostQuery.CanDo:
                case HostQuery.WantMidi:
                default: return IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (module == IntPtr.Zero)
                return;

            try
            {
                CloseEditor();
                StopAudio();
                if (dispatcher != null)
                    Call(Command.Close);
            }
            finally
            {
                input?.Dispose();
                output?.Dispose();
                FreeLibrary(module);
                module = IntPtr.Zero;
                GC.KeepAlive(host);
            }
        }

        private sealed class AudioChannels : IDisposable
        {
            private readonly GCHandle[] pins;
            internal float[][] Samples { get; }
            internal IntPtr Pointers { get; }

            internal AudioChannels(int count)
            {
                Samples = new float[count][];
                pins = new GCHandle[count];
                Pointers = Marshal.AllocHGlobal(IntPtr.Size * count);
                for (int i = 0; i < count; ++i)
                {
                    Samples[i] = new float[BlockSize];
                    pins[i] = GCHandle.Alloc(Samples[i], GCHandleType.Pinned);
                    Marshal.WriteIntPtr(Pointers, i * IntPtr.Size, pins[i].AddrOfPinnedObject());
                }
            }

            public void Dispose()
            {
                foreach (GCHandle pin in pins)
                    if (pin.IsAllocated) pin.Free();
                Marshal.FreeHGlobal(Pointers);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
