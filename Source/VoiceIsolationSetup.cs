using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MicEffects
{
    internal sealed partial class AudioHost
    {
        private const int IsolationSeconds = 6;
        private readonly Dictionary<Guid, bool> isolationSettings = new Dictionary<Guid, bool>();
        private VoiceIsolationEffect isolationEffect;
        private Task<VoiceIsolationEffect> isolationLoad;
        private AudioSample isolationSample;
        private AudioBridge isolationRoute;
        private RecordingComparison isolationComparison;
        private string isolationStage, isolationError;
        private bool isolationResumeMonitor, isolationLoadFailed, isolationReportedFailure;
        private int isolationStarted, isolationProgress;

        private bool IsolationRequested => selected != null && isolationSettings.TryGetValue(selected.Id, out bool enabled) && enabled;

        private VoiceIsolationEffect ActiveIsolation()
        {
            if (route == null || !IsolationRequested) return null;
            if (isolationEffect == null && isolationLoad == null && !isolationLoadFailed)
                _ = RestoreIsolation();
            return isolationEffect;
        }

        private async Task RestoreIsolation()
        {
            try
            {
                await LoadIsolation();
                if (!closing) ApplyChain();
            }
            catch { isolationLoadFailed = true; }
            if (!closing) Publish();
        }

        private async Task LoadIsolation()
        {
            if (isolationEffect != null) return;
            if (isolationLoad == null)
                isolationLoad = Task.Run(() => new VoiceIsolationEffect());
            try
            {
                VoiceIsolationEffect loaded = await isolationLoad;
                if (closing) loaded.Dispose();
                else
                {
                    isolationEffect = loaded;
                    isolationLoadFailed = false;
                }
            }
            catch (Exception error)
            {
                isolationLoadFailed = true;
                throw new InvalidOperationException("isolationUnavailable", error);
            }
            finally { isolationLoad = null; }
        }

        private void BeginIsolationRecording()
        {
            int sampleRate = route?.SampleRate ?? 0;
            if (selected == null || sampleRate < 8000 || sampleRate > 384000)
                throw new InvalidOperationException("isolationNeedsMicrophone");
            if (isolationSample != null) throw new InvalidOperationException("isolationBusy");
            isolationResumeMonitor |= capture?.IsListening == true;
            if (isolationResumeMonitor) StopMonitor();
            isolationSample = new AudioSample(sampleRate, IsolationSeconds);
            isolationRoute = route;
            isolationComparison = null;
            isolationStage = "recording";
            isolationError = null;
            isolationStarted = Environment.TickCount;
            isolationProgress = 0;
        }

        private void ClearIsolationSetup(bool restoreMonitor = true)
        {
            isolationRoute?.RecordSample(null);
            isolationRoute = null;
            isolationSample = null;
            isolationComparison = null;
            isolationStage = null;
            isolationError = null;
            isolationProgress = 0;
            if (isolationResumeMonitor && restoreMonitor && capture != null) StartMonitor();
            isolationResumeMonitor = false;
        }

        private void FailIsolationRecording(string error)
        {
            ClearIsolationSetup();
            isolationError = error;
            Publish();
        }

        private void TickIsolation(int now)
        {
            bool failure = route != null && IsolationRequested && isolationEffect?.Failed == true;
            if (isolationReportedFailure != failure)
            {
                isolationReportedFailure = failure;
                Publish();
            }
            if (isolationStage != "recording") return;
            AudioSample sample = isolationSample;
            if (route != isolationRoute || route?.SampleRate != sample.Rate || route.Error != null || capture?.Error != null)
            {
                FailIsolationRecording("isolationNeedsMicrophone");
                return;
            }
            int elapsed = unchecked(now - isolationStarted);
            if (elapsed >= 350) isolationRoute.RecordSample(sample);
            if (!sample.Complete)
            {
                if (elapsed > (IsolationSeconds + 4) * 1000) FailIsolationRecording("isolationNoSignal");
                else if (sample.Progress != isolationProgress)
                {
                    isolationProgress = sample.Progress;
                    Write(new { level = peak, isolation = IsolationStatus() });
                }
                return;
            }
            isolationRoute.RecordSample(null);
            if (!sample.HasSignal)
            {
                FailIsolationRecording("isolationNoSignal");
                return;
            }
            isolationStage = "processing";
            isolationProgress = 100;
            Publish();
            _ = ReviewIsolation(sample);
        }

        private async Task ReviewIsolation(AudioSample sample)
        {
            try
            {
                RecordingComparison comparison = await Task.Run(() => VoiceIsolationEffect.Compare(sample));
                if (closing || isolationSample != sample) return;
                isolationComparison = comparison;
                isolationStage = "review";
                isolationError = sample.IsClipping ? "isolationClipping" : null;
                isolationSample = null;
                Publish();
            }
            catch
            {
                if (!closing && isolationSample == sample) FailIsolationRecording("isolationUnavailable");
            }
        }

        private async Task SetIsolation(bool enabled, bool fromReview = false)
        {
            if (selected == null || route == null) throw new InvalidOperationException("isolationNeedsMicrophone");
            Guid id = selected.Id;
            var currentRoute = route;
            var review = isolationComparison;
            bool configured = isolationSettings.TryGetValue(id, out bool previous);
            if (fromReview ? review == null : !configured)
                throw new InvalidOperationException("recordingPreviewUnavailable");
            if (enabled) await LoadIsolation();
            if (closing || selected?.Id != id || route != currentRoute || (fromReview && isolationComparison != review))
                throw new InvalidOperationException("isolationNeedsMicrophone");
            isolationSettings[id] = enabled;
            try { ApplyChain(); Save(); }
            catch
            {
                if (configured) isolationSettings[id] = previous;
                else isolationSettings.Remove(id);
                ApplyChain();
                throw;
            }
            if (fromReview) ClearIsolationSetup();
        }

        private void ResetIsolation(string microphone)
        {
            if (!Guid.TryParse(microphone, out Guid id) || selected?.Id != id)
                throw new InvalidOperationException("isolationNeedsMicrophone");
            if (!isolationSettings.TryGetValue(id, out bool previous)) return;

            isolationSettings.Remove(id);
            try { ApplyChain(); Save(); }
            catch
            {
                isolationSettings[id] = previous;
                ApplyChain();
                throw;
            }
            ClearIsolationSetup();
        }

        private object IsolationStatus() => new
        {
            configured = selected != null && isolationSettings.ContainsKey(selected.Id),
            enabled = IsolationRequested && route != null && isolationEffect?.Failed == false,
            requested = IsolationRequested,
            failure = route != null && IsolationRequested && (isolationLoadFailed || isolationEffect?.Failed == true)
                ? "isolationUnavailable" : null,
            stage = isolationStage, error = isolationError, progress = isolationProgress,
            wave = isolationStage == "recording" ? isolationSample.Waveform() : null,
            before = isolationComparison?.BeforeWave, after = isolationComparison?.AfterWave
        };
    }
}
