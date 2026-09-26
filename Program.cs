using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Multiplayer;

namespace FruktServer
{
    static class Program
    {
        static readonly ConcurrentQueue<string> Commands = new ConcurrentQueue<string>();
        static volatile bool _stopping;

        static int Main(string[] args)
        {
            Settings settings;
            try
            {
                settings = Settings.Read(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                return 2;
            }

            Server server;
            try
            {
                server = new Server(settings);
            }
            catch (SocketException e)
            {
                Server.Log("Could not open UDP port " + settings.Port + ": " + e.Message);
                return 1;
            }

            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
            new Thread(ReadCommands) { IsBackground = true, Name = "console" }.Start();

            Server.Log("FruktServer " + typeof(Program).Assembly.GetName().Version.ToString(3) + ", protocol " + Wire.Protocol + ".");
            Server.Log(settings.Name + " is up on UDP port " + settings.Port + ", map " + settings.Map + ", up to " + settings.MaxPlayers + " players. Type help for commands.");

            using (server)
            {
                while (!_stopping)
                {
                    server.Tick(10);
                    while (Commands.TryDequeue(out string line))
                        Run(server, line);
                }
                Server.Log("Stopping" + (server.PlayerCount > 0 ? ", telling " + server.PlayerCount + " player(s) the game ended." : "."));
            }
            Server.Log("Stopped.");
            return 0;
        }

        static void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            _stopping = true;
        }

        static void ReadCommands()
        {
            try
            {
                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    if (line.Trim().Length > 0)
                        Commands.Enqueue(line.Trim());
                }
            }
            catch (Exception)
            {
            }
        }

        static void Run(Server server, string line)
        {
            string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string command = parts[0].ToLowerInvariant();
            string rest = parts.Length > 1 ? parts[1].Trim() : "";
            switch (command)
            {
                case "help":
                case "?":
                    Server.Log("Commands: status, players, say <message>, kick <id or name>, stop.");
                    return;
                case "say":
                    if (rest.Length == 0)
                        Server.Log("Usage: say <message>. Players see it in chat as the server's name.");
                    else
                        server.Say(rest);
                    return;
                case "status":
                    server.Status();
                    return;
                case "players":
                case "list":
                    server.Players();
                    return;
                case "kick":
                    if (rest.Length == 0)
                        Server.Log("Usage: kick <id or name>. The ids are in the players list.");
                    else if (!server.Kick(rest))
                        Server.Log("Nobody called or numbered \"" + rest + "\" is connected.");
                    return;
                case "stop":
                case "quit":
                case "exit":
                    _stopping = true;
                    return;
                default:
                    Server.Log("Unknown command \"" + command + "\". Type help for commands.");
                    return;
            }
        }
    }
}
