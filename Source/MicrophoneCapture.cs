using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicEffects
{
    internal sealed class MicrophoneCapture : IDisposable
    {
        private MMDevice source;
        private WasapiCapture capture;
        private MMDevice destination;
        private WasapiOut playback;
        private BufferedWaveProvider output;
        private Exception captureError;
        private Exception playbackError;
        private float peak;
        private int lastSamples;

        internal int SampleRate => capture.WaveFormat.SampleRate;
        internal float Peak => unchecked(Environment.TickCount - Volatile.Read(ref lastSamples)) < 300
            ? Volatile.Read(ref peak) : 0;
        internal Exception Error => Volatile.Read(ref captureError);
        internal Exception PlaybackError => Volatile.Read(ref playbackError);
        internal bool IsListening => playback != null;
        internal int? Volume => Endpoint.ReadVolume(source);

        internal void SetVolume(int? value) => Endpoint.SetVolume(source, value);

        internal MicrophoneCapture(string systemId)
        {
            try
            {
                using (var devices = new MMDeviceEnumerator())
                    source = devices.GetDevice(systemId);

                // This shared input keeps the microphone and its system effects running.
                capture = new WasapiCapture(source, useEventSync: true, audioBufferMillisecondsLength: 25);
                WaveFormat mix = capture.WaveFormat;
                capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
                capture.DataAvailable += OnAudio;
                capture.RecordingStopped += OnCaptureStopped;
                capture.StartRecording();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void StartListening()
        {
            if (IsListening) return;
            Volatile.Write(ref playbackError, null);
            try
            {
                using (var devices = new MMDeviceEnumerator())
                    destination = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var buffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferLength = capture.WaveFormat.ConvertLatencyToByteSize(120),
                    DiscardOnBufferOverflow = true
                };
                buffer.AddSamples(Array.Empty<byte>(), 0, 0);
                playback = new WasapiOut(destination, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
                playback.Init(buffer);
                playback.PlaybackStopped += OnPlaybackStopped;
                Volatile.Write(ref output, buffer);
                playback.Play();
            }
            catch
            {
                StopListening();
                throw;
            }
        }

        private unsafe void OnAudio(object sender, WaveInEventArgs args)
        {
            if (args.BytesRecorded == 0) return;
            float maximum = 0;
            fixed (byte* data = args.Buffer)
            {
                float* samples = (float*)data;
                for (int index = 0; index < args.BytesRecorded / sizeof(float); index++)
                {
                    float amplitude = Math.Abs(samples[index]);
                    if (amplitude > maximum) maximum = amplitude;
                }
            }
            Volatile.Write(ref peak, maximum);
            Volatile.Write(ref lastSamples, Environment.TickCount);

            var buffer = Volatile.Read(ref output);
            if (buffer == null) return;
            // Drop old queued audio if the input and output clocks drift apart.
            if (buffer.BufferedBytes + args.BytesRecorded > buffer.BufferLength)
                buffer.ClearBuffer();
            buffer.AddSamples(args.Buffer, 0, Math.Min(args.BytesRecorded, buffer.BufferLength));
        }

        private void OnCaptureStopped(object sender, StoppedEventArgs args) =>
            Volatile.Write(ref captureError, args.Exception ?? new InvalidOperationException("Микрофон отключился."));

        private void OnPlaybackStopped(object sender, StoppedEventArgs args)
        {
            if (ReferenceEquals(sender, playback))
                Volatile.Write(ref playbackError, args.Exception ?? new InvalidOperationException("Устройство воспроизведения отключилось."));
        }

        internal void StopListening()
        {
            Volatile.Write(ref output, null);
            WasapiOut previous = playback;
            playback = null;
            if (previous != null) previous.PlaybackStopped -= OnPlaybackStopped;
            try { previous?.Dispose(); }
            finally
            {
                destination?.Dispose();
                destination = null;
            }
        }

        public void Dispose()
        {
            try { StopListening(); }
            finally
            {
                if (capture != null)
                {
                    capture.DataAvailable -= OnAudio;
                    capture.RecordingStopped -= OnCaptureStopped;
                }
                try { capture?.Dispose(); }
                finally { source?.Dispose(); }
            }
        }
    }
}
