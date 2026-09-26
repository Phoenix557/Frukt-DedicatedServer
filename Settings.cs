using System;

namespace FruktServer
{
    /// <summary>
    /// Server settings, from the environment (a Pterodactyl egg sets these) and then the command line.
    /// </summary>
    sealed class Settings
    {
        public const int MostPlayers = 16;

        public string Name = "FRUKT Server";
        public int Port = 27777;
        public string Map = "Yard";
        public int MaxPlayers = Multiplayer.Wire.MaxPlayers;
        /// <summary>
        /// Seconds an NPC's body stays after it dies; 0 keeps bodies until someone clears them.
        /// </summary>
        public int BodyCleanup = 120;

        public static Settings Read(string[] args)
        {
            var s = new Settings();
            s.Apply("name", Environment.GetEnvironmentVariable("SERVER_NAME"));
            s.Apply("port", Environment.GetEnvironmentVariable("SERVER_PORT"));
            s.Apply("map", Environment.GetEnvironmentVariable("MAP"));
            s.Apply("max-players", Environment.GetEnvironmentVariable("MAX_PLAYERS"));
            s.Apply("body-cleanup", Environment.GetEnvironmentVariable("BODY_CLEANUP"));
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (!arg.StartsWith("--"))
                    continue;
                string key = arg.Substring(2).ToLowerInvariant();
                if (key == "help" || key == "h")
                {
                    Usage();
                    Environment.Exit(0);
                }
                if (i + 1 >= args.Length)
                    throw new ArgumentException(arg + " needs a value.");
                if (!s.Apply(key, args[++i]))
                    throw new ArgumentException("Unknown option " + arg + ". Try --help.");
            }
            return s;
        }

        bool Apply(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return key == "name" || key == "port" || key == "map" || key == "max-players" || key == "body-cleanup";
            value = value.Trim();
            switch (key)
            {
                case "name":
                    Name = Multiplayer.Wire.Clip(value, Multiplayer.Wire.MaxName);
                    return true;
                case "port":
                    if (!int.TryParse(value, out int port) || port <= 0 || port > 65535)
                        throw new ArgumentException("The port must be a number from 1 to 65535, not \"" + value + "\".");
                    Port = port;
                    return true;
                case "map":
                    Map = Multiplayer.Wire.Clip(value, Multiplayer.Wire.MaxScene);
                    return true;
                case "max-players":
                    if (!int.TryParse(value, out int max) || max < 1 || max > MostPlayers)
                        throw new ArgumentException("Max players must be a number from 1 to " + MostPlayers + ", not \"" + value + "\".");
                    MaxPlayers = max;
                    return true;
                case "body-cleanup":
                    if (!int.TryParse(value, out int seconds) || seconds < 0 || seconds > ushort.MaxValue)
                        throw new ArgumentException("Body cleanup must be a number of seconds from 0 (off) to " + ushort.MaxValue + ", not \"" + value + "\".");
                    BodyCleanup = seconds;
                    return true;
            }
            return false;
        }

        static void Usage()
        {
            Console.WriteLine("FruktServer: a dedicated server for the FRUKT Multiplayer mod. It runs no game; players' games share the world.");
            Console.WriteLine();
            Console.WriteLine("  --name <text>          name in the server list      (env SERVER_NAME, default \"FRUKT Server\")");
            Console.WriteLine("  --port <number>        UDP port                     (env SERVER_PORT, default 27777)");
            Console.WriteLine("  --map <name>           map players load, e.g. Yard  (env MAP, default Yard)");
            Console.WriteLine("  --max-players <1-" + MostPlayers + ">  player limit                 (env MAX_PLAYERS, default " + Multiplayer.Wire.MaxPlayers + ")");
            Console.WriteLine("  --body-cleanup <sec>   remove NPC bodies this long  (env BODY_CLEANUP, default 120, 0 = never)");
            Console.WriteLine("                         after they die");
        }
    }
}
