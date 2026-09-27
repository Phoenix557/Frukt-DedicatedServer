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
        Info = 14,
        Chat = 15,
        AvatarAsk = 16,
        AvatarPart = 17,
        WorldSweep = 18,
        /// <summary>
        /// Server to a player: send the collision of the map you are on (all of it, or the listed pieces).
        /// </summary>
        MapAsk = 19,
        MapPart = 20,
        /// <summary>
        /// Player to server: the collision shapes of one shared thing's bodies.
        /// </summary>
        ShapeInfo = 21,
        ShapeAsk = 22,
        /// <summary>
        /// Player to server: this thing of mine is at rest and nobody holds it, so the server may simulate it.
        /// </summary>
        WorldGive = 23,
        /// <summary>
        /// Where a dead player's body has its head on the screen that killed it, sent to that player so their camera can follow it, and
        /// to everyone else so their copy of the corpse lies the same way.
        /// </summary>
        Corpse = 24,
        /// <summary>
        /// Host to players: the physics rework switches that change what a shot does (ricochets, pellet damage), so every screen follows
        /// the host's. Older mods and the standalone server ignore it.
        /// </summary>
        Rules = 25
    }

    [Flags]
    enum Rule : byte
    {
        None = 0,
        Ricochets = 1,
        PelletDamage = 2,
        /// <summary>Set on every rules packet, so an empty set of switches still reads as "the host sent rules".</summary>
        Sent = 128
    }

    enum Sweep : byte
    {
        /// <summary>
        /// NPCs that have been dead for at least the sweep's age.
        /// </summary>
        Bodies = 1,
        /// <summary>
        /// Every NPC, alive or dead. Other players are never swept.
        /// </summary>
        Everyone = 2
    }

    /// <summary>
    /// A clean-up every screen runs on the NPCs it simulates, so the same ones go everywhere.
    /// </summary>
    sealed class SweepOrder
    {
        public byte From;
        public Sweep What;
        /// <summary>
        /// For Bodies: how many seconds a body must have been dead. 0 takes every dead one.
        /// </summary>
        public ushort DeadFor;
    }

    /// <summary>
    /// One chat line. The host fills in who sent it, so nobody can speak as someone else.
    /// </summary>
    sealed class ChatLine
    {
        public byte From;
        public string Name = "";
        public string Text = "";
    }

    /// <summary>
    /// A player's picture as it arrives in pieces. Data is 64x64 RGB, bottom row first, deflated.
    /// </summary>
    sealed class AvatarImage
    {
        public const int Size = 64;
        public const int PartBytes = 1000;
        public const int MaxParts = 32;

        public uint Hash;
        public byte[][] Parts;
        public int Have;
        public bool Complete => Parts != null && Have == Parts.Length;

        public static AvatarImage From(byte[] data)
        {
            int count = (data.Length + PartBytes - 1) / PartBytes;
            if (count == 0 || count > MaxParts)
                return null;
            var image = new AvatarImage { Hash = Wire.Fingerprint(data), Parts = new byte[count][], Have = count };
            for (int i = 0; i < count; i++)
            {
                int length = Math.Min(PartBytes, data.Length - i * PartBytes);
                image.Parts[i] = new byte[length];
                Buffer.BlockCopy(data, i * PartBytes, image.Parts[i], 0, length);
            }
            return image;
        }

        /// <summary>
        /// Adds a piece; false when it belongs to a different picture than the one being collected.
        /// </summary>
        public bool Add(AvatarPart part)
        {
            if (part.Hash != Hash || part.Count == 0 || part.Count > MaxParts || part.Index >= part.Count)
                return false;
            if (Parts == null || Parts.Length != part.Count)
            {
                Parts = new byte[part.Count][];
                Have = 0;
            }
            if (Parts[part.Index] == null)
            {
                Parts[part.Index] = part.Data;
                Have++;
            }
            return true;
        }

        public byte[] Joined()
        {
            if (!Complete)
                return null;
            int total = 0;
            for (int i = 0; i < Parts.Length; i++)
                total += Parts[i].Length;
            var data = new byte[total];
            int at = 0;
            for (int i = 0; i < Parts.Length; i++)
            {
                Buffer.BlockCopy(Parts[i], 0, data, at, Parts[i].Length);
                at += Parts[i].Length;
            }
            return Wire.Fingerprint(data) == Hash ? data : null;
        }
    }

    sealed class AvatarPart
    {
        public byte Owner;
        public uint Hash;
        public byte Count;
        public byte Index;
        public byte[] Data;
    }

    sealed class KillNotice
    {
        public byte Victim;
        public byte Life;
        public string Killer = "";
    }

    sealed class CorpseHead
    {
        public const int MaxLimbs = 48;

        public byte From;
        public byte Victim;
        public byte Life;
        /// <summary>
        /// Where the dead body's eyes are and which way they face, so its player can look out of them while it falls.
        /// </summary>
        public float X, Y, Z;
        public float Qx, Qy, Qz, Qw = 1f;
        /// <summary>
        /// Every part of the dead body, so its player's screen can lay a copy out the same way.
        /// </summary>
        public LimbPose[] Limbs = new LimbPose[0];
    }

    struct LimbPose
    {
        /// <summary>
        /// Stands for the part's place in the body, the same number on every screen.
        /// </summary>
        public ushort Key;
        public float X, Y, Z;
        public float Qx, Qy, Qz, Qw;
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
        /// <summary>
        /// Fingerprint of their picture, or 0 for none. Ask for it (AvatarAsk) when it is new to you.
        /// </summary>
        public uint Avatar;
        /// <summary>
        /// On a relay server, the map whose props and items the server simulates itself, or empty while it has none loaded.
        /// </summary>
        public string Simulating = "";
        /// <summary>
        /// Dead and flying around as a ghost until they respawn: shown see-through, touches and is touched by nothing.
        /// </summary>
        public bool Ghost;
        /// <summary>
        /// Each limb relative to the eyes, so another screen can wear the same pose. Empty when the sender has no body yet.
        /// </summary>
        public LimbPose[] Pose = new LimbPose[0];
        /// <summary>Limbs that are already gone on the sender, so the copy does not keep drawing them.</summary>
        public ushort[] Missing = new ushort[0];
        public double HeardAt;
    }

    static class Wire
    {
        public const ushort Protocol = 10;
        public const int MaxPlayers = 8;
        /// <summary>
        /// Queries are padded to this size so answering one never sends back more than was received.
        /// </summary>
        public const int QuerySize = 400;
        public const int MaxName = 24;
        public const int MaxScene = 64;
        public const int MaxPrefab = 64;
        public const int MaxChat = 160;

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
                w.Write(s.Avatar);
                w.Write(Clip(s.Simulating, MaxScene));
                w.Write(s.Ghost);
                WritePose(w, s.Pose, s.Missing);
            });
        }

        static void WritePose(BinaryWriter w, LimbPose[] pose, ushort[] missing)
        {
            int count = pose == null ? 0 : Math.Min(pose.Length, CorpseHead.MaxLimbs);
            w.Write((byte)count);
            for (int i = 0; i < count; i++)
            {
                LimbPose limb = pose[i];
                w.Write(limb.Key);
                w.Write(PackSpan(limb.X));
                w.Write(PackSpan(limb.Y));
                w.Write(PackSpan(limb.Z));
                w.Write(PackTurn(limb.Qx));
                w.Write(PackTurn(limb.Qy));
                w.Write(PackTurn(limb.Qz));
                w.Write(PackTurn(limb.Qw));
            }
            int gone = missing == null ? 0 : Math.Min(missing.Length, CorpseHead.MaxLimbs);
            w.Write((byte)gone);
            for (int i = 0; i < gone; i++)
                w.Write(missing[i]);
        }

        static short PackSpan(float metres)
        {
            return (short)Math.Max(-32767, Math.Min(32767, (int)Math.Round(metres * 1000.0)));
        }

        static float UnpackSpan(short value)
        {
            return value / 1000f;
        }

        public static PeerState ReadState(BinaryReader r)
        {
            var state = new PeerState
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
                Keeper = r.ReadByte(),
                Avatar = r.ReadUInt32(),
                Simulating = Clip(r.ReadString(), MaxScene),
                Ghost = r.ReadBoolean()
            };
            ReadPose(r, state);
            return state;
        }

        static void ReadPose(BinaryReader r, PeerState state)
        {
            if (r.BaseStream.Position >= r.BaseStream.Length)
                return;
            int count = Math.Min((int)r.ReadByte(), CorpseHead.MaxLimbs);
            state.Pose = new LimbPose[count];
            for (int i = 0; i < count; i++)
            {
                state.Pose[i] = new LimbPose
                {
                    Key = r.ReadUInt16(),
                    X = UnpackSpan(r.ReadInt16()),
                    Y = UnpackSpan(r.ReadInt16()),
                    Z = UnpackSpan(r.ReadInt16()),
                    Qx = UnpackTurn(r.ReadInt16()),
                    Qy = UnpackTurn(r.ReadInt16()),
                    Qz = UnpackTurn(r.ReadInt16()),
                    Qw = UnpackTurn(r.ReadInt16())
                };
            }
            if (r.BaseStream.Position >= r.BaseStream.Length)
                return;
            int gone = Math.Min((int)r.ReadByte(), CorpseHead.MaxLimbs);
            state.Missing = new ushort[gone];
            for (int i = 0; i < gone; i++)
                state.Missing[i] = r.ReadUInt16();
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

        public static byte[] WriteCorpse(CorpseHead c)
        {
            return Write(Kind.Corpse, w =>
            {
                w.Write(c.From);
                w.Write(c.Victim);
                w.Write(c.Life);
                w.Write(c.X);
                w.Write(c.Y);
                w.Write(c.Z);
                w.Write(PackTurn(c.Qx));
                w.Write(PackTurn(c.Qy));
                w.Write(PackTurn(c.Qz));
                w.Write(PackTurn(c.Qw));
                int count = Math.Min(c.Limbs == null ? 0 : c.Limbs.Length, CorpseHead.MaxLimbs);
                w.Write((byte)count);
                for (int i = 0; i < count; i++)
                {
                    LimbPose limb = c.Limbs[i];
                    w.Write(limb.Key);
                    w.Write(limb.X);
                    w.Write(limb.Y);
                    w.Write(limb.Z);
                    w.Write(PackTurn(limb.Qx));
                    w.Write(PackTurn(limb.Qy));
                    w.Write(PackTurn(limb.Qz));
                    w.Write(PackTurn(limb.Qw));
                }
            });
        }

        public static CorpseHead ReadCorpse(BinaryReader r)
        {
            CorpseHead corpse = new CorpseHead
            {
                From = r.ReadByte(),
                Victim = r.ReadByte(),
                Life = r.ReadByte(),
                X = r.ReadSingle(),
                Y = r.ReadSingle(),
                Z = r.ReadSingle(),
                Qx = UnpackTurn(r.ReadInt16()),
                Qy = UnpackTurn(r.ReadInt16()),
                Qz = UnpackTurn(r.ReadInt16()),
                Qw = UnpackTurn(r.ReadInt16())
            };
            int count = Math.Min((int)r.ReadByte(), CorpseHead.MaxLimbs);
            corpse.Limbs = new LimbPose[count];
            for (int i = 0; i < count; i++)
            {
                corpse.Limbs[i] = new LimbPose
                {
                    Key = r.ReadUInt16(),
                    X = r.ReadSingle(),
                    Y = r.ReadSingle(),
                    Z = r.ReadSingle(),
                    Qx = UnpackTurn(r.ReadInt16()),
                    Qy = UnpackTurn(r.ReadInt16()),
                    Qz = UnpackTurn(r.ReadInt16()),
                    Qw = UnpackTurn(r.ReadInt16())
                };
            }
            return corpse;
        }

        static short PackTurn(float value)
        {
            return (short)Math.Round(Math.Max(-1f, Math.Min(1f, value)) * short.MaxValue);
        }

        static float UnpackTurn(short value)
        {
            return value / (float)short.MaxValue;
        }

        public static byte[] WriteRules(Rule rules)
        {
            return Write(Kind.Rules, w => w.Write((byte)(rules | Rule.Sent)));
        }

        public static Rule ReadRules(BinaryReader r)
        {
            return (Rule)r.ReadByte();
        }

        public static byte[] WriteChat(ChatLine line)
        {
            return Write(Kind.Chat, w =>
            {
                w.Write(line.From);
                w.Write(Clip(line.Name, MaxName));
                w.Write(Clip(line.Text, MaxChat));
            });
        }

        public static ChatLine ReadChat(BinaryReader r)
        {
            return new ChatLine
            {
                From = r.ReadByte(),
                Name = Clip(r.ReadString(), MaxName),
                Text = Clip(r.ReadString(), MaxChat)
            };
        }

        /// <summary>
        /// Chat as the host passes it on: one line, no control characters, trimmed; empty when nothing is left.
        /// </summary>
        public static string CleanChat(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var clean = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
                clean.Append(char.IsControl(c) ? ' ' : c);
            return Clip(clean.ToString().Trim(), MaxChat);
        }

        /// <summary>
        /// World packets carry the sender's id in their second byte and are passed on to everyone else.
        /// </summary>
        public static bool IsWorld(Kind kind)
        {
            return kind >= Kind.WorldSpawn && kind <= Kind.WorldCut || kind == Kind.WorldSweep;
        }

        public static byte[] WriteSweep(SweepOrder order)
        {
            return Write(Kind.WorldSweep, w =>
            {
                w.Write(order.From);
                w.Write((byte)order.What);
                w.Write(order.DeadFor);
            });
        }

        /// <summary>
        /// Reads a sweep from a whole world packet, the kind byte included.
        /// </summary>
        public static SweepOrder ReadSweep(byte[] data)
        {
            if (data.Length < 5)
                throw new InvalidDataException("A sweep is too short.");
            var what = (Sweep)data[2];
            if (what != Sweep.Bodies && what != Sweep.Everyone)
                throw new InvalidDataException("Unknown sweep " + data[2] + ".");
            return new SweepOrder { From = data[1], What = what, DeadFor = BitConverter.ToUInt16(data, 3) };
        }

        public static byte[] WriteAvatarAsk(byte owner, uint hash)
        {
            return Write(Kind.AvatarAsk, w =>
            {
                w.Write(owner);
                w.Write(hash);
            });
        }

        public static void ReadAvatarAsk(BinaryReader r, out byte owner, out uint hash)
        {
            owner = r.ReadByte();
            hash = r.ReadUInt32();
        }

        public static byte[] WriteAvatarPart(byte owner, AvatarImage image, int index)
        {
            return Write(Kind.AvatarPart, w =>
            {
                w.Write(owner);
                w.Write(image.Hash);
                w.Write((byte)image.Parts.Length);
                w.Write((byte)index);
                w.Write((ushort)image.Parts[index].Length);
                w.Write(image.Parts[index]);
            });
        }

        public static AvatarPart ReadAvatarPart(BinaryReader r)
        {
            var part = new AvatarPart
            {
                Owner = r.ReadByte(),
                Hash = r.ReadUInt32(),
                Count = r.ReadByte(),
                Index = r.ReadByte()
            };
            int length = r.ReadUInt16();
            if (length > AvatarImage.PartBytes)
                throw new InvalidDataException("avatar piece too big");
            part.Data = r.ReadBytes(length);
            if (part.Data.Length != length)
                throw new EndOfStreamException();
            return part;
        }

        /// <summary>
        /// FNV-1a, never 0 (0 means "no picture").
        /// </summary>
        public static uint Fingerprint(byte[] data)
        {
            uint hash = 2166136261;
            for (int i = 0; i < data.Length; i++)
                hash = (hash ^ data[i]) * 16777619;
            return hash == 0 ? 1u : hash;
        }

        public static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return text.Length > max ? text.Substring(0, max) : text;
        }
    }
}
