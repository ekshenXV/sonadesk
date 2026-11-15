using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace MicEffects
{
    public sealed class Preset
    {
        public string MicrophoneId { get; set; }
        public EffectSettings[] Effects { get; set; }
        public PlaybackPreset[] Playback { get; set; }
        public Dictionary<string, bool> VoiceIsolation { get; set; }

        // Retain the old fields so existing single-effect presets can be loaded.
        public string PluginPath { get; set; }
        public byte[] Chunk { get; set; }
        public float[] Parameters { get; set; }
        public int ProgramIndex { get; set; }

        internal EffectSettings[] LoadEffects()
        {
            if (Effects != null)
                return Effects.Where(effect => effect != null && !effect.LearnedNoiseProfile).ToArray();
            if (string.IsNullOrWhiteSpace(PluginPath))
                return Array.Empty<EffectSettings>();
            return new[]
            {
                new EffectSettings { PluginPath = PluginPath, Enabled = true, Chunk = Chunk,
                    Parameters = Parameters, ProgramIndex = ProgramIndex }
            };
        }

        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicEffects");
        private static readonly string PresetPath = Path.Combine(Folder, "preset.json");
        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(Preset), new DataContractJsonSerializerSettings
            {
                MaxItemsInObjectGraph = 32 * 1024 * 1024
            });

        internal static Preset Load()
        {
            if (!File.Exists(PresetPath))
                return new Preset();

            using (FileStream file = File.OpenRead(PresetPath))
                return (Preset)Serializer.ReadObject(file) ?? new Preset();
        }

        internal void Save() => Save(false);

        internal void Save(bool preservePrevious)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PresetPath));
            string temporary = PresetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew))
                {
                    Serializer.WriteObject(file, this);
                    file.Flush(flushToDisk: true);
                }

                if (File.Exists(PresetPath))
                {
                    string backup = preservePrevious ? PresetPath + ".invalid-" + Guid.NewGuid().ToString("N") : null;
                    File.Replace(temporary, PresetPath, backup);
                }
                else
                    File.Move(temporary, PresetPath);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    public sealed class EffectSettings
    {
        public string PluginPath { get; set; }
        public bool Enabled { get; set; }
        public byte[] Chunk { get; set; }
        public float[] Parameters { get; set; }
        public int ProgramIndex { get; set; }
        public string ClassId { get; set; }
        public byte[] ControllerState { get; set; }
        // Used to identify profiles saved by the retired noise wizard in older presets.
        public bool LearnedNoiseProfile { get; set; }
    }
}
