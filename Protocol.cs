using System;
using System.IO;

namespace Multiplayer
{
    // The packet format, shared by the mod and the standalone server (Frukt-DedicatedServer), so keep it free of Unity.

    enum Kind : byte
    {
        Hello = 1,
        Welcome = 2,
        State = 3,
        Leave = 4,
        Full = 5,
        Mismatch = 6,
        Kill = 7,
        Shot = 8,
        WorldSpawn = 9,
        WorldPoses = 10,
        WorldGone = 11,
        WorldCut = 12,
        Query = 13,
        Info = 14
    }

    sealed class KillNotice
    {
        public byte Victim;
        public byte Life;
        public string Killer = "";
    }

    /// <summary>
    /// One shot from a player's gun: which gun, where its muzzle was and which way the round left.
    /// </summary>
    sealed class ShotNotice
    {
        public byte Shooter;
        public string Weapon = "";
        public float Ox, Oy, Oz;
        public float Dx, Dy, Dz;
        public double HeardAt;
    }

    sealed class PeerState
    {
        public byte Id;
        public string Name = "";
        public string Scene = "";
        public float X, Y, Z;
        public float Qx, Qy, Qz, Qw = 1f;
        public byte Life;
        /// <summary>
        /// Prefab id of the thing in their hand, or empty. Its pose is relative to their camera.
        /// </summary>
        public string Held = "";
        public float Hx, Hy, Hz;
        public float Hqx, Hqy, Hqz, Hqw = 1f;
        public float Hs = 1f;
        /// <summary>
        /// A server's own player: it has no body anyone should see.
        /// </summary>
        public bool Hidden;
        /// <summary>
        /// Sent by the standalone server, which runs no game: a player's screen simulates the shared world instead.
        /// </summary>
        public bool Relay;
        /// <summary>
        /// On a relay server, the player whose screen owns the map's props and takes over things whose owner left.
        /// </summary>
        public byte Keeper;
        public double HeardAt;
    }

    static class Wire
    {
        public const ushort Protocol = 6;
        public const int MaxPlayers = 8;
        /// <summary>
        /// Queries are padded to this size so answering one never sends back more than was received.
        /// </summary>
        public const int QuerySize = 400;
        public const int MaxName = 24;
        public const int MaxScene = 64;
        public const int MaxPrefab = 64;

        public static byte[] Write(Kind kind, Action<BinaryWriter> body)
        {
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);
            writer.Write((byte)kind);
            body(writer);
            writer.Flush();
            return stream.ToArray();
        }

        public static byte[] WriteQuery(uint token)
        {
            var data = new byte[QuerySize];
            data[0] = (byte)Kind.Query;
            BitConverter.GetBytes(token).CopyTo(data, 1);
            return data;
        }

        public static byte[] WriteInfo(uint token, uint serverId, string name, string map, int players, int max, bool dedicated)
        {
            return Write(Kind.Info, w =>
            {
                w.Write(token);
                w.Write(Protocol);
                w.Write(serverId);
                w.Write(Clip(name, MaxName));
                w.Write(Clip(map, MaxScene));
                w.Write((byte)players);
                w.Write((byte)max);
                w.Write(dedicated);
            });
        }

        public static byte[] WriteState(PeerState s)
        {
            return Write(Kind.State, w =>
            {
                w.Write(s.Id);
                w.Write(Clip(s.Name, MaxName));
                w.Write(Clip(s.Scene, MaxScene));
                w.Write(s.X);
                w.Write(s.Y);
                w.Write(s.Z);
                w.Write(s.Qx);
                w.Write(s.Qy);
                w.Write(s.Qz);
                w.Write(s.Qw);
                w.Write(s.Life);
                w.Write(Clip(s.Held, MaxPrefab));
                w.Write(s.Hx);
                w.Write(s.Hy);
                w.Write(s.Hz);
                w.Write(s.Hqx);
                w.Write(s.Hqy);
                w.Write(s.Hqz);
                w.Write(s.Hqw);
                w.Write(s.Hs);
                w.Write(s.Hidden);
                w.Write(s.Relay);
                w.Write(s.Keeper);
            });
        }

        public static PeerState ReadState(BinaryReader r)
        {
            return new PeerState
            {
                Id = r.ReadByte(),
                Name = Clip(r.ReadString(), MaxName),
                Scene = Clip(r.ReadString(), MaxScene),
                X = r.ReadSingle(),
                Y = r.ReadSingle(),
                Z = r.ReadSingle(),
                Qx = r.ReadSingle(),
                Qy = r.ReadSingle(),
                Qz = r.ReadSingle(),
                Qw = r.ReadSingle(),
                Life = r.ReadByte(),
                Held = Clip(r.ReadString(), MaxPrefab),
                Hx = r.ReadSingle(),
                Hy = r.ReadSingle(),
                Hz = r.ReadSingle(),
                Hqx = r.ReadSingle(),
                Hqy = r.ReadSingle(),
                Hqz = r.ReadSingle(),
                Hqw = r.ReadSingle(),
                Hs = r.ReadSingle(),
                Hidden = r.ReadBoolean(),
                Relay = r.ReadBoolean(),
                Keeper = r.ReadByte()
            };
        }

        public static byte[] WriteShot(ShotNotice s)
        {
            return Write(Kind.Shot, w =>
            {
                w.Write(s.Shooter);
                w.Write(Clip(s.Weapon, MaxPrefab));
                w.Write(s.Ox);
                w.Write(s.Oy);
                w.Write(s.Oz);
                w.Write(s.Dx);
                w.Write(s.Dy);
                w.Write(s.Dz);
            });
        }

        public static ShotNotice ReadShot(BinaryReader r)
        {
            return new ShotNotice
            {
                Shooter = r.ReadByte(),
                Weapon = Clip(r.ReadString(), MaxPrefab),
                Ox = r.ReadSingle(),
                Oy = r.ReadSingle(),
                Oz = r.ReadSingle(),
                Dx = r.ReadSingle(),
                Dy = r.ReadSingle(),
                Dz = r.ReadSingle()
            };
        }

        public static byte[] WriteKill(KillNotice k)
        {
            return Write(Kind.Kill, w =>
            {
                w.Write(k.Victim);
                w.Write(k.Life);
                w.Write(Clip(k.Killer, MaxName));
            });
        }

        public static KillNotice ReadKill(BinaryReader r)
        {
            return new KillNotice
            {
                Victim = r.ReadByte(),
                Life = r.ReadByte(),
                Killer = Clip(r.ReadString(), MaxName)
            };
        }

        public static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return text.Length > max ? text.Substring(0, max) : text;
        }
    }
}
