using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed record EventHorizonSettings
{
    public bool Enabled
    {
        get;
        init;
    } = true;
    public bool CollisionWaves
    {
        get;
        init;
    } = true;
    public float WaveScale
    {
        get;
        init;
    } = 1.25f;
    public float Distortion
    {
        get;
        init;
    } = .04f;
    public float Speed
    {
        get;
        init;
    } = 7.0f;
    public float Damping
    {
        get;
        init;
    } = 2.4f;
    public float BoundaryResponse
    {
        get;
        init;
    } = 2.5f;
    public float WaveSpeed
    {
        get;
        init;
    } = 1.35f;
    public float DisturbanceSmoothing
    {
        get;
        init;
    } = .05f; // Metres; filters short contact bumps.
    public float RimBrightness
    {
        get;
        init;
    } = .55f;
    public int SimulationResolution
    {
        get;
        init;
    } // Zero selects graphics preset.
    public static int Substeps(int resolution, float radius, float speed = 1.8f) =>
        Math.Max(1, (int)Math.Ceiling(speed / 60 / (2 * radius / resolution) / .65f));
    public float NormalStrength
    {
        get;
        init;
    } = 1.15f;
    public float HighlightWidth
    {
        get;
        init;
    } = .032f;
    public Vector3 Blue
    {
        get;
        init;
    } = new(.013f, .030f, .085f);
    public float CentreEmphasis
    {
        get;
        init;
    } = 1.8f;
    public Vector2 EdgeFade
    {
        get;
        init;
    } = new(.2f, 1f);
    public float BloomStrength
    {
        get;
        init;
    } = .16f;
    public float GrazingCompensation
    {
        get;
        init;
    } = .85f;
    public float EmissionIntensity
    {
        get;
        init;
    } = .35f;
}

// Visual-only contacts. No native physics state, authoritative events or save data.
sealed class EventHorizonState
{
    public Action<Vector2>? Entered
    {
        get;
        set;
    }
    public const float ModelCentreY = 3.063692f, ModelRadius = 2.30f;
    public const int Capacity = 16;
    public const double Lifetime = 3;
    // Small player-entry history for diagnostics; only polygon impulses drive rendering.
    public readonly record struct Ripple(Vector2 Position, double Started);
    readonly record struct Contact(Vector3 Feet, bool Touching);
    readonly record struct PlayerContactId(int Value);
    readonly record struct PropContactId(int Value);
    readonly Dictionary<PlayerContactId, Contact> contacts = [];
    readonly Dictionary<PropContactId, PropContact> propContacts = []; // Separate namespace from player IDs.
    readonly record struct PropContact(BodyState Body, bool Touching, double SuppressUntil);
    public sealed record ContactFootprint(int BodyId, Vector2[] Vertices, BodyState Previous, BodyState Current,
        Vector3 HalfExtent, bool SuppressImpact, float Motion, double PreviousTime, double CurrentTime, bool Barrel = false);
    // Collision-shape projection stays outside the GPU renderer. Future convex
    // shapes can change this provider while the wave passes still consume polygons.
    public static Vector2[] SampleFootprint(ContactFootprint contact, double time, StaticEntity gate)
    {
        float t = contact.SuppressImpact ? 1 : Math.Clamp((float)((time - contact.PreviousTime) / (contact.CurrentTime - contact.PreviousTime)), 0, 1);
        var sample = contact.Current with
        {
            Position = Vector3.Lerp(contact.Previous.Position, contact.Current.Position, t),
            Rotation = Quaternion.Slerp(contact.Previous.Rotation, contact.Current.Rotation, t)
        };
        return Footprint(sample, contact.HalfExtent, Vector3.Zero, ModelRadius * gate.Scale, contact.Barrel).ToArray();
    }
    public sealed record ContourImpulse(Vector2[][] Polygons, double Started, float Strength, float Displacement = 0)
    {
        public Vector2[] Vertices => Polygons[0];
    }
    public sealed record PlayerFootprint(int PlayerId, Vector2[] Vertices, bool SuppressImpact, float Motion);
    readonly List<PlayerFootprint> playerFootprints = [];
    public IReadOnlyList<PlayerFootprint> PlayerFootprints => playerFootprints;
    readonly List<ContactFootprint> footprints = [];
    readonly List<ContourImpulse> impulses = [];
    public IReadOnlyList<ContactFootprint> Footprints => footprints;
    public IReadOnlyList<ContourImpulse> Impulses => impulses;
    public int Generation
    {
        get;
        private set;
    }
    public static Vector2[] Capsule(Vector2 centre, float radius, float height) => Enumerable.Range(0, 24).Select(i =>
    {
        // Two complete semicircles, including their side endpoints. Sign(sin(a))
        // puts the zero-angle vertex halfway down one side and dents the mask.
        float a = (i % 12) * MathF.PI / 11 + (i >= 12 ? MathF.PI : 0);
        return centre + new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius + (i < 12 ? 1 : -1) * (height * .5f - radius));
    }).ToArray();
    readonly List<Ripple> ripples = [];
    Guid location;
    long epoch = -1;
    Matrix4x4 gateTransform;
    double lastTime = double.NaN;
    public IReadOnlyList<Ripple> Ripples => ripples;
    public static Vector3 Centre(StaticEntity gate) => Vector3.Transform(new Vector3(0, ModelCentreY, 0), gate.Transform);
    public void Clear()
    {
        contacts.Clear();
        propContacts.Clear();
        ripples.Clear();
        footprints.Clear();
        playerFootprints.Clear();
        impulses.Clear();
        Generation++;
        lastTime = double.NaN;
        epoch = -1;
    }
    public void Emit(Vector2 point, double time)
    {
        if (ripples.Count == Capacity)
            ripples.RemoveAt(0);
        ripples.Add(new(point, time));
    }
    public void Update(StaticEntity gate, IEnumerable<PlayerState> players, double time, Guid newLocation, long newEpoch,
        IEnumerable<BodyState>? bodies = null, IReadOnlyList<PropDefinition>? definitions = null)
    {
        if (newLocation != location || gate.Transform != gateTransform || time < lastTime)
        {
            Clear();
            location = newLocation;
            gateTransform = gate.Transform;
        }
        if (newEpoch != epoch)
        {
            contacts.Clear();
            propContacts.Clear();
            impulses.Clear();
            epoch = newEpoch;
            lastTime = double.NaN;
        }
        ripples.RemoveAll(r => time - r.Started >= Lifetime);
        impulses.RemoveAll(r => time - r.Started >= Lifetime);
        footprints.Clear();
        playerFootprints.Clear();
        double dt = time - lastTime;
        lastTime = time;
        var centre = Centre(gate);
        float radius = ModelRadius * gate.Scale;
        var inverse = Quaternion.Inverse(gate.Rotation);
        HashSet<int> seen = [];
        foreach (var player in players)
        {
            seen.Add(player.Id);
            var feet = Vector3.Transform(player.Position - centre, inverse);
            bool InOpening(Vector3 p, float margin = 0)
            {
                float y = Math.Clamp(0, p.Y + Movement.Radius, p.Y + Movement.BodyHeight(player) - Movement.Radius);
                return new Vector2(p.X, y).Length() < radius + Movement.Radius + margin;
            }
            bool touching = Math.Abs(feet.Z) <= Movement.Radius && InOpening(feet);
            bool observed = contacts.TryGetValue(new(player.Id), out var old);
            bool continuous = observed && dt > 0 && dt <= .25 && Vector3.Distance(feet, old.Feet) <= Math.Max(.75, dt * 12) + .01;
            if (continuous)
            {
                // Closest point on the swept path to the plane; catches a complete crossing in one frame.
                float dz = feet.Z - old.Feet.Z;
                float t = Math.Abs(dz) < 1e-6f ? 1 : Math.Clamp(-old.Feet.Z / dz, 0, 1);
                var hit = Vector3.Lerp(old.Feet, feet, t);
                bool inOpening = InOpening(hit);
                if (!old.Touching && Math.Abs(hit.Z) <= Movement.Radius && inOpening)
                {
                    var point = new Vector2(hit.X, hit.Y + Movement.BodyHeight(player) * .5f);
                    if (point.Length() > radius - .05f)
                        point = Vector2.Normalize(point) * (radius - .05f);
                    // A quiet contour packet catches complete swept crossings. Persistent
                    // contact below bends existing ripples without an added height pulse.
                    Entered?.Invoke(point);
                    Emit(point, time);
                    AddImpulse(Capsule(point, Movement.Radius, Movement.BodyHeight(player)), time, .025f, 0);
                    touching = true;
                }
                // Hysteresis prevents repeated contact events from correction jitter at the plane.
                if (old.Touching && Math.Abs(feet.Z) < Movement.Radius + .12f && InOpening(feet, .12f))
                    touching = true;
            }
            if (Math.Abs(feet.Z) < Movement.Radius && InOpening(feet))
            {
                float crossRadius = MathF.Sqrt(Math.Max(0, Movement.Radius * Movement.Radius - feet.Z * feet.Z));
                var point = new Vector2(feet.X, feet.Y + Movement.BodyHeight(player) * .5f);
                playerFootprints.Add(new(player.Id, Capsule(point, Math.Max(.005f, crossRadius), Movement.BodyHeight(player) - 2 * Movement.Radius + 2 * crossRadius),
                    !continuous, continuous ? Vector3.Distance(feet, old.Feet) / (float)dt : 0));
            }
            contacts[new(player.Id)] = new(feet, touching);
        }
        foreach (var id in contacts.Keys.Where(id => !seen.Contains(id.Value)).ToArray())
            contacts.Remove(id);
        // Store contacts in gate-local metres, leaving authoritative bodies unchanged.
        var localBodies = (bodies ?? []).Select(b => b with
        {
            Position = Vector3.Transform(b.Position - centre, inverse),
            Rotation = Quaternion.Normalize(Quaternion.Concatenate(b.Rotation, inverse)),
            LinearVelocity = Vector3.Transform(b.LinearVelocity, inverse),
            AngularVelocity = Vector3.Transform(b.AngularVelocity, inverse)
        });
        UpdateProps(Vector3.Zero, radius, localBodies, definitions ?? [], time, dt);
    }

    void AddImpulse(Vector2[] polygon, double time, float strength, float displacement = 0)
    {
        if (impulses.Count == Capacity)
            impulses.RemoveAt(0);
        impulses.Add(new([polygon], time, strength, displacement));
    }
    void UpdateProps(Vector3 centre, float radius, IEnumerable<BodyState> bodies, IReadOnlyList<PropDefinition> definitions, double time, double dt)
    {
        HashSet<int> seen = [];
        foreach (var body in bodies.OrderBy(b => b.Id))
        {
            var definition = definitions.FirstOrDefault(p => p.Id == body.Id);
            if (definition is null)
                continue;
            seen.Add(body.Id);
            var polygon = Footprint(body, definition.HalfExtent, centre, radius, definition.IsBarrel);
            bool touching = polygon.Count >= 3;
            bool observed = propContacts.TryGetValue(new(body.Id), out var old);
            float distance = observed ? Vector3.Distance(old.Body.Position, body.Position) : 0;
            float angle = observed ? 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(old.Body.Rotation, body.Rotation)), 0, 1)) : 0;
            bool continuous = observed && dt > 0 && dt <= .25 && distance < 20 &&
                distance <= Math.Max(.75, dt * (Math.Max(old.Body.LinearVelocity.Length(), body.LinearVelocity.Length()) + 4) + .1) &&
                angle <= Math.Max(.5, dt * (Math.Max(old.Body.AngularVelocity.Length(), body.AngularVelocity.Length()) + 4));
            float motion = continuous ? (distance + angle * definition.HalfExtent.Length()) / (float)dt : 0;
            double suppressUntil = continuous ? old.SuppressUntil : time + .05;
            bool suppressImpact = time < suppressUntil;
            if (touching)
                footprints.Add(new(body.Id, polygon.ToArray(), suppressImpact ? body : old.Body, body, definition.HalfExtent, suppressImpact, suppressImpact ? 0 : motion, time - dt, time, definition.IsBarrel));
            if (continuous && !old.Touching)
            {
                // Preserve the swept contour, including crossings completed between observations.
                int steps = Math.Clamp((int)Math.Ceiling((distance + angle * definition.HalfExtent.Length()) / .04f), 1, 128);
                List<Vector2[]> swept = [];
                for (int step = 1; step <= steps; step++)
                {
                    float t = step / (float)steps;
                    var sample = body with
                    {
                        Position = Vector3.Lerp(old.Body.Position, body.Position, t),
                        Rotation = Quaternion.Slerp(old.Body.Rotation, body.Rotation, t)
                    };
                    var hit = Footprint(sample, definition.HalfExtent, centre, radius, definition.IsBarrel);
                    if (hit.Count < 3)
                        continue;
                    // Keep up to sixteen sections across the sweep, not a random point
                    // or only the first tiny triangle of a rotating corner.
                    if (swept.Count < 16 && (swept.Count == 0 || step == steps || step % Math.Max(1, steps / 14) == 0))
                        swept.Add(hit.ToArray());
                }
                if (swept.Count > 0)
                {
                    Entered?.Invoke(swept[0].Aggregate(Vector2.Zero, (sum, p) => sum + p) / swept[0].Length);
                    if (impulses.Count == Capacity)
                        impulses.RemoveAt(0);
                    impulses.Add(new(swept.ToArray(), time, Math.Clamp(.012f + motion * .002f, .012f, .04f)));
                }
            }
            // Latch through sub-centimetre jitter, without enlarging the actual obstacle.
            bool latched = touching || (observed && old.Touching && Footprint(body, definition.HalfExtent + new Vector3(.02f), centre, radius, definition.IsBarrel).Count >= 3);
            propContacts[new(body.Id)] = new(body, latched, suppressUntil);
        }
        foreach (var id in propContacts.Keys.Where(id => !seen.Contains(id.Value)).ToArray())
            propContacts.Remove(id);
    }

    internal static List<Vector2> Footprint(BodyState body, Vector3 half, Vector3 centre, float radius, bool barrel = false)
    {
        var hull = PropGeometry.Build(half, barrel);
        Vector3[] corners = hull.Vertices.Select(p => Vector3.Transform(p, body.Rotation) + body.Position - centre).ToArray();
        List<Vector2> points = [];
        foreach (var (i, j) in hull.Edges)
        {
            var a = corners[i];
            var b = corners[j];
            if (Math.Abs(a.Z) < 1e-5f)
                points.Add(new(a.X, a.Y));
            if (Math.Abs(b.Z) < 1e-5f)
                points.Add(new(b.X, b.Y));
            if (a.Z * b.Z < 0)
            {
                var p = Vector3.Lerp(a, b, -a.Z / (b.Z - a.Z));
                points.Add(new(p.X, p.Y));
            }
        }
        points = points.Distinct().ToList();
        if (points.Count < 3)
            return [];
        var mean = points.Aggregate(Vector2.Zero, (a, b) => a + b) / points.Count;
        points = points.OrderBy(p => MathF.Atan2(p.Y - mean.Y, p.X - mean.X)).ToList();
        // Clip the convex cross-section against an inscribed 64-sided opening.
        for (int i = 0; i < 64 && points.Count > 0; i++)
        {
            float angle = (i + .5f) * MathF.Tau / 64;
            var n = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            float limit = (radius - .01f) * MathF.Cos(MathF.PI / 64);
            List<Vector2> clipped = [];
            var a = points[^1];
            float da = Vector2.Dot(a, n) - limit;
            foreach (var b in points)
            {
                float db = Vector2.Dot(b, n) - limit;
                if ((da <= 0) != (db <= 0))
                    clipped.Add(Vector2.Lerp(a, b, da / (da - db)));
                if (db <= 0)
                    clipped.Add(b);
                a = b;
                da = db;
            }
            points = clipped;
        }
        return points;
    }
}
