using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicEffects
{
    internal static class ApoSetup
    {
        private const string ApoClass = "{58E9874D-7075-4074-9A8B-44BD70839769}";
        private const string ApoInterface = "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}";
        private const string EndpointEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
        private const string DisableSystemEffects = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
        private const string CompositeEffects = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15";
        private const string Modes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";
        private const string DefaultMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
        private const string AudioDevices = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\";
        private const string Installations = @"SOFTWARE\MicEffects\Endpoints\";
        private const string AudioEngine = @"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\";
        private const int RegistrationVersion = 7;
        private const string LegacyEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1";
        private const string StreamEffect = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";
        private const string CompositeStreamEffects = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13";
        private const string StreamModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5";
        private static readonly (string Key, string Backup)[] CaptureValues =
        {
            (StreamEffect, "OriginalStream"), (CompositeStreamEffects, "OriginalStreamList"),
            (StreamModes, "OriginalStreamModes"), (LegacyEffect, "OriginalLegacy")
        };
        private enum StreamPlacement { Modern, Legacy }
        private const string RegistryClass = @"SOFTWARE\Classes\CLSID\" + ApoClass + @"\InprocServer32";
        private static readonly string InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mic Effects APO");

        internal static bool Ready(Guid id, bool playback = false)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return ReadyForOwner(id, identity.User, playback);
        }

        private static bool ReadyForOwner(Guid id, SecurityIdentifier owner, bool playback)
        {
            using (var record = Registry.LocalMachine.OpenSubKey(Installations + id.ToString("B")))
            using (var endpoint = Registry.LocalMachine.OpenSubKey(EffectsPath(id, playback)))
            {
                return Equals(record?.GetValue("RegistrationVersion"), RegistrationVersion) &&
                    Equals(record.GetValue("Owner"), owner.Value) &&
                    !Equals(record.GetValue("ReloadPending"), 1) && IsAttached(endpoint);
            }
        }

        internal static bool WasSelected(Guid id)
        {
            using (var identity = WindowsIdentity.GetCurrent())
            using (var record = Registry.LocalMachine.OpenSubKey(Installations + id.ToString("B")))
            using (var endpoint = Registry.LocalMachine.OpenSubKey(EffectsPath(id)))
                return Equals(record?.GetValue("Owner"), identity.User.Value) && IsAttached(endpoint);
        }

        internal static bool EnhancementsDisabled(Guid id, bool playback = false)
        {
            using (var effects = Registry.LocalMachine.OpenSubKey(EffectsPath(id, playback)))
                return Equals(effects?.GetValue(DisableSystemEffects), 1);
        }

        internal static bool UsesProtectedAudio()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio"))
                return !Equals(key?.GetValue("DisableProtectedAudioDG"), 1);
        }

        internal static int? BridgeError(Guid id)
        {
            try
            {
                using (var file = new FileStream(Path.Combine(InstallDirectory, id.ToString("N") + ".lock"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(file))
                {
                    if (file.Length < 5) return null;
                    file.Position = 1;
                    int error = reader.ReadInt32();
                    return error == 0 ? (int?)null : error;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static int Run(string[] args)
        {
            if (args.Length != 3 || !Guid.TryParse(args[1], out Guid id))
                return 2;
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                        return 3;
                    return Configure(id, new SecurityIdentifier(args[2]), args[0] == "--attach");
                }
            }
            catch (ArgumentException) { return 2; }
        }

        internal static int Configure(Guid id, SecurityIdentifier owner, bool install, bool enableEnhancements = false, bool playback = false)
        {
            try
            {
                // A previous UI can exit while its elevated worker is restoring the endpoint.
                using (var gate = new Mutex(false, @"Local\MicEffects.AudioSetup"))
                {
                    try { if (!gate.WaitOne(TimeSpan.FromSeconds(20))) return 1; }
                    catch (AbandonedMutexException) { }
                    try
                    {
                        if (install)
                        {
                            if (!HasActiveRegistryEndpoint(id, playback)) return 1;
                            if (EnhancementsDisabled(id, playback))
                            {
                                if (!enableEnhancements) return 7;
                                bool alreadyAttached = ReadyForOwner(id, owner, playback);
                                using (var effects = OpenEffectProperties(id, playback))
                                {
                                    using (var record = Registry.LocalMachine.OpenSubKey(Installations + id.ToString("B")))
                                        if (IsAttached(effects) && !Equals(record?.GetValue("Owner"), owner.Value))
                                            return 3;
                                    effects.SetValue(DisableSystemEffects, 0, RegistryValueKind.DWord);
                                }
                                if (alreadyAttached)
                                {
                                    try { ReloadAudio(); }
                                    catch
                                    {
                                        using (var effects = OpenEffectProperties(id, playback))
                                            effects.SetValue(DisableSystemEffects, 1, RegistryValueKind.DWord);
                                        return 5;
                                    }
                                    return 0;
                                }
                            }
                        }
                        return ConfigureEndpoint(id, owner, install, playback);
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
            catch (UnauthorizedAccessException) { return 3; }
            catch (IOException) { return 4; }
            catch { return 1; }
        }

        private static int ConfigureEndpoint(Guid id, SecurityIdentifier owner, bool install, bool playback)
        {
            bool changed = install ? Attach(id, owner, playback) : Detach(id, playback);
            if (!changed && !NeedsAudioReload(id))
                return 0;

            try { ReloadAudio(); }
            catch { return 5; }
            if (!install)
                return 0;

            bool accepted = false;
            try
            {
                accepted = playback ? ProbePlayback(id) : ProbeCapture(id);
                if (!accepted && !playback)
                {
                    foreach (var placement in new[] { StreamPlacement.Modern, StreamPlacement.Legacy })
                    {
                        if (!EnhancementsDisabled(id) && TryStreamEffect(id, placement) && ProbeCapture(id))
                        {
                            accepted = true;
                            break;
                        }
                    }
                }
                if (!accepted)
                    return 6;

                using (var record = Registry.LocalMachine.OpenSubKey(Installations + id.ToString("B"), true))
                    record.DeleteValue("ReloadPending", false);
                return 0;
            }
            finally
            {
                if (!accepted && Detach(id, playback))
                    ReloadAudio();
            }
        }

        internal static bool HasActiveRegistryEndpoint(Guid id, bool playback = false)
        {
            using (var device = Registry.LocalMachine.OpenSubKey(DevicePath(id, playback)))
                return device != null && ((int)device.GetValue("DeviceState", 0) & 1) != 0;
        }

        private static bool Attach(Guid id, SecurityIdentifier owner, bool playback)
        {
            string bridge = Path.Combine(InstallDirectory, id.ToString("N") + ".lock");
            using (var record = Registry.LocalMachine.OpenSubKey(Installations + id.ToString("B")))
            using (var effects = Registry.LocalMachine.OpenSubKey(EffectsPath(id, playback)))
            {
                if (IsAttached(effects) && !Equals(record?.GetValue("Owner"), owner.Value))
                    throw new UnauthorizedAccessException();
                if (IsAttached(effects) && Equals(record.GetValue("RegistrationVersion"), RegistrationVersion))
                    return false;
            }

            if (!playback) RestoreCaptureValues(id);
            string dll = InstallModule();
            if (!File.Exists(bridge))
                using (File.Create(bridge)) { }
            RejectReparsePoint(bridge);
            var file = new FileInfo(bridge);
            var acl = file.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
                FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize, AccessControlType.Allow));
            file.SetAccessControl(acl);

            Register(dll);
            using (var fx = OpenEffectProperties(id, playback))
            using (var record = Registry.LocalMachine.CreateSubKey(Installations + id.ToString("B")))
            {
                bool previous = IsAttached(fx);
                string[] original = ListValue(fx, CompositeEffects).Where(value => !SameApo(value)).ToArray();
                string existing = fx.GetValue(EndpointEffect) as string;
                if (SameApo(existing))
                    existing = null;
                string[] composite = original;
                if (!string.IsNullOrWhiteSpace(existing) &&
                    !composite.Contains(existing, StringComparer.OrdinalIgnoreCase))
                    composite = new[] { existing }.Concat(composite).ToArray();

                // Persist the original values before changing what the audio engine loads.
                record.SetValue("BridgePath", bridge);
                record.SetValue("Owner", owner.Value);
                var registryAcl = record.GetAccessControl();
                registryAcl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
                    RegistryRights.ReadKey, AccessControlType.Allow));
                record.SetAccessControl(registryAcl);
                if (!previous)
                    SaveValues(record, fx);

                var previousValues = new[] { EndpointEffect, CompositeEffects, Modes }.Select(name =>
                {
                    object value = fx.GetValue(name);
                    return (Name: name, Value: value, Kind: value == null ? RegistryValueKind.None : fx.GetValueKind(name));
                }).ToArray();
                try
                {
                    if (fx.GetValue(Modes) == null)
                        fx.SetValue(Modes, new[] { DefaultMode }, RegistryValueKind.MultiString);
                    if (composite.Length == 0)
                    {
                        fx.SetValue(EndpointEffect, ApoClass, RegistryValueKind.String);
                        fx.DeleteValue(CompositeEffects, false);
                    }
                    else
                    {
                        fx.SetValue(CompositeEffects, composite.Concat(new[] { ApoClass }).ToArray(), RegistryValueKind.MultiString);
                    }
                    record.SetValue("RegistrationVersion", RegistrationVersion);
                    record.SetValue("ReloadPending", 1);
                }
                catch
                {
                    foreach (var value in previousValues)
                    {
                        if (value.Value == null)
                            fx.DeleteValue(value.Name, false);
                        else
                            fx.SetValue(value.Name, value.Value, value.Kind);
                    }
                    throw;
                }
                return true;
            }
        }

        private static bool Detach(Guid id, bool playback)
        {
            bool restoredStream = !playback && RestoreCaptureValues(id);
            using (var fx = OpenEffectProperties(id, playback))
            using (var record = Registry.LocalMachine.OpenSubKey(Installations + id.ToString("B"), true))
            {
                string[] current = ListValue(fx, CompositeEffects);
                bool restoreEndpoint = SameApo(fx.GetValue(EndpointEffect) as string) || current.Any(SameApo);
                record?.DeleteValue("RegistrationVersion", false);
                record?.DeleteValue("ReloadPending", false);
                if (!restoreEndpoint)
                    return restoredStream;

                string[] remaining = current.Where(value => !SameApo(value)).ToArray();
                string existing = fx.GetValue(EndpointEffect) as string;
                if (SameApo(existing))
                {
                    fx.DeleteValue(EndpointEffect, false);
                    existing = null;
                }
                var original = new HashSet<string>(ListValue(record, "OriginalComposite"), StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(existing))
                    original.Add(existing);
                if (record != null && original.SetEquals(remaining))
                {
                    RestoreValue(record, fx, CompositeEffects);
                    if (ListValue(fx, Modes).SequenceEqual(new[] { DefaultMode }, StringComparer.OrdinalIgnoreCase))
                        RestoreValue(record, fx, Modes);
                }
                else
                {
                    if (remaining.Length == 0)
                        fx.DeleteValue(CompositeEffects, false);
                    else
                        fx.SetValue(CompositeEffects, remaining, RegistryValueKind.MultiString);
                }
                return true;
            }
        }

        private static bool SameApo(string value) => string.Equals(value, ApoClass, StringComparison.OrdinalIgnoreCase);

        private static bool IsAttached(RegistryKey fx) =>
            SameApo(fx?.GetValue(EndpointEffect) as string) || ListValue(fx, CompositeEffects).Any(SameApo) ||
            SameApo(fx?.GetValue(StreamEffect) as string) || ListValue(fx, CompositeStreamEffects).Any(SameApo) ||
            SameApo(fx?.GetValue(LegacyEffect) as string);

        private static bool ProbeCapture(Guid id)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var device = enumerator.GetDevice("{0.0.1.00000000}." + id.ToString("B")))
                using (var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 25))
                {
                    capture.StartRecording();
                    return WaitForBridge(id);
                }
            }
            catch (Exception) { }
            return false;
        }

        private static bool ProbePlayback(Guid id)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var device = enumerator.GetDevice("{0.0.0.00000000}." + id.ToString("B")))
                using (var client = device.AudioClient)
                using (var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 40))
                {
                    output.Init(new SilenceProvider(client.MixFormat));
                    output.Play();
                    return WaitForBridge(id);
                }
            }
            catch (Exception) { return false; }
        }

        private static bool WaitForBridge(Guid id)
        {
            for (int i = 0; i < 25; i++)
            {
                try
                {
                    using (var mapping = MemoryMappedFile.OpenExisting(AudioBridge.MappingName(id), MemoryMappedFileRights.Read))
                    using (var view = mapping.CreateViewAccessor(0, 64, MemoryMappedFileAccess.Read))
                        if (view.ReadInt32(0) == AudioBridge.ProtocolVersion && view.ReadInt32(AudioBridge.CapturedFramesOffset) != 0)
                            return true;
                }
                catch (FileNotFoundException) { }
                Thread.Sleep(40);
            }
            return false;
        }

        private static bool TryStreamEffect(Guid id, StreamPlacement placement)
        {
            using (var fx = OpenEffectProperties(id))
            using (var record = Registry.LocalMachine.OpenSubKey(Installations + id.ToString("B"), true))
            {
                bool backedUp = Equals(record.GetValue("StreamBackup"), 1);
                string existing = (backedUp ? record.GetValue("OriginalStream") : fx.GetValue(StreamEffect)) as string;
                string[] list = ListValue(backedUp ? record : fx, backedUp ? "OriginalStreamList" : CompositeStreamEffects);
                bool occupied = list.Length > 0 || !string.IsNullOrWhiteSpace(existing);
                if (placement == StreamPlacement.Legacy && (occupied || fx.GetValue(LegacyEffect) != null))
                    return false;

                if (!backedUp)
                {
                    foreach (var value in CaptureValues)
                        SaveValue(record, fx, value.Key, value.Backup);
                    record.SetValue("StreamBackup", 1);
                }

                // Use only one processing position. The stream alternative is used if endpoint
                // processing never gets opened by the installed capture driver.
                if (SameApo(fx.GetValue(EndpointEffect) as string))
                    fx.DeleteValue(EndpointEffect, false);
                if (ListValue(fx, CompositeEffects).Any(SameApo))
                    RestoreValue(record, fx, CompositeEffects, "OriginalComposite");
                if (fx.GetValue(Modes) != null && ListValue(fx, Modes).SequenceEqual(new[] { DefaultMode }))
                    RestoreValue(record, fx, Modes, "OriginalModes");
                if (fx.GetValue(StreamModes) == null)
                    fx.SetValue(StreamModes, new[] { DefaultMode }, RegistryValueKind.MultiString);

                if (placement == StreamPlacement.Legacy)
                {
                    fx.SetValue(LegacyEffect, ApoClass);
                    fx.DeleteValue(StreamEffect, false);
                    fx.DeleteValue(CompositeStreamEffects, false);
                }
                else if (!occupied)
                {
                    fx.SetValue(StreamEffect, ApoClass);
                    fx.DeleteValue(CompositeStreamEffects, false);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(existing) && !list.Contains(existing, StringComparer.OrdinalIgnoreCase))
                        list = new[] { existing }.Concat(list).ToArray();
                    fx.SetValue(CompositeStreamEffects, list.Concat(new[] { ApoClass }).ToArray(), RegistryValueKind.MultiString);
                }
            }
            ReloadAudio();
            return true;
        }

        private static bool RestoreCaptureValues(Guid id)
        {
            using (var record = Registry.LocalMachine.OpenSubKey(Installations + id.ToString("B"), true))
            {
                if (!Equals(record?.GetValue("StreamBackup"), 1))
                    return false;
                using (var fx = OpenEffectProperties(id))
                {
                    foreach (var value in CaptureValues)
                        RestoreValue(record, fx, value.Key, value.Backup);
                }
                record.DeleteValue("StreamBackup", false);
                return true;
            }
        }

        private static bool NeedsAudioReload(Guid id)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(Installations + id.ToString("B")))
                return Equals(key?.GetValue("ReloadPending"), 1);
        }

        private static void ReloadAudio()
        {
            var services = new List<ServiceController>();
            try
            {
                AddRunningServices(new ServiceController("Audiosrv"), services, includeStopped: true);
                var timeout = TimeSpan.FromSeconds(8);
                try
                {
                    foreach (var service in services)
                    {
                        service.Refresh();
                        if (service.Status == ServiceControllerStatus.StartPending)
                            service.WaitForStatus(ServiceControllerStatus.Running, timeout);
                        service.Refresh();
                        if (service.Status == ServiceControllerStatus.Running)
                            service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    }
                }
                finally
                {
                    bool failed = false;
                    for (int i = services.Count - 1; i >= 0; i--)
                    {
                        var service = services[i];
                        try
                        {
                            service.Refresh();
                            if (service.Status == ServiceControllerStatus.StopPending)
                                service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                            if (service.Status == ServiceControllerStatus.Stopped)
                                service.Start();
                            service.WaitForStatus(ServiceControllerStatus.Running, timeout);
                        }
                        catch { failed = true; }
                    }
                    if (failed) throw new InvalidOperationException("Windows Audio did not restart.");
                }
            }
            finally
            {
                foreach (var service in services)
                    service.Dispose();
            }
        }

        private static void AddRunningServices(ServiceController service, List<ServiceController> services, bool includeStopped = false)
        {
            if ((!includeStopped && service.Status == ServiceControllerStatus.Stopped) ||
                services.Any(existing => existing.ServiceName == service.ServiceName))
            {
                service.Dispose();
                return;
            }
            foreach (var dependent in service.DependentServices)
                AddRunningServices(dependent, services);
            services.Add(service);
        }

        private static string DevicePath(Guid id, bool playback) => AudioDevices + (playback ? @"Render\" : @"Capture\") + id.ToString("B");
        private static string EffectsPath(Guid id, bool playback = false) => DevicePath(id, playback) + @"\FxProperties";

        private static RegistryKey OpenEffectProperties(Guid id, bool playback = false)
        {
            string path = EffectsPath(id, playback);
            return Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadKey | RegistryRights.SetValue)
                ?? Registry.LocalMachine.CreateSubKey(path);
        }

        private static string[] ListValue(RegistryKey key, string name) => key?.GetValue(name) as string[] ?? Array.Empty<string>();

        private static void SaveValues(RegistryKey record, RegistryKey fx)
        {
            foreach (var entry in new[] { (Key: CompositeEffects, Backup: "OriginalComposite"), (Key: Modes, Backup: "OriginalModes") })
                SaveValue(record, fx, entry.Key, entry.Backup);
        }

        private static void SaveValue(RegistryKey record, RegistryKey fx, string name, string backup)
        {
            object value = fx.GetValue(name);
            if (value == null)
                record.DeleteValue(backup, false);
            else
                record.SetValue(backup, value, fx.GetValueKind(name));
        }

        private static void RestoreValue(RegistryKey record, RegistryKey fx, string name, string backup = null)
        {
            backup = backup ?? (name == CompositeEffects ? "OriginalComposite" : "OriginalModes");
            object value = record.GetValue(backup);
            if (value == null)
                fx.DeleteValue(name, false);
            else
                fx.SetValue(name, value, record.GetValueKind(backup));
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Папка модуля или файл перенаправлены.");
        }

        private static string InstallModule()
        {
            Directory.CreateDirectory(InstallDirectory);
            RejectReparsePoint(InstallDirectory);
            using (Stream module = typeof(ApoSetup).Assembly.GetManifestResourceStream("MicEffects.Apo.dll"))
            using (var sha = SHA256.Create())
            {
                string version = BitConverter.ToString(sha.ComputeHash(module)).Replace("-", "");
                string directory = Path.Combine(InstallDirectory, version.Substring(0, 16));
                Directory.CreateDirectory(directory);
                RejectReparsePoint(directory);
                string path = Path.Combine(directory, "MicEffects.Apo.dll");
                if (!File.Exists(path))
                {
                    string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        module.Position = 0;
                        using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            module.CopyTo(target);
                            target.Flush(flushToDisk: true);
                        }
                        File.Move(temporary, path);
                    }
                    finally { File.Delete(temporary); }
                }
                else
                {
                    RejectReparsePoint(path);
                    using (Stream target = File.OpenRead(path))
                        if (BitConverter.ToString(sha.ComputeHash(target)).Replace("-", "") != version)
                            throw new IOException("Системный модуль отличается от файла в программе.");
                }
                return path;
            }
        }

        private static void Register(string path)
        {
            using (var registration = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes\CLSID\" + ApoClass))
                registration.SetValue(string.Empty, "Mic Effects Capture");
            using (var server = Registry.LocalMachine.CreateSubKey(RegistryClass))
            {
                server.SetValue(string.Empty, path);
                server.SetValue("ThreadingModel", "Both");
            }
            using (var apo = Registry.LocalMachine.CreateSubKey(AudioEngine + ApoClass))
            {
                apo.SetValue("FriendlyName", "Mic Effects");
                apo.SetValue("Copyright", "Mic Effects");
                apo.SetValue("MajorVersion", 1);
                apo.SetValue("MinorVersion", 0);
                apo.SetValue("Flags", 15);
                apo.SetValue("MinInputConnections", 1);
                apo.SetValue("MaxInputConnections", 1);
                apo.SetValue("MinOutputConnections", 1);
                apo.SetValue("MaxOutputConnections", 1);
                apo.SetValue("MaxInstances", -1);
                apo.SetValue("NumAPOInterfaces", 1);
                apo.SetValue("APOInterface0", ApoInterface);
            }
        }
    }
}
