using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace MicEffects
{
    internal sealed partial class AudioHost : ApplicationContext
    {
        private readonly Control dispatcher = new Control();
        private readonly Timer timer = new Timer { Interval = 90 };
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly EffectChain chain = new EffectChain();
        private readonly EffectChain playbackChain = new EffectChain();
        private readonly PlaybackEqualizer equalizer = new PlaybackEqualizer();
        private readonly Dictionary<Guid, PlaybackPreset> playbackPresets = new Dictionary<Guid, PlaybackPreset>();
        private readonly EqualizerBand[] flatBands = PlaybackPreset.DefaultBands();
        private readonly List<Row> rows = new List<Row>();
        private readonly IntPtr window;
        private readonly StreamReader input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        private readonly StreamWriter output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        private Endpoint[] inputs = Array.Empty<Endpoint>();
        private Guid? savedInput;
        private Endpoint selected;
        private Endpoint[] outputs = Array.Empty<Endpoint>();
        private Guid? playbackId;
        private MMDevice playbackMeter;
        private int playbackLevel;
        private int? playbackVolume;
        private AudioBridge playbackRoute;
        private bool playbackConfiguring;
        private bool playbackEnhancementsDisabled;
        private string playbackError;
        private int nextPlaybackCheck;
        private int playbackRate;
        private AudioBridge route;
        private MicrophoneCapture capture;
        private int preparedRate;
        private string monitorError;
        private string inputRoutingError;
        private SetupSession setup;
        private bool preservePreset;
        private bool initialized;
        private bool failedToLoadPreset;
        private bool closing;
        private bool configuringInput;
        private Task reconnectingInput;
        private bool enhancementsDisabled;
        private bool audioRestartRequired;
        private int nextInputCheck;
        private int nextEnhancementsCheck;
        private string status = "Загрузка аудиосистемы…";
        private string kind = "idle";
        private int? lastSignal;
        private int waitingSince;
        private int peak;
        private int rate;
        private int? inputVolume;
        private string setupError;
        private bool lastIsolationReady;

        private sealed class Row
        {
            internal readonly string Id = Guid.NewGuid().ToString("N");
            internal EffectSettings State;
            internal IAudioEffect Effect;
            internal Form Editor;
            internal string Error;

            internal string DisplayName => Path.GetFileNameWithoutExtension(State.PluginPath);
        }

        internal AudioHost(IntPtr window)
        {
            this.window = window;
            // The message loop owns the VST editors and all chain mutations.
            _ = dispatcher.Handle;
            timer.Tick += (_, __) => Tick();
            timer.Start();
            _ = ReadCommands();
        }

        private async Task ReadCommands()
        {
            try
            {
                while (true)
                {
                    string line = await input.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    var finished = new TaskCompletionSource<bool>();
                    dispatcher.BeginInvoke((Action)(async () =>
                    {
                        try { await Dispatch(line); }
                        finally { finished.TrySetResult(true); }
                    }));
                    await finished.Task.ConfigureAwait(false);
                    if (closing) return;
                }
                dispatcher.BeginInvoke((Action)ExitThread);
            }
            catch (Exception) when (dispatcher.IsDisposed || closing) { }
            catch (IOException) { dispatcher.BeginInvoke((Action)ExitThread); }
        }

        private async Task Dispatch(string line)
        {
            int id = 0;
            try
            {
                var command = json.Deserialize<Command>(line);
                id = command.Id;
                if (command.Op != "cancel-isolation" && reconnectingInput != null) await reconnectingInput;
                switch (command.Op)
                {
                    case "init":
                        if (!initialized) { await Initialize(command.Enabled); initialized = true; }
                        Write(new { id, state = Snapshot(), data = LegacyStartupCommand() });
                        return;
                    case "select":
                        Endpoint device = inputs.FirstOrDefault(value => value.Id.ToString("D") == command.Value);
                        if (device == null) throw new InvalidOperationException("Микрофон недоступен.");
                        SetDefaultInput(device);
                        inputRoutingError = null;
                        if (device != selected || capture == null || route == null)
                        {
                            bool resumeMonitor = capture?.IsListening == true;
                            StopMonitor();
                            monitorError = null;
                            selected = device;
                            savedInput = device.Id;
                            await Select();
                            if (resumeMonitor && capture != null) StartMonitor();
                            Save();
                        }
                        break;
                    case "enable-enhancements":
                        if (selected == null || selected.Id.ToString("D") != command.Value)
                            throw new InvalidOperationException("Сначала выбери микрофон.");
                        if (enhancementsDisabled || ApoSetup.EnhancementsDisabled(selected.Id))
                        {
                            bool resume = capture?.IsListening == true;
                            await Select(enableEnhancements: true);
                            if (resume && capture != null) StartMonitor();
                        }
                        break;
                    case "monitor":
                        monitorError = null;
                        if (command.Enabled) StartMonitor();
                        else StopMonitor();
                        break;
                    case "set-input-volume":
                        if (capture == null || selected?.Id.ToString("D") != command.Value)
                            throw new InvalidOperationException("volumeChangeFailed");
                        capture.SetVolume(command.Volume);
                        Write(new { id, state = new { inputVolume = capture.Volume } });
                        return;
                    case "set-playback-volume":
                        RequirePlayback(command.Value);
                        Endpoint.SetVolume(playbackMeter, command.Volume);
                        Write(new { id, state = new { playbackVolume = Endpoint.ReadVolume(playbackMeter) } });
                        return;
                    case "record-isolation": BeginIsolationRecording(); break;
                    case "cancel-isolation": ClearIsolationSetup(); break;
                    case "save-isolation": await SetIsolation(true, fromReview: true); break;
                    case "toggle-isolation": await SetIsolation(command.Enabled); break;
                    case "reset-isolation": ResetIsolation(command.Value); break;
                    case "isolation-preview":
                        RecordingComparison voicePreview = isolationComparison;
                        if (voicePreview == null) throw new InvalidOperationException("recordingPreviewUnavailable");
                        string clip = await Task.Run(() => voicePreview.Encode(command.Enabled));
                        Write(new { id, data = clip });
                        return;
                    case "select-playback":
                        Endpoint output = outputs.FirstOrDefault(device => device.Id.ToString("D") == command.Value);
                        if (output == null)
                            throw new InvalidOperationException("outputUnavailable");
                        try { Endpoint.SetDefault(output, DataFlow.Render); }
                        catch (Exception error) when (error is COMException || error is InvalidCastException)
                        {
                            throw new InvalidOperationException("outputSwitchFailed", error);
                        }
                        TrackPlayback(output.Id);
                        await ConnectPlayback(allowSetup: true);
                        Save();
                        break;
                    case "toggle-equalizer":
                        RequirePlayback(command.Value);
                        PlaybackSettings.Enabled = command.Enabled;
                        if (command.Enabled) await ConnectPlayback(allowSetup: true);
                        else { StopPlayback(); playbackError = null; }
                        Save();
                        break;
                    case "enable-playback-enhancements":
                        RequirePlayback(command.Value);
                        if (PlaybackSettings.Enabled) await ConnectPlayback(allowSetup: true, enableEnhancements: true);
                        break;
                    case "set-equalizer":
                        RequirePlayback(command.Value);
                        if (!PlaybackSettings.Enabled) throw new InvalidOperationException("enableEqualizerFirst");
                        if (!ValidBands(command.Bands)) throw new InvalidOperationException("invalidBands");
                        SetPlaybackBands(command.Bands);
                        break;
                    case "add": Add(command.Value); break;
                    case "toggle":
                        Row row = Find(command.Value);
                        if (row.Effect == null) throw new InvalidOperationException(row.Error);
                        bool previous = row.State.Enabled;
                        row.State.Enabled = command.Enabled;
                        try { ApplyChain(); }
                        catch { row.State.Enabled = previous; throw; }
                        Save();
                        break;
                    case "edit": OpenEditor(Find(command.Value)); break;
                    case "remove": Remove(Find(command.Value)); break;
                    case "hide":
                        ClearIsolationSetup(restoreMonitor: false);
                        StopMonitor(); monitorError = null; CloseEditors();
                        break;
                    case "quit":
                        StopMonitor();
                        CloseEditors();
                        Reply(id);
                        ExitThread();
                        return;
                    default: throw new InvalidOperationException("Неизвестная команда.");
                }
                Reply(id);
            }
            catch (Exception error)
            {
                Write(new { id, error = error.Message, state = Snapshot() });
            }
        }

        private sealed class Command
        {
            public int Id { get; set; }
            public string Op { get; set; }
            public string Value { get; set; }
            public bool Enabled { get; set; }
            public int? Volume { get; set; }
            public EqualizerBand[] Bands { get; set; }
        }

        private static string LegacyStartupCommand()
        {
            try
            {
                using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return run?.GetValue("Mic Effects", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is SecurityException)
            {
                return null;
            }
        }

        private async Task Initialize(bool startedAtLogin)
        {
            inputs = Endpoint.List();
            foreach (Endpoint microphone in inputs) LegacyInputLevel.RestoreIfInterrupted(microphone.Id);
            outputs = Endpoint.List(DataFlow.Render);
            try
            {
                // Prompt once before any third-party DLL is loaded.
                if (!startedAtLogin && inputs.Any(device => ApoSetup.HasActiveRegistryEndpoint(device.Id) &&
                    !ApoSetup.Ready(device.Id) && !ApoSetup.EnhancementsDisabled(device.Id)))
                    setup = SetupSession.Start();
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223) { }
            catch (Win32Exception error) { setupError = error.Message; }
            catch (IOException error) { setupError = error.Message; }

            try
            {
                Preset preset = Preset.Load();
                foreach (var preference in preset.VoiceIsolation ?? new Dictionary<string, bool>())
                    if (Guid.TryParse(preference.Key, out Guid input)) isolationSettings[input] = preference.Value;
                foreach (PlaybackPreset entry in preset.Playback ?? Array.Empty<PlaybackPreset>())
                    if (entry != null && Guid.TryParse(entry.DeviceId, out Guid outputId) && ValidBands(entry.Bands))
                        playbackPresets[outputId] = entry;
                string id = preset.MicrophoneId;
                int brace = id?.LastIndexOf('{') ?? -1;
                if (Guid.TryParse(brace < 0 ? id : id.Substring(brace), out Guid parsed))
                    savedInput = parsed;
                foreach (EffectSettings state in preset.LoadEffects().Where(value => !string.IsNullOrWhiteSpace(value?.PluginPath)))
                {
                    var row = new Row { State = state };
                    try { row.Effect = LoadEffect(state); }
                    catch (Exception error) { row.Error = error.Message; }
                    rows.Add(row);
                }
            }
            catch (SerializationException) { preservePreset = true; }
            catch (IOException) { failedToLoadPreset = true; }
            catch (UnauthorizedAccessException) { failedToLoadPreset = true; }

            selected = savedInput.HasValue
                ? inputs.FirstOrDefault(device => device.Id == savedInput.Value)
                : inputs.FirstOrDefault(device => ApoSetup.WasSelected(device.Id));
            savedInput = selected?.Id ?? savedInput;
            await Select();
            if (selected != null)
            {
                try { SetDefaultInput(selected); }
                catch (InvalidOperationException error) { inputRoutingError = error.Message; }
            }
            try { TrackPlayback(Endpoint.DefaultPlayback()); }
            catch (COMException) { }
            await ConnectPlayback(allowSetup: !startedAtLogin && setup != null);
            if (preservePreset)
                SetStatus("Пресет повреждён. При сохранении исходный файл останется в копии.", "error");
            if (failedToLoadPreset)
                SetStatus("Нет доступа к пресету. Проверь файл и перезапусти SonaDesk.", "error");
        }

        private static void SetDefaultInput(Endpoint device)
        {
            try { Endpoint.SetDefault(device, DataFlow.Capture); }
            catch (Exception error) when (error is COMException || error is InvalidCastException || error is UnauthorizedAccessException)
            {
                throw new InvalidOperationException("inputSwitchFailed", error);
            }
        }

        private async Task Select(bool enableEnhancements = false)
        {
            if (selected == null)
            {
                StopRoute();
                ApplyChain();
                enhancementsDisabled = false;
                SetStatus(inputs.Length == 0 ? "Windows пока не видит микрофоны." : "Выбери микрофон.");
                return;
            }
            Guid endpoint = selected.Id;
            configuringInput = true;
            try
            {
                enhancementsDisabled = ApoSetup.EnhancementsDisabled(endpoint);
                if (enableEnhancements && enhancementsDisabled && setup == null)
                {
                    try { setup = SetupSession.Start(); }
                    catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return; }
                }

                StopRoute();
                ApplyChain();
                audioRestartRequired = false;
                if (!ApoSetup.HasActiveRegistryEndpoint(endpoint))
                {
                    OpenCapture(selected, useEffects: false);
                    SetStatus("inputEffectsUnavailable", "error");
                    return;
                }
                bool readyForEffects = ApoSetup.Ready(endpoint);
                if (enhancementsDisabled && !enableEnhancements)
                {
                    SetStatus("Улучшения звука отключены.");
                    OpenCapture(selected, useEffects: readyForEffects);
                    return;
                }

                if (!readyForEffects || enhancementsDisabled)
                {
                    if (setup == null)
                    {
                        SetStatus(setupError ?? "Перезапусти SonaDesk и подтверди запрос Windows, чтобы подключить этот вход.", "error");
                        OpenCapture(selected, useEffects: false);
                        return;
                    }
                    SetStatus("Подключаю микрофон…");
                    Publish();
                    int code = await setup.AttachAsync(endpoint, enableEnhancements);
                    enhancementsDisabled = ApoSetup.EnhancementsDisabled(endpoint);
                    if (code != 0)
                    {
                        audioRestartRequired = code == 5;
                        if (code == 7) SetStatus("Улучшения звука отключены.");
                        else SetStatus(code == 3 ? "Windows не дала изменить подключение микрофона."
                            : code == 5 ? "Перезапусти Windows, чтобы загрузить системный модуль."
                            : code == 6 ? "Windows не загрузила APO. Исходные эффекты микрофона восстановлены."
                            : "Не удалось подключить микрофон. Код " + code + ".", "error");
                        OpenCapture(selected, useEffects: false);
                        return;
                    }
                }
                OpenCapture(selected, useEffects: true);
                UpdateStatus();
            }
            catch (Exception error) { SetStatus(error.Message, "error"); }
            finally { configuringInput = false; }
        }

        private void OpenCapture(Endpoint endpoint, bool useEffects)
        {
            capture = new MicrophoneCapture(endpoint.SystemId);
            if (useEffects) StartEffects(endpoint.Id);
        }

        private void StartEffects(Guid endpoint)
        {
            chain.StartAudio(capture.SampleRate);
            preparedRate = capture.SampleRate;
            route = new AudioBridge(endpoint, chain);
            ApplyChain();
        }

        private Row Find(string id) => rows.FirstOrDefault(row => row.Id == id)
            ?? throw new InvalidOperationException("Эффект уже удалён.");

        private static IAudioEffect LoadEffect(EffectSettings state) =>
            string.Equals(Path.GetExtension(state.PluginPath), ".vst3", StringComparison.OrdinalIgnoreCase)
                ? (IAudioEffect)new Vst3Effect(state)
                : new VstEffect(state.PluginPath, state);

        private void Add(string path)
        {
            var state = new EffectSettings { PluginPath = path, Enabled = true };
            var row = new Row { State = state, Effect = LoadEffect(state) };
            rows.Add(row);
            try { ApplyChain(); }
            catch
            {
                rows.Remove(row);
                row.Effect.Dispose();
                throw;
            }
            row.Effect.SaveState(state);
            Save();
        }

        private void Remove(Row row)
        {
            rows.Remove(row);
            ApplyChain();
            row.Editor?.Close();
            row.Effect?.Dispose();
            Save();
        }

        private void ApplyChain()
        {
            VoiceIsolationEffect isolation = ActiveIsolation();
            var effects = rows.Where(row => row.Effect != null && row.State.Enabled).Select(row => row.Effect);
            if (isolation != null) effects = effects.Prepend(isolation);
            chain.SetEffects(effects.ToArray());
            UpdateStatus();
        }

        private void Save()
        {
            if (failedToLoadPreset) throw new IOException("Нет доступа к пресету. Изменения применены до выхода из программы.");
            new Preset { MicrophoneId = savedInput?.ToString("B"), Effects = rows.Select(row => row.State).ToArray(),
                Playback = playbackPresets.Values.ToArray(),
                VoiceIsolation = isolationSettings.ToDictionary(pair => pair.Key.ToString("D"), pair => pair.Value) }
                .Save(preservePreset);
            preservePreset = false;
        }

        private static bool ValidBands(EqualizerBand[] bands) =>
            bands?.Length == 10 && bands.All(band => band?.IsValid == true);

        private PlaybackPreset PlaybackSettings
        {
            get
            {
                Guid id = playbackId.Value;
                if (!playbackPresets.TryGetValue(id, out PlaybackPreset preset))
                    playbackPresets[id] = preset = new PlaybackPreset { DeviceId = id.ToString("D"), Bands = PlaybackPreset.DefaultBands() };
                return preset;
            }
        }

        private void RequirePlayback(string value)
        {
            if (!Guid.TryParse(value, out Guid id) || id != playbackId || !outputs.Any(output => output.Id == id))
                throw new InvalidOperationException("outputUnavailable");
        }

        private void UpdatePlaybackChain()
        {
            bool needsFilters = playbackRoute != null && PlaybackSettings.Enabled && PlaybackSettings.Bands.Any(band => band.Gain != 0);
            playbackChain.SetEffects(needsFilters ? new IAudioEffect[] { equalizer } : Array.Empty<IAudioEffect>());
        }

        private void SetPlaybackBands(EqualizerBand[] bands)
        {
            PlaybackPreset preset = PlaybackSettings;
            EqualizerBand[] previous = preset.Bands;
            preset.Bands = bands;
            try { Save(); }
            catch { preset.Bands = previous; throw; }
            equalizer.SetBands(bands);
            UpdatePlaybackChain();
        }

        private async Task ConnectPlayback(bool allowSetup, bool enableEnhancements = false)
        {
            StopPlayback();
            playbackError = null;
            playbackEnhancementsDisabled = false;
            if (!playbackId.HasValue || !outputs.Any(output => output.Id == playbackId)) return;

            Guid id = playbackId.Value;
            try
            {
                playbackEnhancementsDisabled = ApoSetup.EnhancementsDisabled(id, playback: true);
                if (!PlaybackSettings.Enabled || (playbackEnhancementsDisabled && !enableEnhancements)) return;
                if (!ApoSetup.HasActiveRegistryEndpoint(id, playback: true))
                {
                    playbackError = "outputEffectsUnavailable";
                    return;
                }
                if (!ApoSetup.Ready(id, playback: true) || playbackEnhancementsDisabled)
                {
                    if (!allowSetup) { playbackError = "outputNeedsSetup"; return; }
                    if (setup == null) setup = SetupSession.Start();
                    playbackConfiguring = true;
                    Publish();
                    bool restoreCapture = capture != null;
                    bool resumeMonitor = capture?.IsListening == true;
                    if (restoreCapture) StopRoute();
                    int code;
                    try { code = await setup.AttachAsync(id, enableEnhancements, playback: true); }
                    finally
                    {
                        if (restoreCapture && selected != null)
                        {
                            try
                            {
                                OpenCapture(selected, ApoSetup.Ready(selected.Id));
                                if (resumeMonitor) StartMonitor();
                                UpdateStatus();
                            }
                            catch (Exception error) { SetStatus(error.Message, "error"); }
                        }
                    }
                    playbackEnhancementsDisabled = ApoSetup.EnhancementsDisabled(id, playback: true);
                    if (code != 0)
                    {
                        playbackError = code == 7 ? null : code == 5 ? "outputRestart" :
                            code == 6 ? "outputUnsupported" : "outputConnectFailed";
                        return;
                    }
                }

                equalizer.SetBands(PlaybackSettings.Bands);
                playbackRoute = new AudioBridge(id, playbackChain);
                UpdatePlaybackChain();
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223) { playbackError = "outputNeedsSetup"; }
            catch (Exception error) { playbackError = error.Message; }
            finally { playbackConfiguring = false; }
        }

        private void StopPlayback()
        {
            playbackRoute?.Dispose();
            playbackRoute = null;
            playbackChain.StopAudio();
            playbackChain.SetEffects(Array.Empty<IAudioEffect>());
            playbackRate = 0;
        }

        private void TrackPlayback(Guid? id)
        {
            if (id == playbackId) return;
            playbackId = id;
            ClearPlaybackMeter();
            ReadPlaybackLevel();
            if (capture?.IsListening == true)
            {
                StopMonitor();
                StartMonitor();
            }
        }

        private void ClearPlaybackMeter()
        {
            playbackMeter?.Dispose();
            playbackMeter = null;
            playbackLevel = 0;
        }

        private void ReadPlaybackLevel()
        {
            try
            {
                if (playbackMeter == null && playbackId != null)
                {
                    Endpoint output = outputs.FirstOrDefault(device => device.Id == playbackId);
                    if (output == null) return;
                    using (var enumerator = new MMDeviceEnumerator())
                        playbackMeter = enumerator.GetDevice(output.SystemId);
                }
                float level = playbackMeter?.AudioMeterInformation.MasterPeakValue ?? 0;
                playbackLevel = MeterLevel(level);
            }
            catch (COMException)
            {
                playbackLevel = 0;
                if (Endpoint.ReadVolume(playbackMeter) == null) ClearPlaybackMeter();
            }
        }

        private static int MeterLevel(float peak) => (int)(Math.Sqrt(Math.Max(0, Math.Min(1, peak))) * 100);

        private void TickPlayback(int now)
        {
            if (playbackConfiguring) return;
            if (!configuringInput && initialized && unchecked(now - nextPlaybackCheck) >= 0)
            {
                nextPlaybackCheck = unchecked(now + 1600);
                try
                {
                    Endpoint[] detected = Endpoint.List(DataFlow.Render);
                    Guid? defaultOutput = Endpoint.DefaultPlayback();
                    if (!detected.Any(device => device.Id == defaultOutput)) defaultOutput = null;
                    bool changed = !outputs.Select(output => (output.Id, output.Name))
                        .SequenceEqual(detected.Select(output => (output.Id, output.Name)));
                    outputs = detected;
                    bool disabled = defaultOutput.HasValue && ApoSetup.EnhancementsDisabled(defaultOutput.Value, playback: true);
                    if (defaultOutput != playbackId || disabled != playbackEnhancementsDisabled)
                    {
                        TrackPlayback(defaultOutput);
                        _ = ConnectPlayback(allowSetup: false);
                        changed = true;
                    }
                    if (changed) Publish();
                }
                catch (COMException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            ReadPlaybackLevel();
            if (playbackRoute == null) return;
            if (playbackRoute.Error is Exception failure)
            {
                StopPlayback();
                playbackError = failure.Message;
                Publish();
                return;
            }
            int sampleRate = playbackRoute.SampleRate;
            if (playbackRate == sampleRate) return;
            if (sampleRate > 0) playbackChain.StartAudio(sampleRate);
            else playbackChain.StopAudio();
            playbackRate = sampleRate;
            Publish();
        }

        private void OpenEditor(Row row)
        {
            if (row.Editor != null)
            {
                row.Editor.Activate();
                return;
            }
            IAudioEffect plugin = row.Effect;
            if (plugin?.HasNativeEditor != true) return;
            var editor = new PluginWindow
            {
                Text = row.DisplayName,
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None,
                ClientSize = plugin.EditorSize()
            };
            var idle = new Timer { Interval = 30 };
            idle.Tick += (_, __) => plugin.IdleEditor();
            editor.FormClosed += (_, __) =>
            {
                idle.Dispose();
                plugin.CloseEditor();
                row.Editor = null;
                if (rows.Contains(row))
                {
                    try { plugin.SaveState(row.State); Save(); }
                    catch (Exception error) { SetStatus(error.Message, "error"); Publish(); }
                }
            };
            try
            {
                plugin.OpenEditor(editor.Handle);
                editor.ClientSize = plugin.EditorSize();
                row.Editor = editor;
                if (window != IntPtr.Zero) editor.Show(new WindowOwner(window));
                else editor.Show();
                idle.Start();
            }
            catch
            {
                idle.Dispose();
                plugin.CloseEditor();
                editor.Dispose();
                row.Editor = null;
                throw;
            }
        }

        private sealed class WindowOwner : IWin32Window
        {
            internal WindowOwner(IntPtr window) => Handle = window;
            public IntPtr Handle { get; }
        }

        private sealed class PluginWindow : Form
        {
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == Vst3Effect.ResizeEditorMessage)
                {
                    ClientSize = new Size(message.WParam.ToInt32(), message.LParam.ToInt32());
                    return;
                }
                base.WndProc(ref message);
            }
        }

        private void CloseEditors()
        {
            foreach (var row in rows) row.Editor?.Close();
        }

        private void UpdateStatus()
        {
            waitingSince = Environment.TickCount;
            if (route == null) return;
            if (chain.HasEnabledEffects)
                SetStatus("Жду сигнал от Windows.", "waiting");
            else if (rows.Any(row => row.Error != null && row.State.Enabled))
                SetStatus("Некоторые плагины не загрузились.", "error");
            else
                SetStatus(rows.Count == 0 ? "Микрофон подключён. Добавь эффект." : "Эффекты выключены. Звук без обработки.");
            if (!chain.HasEnabledEffects) lastSignal = null;
        }

        private void SetStatus(string message, string mode = "idle")
        {
            status = message;
            kind = mode;
        }

        private void TickInputs(int now)
        {
            if (!initialized || configuringInput || playbackConfiguring || unchecked(now - nextInputCheck) < 0)
                return;
            nextInputCheck = unchecked(now + 1600);
            try { RefreshInputs(Endpoint.List()); }
            catch (COMException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        private void RefreshInputs(Endpoint[] detected)
        {
            if (inputs.Select(device => (device.Id, device.Name))
                .SequenceEqual(detected.Select(device => (device.Id, device.Name)))) return;

            inputs = detected;
            Endpoint next = inputs.FirstOrDefault(device => device.Id == savedInput);
            bool change = selected?.Id != next?.Id;
            selected = next;
            if (change)
            {
                monitorError = null;
                inputRoutingError = null;
                reconnectingInput = ReconnectInput(resume: false);
            }
            else Publish();
        }

        private void Tick()
        {
            string previousStatus = status;
            int previousRate = rate;
            int previousPeak = peak;
            int previousPlaybackLevel = playbackLevel;
            int? previousInputVolume = inputVolume;
            int? previousPlaybackVolume = playbackVolume;
            int now = Environment.TickCount;
            TickIsolation(now);
            TickPlayback(now);
            TickInputs(now);
            if (!configuringInput && selected != null && unchecked(now - nextEnhancementsCheck) >= 0)
            {
                nextEnhancementsCheck = unchecked(now + 1000);
                try
                {
                    bool disabled = ApoSetup.EnhancementsDisabled(selected.Id);
                    if (disabled != enhancementsDisabled)
                    {
                        enhancementsDisabled = disabled;
                        if (!disabled && capture != null && route == null && !audioRestartRequired)
                        {
                            try
                            {
                                if (ApoSetup.Ready(selected.Id)) StartEffects(selected.Id);
                                else
                                {
                                    bool resume = capture.IsListening;
                                    configuringInput = true;
                                    reconnectingInput = ReconnectInput(resume);
                                    return;
                                }
                            }
                            catch (Exception error)
                            {
                                SetStatus(error.Message, "error");
                                Publish();
                                return;
                            }
                        }
                        if (disabled) SetStatus("Улучшения звука отключены.");
                        else UpdateStatus();
                        Publish();
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            if (capture?.Error is Exception captureFailure)
            {
                SetStatus(captureFailure.Message, "error");
                StopRoute();
                Publish();
            }
            if (route?.Error is Exception routeFailure)
            {
                route.Dispose();
                route = null;
                chain.StopAudio();
                preparedRate = 0;
                ClearIsolationSetup();
                ApplyChain();
                SetStatus(routeFailure.Message, "error");
                Publish();
            }
            if (capture?.IsListening == true && capture.PlaybackError is Exception listenFailure)
            {
                StopMonitor();
                monitorError = "Прослушивание остановилось. " + listenFailure.Message;
                Publish();
            }
            if (route != null && route.SampleRate > 0 && route.SampleRate != preparedRate)
            {
                try
                {
                    int sampleRate = route.SampleRate;
                    chain.StartAudio(sampleRate);
                    preparedRate = sampleRate;
                    ApplyChain();
                    Publish();
                }
                catch (Exception error)
                {
                    route.Dispose();
                    route = null;
                    preparedRate = 0;
                    ClearIsolationSetup();
                    ApplyChain();
                    SetStatus(error.Message, "error");
                    Publish();
                }
            }
            float level = capture?.Peak ?? 0;
            peak = MeterLevel(level);
            rate = capture?.SampleRate ?? 0;
            inputVolume = capture?.Volume;
            playbackVolume = Endpoint.ReadVolume(playbackMeter);
            if (route != null && chain.HasEnabledEffects)
            {
                if (route.IsProcessing)
                {
                    lastSignal = now;
                    SetStatus("Эффекты применяются", "active");
                }
                else if (kind == "active" && lastSignal.HasValue && unchecked(now - lastSignal.Value) >= 900)
                {
                    waitingSince = now;
                    SetStatus("Жду сигнал от Windows.", "waiting");
                }
                else if (!enhancementsDisabled && kind == "waiting" && route.SampleRate == 0 && unchecked(now - waitingSince) > 8000)
                {
                    int? error = ApoSetup.BridgeError(selected.Id);
                    if (error.HasValue) SetStatus("Не удалось получить звук. Код 0x" + error.Value.ToString("X8") + ".", "error");
                    else SetStatus("Windows пока не передаёт звук. Включи «Слышать себя» для проверки.", "waiting");
                    waitingSince = now;
                }
            }
            bool isolationReady = route?.SampleRate > 0;
            if (peak != previousPeak || playbackLevel != previousPlaybackLevel || rate != previousRate || status != previousStatus ||
                isolationReady != lastIsolationReady || inputVolume != previousInputVolume || playbackVolume != previousPlaybackVolume)
                Write(new { level = peak, playbackLevel, rate, status, kind, connected = capture != null, isolationReady, inputVolume, playbackVolume });
            lastIsolationReady = isolationReady;
        }

        private async Task ReconnectInput(bool resume)
        {
            await Select();
            if (resume && capture != null) StartMonitor();
            Publish();
        }

        private object Snapshot() => new
        {
            inputs = inputs.Select(device => new { id = device.Id.ToString("D"), name = device.Name }).ToArray(),
            selected = selected?.Id.ToString("D"),
            outputs = outputs.Select(device => new { id = device.Id.ToString("D"), name = device.Name }).ToArray(),
            playback = new {
                selected = outputs.Any(device => device.Id == playbackId) ? playbackId?.ToString("D") : null,
                enabled = playbackId.HasValue && PlaybackSettings.Enabled,
                bands = playbackId.HasValue ? PlaybackSettings.Bands : flatBands,
                rate = playbackRate, configuring = playbackConfiguring, connected = playbackRoute != null,
                enhancementsDisabled = playbackEnhancementsDisabled, error = playbackError
            },
            effects = rows.Select(row => new
            {
                id = row.Id, name = row.DisplayName,
                enabled = row.State.Enabled && row.Effect != null,
                editor = row.Effect?.HasNativeEditor == true, error = row.Error
            }).ToArray(),
            level = peak, playbackLevel, rate, status, kind, connected = capture != null,
            inputVolume = capture?.Volume, playbackVolume = Endpoint.ReadVolume(playbackMeter),
            monitoring = capture?.IsListening == true, monitorError, inputRoutingError, enhancementsDisabled,
            isolationReady = route?.SampleRate > 0,
            isolation = IsolationStatus()
        };

        private void Reply(int id) => Write(new { id, state = Snapshot() });
        private void Publish() => Write(new { state = Snapshot() });

        private void Write(object message)
        {
            try { output.WriteLine(json.Serialize(message)); }
            catch (IOException) { dispatcher.BeginInvoke((Action)ExitThread); }
        }

        private void StopRoute()
        {
            ClearIsolationSetup(restoreMonitor: false);
            route?.Dispose();
            route = null;
            chain.StopAudio();
            preparedRate = 0;
            lastSignal = null;
            capture?.Dispose();
            capture = null;
            peak = 0;
            rate = 0;
        }

        private void StartMonitor()
        {
            if (capture == null)
                throw new InvalidOperationException("Сначала подключи микрофон.");

            try { capture.StartListening(); }
            catch (Exception error)
            {
                monitorError = "Не удалось включить прослушивание. " + error.Message;
            }
        }

        private void StopMonitor() => capture?.StopListening();

        protected override void ExitThreadCore()
        {
            if (closing) return;
            closing = true;
            timer.Stop();
            StopMonitor();
            StopRoute();
            StopPlayback();
            ClearPlaybackMeter();
            CloseEditors();
            foreach (var row in rows) row.Effect?.Dispose();
            isolationEffect?.Dispose();
            setup?.Dispose();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                output.Dispose();
                dispatcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
