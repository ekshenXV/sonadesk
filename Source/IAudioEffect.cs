using System;
using System.Drawing;

namespace MicEffects
{
    internal interface IAudioEffect : IDisposable
    {
        bool HasNativeEditor { get; }
        void StartAudio(int rate);
        void StopAudio();
        void Process(float[] source, int channels, float[] destination, int frames);
        void OpenEditor(IntPtr parent);
        void CloseEditor();
        void IdleEditor();
        Size EditorSize();
        void SaveState(EffectSettings state);
    }
}
