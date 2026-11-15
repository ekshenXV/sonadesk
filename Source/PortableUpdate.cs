using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MicEffects
{
    internal static class PortableUpdate
    {
        private const string Launcher = "MicEffects.exe";
        private const string Runtime = "app";
        private const string Prepared = "prepared";
        private const string Previous = "previous";
        private const string Archive = "MicEffects.zip";
        private const string ZipRoot = "Mic Effects/";
        private const long ExpandedLimit = 3L * 1024 * 1024 * 1024;
        private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();
        private static readonly Regex ReservedName = new Regex(@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase);

        internal static int Run(string[] args)
        {
            try
            {
                string stage = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
                if (!Regex.IsMatch(Path.GetFileName(stage), @"^\.mic-effects-update-[0-9a-f]{32}$") || IsLink(stage)) return 2;
                if (args[0] == "--prepare-update" && args.Length == 3)
                {
                    Prepare(stage, args[1], args[2]);
                    return 0;
                }
                if (args[0] == "--install-update" && args.Length == 2 && int.TryParse(args[1], out int pid) && pid > 0)
                    return Install(stage, pid);
                return 2;
            }
            catch (Exception) { return 1; }
        }

        private static void Prepare(string stage, string version, string digest)
        {
            string target = Path.Combine(stage, Prepared);
            if (Directory.Exists(target)) throw new IOException("Update was already unpacked.");
            using (var file = File.OpenRead(Path.Combine(stage, Archive)))
            using (var hash = SHA256.Create())
            {
                if (!string.Equals(BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""), digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update checksum mismatch.");
                file.Position = 0;
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read)) Unpack(zip, target);
            }
            ValidateLayout(target);
            string package = Path.Combine(target, Runtime, "resources", "app", "package.json");
            var contents = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(package));
            if (!contents.TryGetValue("version", out object value) || !string.Equals(value as string, version, StringComparison.Ordinal))
                throw new InvalidDataException("The release version and package version differ.");
        }

        private static void Unpack(ZipArchive zip, string target)
        {
            if (zip.Entries.Count > 2048) throw new InvalidDataException("Too many files in the update.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            byte[] buffer = new byte[128 * 1024];
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                if (!name.StartsWith(ZipRoot, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected ZIP layout.");
                string relative = name.Substring(ZipRoot.Length);
                if (relative != Launcher && !relative.StartsWith(Runtime + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("Unexpected file in the program directory.");
                foreach (string segment in relative.Split('/'))
                    if (segment.Length == 0 || segment.IndexOfAny(InvalidNameCharacters) >= 0 || segment.EndsWith(".") ||
                        segment.EndsWith(" ") || ReservedName.IsMatch(segment)) throw new InvalidDataException("Unsafe ZIP path.");
                if (!seen.Add(relative)) throw new InvalidDataException("Duplicate ZIP path.");
                if (entry.Length < 0 || entry.Length > ExpandedLimit - total) throw new InvalidDataException("Update is too large.");
                total += entry.Length;
                string file = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                using (var input = entry.Open())
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    long written = 0;
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        written += count;
                        if (written > entry.Length) throw new InvalidDataException("ZIP entry exceeds its declared size.");
                        output.Write(buffer, 0, count);
                    }
                    if (written != entry.Length) throw new InvalidDataException("Incomplete ZIP entry.");
                }
            }
        }

        private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static void ValidateLayout(string root)
        {
            string launcher = Path.Combine(root, Launcher);
            string runtime = Path.Combine(root, Runtime);
            if (!File.Exists(launcher) || !Directory.Exists(runtime) || IsLink(launcher) || IsLink(runtime) ||
                !File.Exists(Path.Combine(runtime, Launcher)) ||
                !File.Exists(Path.Combine(runtime, "resources", "MicEffects.Audio.exe")) ||
                !File.Exists(Path.Combine(runtime, "resources", "app", "main.cjs")))
                throw new InvalidDataException("Incomplete Mic Effects installation.");
        }

        private static int Install(string stage, int pid)
        {
            string root = Path.GetDirectoryName(stage);
            string prepared = Path.Combine(stage, Prepared);
            string previous = Path.Combine(stage, Previous);
            string activeRuntime = Path.Combine(root, Runtime);
            string newRuntime = Path.Combine(prepared, Runtime);
            string oldRuntime = Path.Combine(previous, Runtime);
            string activeLauncher = Path.Combine(root, Launcher);
            string oldLauncher = Path.Combine(previous, Launcher);
            bool movedPrevious = false, movedUpdate = false, replacedLauncher = false;

            try
            {
                using (var process = Process.GetProcessById(pid))
                    if (!process.WaitForExit(20000)) return 1;
            }
            catch (ArgumentException) { /* The application has already exited. */ }

            try
            {
                ValidateLayout(root);
                ValidateLayout(prepared);
                Directory.CreateDirectory(previous);
                Retry(() => Directory.Move(activeRuntime, oldRuntime));
                movedPrevious = true;
                Retry(() => Directory.Move(newRuntime, activeRuntime));
                movedUpdate = true;
                Retry(() => File.Replace(Path.Combine(prepared, Launcher), activeLauncher, oldLauncher));
                replacedLauncher = true;
                Relaunch(root, stage, "installed");
                return 0;
            }
            catch (Exception)
            {
                try
                {
                    if (replacedLauncher) Retry(() => File.Replace(oldLauncher, activeLauncher, null));
                    if (movedUpdate) Retry(() => Directory.Move(activeRuntime, newRuntime));
                    if (movedPrevious) Retry(() => Directory.Move(oldRuntime, activeRuntime));
                    Relaunch(root, stage, "restored");
                }
                catch (Exception)
                {
                    bool russian = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
                    MessageBox.Show((russian ? "Не удалось завершить обновление. Файлы восстановления сохранены в папке " :
                        "The update could not finish. Recovery files are in ") + stage, "Mic Effects", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return 1;
            }
        }

        private static void Relaunch(string root, string stage, string outcome)
        {
            File.WriteAllText(Path.Combine(stage, "result"), outcome);
            using (var process = Process.Start(new ProcessStartInfo(Path.Combine(root, Launcher), "--mic-effects-update=" + Path.GetFileName(stage))
            {
                WorkingDirectory = root, UseShellExecute = false
            }))
                if (process == null) throw new IOException("Could not restart Mic Effects.");
        }

        private static void Retry(Action action)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { action(); return; }
                catch (Exception error) when (attempt < 39 && (error is IOException || error is UnauthorizedAccessException))
                { Thread.Sleep(100); }
            }
        }
    }
}
