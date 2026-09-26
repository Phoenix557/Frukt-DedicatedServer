using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using Multiplayer;

namespace FruktServer
{
    /// <summary>
    /// One body in the server's physics. Its origin is where the game's rigidbody is; Bepu keeps the centre of mass instead.
    /// </summary>
    sealed class SimBody
    {
        public BodyHandle Handle;
        public ushort Name;
        public TypedIndex Shape;
        public Vector3 Center;
        public bool Dynamic;
        public float Mass;
    }

    /// <summary>
    /// The server's own physics world for one map: the map's collision stands still, the props and items the server owns fall
    /// and tumble, and things players or NPCs move are pushed along as obstacles.
    /// </summary>
    sealed class Physics : IDisposable
    {
        public const float Step = 1f / 60f;
        const float TeleportBeyond = 3f;

        readonly BufferPool _pool = new BufferPool();
        readonly Simulation _sim;

        public float LowestY { get; private set; } = float.MaxValue;
        public int Solids { get; private set; }
        public int Triangles { get; private set; }

        public Physics(MapShape map)
        {
            _sim = Simulation.Create(_pool, new Contacts(), new Gravity(new Vector3(0f, -9.81f, 0f)), new SolveDescription(8, 1));
            for (int i = 0; i < map.Solids.Count; i++)
                AddSolid(map.Solids[i]);
            AddTriangles(map);
            if (LowestY == float.MaxValue)
                LowestY = 0f;
        }

        void AddSolid(ShapeDesc s)
        {
            var pose = new RigidPose(new Vector3(s.Px, s.Py, s.Pz), Normalized(s.Qx, s.Qy, s.Qz, s.Qw));
            TypedIndex shape;
            switch (s.Type)
            {
                case ShapeType.Box:
                    shape = _sim.Shapes.Add(new Box(Math.Max(s.A * 2f, 0.001f), Math.Max(s.B * 2f, 0.001f), Math.Max(s.C * 2f, 0.001f)));
                    break;
                case ShapeType.Sphere:
                    shape = _sim.Shapes.Add(new Sphere(Math.Max(s.A, 0.001f)));
                    break;
                case ShapeType.Capsule:
                    shape = _sim.Shapes.Add(new Capsule(Math.Max(s.A, 0.001f), s.B * 2f));
                    break;
                case ShapeType.Hull:
                    if (!Hull(s, out ConvexHull hull, out Vector3 center))
                        return;
                    pose.Position += Vector3.Transform(center, pose.Orientation);
                    shape = _sim.Shapes.Add(hull);
                    break;
                default:
                    return;
            }
            _sim.Statics.Add(new StaticDescription(pose, shape));
            LowestY = Math.Min(LowestY, s.Py);
            Solids++;
        }

        /// <summary>
        /// Bepu's triangles only collide from their front, and which side is the front differs from Unity, so every triangle goes in
        /// both ways round.
        /// </summary>
        void AddTriangles(MapShape map)
        {
            int count = map.Triangles.Length / 3;
            if (count == 0)
                return;
            _pool.Take(count * 2, out Buffer<Triangle> triangles);
            for (int i = 0; i < count; i++)
            {
                Vector3 a = Vertex(map, map.Triangles[i * 3]);
                Vector3 b = Vertex(map, map.Triangles[i * 3 + 1]);
                Vector3 c = Vertex(map, map.Triangles[i * 3 + 2]);
                triangles[i * 2] = new Triangle(a, b, c);
                triangles[i * 2 + 1] = new Triangle(a, c, b);
                LowestY = Math.Min(LowestY, Math.Min(a.Y, Math.Min(b.Y, c.Y)));
            }
            var mesh = new Mesh(triangles, Vector3.One, _pool);
            _sim.Statics.Add(new StaticDescription(RigidPose.Identity, _sim.Shapes.Add(mesh)));
            Triangles = count;
        }

        static Vector3 Vertex(MapShape map, int index) => new Vector3(map.Vertices[index * 3], map.Vertices[index * 3 + 1], map.Vertices[index * 3 + 2]);

        bool Hull(ShapeDesc s, out ConvexHull hull, out Vector3 center)
        {
            int n = s.Points.Length / 3;
            var points = new Vector3[n];
            for (int i = 0; i < n; i++)
                points[i] = new Vector3(s.Points[i * 3], s.Points[i * 3 + 1], s.Points[i * 3 + 2]);
            try
            {
                hull = new ConvexHull(points, _pool, out center);
                return true;
            }
            catch (Exception)
            {
                hull = default;
                center = default;
                return false;
            }
        }

        static float Volume(ShapeDesc s)
        {
            switch (s.Type)
            {
                case ShapeType.Box:
                    return Math.Max(8f * s.A * s.B * s.C, 1e-6f);
                case ShapeType.Sphere:
                    return Math.Max(4.18879f * s.A * s.A * s.A, 1e-6f);
                case ShapeType.Capsule:
                    return Math.Max(3.14159f * s.A * s.A * (s.B * 2f + 1.33333f * s.A), 1e-6f);
                default:
                    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                    for (int i = 0; i + 2 < s.Points.Length; i += 3)
                    {
                        minX = Math.Min(minX, s.Points[i]);
                        maxX = Math.Max(maxX, s.Points[i]);
                        minY = Math.Min(minY, s.Points[i + 1]);
                        maxY = Math.Max(maxY, s.Points[i + 1]);
                        minZ = Math.Min(minZ, s.Points[i + 2]);
                        maxZ = Math.Max(maxZ, s.Points[i + 2]);
                    }
                    return Math.Max((maxX - minX) * (maxY - minY) * (maxZ - minZ) * 0.6f, 1e-6f);
            }
        }

        /// <summary>
        /// Adds one of a thing's bodies at the game's rigidbody pose. Dynamic bodies are simulated; the rest only get pushed around by
        /// Drive. Null when none of its shapes can be built.
        /// </summary>
        public SimBody Add(BodyDesc desc, Vector3 at, Quaternion turn, bool dynamic)
        {
            if (desc.Shapes.Count == 0)
                return null;
            float total = 0f;
            for (int i = 0; i < desc.Shapes.Count; i++)
                total += Volume(desc.Shapes[i]);
            float mass = Math.Clamp(desc.Mass, 0.05f, 5000f);

            var builder = new CompoundBuilder(_pool, _sim.Shapes, desc.Shapes.Count);
            try
            {
                int added = 0;
                for (int i = 0; i < desc.Shapes.Count; i++)
                {
                    ShapeDesc s = desc.Shapes[i];
                    var local = new RigidPose(new Vector3(s.Px, s.Py, s.Pz), Normalized(s.Qx, s.Qy, s.Qz, s.Qw));
                    float weight = dynamic ? mass * Volume(s) / total : 1f;
                    switch (s.Type)
                    {
                        case ShapeType.Box:
                            builder.Add(new Box(Math.Max(s.A * 2f, 0.002f), Math.Max(s.B * 2f, 0.002f), Math.Max(s.C * 2f, 0.002f)), local, weight);
                            break;
                        case ShapeType.Sphere:
                            builder.Add(new Sphere(Math.Max(s.A, 0.002f)), local, weight);
                            break;
                        case ShapeType.Capsule:
                            builder.Add(new Capsule(Math.Max(s.A, 0.002f), s.B * 2f), local, weight);
                            break;
                        case ShapeType.Hull:
                            if (!Hull(s, out ConvexHull hull, out Vector3 center))
                                continue;
                            local.Position += Vector3.Transform(center, local.Orientation);
                            builder.Add(hull, local, weight);
                            break;
                        default:
                            continue;
                    }
                    added++;
                }
                if (added == 0)
                    return null;

                var body = new SimBody { Dynamic = dynamic, Mass = mass, Name = desc.Name };
                Buffer<CompoundChild> children;
                BodyInertia inertia = default;
                if (dynamic)
                    builder.BuildDynamicCompound(out children, out inertia, out body.Center);
                else
                    builder.BuildKinematicCompound(out children, out body.Center);
                body.Shape = _sim.Shapes.Add(new Compound(children));
                turn = Quaternion.Normalize(turn);
                var pose = new RigidPose(at + Vector3.Transform(body.Center, turn), turn);
                var collidable = new CollidableDescription(body.Shape, dynamic ? ContinuousDetection.Continuous() : ContinuousDetection.Discrete);
                body.Handle = dynamic
                    ? _sim.Bodies.Add(BodyDescription.CreateDynamic(pose, inertia, collidable, new BodyActivityDescription(0.01f)))
                    : _sim.Bodies.Add(BodyDescription.CreateKinematic(pose, collidable, new BodyActivityDescription(0.01f)));
                return body;
            }
            finally
            {
                builder.Dispose();
            }
        }

        public void Remove(SimBody body)
        {
            if (body == null || !_sim.Bodies.BodyExists(body.Handle))
                return;
            _sim.Bodies.Remove(body.Handle);
            _sim.Shapes.RecursivelyRemoveAndDispose(body.Shape, _pool);
        }

        public void Read(SimBody body, out Vector3 at, out Quaternion turn)
        {
            RigidPose pose = _sim.Bodies[body.Handle].Pose;
            turn = pose.Orientation;
            at = pose.Position - Vector3.Transform(body.Center, turn);
        }

        public bool Awake(SimBody body) => _sim.Bodies[body.Handle].Awake;

        /// <summary>
        /// Puts a body somewhere at once, standing still.
        /// </summary>
        public void Place(SimBody body, Vector3 at, Quaternion turn)
        {
            BodyReference reference = _sim.Bodies[body.Handle];
            turn = Quaternion.Normalize(turn);
            reference.Pose = new RigidPose(at + Vector3.Transform(body.Center, turn), turn);
            reference.Velocity = default;
            reference.Awake = true;
        }

        /// <summary>
        /// Moves an obstacle towards where its owner says it is over the next moment, with a real velocity so what it hits is pushed.
        /// </summary>
        public void Drive(SimBody body, Vector3 at, Quaternion turn, float within)
        {
            BodyReference reference = _sim.Bodies[body.Handle];
            turn = Quaternion.Normalize(turn);
            Vector3 target = at + Vector3.Transform(body.Center, turn);
            RigidPose pose = reference.Pose;
            if (Vector3.DistanceSquared(target, pose.Position) > TeleportBeyond * TeleportBeyond)
            {
                reference.Pose = new RigidPose(target, turn);
                reference.Velocity = default;
                return;
            }
            Vector3 linear = (target - pose.Position) / within;
            Quaternion delta = Quaternion.Normalize(turn * Quaternion.Conjugate(pose.Orientation));
            if (delta.W < 0f)
                delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
            float sin = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
            Vector3 angular = sin > 1e-5f ? new Vector3(delta.X, delta.Y, delta.Z) / sin * (2f * MathF.Atan2(sin, delta.W) / within) : Vector3.Zero;
            bool moving = linear.LengthSquared() > 1e-6f || angular.LengthSquared() > 1e-6f;
            if (!moving && !reference.Awake)
                return;
            reference.Velocity.Linear = linear;
            reference.Velocity.Angular = angular;
            if (moving)
                reference.Awake = true;
        }

        public void Push(SimBody body, Vector3 impulse, Vector3 at)
        {
            BodyReference reference = _sim.Bodies[body.Handle];
            reference.Awake = true;
            reference.ApplyImpulse(impulse, at - reference.Pose.Position);
        }

        /// <summary>
        /// The first body a ray hits within reach, or false when it hits the map or nothing.
        /// </summary>
        public bool Ray(Vector3 from, Vector3 direction, float reach, out BodyHandle hit, out Vector3 point)
        {
            var handler = new FirstHit { T = float.MaxValue };
            direction = Vector3.Normalize(direction);
            _sim.RayCast(from, direction, reach, ref handler);
            hit = handler.Body;
            point = from + direction * handler.T;
            return handler.T < float.MaxValue && handler.IsBody;
        }

        public void Tick() => _sim.Timestep(Step);

        public void Dispose()
        {
            _sim.Dispose();
            _pool.Clear();
        }

        static Quaternion Normalized(float x, float y, float z, float w)
        {
            var q = new Quaternion(x, y, z, w);
            return q.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(q);
        }

        struct FirstHit : IRayHitHandler
        {
            public float T;
            public bool IsBody;
            public BodyHandle Body;

            public bool AllowTest(CollidableReference collidable) => true;

            public bool AllowTest(CollidableReference collidable, int childIndex) => true;

            public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
            {
                if (t >= T)
                    return;
                T = t;
                maximumT = t;
                IsBody = collidable.Mobility != CollidableMobility.Static;
                if (IsBody)
                    Body = collidable.BodyHandle;
            }
        }

        struct Contacts : INarrowPhaseCallbacks
        {
            public void Initialize(Simulation simulation)
            {
            }

            public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
            {
                return a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;
            }

            public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

            public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
                where TManifold : unmanaged, IContactManifold<TManifold>
            {
                pairMaterial = new PairMaterialProperties { FrictionCoefficient = 0.6f, MaximumRecoveryVelocity = 2f, SpringSettings = new SpringSettings(30f, 1f) };
                return true;
            }

            public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

            public void Dispose()
            {
            }
        }

        struct Gravity : IPoseIntegratorCallbacks
        {
            readonly Vector3 _gravity;
            Vector3Wide _gravityStep;
            System.Numerics.Vector<float> _spinKept;

            public Gravity(Vector3 gravity) : this()
            {
                _gravity = gravity;
            }

            public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
            public bool AllowSubstepsForUnconstrainedBodies => false;
            public bool IntegrateVelocityForKinematics => false;

            public void Initialize(Simulation simulation)
            {
            }

            public void PrepareForIntegration(float dt)
            {
                _gravityStep = Vector3Wide.Broadcast(_gravity * dt);
                _spinKept = new System.Numerics.Vector<float>(MathF.Pow(0.95f, dt));
            }

            public void IntegrateVelocity(System.Numerics.Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia,
                System.Numerics.Vector<int> integrationMask, int workerIndex, System.Numerics.Vector<float> dt, ref BodyVelocityWide velocity)
            {
                velocity.Linear += _gravityStep;
                velocity.Angular *= _spinKept;
            }
        }
    }
}
