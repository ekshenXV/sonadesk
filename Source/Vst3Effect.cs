using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace MicEffects
{
    internal sealed class Vst3Effect : IAudioEffect
    {
        internal const int ResizeEditorMessage = 0x8000 + 45;
        private const string Library = "MicEffects.Vst3.dll";
        private IntPtr handle;
        private readonly object gate = new object();
        private readonly byte[] classId = new byte[16];
        public bool HasNativeEditor { get; }

        internal Vst3Effect(EffectSettings state)
        {
            string path = ResolveBinary(state.PluginPath);
            byte[] requested = string.IsNullOrEmpty(state.ClassId) ? null : Guid.ParseExact(state.ClassId, "N").ToByteArray();
            handle = vst3_open(path, requested, classId);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Не удалось открыть VST3. Нужен 64-битный эффект для Windows с аудиовходом и выходом.");
            try
            {
                if (vst3_restore(handle, state.Chunk, state.Chunk?.Length ?? 0,
                    state.ControllerState, state.ControllerState?.Length ?? 0) == 0)
                    throw new InvalidOperationException("VST3 не смог восстановить сохранённые настройки.");
                HasNativeEditor = vst3_has_editor(handle) != 0;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static string ResolveBinary(string path)
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full)) return full;
            string root = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string binary = Path.Combine(root, "Contents", "x86_64-win", Path.GetFileName(root));
            if (File.Exists(binary)) return binary;
            throw new FileNotFoundException("Не найден файл VST3. Выбери файл .vst3 или папку плагина .vst3.", path);
        }

        public void StartAudio(int rate)
        {
            lock (gate)
                if (vst3_start(handle, rate) == 0)
                    throw new InvalidOperationException("VST3 не поддерживает этот формат микрофона: " + rate + " Гц.");
        }

        public void StopAudio()
        {
            lock (gate) vst3_stop(handle);
        }

        public unsafe void Process(float[] source, int channels, float[] output, int frames)
        {
            lock (gate)
            {
                fixed (float* input = source)
                fixed (float* destination = output)
                    if (vst3_process(handle, input, channels, destination, frames) != 0) return;
            }
            for (int i = 0; i < frames; ++i)
            {
                output[i * 2] = source[i * channels];
                output[i * 2 + 1] = source[i * channels + channels - 1];
            }
        }

        public void OpenEditor(IntPtr parent)
        {
            if (vst3_attach(handle, parent) == 0)
                throw new InvalidOperationException("VST3 не смог открыть окно настроек.");
        }

        public void CloseEditor() => vst3_remove(handle);
        public void IdleEditor() { }

        public Size EditorSize() =>
            vst3_editor_size(handle, out int width, out int height) != 0 ? new Size(width, height) : new Size(640, 440);

        public void SaveState(EffectSettings state)
        {
            lock (gate)
            {
                vst3_save(handle, out int componentBytes, out int controllerBytes);
                var component = componentBytes == 0 ? null : new byte[componentBytes];
                var controller = controllerBytes == 0 ? null : new byte[controllerBytes];
                vst3_copy_state(handle, component, controller);
                state.ClassId = new Guid(classId).ToString("N");
                state.Chunk = component;
                state.ControllerState = controller;
            }
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            vst3_close(handle);
            handle = IntPtr.Zero;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr vst3_open(string path, byte[] requested, [Out] byte[] actual);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void vst3_close(IntPtr effect);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vst3_start(IntPtr effect, int rate);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void vst3_stop(IntPtr effect);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int vst3_process(IntPtr effect, float* input, int channels, float* output, int frames);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vst3_has_editor(IntPtr effect);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vst3_editor_size(IntPtr effect, out int width, out int height);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vst3_attach(IntPtr effect, IntPtr parent);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void vst3_remove(IntPtr effect);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vst3_restore(IntPtr effect, byte[] component, int componentBytes, byte[] controller, int controllerBytes);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void vst3_save(IntPtr effect, out int componentBytes, out int controllerBytes);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void vst3_copy_state(IntPtr effect, [Out] byte[] component, [Out] byte[] controller);
    }
}
