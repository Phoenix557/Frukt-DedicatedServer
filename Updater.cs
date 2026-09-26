using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FruktServer
{
    /// <summary>
    /// With AUTO_UPDATE=1 (the egg's default), swaps this file for the newest GitHub release before the server starts,
    /// then restarts in place so the panel sees one process that never stopped.
    /// </summary>
    static class Updater
    {
        const string Latest = "https://api.github.com/repos/Phoenix557/Frukt-DedicatedServer/releases/latest";
        const string DoneKey = "FRUKT_UPDATED_TO";

        public static void Run(string[] args)
        {
            if (Environment.GetEnvironmentVariable("AUTO_UPDATE") != "1" || Environment.GetEnvironmentVariable(DoneKey) != null)
                return;
            string self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self))
                return;
            try
            {
                Update(self, args);
            }
            catch (Exception e)
            {
                Server.Log("Could not check for a newer FruktServer (" + e.GetBaseException().Message + "). Starting this one.");
            }
        }

        static void Update(string self, string[] args)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FruktServer");

            using JsonDocument release = JsonDocument.Parse(http.GetStringAsync(Latest).GetAwaiter().GetResult());
            string tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v'), out Version latest))
                return;
            Version current = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0);
            if (Normal(latest) <= Normal(current))
                return;

            string asset = OperatingSystem.IsWindows() ? "FruktServer.exe" : "FruktServer";
            string url = null;
            foreach (JsonElement file in release.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (file.GetProperty("name").GetString() == asset)
                    url = file.GetProperty("browser_download_url").GetString();
            }
            if (url == null)
                return;

            Server.Log("Downloading FruktServer " + tag + " (this is " + current.ToString(3) + ")...");
            byte[] data = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(self + ".new", data);
                Server.Log("Saved " + tag + " as " + Path.GetFileName(self) + ".new; replace this file with it to update.");
                return;
            }

            string next = self + ".new";
            File.WriteAllBytes(next, data);
            File.SetUnixFileMode(next, (UnixFileMode)0b111_101_101);
            File.Move(next, self, true);
            Server.Log("Updated to " + tag + ". Restarting into it.");
            Environment.SetEnvironmentVariable(DoneKey, tag);

            var argv = new string[args.Length + 2];
            argv[0] = self;
            Array.Copy(args, 0, argv, 1, args.Length);
            execv(self, argv);
            Server.Log("Could not restart into the new version (error " + Marshal.GetLastWin32Error() + "); it runs from the next start.");
        }

        static Version Normal(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        [DllImport("libc", SetLastError = true)]
        static extern int execv(string path, string[] argv);
    }
}
