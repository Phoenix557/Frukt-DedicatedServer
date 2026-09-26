using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Multiplayer;

namespace FruktServer
{
    /// <summary>
    /// Plays host id 0 for the mod without running the game: it passes packets between players and names one of them,
    /// the keeper, whose screen runs the shared world (map props, NPCs, things whose owner left).
    /// </summary>
    sealed class Server : IDisposable
    {
        const double Timeout = 5.0;
        const double StateEvery = 0.1;
        const double AskAvatarEvery = 2.0;
        const float ChatBurst = 4f;
        const double CleanupEvery = 10.0;

        sealed class Client
        {
            public byte Id;
            public IPEndPoint EndPoint;
            public string Key;
            public double HeardAt;
            public double JoinedAt;
            public PeerState State;
            public AvatarImage Picture;
            public double AskedAt = -100.0;
            public float ChatTokens = ChatBurst;
            public double ChatAt;
            public double SweptAt = -100.0;
            public string Name => State != null && State.Name.Length > 0 ? State.Name : "Player " + Id;
        }

        readonly Settings _settings;
        readonly Socket _socket;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Dictionary<string, Client> _clients = new Dictionary<string, Client>();
        readonly HashSet<int> _killsLogged = new HashSet<int>();
        readonly Dictionary<string, double> _refusedAt = new Dictionary<string, double>();
        readonly uint _serverId = (uint)new Random().Next() ^ (uint)Environment.TickCount;
        readonly byte[] _buffer = new byte[65536];
        byte _keeper;
        double _lastState = -100.0;
        double _lastCleanup;
        long _bytesIn, _bytesOut;

        public double Now => _clock.Elapsed.TotalSeconds;
        public int PlayerCount => _clients.Count;

        public Server(Settings settings)
        {
            _settings = settings;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows())
            {
                const int SioUdpConnReset = -1744830452;
                _socket.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
            }
            _socket.Bind(new IPEndPoint(IPAddress.Any, settings.Port));
        }

        /// <summary>
        /// Handles everything that arrived, drops silent players and sends the server's own state. Waits up to waitMs for packets.
        /// </summary>
        public void Tick(int waitMs)
        {
            if (_socket.Poll(waitMs * 1000, SelectMode.SelectRead))
                Drain();
            double now = Now;

            var silent = new List<Client>();
            foreach (Client client in _clients.Values)
            {
                if (now - client.HeardAt > Timeout)
                    silent.Add(client);
            }
            foreach (Client client in silent)
                Drop(client, " timed out");

            ChooseKeeper();
            AskForPictures(now);
            if (now - _lastState >= StateEvery)
                SendOwnState();
            if (_settings.BodyCleanup > 0 && now - _lastCleanup >= CleanupEvery)
            {
                _lastCleanup = now;
                SendSweep(Sweep.Bodies, (ushort)_settings.BodyCleanup);
            }
        }

        /// <summary>
        /// Asks every game in a map to remove its NPCs' bodies (dead at least deadFor seconds), or every NPC. Each game removes the
        /// ones it simulates, so they go from every screen together; players' own bodies are never touched.
        /// </summary>
        void SendSweep(Sweep what, ushort deadFor)
        {
            byte[] data = Wire.WriteSweep(new SweepOrder { From = 0, What = what, DeadFor = deadFor });
            foreach (Client client in _clients.Values)
            {
                if (InGame(client))
                    Send(client.EndPoint, data);
            }
        }

        public void ClearBodies()
        {
            SendSweep(Sweep.Bodies, 0);
            Log(InGameCount() == 0 ? "Nobody is in a map, so there are no bodies to clear." : "Cleared the dead bodies.");
        }

        public void ClearNpcs()
        {
            SendSweep(Sweep.Everyone, 0);
            Log(InGameCount() == 0 ? "Nobody is in a map, so there are no NPCs to delete." : "Deleted every NPC.");
        }

        /// <summary>
        /// Logs a player's clean-up. False when they already sent one this second, so it is not passed on.
        /// </summary>
        bool Swept(Client client, SweepOrder order, double now)
        {
            if (now - client.SweptAt < 1.0)
                return false;
            client.SweptAt = now;
            if (order.DeadFor == 0)
                Log(client.Name + (order.What == Sweep.Everyone ? " deleted every NPC." : " cleared the dead bodies."));
            return true;
        }

        int InGameCount()
        {
            int n = 0;
            foreach (Client client in _clients.Values)
            {
                if (InGame(client))
                    n++;
            }
            return n;
        }

        /// <summary>
        /// Collects each player's picture from their own game, so it can hand it to everyone else.
        /// </summary>
        void AskForPictures(double now)
        {
            foreach (Client client in _clients.Values)
            {
                uint hash = client.State == null ? 0 : client.State.Avatar;
                if (hash == 0 || client.Picture != null && client.Picture.Hash == hash && client.Picture.Complete)
                    continue;
                if (now - client.AskedAt < AskAvatarEvery)
                    continue;
                client.AskedAt = now;
                Send(client.EndPoint, Wire.WriteAvatarAsk(client.Id, hash));
            }
        }

        /// <summary>
        /// Passes a chat line to everyone with the sender's name on it, a few lines a second at most per player.
        /// </summary>
        void Say(Client client, string text, double now)
        {
            text = Wire.CleanChat(text);
            if (text.Length == 0)
                return;
            string name = _settings.Name;
            byte from = 0;
            if (client != null)
            {
                client.ChatTokens = Math.Min(ChatBurst, client.ChatTokens + (float)(now - client.ChatAt));
                client.ChatAt = now;
                if (client.ChatTokens < 1f)
                    return;
                client.ChatTokens -= 1f;
                name = client.Name;
                from = client.Id;
            }
            Log("[Chat] " + name + ": " + text);
            byte[] data = Wire.WriteChat(new ChatLine { From = from, Name = name, Text = text });
            foreach (Client other in _clients.Values)
                Send(other.EndPoint, data);
        }

        /// <summary>
        /// Chat from the console, shown as the server's name.
        /// </summary>
        public void Say(string text)
        {
            if (_clients.Count == 0)
            {
                Log("Nobody is connected to hear that.");
                return;
            }
            Say(null, text, Now);
        }

        void Drain()
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (_socket.Available > 0)
            {
                int length;
                try
                {
                    length = _socket.ReceiveFrom(_buffer, ref from);
                }
                catch (SocketException)
                {
                    continue;
                }
                if (length <= 0)
                    continue;
                _bytesIn += length;
                var data = new byte[length];
                Buffer.BlockCopy(_buffer, 0, data, 0, length);
                try
                {
                    Handle(data, (IPEndPoint)from);
                }
                catch (Exception e) when (e is EndOfStreamException || e is IOException || e is ArgumentException || e is InvalidDataException)
                {
                }
            }
        }

        void Handle(byte[] data, IPEndPoint from)
        {
            double now = Now;
            Kind kind = (Kind)data[0];
            string key = from.ToString();
            _clients.TryGetValue(key, out Client client);

            if (kind == Kind.Query)
            {
                if (data.Length >= Wire.QuerySize)
                    Send(from, Wire.WriteInfo(BitConverter.ToUInt32(data, 1), _serverId, _settings.Name, _settings.Map, _clients.Count, _settings.MaxPlayers, true));
                return;
            }
            if (kind == Kind.Hello)
            {
                Hello(data, from, key, client, now);
                return;
            }
            if (client == null)
                return;
            client.HeardAt = now;

            if (Wire.IsWorld(kind))
            {
                if (data.Length < 2 || !InGame(client))
                    return;
                data[1] = client.Id;
                if (kind == Kind.WorldSweep && !Swept(client, Wire.ReadSweep(data), now))
                    return;
                SendToScene(client, data);
                return;
            }

            var reader = new BinaryReader(new MemoryStream(data, 1, data.Length - 1));
            switch (kind)
            {
                case Kind.State:
                    PeerState state = Wire.ReadState(reader);
                    state.Id = client.Id;
                    state.Hidden = false;
                    state.Relay = false;
                    state.Keeper = 0;
                    PeerState before = client.State;
                    client.State = state;
                    if (before == null)
                        Log(state.Name + " joined from " + from.Address + ", " + Where(state.Scene) + ". " + Count());
                    else
                    {
                        if (before.Name != state.Name)
                            Log(before.Name + " is now called " + state.Name + ".");
                        if (before.Scene != state.Scene)
                            Log(state.Name + (state.Scene.Length == 0 ? " went back to the menu." : " loaded " + state.Scene + "."));
                    }
                    SendToAllBut(client, Wire.WriteState(state));
                    return;

                case Kind.Leave:
                    Drop(client, " left");
                    return;

                case Kind.Kill:
                    KillNotice kill = Wire.ReadKill(reader);
                    Client victim = ById(kill.Victim);
                    if (victim == null)
                        return;
                    if (_killsLogged.Add(kill.Victim * 256 + kill.Life))
                        Log(victim.Name + " was killed by " + kill.Killer + ".");
                    Send(victim.EndPoint, Wire.WriteKill(kill));
                    return;

                case Kind.Shot:
                    ShotNotice shot = Wire.ReadShot(reader);
                    shot.Shooter = client.Id;
                    SendToAllBut(client, Wire.WriteShot(shot));
                    return;

                case Kind.Chat:
                    Say(client, Wire.ReadChat(reader).Text, now);
                    return;

                case Kind.AvatarAsk:
                    Wire.ReadAvatarAsk(reader, out byte owner, out uint hash);
                    Client pictured = ById(owner);
                    AvatarImage picture = pictured?.Picture;
                    if (picture == null || picture.Hash != hash || !picture.Complete)
                        return;
                    for (int i = 0; i < picture.Parts.Length; i++)
                        Send(from, Wire.WriteAvatarPart(owner, picture, i));
                    return;

                case Kind.AvatarPart:
                    AvatarPart part = Wire.ReadAvatarPart(reader);
                    if (part.Owner != client.Id || client.State == null || client.State.Avatar != part.Hash)
                        return;
                    if (client.Picture == null || client.Picture.Hash != part.Hash)
                        client.Picture = new AvatarImage { Hash = part.Hash };
                    client.Picture.Add(part);
                    return;
            }
        }

        void Hello(byte[] data, IPEndPoint from, string key, Client client, double now)
        {
            if (data.Length < 3)
                return;
            ushort theirs = BitConverter.ToUInt16(data, 1);
            if (theirs != Wire.Protocol)
            {
                Send(from, Wire.Write(Kind.Mismatch, w => w.Write(Wire.Protocol)));
                Refused(key, now, "Someone from " + from.Address + " could not join: their Multiplayer mod is "
                    + (theirs < Wire.Protocol ? "older" : "newer") + " than the server's (protocol " + theirs + ", server " + Wire.Protocol + ").");
                return;
            }
            if (client == null)
            {
                byte id = FreeId();
                if (id == 0)
                {
                    Send(from, Wire.Write(Kind.Full, _ => { }));
                    Refused(key, now, "Someone from " + from.Address + " could not join: the server is full. " + Count());
                    return;
                }
                client = new Client { Id = id, EndPoint = from, Key = key, HeardAt = now, JoinedAt = now };
                _clients[key] = client;
            }
            client.HeardAt = now;
            byte welcomeId = client.Id;
            Send(from, Wire.Write(Kind.Welcome, w => w.Write(welcomeId)));
            SendOwnState();
            foreach (Client other in _clients.Values)
            {
                if (other != client && other.State != null)
                    Send(from, Wire.WriteState(other.State));
            }
        }

        /// <summary>
        /// Keeps the keeper while they are still in a game (on the server's map, if anyone is); otherwise the longest-connected player takes over.
        /// </summary>
        void ChooseKeeper()
        {
            bool anyOnMap = false;
            foreach (Client client in _clients.Values)
                anyOnMap |= OnMap(client);

            Client current = ById(_keeper);
            if (current != null && InGame(current) && (OnMap(current) || !anyOnMap))
                return;

            Client best = null;
            foreach (Client client in _clients.Values)
            {
                if (!InGame(client) || anyOnMap && !OnMap(client))
                    continue;
                if (best == null || client.JoinedAt < best.JoinedAt)
                    best = client;
            }
            byte next = best == null ? (byte)0 : best.Id;
            if (next == _keeper)
                return;
            _keeper = next;
            if (best != null)
                Log(best.Name + "'s game now runs the shared world (props and NPCs).");
            else if (_clients.Count > 0)
                Log("Nobody is in a map, so nobody runs the shared world for now.");
            SendOwnState();
        }

        bool InGame(Client client) => client.State != null && client.State.Scene.Length > 0;

        bool OnMap(Client client) => InGame(client) && client.State.Scene.IndexOf(_settings.Map, StringComparison.OrdinalIgnoreCase) >= 0;

        void SendOwnState()
        {
            _lastState = Now;
            if (_clients.Count == 0)
                return;
            byte[] data = Wire.WriteState(new PeerState
            {
                Id = 0,
                Name = _settings.Name,
                Scene = _settings.Map,
                Hidden = true,
                Relay = true,
                Keeper = _keeper
            });
            foreach (Client client in _clients.Values)
                Send(client.EndPoint, data);
        }

        public void Status()
        {
            Log(_settings.Name + " on UDP port " + _settings.Port + ", map " + _settings.Map + ", " + Count() + ", up " + Uptime()
                + ", " + (_bytesIn / 1024) + " KB in / " + (_bytesOut / 1024) + " KB out.");
            Players();
        }

        public void Players()
        {
            if (_clients.Count == 0)
            {
                Log("Nobody is connected.");
                return;
            }
            var sorted = new List<Client>(_clients.Values);
            sorted.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (Client client in sorted)
            {
                string where = client.State == null ? "connecting" : Where(client.State.Scene);
                string keeper = client.Id == _keeper ? ", runs the shared world" : "";
                string picture = client.Picture != null && client.Picture.Complete ? ", has a picture" : "";
                Log("  " + client.Id + "  " + client.Name + "  " + where + ", " + client.EndPoint.Address + ", " + Minutes(Now - client.JoinedAt) + picture + keeper);
            }
        }

        /// <summary>
        /// Removes a player by id or name. False when nobody matches.
        /// </summary>
        public bool Kick(string who)
        {
            Client target = null;
            if (byte.TryParse(who, out byte id))
                target = ById(id);
            if (target == null)
            {
                foreach (Client client in _clients.Values)
                {
                    if (string.Equals(client.Name, who, StringComparison.OrdinalIgnoreCase))
                        target = client;
                }
            }
            if (target == null)
                return false;
            byte gone = target.Id;
            Send(target.EndPoint, Wire.Write(Kind.Leave, w => w.Write(gone)));
            Drop(target, " was removed");
            return true;
        }

        /// <summary>
        /// Tells every player the game ended.
        /// </summary>
        public void Dispose()
        {
            byte[] leave = Wire.Write(Kind.Leave, w => w.Write((byte)0));
            foreach (Client client in _clients.Values)
            {
                Send(client.EndPoint, leave);
                Send(client.EndPoint, leave);
            }
            _clients.Clear();
            _socket.Dispose();
        }

        void Drop(Client client, string why)
        {
            if (!_clients.Remove(client.Key))
                return;
            _killsLogged.RemoveWhere(k => k / 256 == client.Id);
            Log(client.Name + why + ". " + Count());
            byte[] leave = Wire.Write(Kind.Leave, w => w.Write(client.Id));
            foreach (Client other in _clients.Values)
                Send(other.EndPoint, leave);
            ChooseKeeper();
        }

        Client ById(byte id)
        {
            if (id == 0)
                return null;
            foreach (Client client in _clients.Values)
            {
                if (client.Id == id)
                    return client;
            }
            return null;
        }

        byte FreeId()
        {
            for (int id = 1; id <= _settings.MaxPlayers; id++)
            {
                if (ById((byte)id) == null)
                    return (byte)id;
            }
            return 0;
        }

        /// <summary>
        /// Logs why someone could not join, once in a while per address: their game keeps asking every second.
        /// </summary>
        void Refused(string key, double now, string message)
        {
            if (_refusedAt.TryGetValue(key, out double at) && now - at < 30.0)
                return;
            if (_refusedAt.Count > 256)
                _refusedAt.Clear();
            _refusedAt[key] = now;
            Log(message);
        }

        /// <summary>
        /// World packets only matter to players on the sender's map; everyone else would throw them away.
        /// </summary>
        void SendToScene(Client sender, byte[] data)
        {
            foreach (Client other in _clients.Values)
            {
                if (other != sender && other.State != null && other.State.Scene == sender.State.Scene)
                    Send(other.EndPoint, data);
            }
        }

        void SendToAllBut(Client sender, byte[] data)
        {
            foreach (Client other in _clients.Values)
            {
                if (other != sender)
                    Send(other.EndPoint, data);
            }
        }

        void Send(IPEndPoint to, byte[] data)
        {
            try
            {
                _socket.SendTo(data, to);
                _bytesOut += data.Length;
            }
            catch (SocketException)
            {
            }
        }

        string Count() => "(" + _clients.Count + "/" + _settings.MaxPlayers + " players)";

        string Uptime() => Minutes(Now);

        static string Minutes(double seconds)
        {
            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1 ? (int)span.TotalHours + "h " + span.Minutes + "m" : span.Minutes + "m " + span.Seconds + "s";
        }

        static string Where(string scene) => scene.Length == 0 ? "in the menu" : "on " + scene;

        public static void Log(string message)
        {
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
        }
    }
}
