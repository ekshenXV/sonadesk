using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MicEffects
{
    internal sealed class SetupSession : IDisposable
    {
        private const string Prefix = "MicEffects.Setup.";
        private readonly NamedPipeServerStream pipe;
        private readonly int clientProcess;
        private readonly Task connection;

        private SetupSession(NamedPipeServerStream pipe, int clientProcess)
        {
            this.pipe = pipe;
            this.clientProcess = clientProcess;
            connection = Connect();
        }

        internal static SetupSession Start()
        {
            string name = Prefix + Guid.NewGuid().ToString("N");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            using (var identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, security);
            try
            {
                using (var self = Process.GetCurrentProcess())
                using (var helper = Process.Start(new ProcessStartInfo
                {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Arguments = "--setup-session " + name + " " + self.Id,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                    return new SetupSession(server, helper.Id);
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }

        private async Task Connect()
        {
            Task waiting = pipe.WaitForConnectionAsync();
            if (await Task.WhenAny(waiting, Task.Delay(8000)).ConfigureAwait(false) != waiting)
            {
                pipe.Dispose();
                throw new IOException("Windows не запустила помощник подключения.");
            }
            await waiting.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != clientProcess)
                throw new IOException("Не удалось проверить помощник подключения.");
        }

        // The UI serializes requests. The elevated process validates the requested endpoint direction.
        internal async Task<int> AttachAsync(Guid endpoint, bool enableEnhancements = false, bool playback = false)
        {
            await connection.ConfigureAwait(false);
            byte[] request = new byte[17];
            endpoint.ToByteArray().CopyTo(request, 0);
            request[16] = (byte)((enableEnhancements ? 1 : 0) | (playback ? 2 : 0));
            await pipe.WriteAsync(request, 0, request.Length).ConfigureAwait(false);
            byte[] result = new byte[1];
            if (await pipe.ReadAsync(result, 0, 1).ConfigureAwait(false) != 1)
                throw new IOException("Помощник подключения завершился. Перезапусти Mic Effects.");
            return result[0];
        }

        internal static int Run(string[] args)
        {
            if (args.Length != 3 || !args[1].StartsWith(Prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(args[1].Substring(Prefix.Length), "N", out _) ||
                !uint.TryParse(args[2], out uint expectedPid) || expectedPid == 0 || expectedPid > int.MaxValue)
                return 2;
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                        return 3;

                using (var client = new NamedPipeClientStream(".", args[1],
                    PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                    PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.None))
                {
                    client.Connect(7000);
                    if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid) || pid != expectedPid)
                        return 3;
                    SecurityIdentifier owner = CheckLauncher((int)pid);
                    byte[] endpoint = new byte[16];
                    while (ReadRequest(client, endpoint))
                    {
                        int operation = client.ReadByte();
                        if (operation < 0) break;
                        int result = operation <= 3
                            ? ApoSetup.Configure(new Guid(endpoint), owner, install: true, enableEnhancements: (operation & 1) != 0, playback: (operation & 2) != 0)
                            : 2;
                        client.WriteByte((byte)result);
                    }
                }
                return 0;
            }
            catch (IOException) { return 1; }
            catch (TimeoutException) { return 1; }
            catch (UnauthorizedAccessException) { return 3; }
            catch (ArgumentException) { return 3; }
            catch (InvalidOperationException) { return 3; }
            catch (Win32Exception) { return 3; }
        }

        private static bool ReadRequest(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int received = stream.Read(buffer, offset, buffer.Length - offset);
                if (received == 0)
                    return false;
                offset += received;
            }
            return true;
        }

        private static SecurityIdentifier CheckLauncher(int pid)
        {
            using (var launcher = Process.GetProcessById(pid))
            {
                if (!string.Equals(launcher.MainModule.FileName, Assembly.GetExecutingAssembly().Location,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException();
                if (!OpenProcessToken(launcher.Handle, 0x0008, out SafeAccessTokenHandle token))
                    throw new Win32Exception();
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    return identity.User;
            }
        }

        public void Dispose() => pipe.Dispose();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    }
}
