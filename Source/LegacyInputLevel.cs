using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using NAudio.CoreAudioApi;

namespace MicEffects
{
    internal static class LegacyInputLevel
    {
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicEffects", "NoiseInputLevels");
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public sealed class Level
        {
            public float Master { get; set; }
            public float[] Channels { get; set; }
        }

        private static string DeviceId(Guid id) => "{0.0.1.00000000}." + id.ToString("B");
        private static string Journal(Guid id) => Path.Combine(Folder, id.ToString("N") + ".json");

        // Restore levels left behind if the old recording wizard was interrupted.
        internal static void RestoreIfInterrupted(Guid id)
        {
            try { Restore(id); }
            catch (COMException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Restore(Guid id)
        {
            string path = Journal(id);
            if (!File.Exists(path)) return;
            Level previous;
            try { previous = Json.Deserialize<Level>(File.ReadAllText(path)); }
            catch (ArgumentException) { File.Delete(path); return; }
            catch (InvalidOperationException) { File.Delete(path); return; }
            if (previous?.Channels == null) { File.Delete(path); return; }
            using (var devices = new MMDeviceEnumerator())
            using (var device = devices.GetDevice(DeviceId(id)))
            {
                var volume = device.AudioEndpointVolume;
                if (volume.MasterVolumeLevelScalar >= .9999f)
                {
                    volume.MasterVolumeLevel = previous.Master;
                    for (int index = 0; index < Math.Min(previous.Channels.Length, volume.Channels.Count); index++)
                        volume.Channels[index].VolumeLevel = previous.Channels[index];
                }
            }
            File.Delete(path);
        }
    }
}
