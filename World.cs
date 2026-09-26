using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Multiplayer;

namespace FruktServer
{
    /// <summary>
    /// The server's own world: it keeps track of every shared thing on the keeper's map, simulates the props and items players hand
    /// it with its own physics, and moves everything players and NPCs push around as obstacles in that physics.
    /// </summary>
    sealed partial class Server
    {
        const double PoseEvery = 0.1;
        const double KeepAliveEvery = 2.0;
        const double AnnounceEvery = 3.0;
        const double MapAskEvery = 2.0;
        const double MapRetryAfterFailure = 30.0;
        const double ShapeAskEvery = 5.0;
        const float FollowWithin = 0.1f;
        const float MoveEnough = 0.0005f;
        const float TurnEnough = 0.2f;
        const float FallenBelow = 50f;
        const int PacketBudget = 1150;
        const int ShapeAsksPerTick = 20;
        const uint MapBit = 0x80000000u;
        const byte Spawnable = 1, Human = 2, MapProp = 3;

        sealed class BodyPose
        {
            public Vector3 At;
            public Quaternion Turn;
        }

        sealed class Thing
        {
            public uint Id;
            public byte Kind;
            public string Prefab = "";
            public byte Owner;
            public ushort Epoch;
            public bool Known;
            public ThingShape Shape;
            public double ShapeAskedAt = -100.0;
            public readonly Dictionary<ushort, BodyPose> Poses = new Dictionary<ushort, BodyPose>();
            public SimBody[] Sim;
            public bool SimDynamic;
            public Vector3 SentAt3;
            public Quaternion SentTurn = Quaternion.Identity;
            public double SentAt = -100.0;
            public double AnnouncedAt = -100.0;
            public Vector3 Home;
            public Quaternion HomeTurn = Quaternion.Identity;
        }

        readonly Dictionary<uint, Thing> _things = new Dictionary<uint, Thing>();
        readonly Dictionary<int, Thing> _byHandle = new Dictionary<int, Thing>();
        readonly HashSet<uint> _gone = new HashSet<uint>();
        Physics _physics;
        string _simScene = "";
        string _simulating = "";
        double _simClock;
        double _simAt;
        double _lastPoses;
        uint _uploadHash;
        byte[][] _pieces;
        int _piecesHave;
        double _pieceAt;
        double _mapAskAt = -100.0;
        double _mapFailedAt = -100.0;
        string _toldAsk = "";
        bool _toldTakeOver;
        bool _toldFallen;

        static string MapFolder => Path.Combine(Directory.GetCurrentDirectory(), "maps");

        string CachePath(string scene)
        {
            var name = new System.Text.StringBuilder();
            foreach (char c in scene)
                name.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return Path.Combine(MapFolder, name + ".v" + Shapes.MapVersion + ".bin");
        }

        void TickWorld(double now)
        {
            Client keeper = ById(_keeper);
            string want = keeper != null && InGame(keeper) ? keeper.State.Scene : "";
            if (want != _simScene)
                ResetWorld(want, now);
            if (_simScene.Length == 0)
                return;
            if (_physics == null)
            {
                AskForMap(keeper, now);
                return;
            }

            _simClock += Math.Min(now - _simAt, 0.25);
            _simAt = now;
            while (_simClock >= Physics.Step)
            {
                DriveObstacles();
                _physics.Tick();
                _simClock -= Physics.Step;
            }
            BringBackFallen();
            if (now - _lastPoses >= PoseEvery)
            {
                _lastPoses = now;
                SendServerPoses(now);
            }
            AskForShapes(keeper, now);
        }

        void ResetWorld(string scene, double now)
        {
            foreach (Thing thing in _things.Values)
                RemoveSim(thing);
            _things.Clear();
            _byHandle.Clear();
            _gone.Clear();
            _physics?.Dispose();
            _physics = null;
            _pieces = null;
            _mapAskAt = -100.0;
            _simScene = scene;
            _simulating = "";
            if (scene.Length > 0)
            {
                MapShape cached = LoadCachedMap(scene);
                if (cached != null)
                    StartPhysics(cached, "from " + Path.GetFileName(CachePath(scene)));
            }
            SendOwnState();
        }

        MapShape LoadCachedMap(string scene)
        {
            string path = CachePath(scene);
            if (!File.Exists(path))
                return null;
            try
            {
                MapShape map = Shapes.UnpackMap(File.ReadAllBytes(path));
                return map.Scene == scene ? map : null;
            }
            catch (Exception e)
            {
                Log("The saved collision of " + scene + " is unusable (" + e.Message + "), so it will be fetched again.");
                return null;
            }
        }

        void StartPhysics(MapShape map, string source)
        {
            try
            {
                _physics = new Physics(map);
            }
            catch (Exception e)
            {
                Log("Could not build physics for " + map.Scene + ": " + e.Message);
                _mapFailedAt = Now;
                return;
            }
            _simAt = Now;
            _simClock = 0.0;
            _simulating = _simScene;
            Log("The server now runs the physics of " + _simScene + " (" + _physics.Solids + " solids, " + _physics.Triangles + " triangles, " + source + ").");
            SendOwnState();
        }

        // ---------------------------------------------------------------- the map's collision

        void AskForMap(Client keeper, double now)
        {
            if (keeper == null || !InGame(keeper) || keeper.State.Scene != _simScene || now - _mapFailedAt < MapRetryAfterFailure)
                return;
            if (now - _mapAskAt < MapAskEvery || (_pieces != null && now - _pieceAt < MapAskEvery))
                return;
            _mapAskAt = now;
            var missing = new List<ushort>();
            if (_pieces != null)
            {
                for (int i = 0; i < _pieces.Length && missing.Count < 400; i++)
                {
                    if (_pieces[i] == null)
                        missing.Add((ushort)i);
                }
            }
            else if (_toldAsk != _simScene)
            {
                _toldAsk = _simScene;
                Log("Asking " + keeper.Name + "'s game for the collision of " + _simScene + ".");
            }
            Send(keeper.EndPoint, Shapes.WriteMapAsk(_simScene, missing));
        }

        void TakeMapPiece(Client client, MapPiece piece, double now)
        {
            if (_physics != null || piece.Scene != _simScene || client.State == null || client.State.Scene != _simScene)
                return;
            if (_pieces == null || piece.Hash != _uploadHash || piece.Total != _pieces.Length)
            {
                _pieces = new byte[piece.Total][];
                _piecesHave = 0;
                _uploadHash = piece.Hash;
            }
            _pieceAt = now;
            if (_pieces[piece.Index] != null)
                return;
            _pieces[piece.Index] = piece.Data;
            _piecesHave++;
            if (_piecesHave < _pieces.Length)
                return;

            var whole = new MemoryStream();
            foreach (byte[] part in _pieces)
                whole.Write(part, 0, part.Length);
            byte[] blob = whole.ToArray();
            _pieces = null;
            try
            {
                if (Wire.Fingerprint(blob) != _uploadHash)
                    throw new InvalidDataException("the pieces do not add up");
                MapShape map = Shapes.UnpackMap(blob);
                if (map.Scene != _simScene)
                    throw new InvalidDataException("it is for " + map.Scene);
                Directory.CreateDirectory(MapFolder);
                File.WriteAllBytes(CachePath(_simScene), blob);
                StartPhysics(map, (blob.Length / 1024) + " KB from " + client.Name + "'s game");
            }
            catch (Exception e)
            {
                _mapFailedAt = now;
                _toldAsk = "";
                Log("The collision of " + _simScene + " from " + client.Name + "'s game is unusable (" + e.Message + "). Trying again in 30 seconds.");
            }
        }

        // ---------------------------------------------------------------- what players send

        Thing Get(uint id)
        {
            if (!_things.TryGetValue(id, out Thing thing))
            {
                thing = new Thing { Id = id, Kind = (id & MapBit) != 0 ? MapProp : (byte)0 };
                _things[id] = thing;
            }
            return thing;
        }

        bool Accept(Thing thing, byte from, ushort epoch)
        {
            if (thing.Known)
            {
                bool newer = (short)(epoch - thing.Epoch) > 0;
                bool same = epoch == thing.Epoch && (from == thing.Owner || from < thing.Owner);
                if (!newer && !same)
                    return false;
            }
            bool lost = thing.Known && thing.Owner == 0 && from != 0;
            thing.Known = true;
            thing.Owner = from;
            thing.Epoch = epoch;
            if (lost)
                RemoveSim(thing);
            return true;
        }

        /// <summary>
        /// Keeps track of the world from the packets it passes on. Only the keeper's map counts.
        /// </summary>
        void ObserveWorld(Client client, Kind kind, byte[] data)
        {
            if (_simScene.Length == 0 || client.State.Scene != _simScene)
                return;
            var r = new BinaryReader(new MemoryStream(data, 2, data.Length - 2));
            switch (kind)
            {
                case Kind.WorldSpawn:
                {
                    uint id = r.ReadUInt32();
                    byte thingKind = r.ReadByte();
                    ushort epoch = r.ReadUInt16();
                    string scene = r.ReadString();
                    string prefab = r.ReadString();
                    if (scene != _simScene || _gone.Contains(id))
                        return;
                    Thing thing = Get(id);
                    thing.Kind = thingKind;
                    thing.Prefab = Wire.Clip(prefab, Wire.MaxPrefab);
                    Accept(thing, client.Id, epoch);
                    return;
                }
                case Kind.WorldPoses:
                {
                    int count = r.ReadByte();
                    for (int c = 0; c < count; c++)
                    {
                        uint id = r.ReadUInt32();
                        ushort epoch = r.ReadUInt16();
                        int n = r.ReadByte();
                        Thing thing = _gone.Contains(id) ? null : Get(id);
                        bool take = thing != null && Accept(thing, client.Id, epoch);
                        for (int i = 0; i < n; i++)
                        {
                            ushort name = r.ReadUInt16();
                            var at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            var turn = new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f);
                            if (!take || float.IsNaN(at.X) || turn.LengthSquared() < 0.5f)
                                continue;
                            if (!thing.Poses.TryGetValue(name, out BodyPose pose))
                            {
                                pose = new BodyPose();
                                thing.Poses[name] = pose;
                            }
                            pose.At = at;
                            pose.Turn = Quaternion.Normalize(turn);
                        }
                        if (take)
                            EnsureSim(thing);
                    }
                    return;
                }
                case Kind.WorldGone:
                {
                    uint id = r.ReadUInt32();
                    _gone.Add(id);
                    if (_things.TryGetValue(id, out Thing thing))
                    {
                        RemoveSim(thing);
                        _things.Remove(id);
                    }
                    return;
                }
            }
        }

        void TakeShape(Client client, ThingShape shape)
        {
            if (_simScene.Length == 0 || client.State == null || client.State.Scene != _simScene || _gone.Contains(shape.Id))
                return;
            Thing thing = Get(shape.Id);
            thing.Shape = shape;
            RemoveSim(thing);
            EnsureSim(thing);
        }

        void TakeGift(Client client, uint id, ushort epoch)
        {
            if (!_things.TryGetValue(id, out Thing thing) || thing.Owner != client.Id || thing.Epoch != epoch)
                return;
            if (thing.Shape == null)
            {
                AskForShape(thing, client, Now);
                return;
            }
            TakeOver(thing);
        }

        /// <summary>
        /// When a player leaves, the server simulates what it can of theirs; the keeper adopts the rest.
        /// </summary>
        void OwnerLeft(Client client)
        {
            foreach (Thing thing in _things.Values)
            {
                if (thing.Owner == client.Id && !TakeOver(thing))
                    RemoveSim(thing);
            }
        }

        /// <summary>
        /// A bullet from a player's gun knocks what the server simulates.
        /// </summary>
        void ShotHit(Client client, ShotNotice shot)
        {
            if (_physics == null || client.State.Scene != _simScene)
                return;
            var from = new Vector3(shot.Ox, shot.Oy, shot.Oz);
            var direction = new Vector3(shot.Dx, shot.Dy, shot.Dz);
            if (direction.LengthSquared() < 1e-6f || !_physics.Ray(from, direction, 300f, out BepuPhysics.BodyHandle hit, out Vector3 point))
                return;
            if (!_byHandle.TryGetValue(hit.Value, out Thing thing) || thing.Owner != 0 || !thing.SimDynamic)
                return;
            SimBody body = thing.Sim[0];
            _physics.Push(body, Vector3.Normalize(direction) * Math.Clamp(body.Mass * 3f, 1f, 40f), point);
        }

        // ---------------------------------------------------------------- simulating

        bool CanSimulate(Thing thing)
        {
            if (_physics == null || thing.Shape == null || thing.Kind == Human || thing.Kind == 0 && (thing.Id & MapBit) == 0)
                return false;
            if (thing.Shape.Bodies.Count != 1)
                return false;
            BodyDesc body = thing.Shape.Bodies[0];
            return !body.Kinematic && body.Mass > 0f && body.Shapes.Count > 0 && thing.Poses.ContainsKey(body.Name);
        }

        /// <summary>
        /// The server becomes the owner and simulates the thing from where it rests.
        /// </summary>
        bool TakeOver(Thing thing)
        {
            if (!CanSimulate(thing))
                return false;
            RemoveSim(thing);
            thing.Owner = 0;
            thing.Epoch++;
            thing.SentAt = -100.0;
            thing.AnnouncedAt = -100.0;
            BodyPose pose = thing.Poses[thing.Shape.Bodies[0].Name];
            thing.Home = pose.At;
            thing.HomeTurn = pose.Turn;
            EnsureSim(thing);
            if (thing.Sim == null)
                return false;
            if (!_toldTakeOver)
            {
                _toldTakeOver = true;
                Log("The server took over its first " + (thing.Kind == MapProp ? "prop" : "item") + (thing.Prefab.Length > 0 ? " (" + thing.Prefab + ")" : "") + ".");
            }
            return true;
        }

        /// <summary>
        /// Gives a thing its bodies in the server's physics: simulated when the server owns it, otherwise obstacles that follow the
        /// owner's poses.
        /// </summary>
        void EnsureSim(Thing thing)
        {
            if (_physics == null || thing.Shape == null)
                return;
            bool dynamic = thing.Owner == 0;
            if (!dynamic && ById(thing.Owner) == null)
                return;
            if (thing.Sim != null && thing.SimDynamic == dynamic)
                return;
            RemoveSim(thing);
            var bodies = new List<SimBody>();
            foreach (BodyDesc desc in thing.Shape.Bodies)
            {
                if (!thing.Poses.TryGetValue(desc.Name, out BodyPose pose))
                    continue;
                SimBody body = _physics.Add(desc, pose.At, pose.Turn, dynamic);
                if (body == null)
                    continue;
                bodies.Add(body);
                _byHandle[body.Handle.Value] = thing;
                if (dynamic)
                    break;
            }
            if (bodies.Count == 0)
                return;
            thing.Sim = bodies.ToArray();
            thing.SimDynamic = dynamic;
        }

        void RemoveSim(Thing thing)
        {
            if (thing.Sim == null)
                return;
            foreach (SimBody body in thing.Sim)
            {
                _byHandle.Remove(body.Handle.Value);
                _physics?.Remove(body);
            }
            thing.Sim = null;
        }

        void DriveObstacles()
        {
            foreach (Thing thing in _things.Values)
            {
                if (thing.Sim == null || thing.SimDynamic)
                    continue;
                foreach (SimBody body in thing.Sim)
                {
                    if (thing.Poses.TryGetValue(body.Name, out BodyPose pose))
                        _physics.Drive(body, pose.At, pose.Turn, FollowWithin);
                }
            }
        }

        void BringBackFallen()
        {
            float floor = _physics.LowestY - FallenBelow;
            foreach (Thing thing in _things.Values)
            {
                if (thing.Sim == null || !thing.SimDynamic)
                    continue;
                _physics.Read(thing.Sim[0], out Vector3 at, out Quaternion _);
                if (at.Y >= floor)
                    continue;
                _physics.Place(thing.Sim[0], thing.Home, thing.HomeTurn);
                if (!_toldFallen)
                {
                    _toldFallen = true;
                    Log("Something fell out of the map, so it was put back where it was handed over. The map's collision may have a hole.");
                }
            }
        }

        // ---------------------------------------------------------------- sending

        void SendServerPoses(double now)
        {
            MemoryStream stream = null;
            BinaryWriter w = null;
            byte count = 0;
            foreach (Thing thing in _things.Values)
            {
                if (thing.Owner != 0 || thing.Sim == null || !thing.SimDynamic)
                    continue;
                if (thing.Kind == Spawnable && now - thing.AnnouncedAt >= AnnounceEvery)
                    Announce(thing, now);
                _physics.Read(thing.Sim[0], out Vector3 at, out Quaternion turn);
                ushort name = thing.Shape.Bodies[0].Name;
                thing.Poses[name].At = at;
                thing.Poses[name].Turn = turn;
                bool moved = Vector3.DistanceSquared(at, thing.SentAt3) > MoveEnough * MoveEnough || Angle(turn, thing.SentTurn) > TurnEnough;
                if (!moved && now - thing.SentAt < KeepAliveEvery)
                    continue;
                if (w != null && (stream.Length + 29 > PacketBudget || count == 255))
                {
                    FinishPoses(stream, count);
                    w = null;
                }
                if (w == null)
                {
                    stream = new MemoryStream();
                    w = new BinaryWriter(stream);
                    w.Write((byte)Kind.WorldPoses);
                    w.Write((byte)0);
                    w.Write((byte)0);
                    count = 0;
                }
                thing.SentAt = now;
                thing.SentAt3 = at;
                thing.SentTurn = turn;
                w.Write(thing.Id);
                w.Write(thing.Epoch);
                w.Write((byte)1);
                w.Write(name);
                w.Write(at.X);
                w.Write(at.Y);
                w.Write(at.Z);
                if (turn.W < 0f)
                    turn = new Quaternion(-turn.X, -turn.Y, -turn.Z, -turn.W);
                w.Write(Pack(turn.X));
                w.Write(Pack(turn.Y));
                w.Write(Pack(turn.Z));
                w.Write(Pack(turn.W));
                w.Flush();
                count++;
            }
            if (w != null)
                FinishPoses(stream, count);
        }

        void FinishPoses(MemoryStream stream, byte count)
        {
            byte[] data = stream.ToArray();
            data[2] = count;
            SendToSimScene(data);
        }

        /// <summary>
        /// Items the server owns are announced now and then, so players who arrive later get a copy to follow.
        /// </summary>
        void Announce(Thing thing, double now)
        {
            thing.AnnouncedAt = now;
            BodyPose pose = thing.Poses[thing.Shape.Bodies[0].Name];
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            w.Write((byte)Kind.WorldSpawn);
            w.Write((byte)0);
            w.Write(thing.Id);
            w.Write(thing.Kind);
            w.Write(thing.Epoch);
            w.Write(_simScene);
            w.Write(thing.Prefab);
            w.Write(pose.At.X);
            w.Write(pose.At.Y);
            w.Write(pose.At.Z);
            w.Write(pose.Turn.X);
            w.Write(pose.Turn.Y);
            w.Write(pose.Turn.Z);
            w.Write(pose.Turn.W);
            w.Flush();
            SendToSimScene(stream.ToArray());
        }

        void SendToSimScene(byte[] data)
        {
            foreach (Client client in _clients.Values)
            {
                if (client.State != null && client.State.Scene == _simScene)
                    Send(client.EndPoint, data);
            }
        }

        void AskForShapes(Client keeper, double now)
        {
            int asked = 0;
            foreach (Thing thing in _things.Values)
            {
                if (asked >= ShapeAsksPerTick)
                    return;
                if (thing.Shape != null || !thing.Known || now - thing.ShapeAskedAt < ShapeAskEvery)
                    continue;
                Client owner = thing.Owner == 0 ? keeper : ById(thing.Owner);
                if (owner == null || owner.State == null || owner.State.Scene != _simScene)
                    continue;
                AskForShape(thing, owner, now);
                asked++;
            }
        }

        void AskForShape(Thing thing, Client from, double now)
        {
            thing.ShapeAskedAt = now;
            Send(from.EndPoint, Shapes.WriteShapeAsk(thing.Id));
        }

        static short Pack(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), -32767, 32767);

        static float Angle(Quaternion a, Quaternion b)
        {
            float dot = Math.Min(Math.Abs(Quaternion.Dot(a, b)), 1f);
            return 2f * MathF.Acos(dot) * 57.29578f;
        }

        /// <summary>
        /// Throws everything the server simulates into the air.
        /// </summary>
        public void Shake()
        {
            if (_physics == null)
            {
                Log("The server is not simulating a map right now.");
                return;
            }
            var random = new Random();
            int thrown = 0;
            foreach (Thing thing in _things.Values)
            {
                if (thing.Sim == null || !thing.SimDynamic)
                    continue;
                SimBody body = thing.Sim[0];
                _physics.Read(body, out Vector3 at, out Quaternion turn);
                var kick = new Vector3((float)random.NextDouble() - 0.5f, 1f, (float)random.NextDouble() - 0.5f) * (body.Mass * 5f);
                var off = new Vector3((float)random.NextDouble() - 0.5f, 0f, (float)random.NextDouble() - 0.5f) * 0.1f;
                _physics.Push(body, kick, at + Vector3.Transform(body.Center, turn) + off);
                thrown++;
            }
            Log(thrown == 0 ? "The server simulates nothing to shake yet." : "Threw " + thrown + " props and items into the air.");
        }

        string WorldStatus()
        {
            if (_simScene.Length == 0)
                return "No shared world: nobody is in a map.";
            if (_physics == null)
                return "Waiting for the collision of " + _simScene + (_pieces != null ? " (" + _piecesHave + "/" + _pieces.Length + " pieces)" : "") + ".";
            int simulated = 0, obstacles = 0, shapes = 0, awake = 0;
            float drift = 0f;
            foreach (Thing thing in _things.Values)
            {
                if (thing.Shape != null)
                    shapes++;
                if (thing.Sim == null)
                    continue;
                if (thing.SimDynamic)
                {
                    simulated++;
                    if (_physics.Awake(thing.Sim[0]))
                        awake++;
                    _physics.Read(thing.Sim[0], out Vector3 at, out Quaternion _);
                    drift = Math.Max(drift, Vector3.Distance(at, thing.Home));
                }
                else
                    obstacles++;
            }
            return "World " + _simScene + ": " + _things.Count + " shared things (" + shapes + " with shapes), the server simulates " + simulated
                + " (" + awake + " moving, the furthest " + drift.ToString("0.00") + " m from where it was handed over), " + obstacles
                + " moved by players or NPCs.";
        }
    }
}
