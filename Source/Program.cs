using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

namespace MicEffects
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += LoadAudioLibrary;
            if (args.Length > 0)
                return args[0] == "--host" ? RunHost(args)
                    : args[0] == "--prepare-update" || args[0] == "--install-update" ? PortableUpdate.Run(args)
                    : args[0] == "--setup-session" ? SetupSession.Run(args)
                    : args[0] == "--attach" || args[0] == "--detach" ? ApoSetup.Run(args) : 2;
            return 2;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunHost(string[] args)
        {
            if (args.Length != 2 || !long.TryParse(args[1], out long owner)) return 2;
            using (var instance = new Mutex(true, @"Local\MicEffects.19646439", out bool first))
            {
                if (!first) return 8;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var host = new AudioHost(new IntPtr(owner)))
                    Application.Run(host);
                return 0;
            }
        }

        private static Assembly LoadAudioLibrary(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            if (name != "NAudio.Core" && name != "NAudio.Wasapi")
                return null;

            using (Stream resource = typeof(Program).Assembly.GetManifestResourceStream(name + ".dll"))
            using (var bytes = new MemoryStream())
            {
                resource.CopyTo(bytes);
                return Assembly.Load(bytes.ToArray());
            }
        }
    }
}
