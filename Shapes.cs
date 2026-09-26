using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Multiplayer
{
    // Collision shapes for the server's physics, shared by the mod and the standalone server (Frukt-DedicatedServer), so keep it
    // free of Unity. Positions are Unity world units; rotations are Unity quaternions.

    enum ShapeType : byte
    {
        Box = 1,
        Sphere = 2,
        Capsule = 3,
        Hull = 4
    }

    /// <summary>
    /// One collider, placed relative to what holds it: a body, or the world for the map's solids.
    /// </summary>
    sealed class ShapeDesc
    {
        public ShapeType Type;
        public float Px, Py, Pz;
        public float Qx, Qy, Qz, Qw = 1f;
        /// <summary>
        /// Box: half extents. Sphere: A is the radius. Capsule: A is the radius and B half the distance between the two cap
        /// centres, along the shape's own Y.
        /// </summary>
        public float A, B, C;
        /// <summary>
        /// Hull: corner points as x, y, z one after another, in the shape's own frame.
        /// </summary>
        public float[] Points;
    }

    sealed class BodyDesc
    {
        public ushort Name;
        public float Mass;
        public bool Kinematic;
        public readonly List<ShapeDesc> Shapes = new List<ShapeDesc>();
    }

    /// <summary>
    /// What one shared thing's bodies are made of, so the server can simulate it or have its physics bump into it.
    /// </summary>
    sealed class ThingShape
    {
        public uint Id;
        public readonly List<BodyDesc> Bodies = new List<BodyDesc>();
    }

    /// <summary>
    /// A map's static collision: simple solids plus one triangle soup, all in world space.
    /// </summary>
    sealed class MapShape
    {
        public string Scene = "";
        public readonly List<ShapeDesc> Solids = new List<ShapeDesc>();
        public float[] Vertices = new float[0];
        public int[] Triangles = new int[0];
    }

    sealed class MapPiece
    {
        public string Scene;
        /// <summary>
        /// Fingerprint of the whole packed map, so pieces of two different captures are never mixed.
        /// </summary>
        public uint Hash;
        public ushort Index;
        public ushort Total;
        public byte[] Data;
    }

    static class Shapes
    {
        /// <summary>
        /// Bump when the way maps are captured changes, so servers throw away maps they saved from older mods.
        /// </summary>
        public const ushort MapVersion = 1;
        public const int PieceBytes = 1000;
        public const int MaxPieces = 12000;
        public const int MaxHullPoints = 48;
        public const int MaxBodies = 64;
        public const int MaxShapesPerBody = 32;
        public const int MaxSolids = 200000;
        public const int MaxVertices = 3000000;
        public const int MaxTriangles = 3000000;
        /// <summary>
        /// Shape packets bigger than this are simplified by the sender until they fit.
        /// </summary>
        public const int MaxShapePacket = 1200;

        public static void WriteShape(BinaryWriter w, ShapeDesc s)
        {
            w.Write((byte)s.Type);
            w.Write(s.Px);
            w.Write(s.Py);
            w.Write(s.Pz);
            w.Write(s.Qx);
            w.Write(s.Qy);
            w.Write(s.Qz);
            w.Write(s.Qw);
            if (s.Type == ShapeType.Hull)
            {
                int n = s.Points == null ? 0 : Math.Min(s.Points.Length / 3, MaxHullPoints);
                w.Write((byte)n);
                for (int i = 0; i < n * 3; i++)
                    w.Write(s.Points[i]);
                return;
            }
            w.Write(s.A);
            w.Write(s.B);
            w.Write(s.C);
        }

        public static ShapeDesc ReadShape(BinaryReader r)
        {
            var s = new ShapeDesc { Type = (ShapeType)r.ReadByte() };
            if (s.Type < ShapeType.Box || s.Type > ShapeType.Hull)
                throw new InvalidDataException("Unknown shape " + (byte)s.Type + ".");
            s.Px = Finite(r.ReadSingle());
            s.Py = Finite(r.ReadSingle());
            s.Pz = Finite(r.ReadSingle());
            s.Qx = Finite(r.ReadSingle());
            s.Qy = Finite(r.ReadSingle());
            s.Qz = Finite(r.ReadSingle());
            s.Qw = Finite(r.ReadSingle());
            if (s.Type == ShapeType.Hull)
            {
                int n = r.ReadByte();
                if (n < 4 || n > MaxHullPoints)
                    throw new InvalidDataException("A hull needs 4 to " + MaxHullPoints + " points.");
                s.Points = new float[n * 3];
                for (int i = 0; i < s.Points.Length; i++)
                    s.Points[i] = Finite(r.ReadSingle());
                return s;
            }
            s.A = Size(r.ReadSingle());
            s.B = Size(r.ReadSingle());
            s.C = Size(r.ReadSingle());
            return s;
        }

        static float Finite(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > 100000f)
                throw new InvalidDataException("A shape number is out of range.");
            return v;
        }

        static float Size(float v)
        {
            v = Finite(v);
            if (v < 0f)
                throw new InvalidDataException("A shape size is negative.");
            return v;
        }

        public static byte[] WriteShapeInfo(ThingShape thing)
        {
            return Wire.Write(Kind.ShapeInfo, w =>
            {
                w.Write((byte)0);
                w.Write(thing.Id);
                int bodies = Math.Min(thing.Bodies.Count, MaxBodies);
                w.Write((byte)bodies);
                for (int b = 0; b < bodies; b++)
                {
                    BodyDesc body = thing.Bodies[b];
                    w.Write(body.Name);
                    w.Write(body.Mass);
                    w.Write(body.Kinematic);
                    int shapes = Math.Min(body.Shapes.Count, MaxShapesPerBody);
                    w.Write((byte)shapes);
                    for (int s = 0; s < shapes; s++)
                        WriteShape(w, body.Shapes[s]);
                }
            });
        }

        /// <summary>
        /// Reads a shape packet after its kind and sender bytes.
        /// </summary>
        public static ThingShape ReadShapeInfo(BinaryReader r)
        {
            var thing = new ThingShape { Id = r.ReadUInt32() };
            int bodies = r.ReadByte();
            if (bodies > MaxBodies)
                throw new InvalidDataException("Too many bodies.");
            for (int b = 0; b < bodies; b++)
            {
                var body = new BodyDesc { Name = r.ReadUInt16(), Mass = Size(r.ReadSingle()), Kinematic = r.ReadBoolean() };
                int shapes = r.ReadByte();
                if (shapes > MaxShapesPerBody)
                    throw new InvalidDataException("Too many shapes on one body.");
                for (int s = 0; s < shapes; s++)
                    body.Shapes.Add(ReadShape(r));
                thing.Bodies.Add(body);
            }
            return thing;
        }

        public static byte[] WriteShapeAsk(uint id)
        {
            return Wire.Write(Kind.ShapeAsk, w => w.Write(id));
        }

        public static byte[] WriteGive(uint id, ushort epoch)
        {
            return Wire.Write(Kind.WorldGive, w =>
            {
                w.Write((byte)0);
                w.Write(id);
                w.Write(epoch);
            });
        }

        public static byte[] WriteMapAsk(string scene, IList<ushort> missing)
        {
            return Wire.Write(Kind.MapAsk, w =>
            {
                w.Write(Wire.Clip(scene, Wire.MaxScene));
                int n = missing == null ? 0 : Math.Min(missing.Count, 400);
                w.Write((ushort)n);
                for (int i = 0; i < n; i++)
                    w.Write(missing[i]);
            });
        }

        /// <summary>
        /// Reads a map request after its kind byte. An empty list means every piece.
        /// </summary>
        public static List<ushort> ReadMapAsk(BinaryReader r, out string scene)
        {
            scene = Wire.Clip(r.ReadString(), Wire.MaxScene);
            int n = r.ReadUInt16();
            var missing = new List<ushort>(Math.Min(n, 400));
            for (int i = 0; i < n && i < 400; i++)
                missing.Add(r.ReadUInt16());
            return missing;
        }

        public static byte[] WriteMapPiece(string scene, uint hash, ushort index, ushort total, byte[] blob)
        {
            int start = index * PieceBytes;
            int length = Math.Min(PieceBytes, blob.Length - start);
            return Wire.Write(Kind.MapPart, w =>
            {
                w.Write(Wire.Clip(scene, Wire.MaxScene));
                w.Write(hash);
                w.Write(index);
                w.Write(total);
                w.Write((ushort)length);
                w.Write(blob, start, length);
            });
        }

        public static MapPiece ReadMapPiece(BinaryReader r)
        {
            var piece = new MapPiece { Scene = Wire.Clip(r.ReadString(), Wire.MaxScene), Hash = r.ReadUInt32(), Index = r.ReadUInt16(), Total = r.ReadUInt16() };
            int length = r.ReadUInt16();
            if (piece.Total == 0 || piece.Total > MaxPieces || piece.Index >= piece.Total || length > PieceBytes)
                throw new InvalidDataException("Bad map piece.");
            piece.Data = r.ReadBytes(length);
            if (piece.Data.Length != length)
                throw new EndOfStreamException();
            return piece;
        }

        public static int PieceCount(int bytes) => (bytes + PieceBytes - 1) / PieceBytes;

        public static byte[] PackMap(MapShape map)
        {
            var output = new MemoryStream();
            using (var zip = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
            using (var w = new BinaryWriter(zip))
            {
                w.Write(MapVersion);
                w.Write(Wire.Clip(map.Scene, Wire.MaxScene));
                w.Write(map.Solids.Count);
                for (int i = 0; i < map.Solids.Count; i++)
                    WriteShape(w, map.Solids[i]);
                w.Write(map.Vertices.Length / 3);
                for (int i = 0; i < map.Vertices.Length; i++)
                    w.Write(map.Vertices[i]);
                w.Write(map.Triangles.Length / 3);
                for (int i = 0; i < map.Triangles.Length; i++)
                    w.Write(map.Triangles[i]);
            }
            return output.ToArray();
        }

        public static MapShape UnpackMap(byte[] packed)
        {
            using (var zip = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
            using (var r = new BinaryReader(zip))
            {
                ushort version = r.ReadUInt16();
                if (version != MapVersion)
                    throw new InvalidDataException("The map was captured by a different mod version (" + version + ").");
                var map = new MapShape { Scene = Wire.Clip(r.ReadString(), Wire.MaxScene) };
                int solids = r.ReadInt32();
                if (solids < 0 || solids > MaxSolids)
                    throw new InvalidDataException("Too many solids.");
                for (int i = 0; i < solids; i++)
                    map.Solids.Add(ReadShape(r));
                int vertices = r.ReadInt32();
                if (vertices < 0 || vertices > MaxVertices)
                    throw new InvalidDataException("Too many vertices.");
                map.Vertices = new float[vertices * 3];
                for (int i = 0; i < map.Vertices.Length; i++)
                    map.Vertices[i] = Finite(r.ReadSingle());
                int triangles = r.ReadInt32();
                if (triangles < 0 || triangles > MaxTriangles)
                    throw new InvalidDataException("Too many triangles.");
                map.Triangles = new int[triangles * 3];
                for (int i = 0; i < map.Triangles.Length; i++)
                {
                    int index = r.ReadInt32();
                    if (index < 0 || index >= vertices)
                        throw new InvalidDataException("A triangle points past the vertices.");
                    map.Triangles[i] = index;
                }
                return map;
            }
        }
    }
}
