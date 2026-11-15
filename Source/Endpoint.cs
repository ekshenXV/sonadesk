using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MicEffects
{
    internal sealed class Endpoint
    {
        internal Guid Id { get; }
        internal string Name { get; }
        internal string SystemId { get; }

        private Endpoint(Guid id, string name, string systemId)
        {
            Id = id;
            Name = name;
            SystemId = systemId;
        }

        public override string ToString() => Name;

        internal static int? ReadVolume(MMDevice device)
        {
            if (device == null) return null;
            try
            {
                float scalar = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                if (float.IsNaN(scalar)) return null;
                return (int)Math.Round(Math.Max(0, Math.Min(1, scalar)) * 100, MidpointRounding.AwayFromZero);
            }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (InvalidCastException) { return null; }
        }

        internal static void SetVolume(MMDevice device, int? value)
        {
            if (device == null || !value.HasValue || value < 0 || value > 100)
                throw new InvalidOperationException("volumeChangeFailed");
            try { device.AudioEndpointVolume.MasterVolumeLevelScalar = value.Value / 100f; }
            catch (Exception error) when (error is COMException || error is UnauthorizedAccessException || error is InvalidCastException)
            {
                throw new InvalidOperationException("volumeChangeFailed", error);
            }
        }

        internal static Endpoint[] List(DataFlow flow = DataFlow.Capture)
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                var result = new List<Endpoint>();
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    using (device)
                    {
                        Guid? id = ParseId(device.ID);
                        if (id.HasValue)
                            result.Add(new Endpoint(id.Value, device.FriendlyName, device.ID));
                    }
                }
                return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
        }

        private static string DefaultDeviceId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
            using (var device = enumerator.GetDefaultAudioEndpoint(flow, role))
                return device.ID;
        }

        internal static Guid? DefaultPlayback()
        {
            using (var enumerator = new MMDeviceEnumerator())
                return ParseId(DefaultDeviceId(enumerator, DataFlow.Render, Role.Multimedia));
        }

        internal static void SetDefault(Endpoint endpoint, DataFlow flow)
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                var roles = new[] { Role.Console, Role.Multimedia, Role.Communications };
                var previous = roles.Select(role => DefaultDeviceId(enumerator, flow, role)).ToArray();
                if (previous.All(id => string.Equals(id, endpoint.SystemId, StringComparison.OrdinalIgnoreCase))) return;
                var policy = (IAudioPolicy)new AudioPolicy();
                try
                {
                    for (int index = 0; index < roles.Length; index++)
                        if (!string.Equals(previous[index], endpoint.SystemId, StringComparison.OrdinalIgnoreCase))
                            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(endpoint.SystemId, roles[index]));
                }
                catch
                {
                    for (int index = roles.Length - 1; index >= 0; index--)
                    {
                        try
                        {
                            if (previous[index] != null && !string.Equals(previous[index], endpoint.SystemId, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(DefaultDeviceId(enumerator, flow, roles[index]), endpoint.SystemId, StringComparison.OrdinalIgnoreCase))
                                policy.SetDefaultEndpoint(previous[index], roles[index]);
                        }
                        catch (COMException) { }
                    }
                    throw;
                }
                finally { Marshal.ReleaseComObject(policy); }
            }
        }

        private static Guid? ParseId(string value)
        {
            int start = value?.LastIndexOf('{') ?? -1;
            return start >= 0 && Guid.TryParse(value.Substring(start), out Guid id) ? id : (Guid?)null;
        }

        [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        private class AudioPolicy { }

        // Windows audio policy COM interface. These slots precede SetDefaultEndpoint in its vtable.
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioPolicy
        {
            void GetMixFormat();
            void GetDeviceFormat();
            void ResetDeviceFormat();
            void SetDeviceFormat();
            void GetProcessingPeriod();
            void SetProcessingPeriod();
            void GetShareMode();
            void SetShareMode();
            void GetPropertyValue();
            void SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        }
    }
}
