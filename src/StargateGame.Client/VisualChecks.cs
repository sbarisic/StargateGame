using System.Numerics;
using FishGfx.Graphics;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Client;

// Render-only, repeatable inspection cameras. These never alter authoritative player/body state.
static partial class VisualChecks
{
    public static SceneRenderer.SceneFrame TreeScaleReference(SceneRenderer.SceneFrame frame)
    {
        var person = new PlayerState(101, "Standing player", new Vector3(-2, 0, 2), default, 0, 0, true, 0);
        return frame with
        {
            Remotes = frame.Remotes.Append(person).ToArray()
        };
    }

    public static SceneRenderer.SceneFrame TreeOcclusion(SceneRenderer.SceneFrame frame)
    {
        var props = frame.Bodies.ToArray();
        // Same sight line as the crown: one crate in front and one behind it.
        for (int i = 0; i < Math.Min(2, props.Length); i++)
            props[i] = props[i] with
            {
                Position = i == 0 ? new Vector3(2.8f, 5.2f, 6) : new Vector3(-3, 5.2f, -6),
                Rotation = Quaternion.Identity
            };
        return frame with
        {
            Bodies = props
        };
    }

    public static SceneRenderer.SceneFrame HorizonPlayers(SceneRenderer.SceneFrame frame, StaticEntity gate, double time)
    {
        var ground = gate.Position + Vector3.UnitY * PlanetSettings.GateEmbedDepth;
        float z = 1.4f * (float)Math.Cos(time * Math.PI * .5);
        var local = frame.Local with
        {
            Position = ground + new Vector3(-.65f, 0, z)
        };
        var remote = new PlayerState(101, "Horizon crossing", ground + new Vector3(.8f, 0, -z), default, 0, 0, true, 0);
        return frame with
        {
            Local = local,
            Remotes = [remote]
        };
    }
    // Deliberately render-only fixtures for foreground/background depth checks.
    public static SceneRenderer.SceneFrame HorizonOcclusion(SceneRenderer.SceneFrame frame, StaticEntity gate)
    {
        var centre = EventHorizonState.Centre(gate);
        var front = new PlayerState(101, "Front", centre + new Vector3(-.8f, -.9f, .7f), default, 0, 0, true, 0);
        var rear = front with
        {
            Id = 102,
            Name = "Behind",
            Position = centre + new Vector3(.8f, -.9f, -.7f)
        };
        var props = frame.Bodies.ToArray();
        if (props.Length > 0)
            props[0] = props[0] with
            {
                Position = centre + new Vector3(.8f, -.9f, 1.2f),
                Rotation = Quaternion.Identity
            };
        return frame with
        {
            Remotes = frame.Remotes.Concat(new[] { front, rear }).ToArray(),
            Bodies = props
        };
    }
    public static SceneRenderer.SceneFrame HorizonProps(SceneRenderer.SceneFrame frame, StaticEntity gate, double time, string mode = "motion")
    {
        var centre = EventHorizonState.Centre(gate);
        var props = frame.Bodies.ToArray();
        bool clipped = mode is "clipped" or "rear-clipped";
        if (mode == "withdrawal" && time >= 10)
            return frame with
            {
                Bodies = []
            };
        for (int i = 0; i < props.Length; i++)
        {
            float z = i == 0 || mode != "motion" ? 0 : (float)Math.Sin(time * .8 + i) * 1.5f;
            props[i] = props[i] with
            {
                Position = centre + new Vector3((i - 1.5f) * .9f, -1, z),
                Rotation = i == 0 || mode == "stationary" || clipped ? Quaternion.Identity : Quaternion.CreateFromYawPitchRoll((float)time * .4f, 0, (float)time * .2f),
                LinearVelocity = new(0, 0, i == 0 || mode != "motion" ? 0 : (float)Math.Cos(time * .8 + i) * 1.2f),
                AngularVelocity = i == 0 || mode == "stationary" ? Vector3.Zero : new(0, .4f, .2f),
                Sleeping = i == 0 || mode == "stationary"
            };
        }
        var plane = GateFrame.From(gate);
        if (mode == "rear-clipped")
            plane = plane with
            {
                Rotation = Quaternion.Normalize(plane.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI))
            };
        return frame with
        {
            Bodies = props,
            Clips = clipped ? props.Select(p => new PropClip(p.Id, plane)).ToArray() : frame.Clips
        };
    }
    static (WorldChanges? Original, bool Open, WorldChanges Result) lightFixture;
    public static WorldChanges GateShadowFixture(WorldChanges original, StaticEntity gate, bool open)
    {
        if (ReferenceEquals(original, lightFixture.Original) && open == lightFixture.Open)
            return lightFixture.Result;
        var structure = new StructureRecord(Guid.Parse("86abff09-4a6b-4508-bfe2-a16ac86c5671"), gate.Position + new Vector3(-1.5f, PlanetSettings.GateEmbedDepth + .25f, 2.4f), 0);
        var foundation = new BuildingPiece(Guid.Parse("86abff09-4a6b-4508-bfe2-a16ac86c5672"), structure.Id, PieceKind.SquareFoundation, default, 0, 0, false, structure.Origin.Y - .5f);
        var doorway = new BuildingPiece(Guid.Parse("86abff09-4a6b-4508-bfe2-a16ac86c5673"), structure.Id, PieceKind.Doorway, default, 0, 0);
        var door = new BuildingPiece(Guid.Parse("86abff09-4a6b-4508-bfe2-a16ac86c5674"), structure.Id, PieceKind.Door, default, 0, 0, open);
        var result = original with
        {
            Structures = [.. original.Structures, structure],
            Pieces = [.. original.Pieces, foundation, doorway, door]
        };
        lightFixture = (original, open, result);
        return result;
    }
    static (WorldChanges? State, bool Roof, WorldChanges Result) winterFixture;
    public static Vector3 WinterRoom(StaticEntity gate) => gate.Position + new Vector3(6, PlanetSettings.GateEmbedDepth + .2f, 4);
    public static WorldChanges WeatherFixture(WorldChanges original, StaticEntity gate, bool roof)
    {
        if (ReferenceEquals(original, winterFixture.State) && roof == winterFixture.Roof)
            return winterFixture.Result;
        var s = new StructureRecord(Guid.Parse("798105bd-fd0c-4dca-ac7d-aad5edac143a"), WinterRoom(gate), 0);
        var f = new BuildingPiece(Guid.NewGuid(), s.Id, PieceKind.SquareFoundation, default, 0, 0, false, s.Origin.Y - .6f);
        List<BuildingPiece> pieces = [f];
        for (int i = 0; i < 4; i++)
        {
            var edge = StargateGame.Building.BuildingGeometry.Edge(f, i);
            pieces.Add(new(Guid.NewGuid(), s.Id, i == 0 ? PieceKind.Doorway : PieceKind.Wall, edge.A, edge.Rotation, 0));
        }
        if (roof)
            pieces.Add(new(Guid.NewGuid(), s.Id, PieceKind.SquareFloor, default, 0, 1));
        var result = original with
        {
            Structures = [.. original.Structures, s],
            Pieces = [.. original.Pieces, .. pieces]
        };
        winterFixture = (original, roof, result);
        return result;
    }
    static (string Key, int Count, Vector3 Target) cached;
    static (WorldChanges? State, Vector3 Target, Vector3 Offset) cachedBuilding;
    static (string Key, int Count, RockInstance? Rock) cachedRock;
    static (string Key, int Count, TreeInstance? Tree) cachedTree;
    public static void Camera(Camera camera, ClientSession session, string view, double time, bool orbit, bool traverse = false, float? hour = null)
    {
        if(view.StartsWith("region:",StringComparison.Ordinal))
        {
            var v=view[7..].Split(',').Select(s=>float.Parse(s,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if(v.Length!=6 || v.Any(f => !float.IsFinite(f)) || new Vector3(v[0]-v[3],v[1]-v[4],v[2]-v[5]).LengthSquared() < .0001f)throw new ArgumentException("region view requires eye xyz and target xyz.");
            camera.Position=new(v[0],v[1],v[2]);camera.LookAt(new(v[3],v[4],v[5]));return;
        }
        if (view == "player")
            return;
        if (view.StartsWith("water-", StringComparison.Ordinal))
        {
            WaterCamera(camera, session, view);
            return;
        }
        if (view.StartsWith("tree-forest-", StringComparison.Ordinal))
        {
            var forestTarget = new Vector3(0, 8, -45);
            camera.Position = new(48, 6, -1);
            if (view == "tree-forest-above")
                camera.Position = new(55, 70, 15);
            if (view == "tree-forest-far")
                camera.Position = new(100, 35, 110);
            if (view == "tree-forest-horizon")
                camera.Position = new(260, 80, 340);
            if (view is "tree-forest-inside" or "tree-forest-walk")
            {
                float z = view == "tree-forest-walk" ? -78 + Math.Clamp((float)time - 20, 0, 20) * 2.7f : -58;
                camera.Position = new(PrototypeForest.PathX(z), 1.7f, z);
                forestTarget = new(PrototypeForest.PathX(z + 10), 2.2f, z + 10);
            }
            camera.LookAt(forestTarget);
            return;
        }
        if (view.StartsWith("tree-prototype", StringComparison.Ordinal))
        {
            float scale = SceneRenderer.TreePrototypeScale;
            float distance = view.EndsWith("-far", StringComparison.Ordinal) ? 65 : 14 * scale;
            camera.Position = new(distance * .5f, 4 * scale, distance);
            if (view == "tree-prototype-below")
                camera.Position = new(4 * scale, 1.7f, 11 * scale);
            if (view == "tree-prototype-above")
                camera.Position = new Vector3(7, 13, 14) * scale;
            if (view == "tree-prototype-back")
                camera.Position = new Vector3(-7, 4, -14) * scale;
            if (view == "tree-prototype-wind")
                camera.Position = new Vector3(12 * MathF.Sin((float)time * .2f), 4, 14) * scale;
            if (view == "tree-prototype-scale")
                camera.Position = new(12, 3, 24);
            camera.LookAt(new(0, 5 * scale, 0));
            return;
        }
        if (view.StartsWith("forest-", StringComparison.Ordinal) && GraphicsFixture.Forest(session.World.Definition))
        {
            Vector3 eye, forestTarget;
            if (view == "forest-spawn")
            {
                eye = new(0, Movement.EyeHeight, 12);
                forestTarget = new(0, 3, -15);
            }
            else if (view == "forest-gate-a")
            {
                eye = new(-11, Movement.EyeHeight, 9);
                forestTarget = new(-6, 2, -5);
            }
            else if (view == "forest-gate-b")
            {
                eye = new(12, Movement.EyeHeight, 10);
                forestTarget = new(6, 2, -6);
            }
            else if (view == "forest-edge")
            {
                eye = new(-28, Movement.EyeHeight, -23);
                forestTarget = new(-46, 4, -51);
            }
            else if (view == "forest-inside")
            {
                eye = new(-GraphicsFixture.ForestPathX(-65), Movement.EyeHeight, -65);
                forestTarget = eye + new Vector3(15, 5, -14);
            }
            else if (view == "forest-walk")
            {
                // Keep this camera-only inspection within the client's prepared near cover.
                float t = Math.Clamp((float)(time - 12) / 20, 0, 1), z = float.Lerp(-18, -46, t);
                eye = new(-GraphicsFixture.ForestPathX(z), Movement.EyeHeight, z);
                forestTarget = new(-GraphicsFixture.ForestPathX(z - 8) + 3 * MathF.Sin(t * MathF.PI), Movement.EyeHeight + .5f, z - 8);
            }
            else
                throw new ArgumentException("Unknown forest inspection view.");
            camera.Position = eye;
            camera.LookAt(forestTarget);
            return;
        }
        if (view == "gate-pair")
        {
            camera.Position = new(0, 3.8f, 18);
            camera.LookAt(new(0, 2, 0));
            return;
        }
        if (view == "gate-weapon-edge")
        {
            var g = GateFrame.From(session.World.Definition.Entities.First(e => e.Model == "stargate"));
            camera.Position = g.World(new(.5f, 0, 2));
            camera.LookAt(g.World(new(2.1f, 1.5f, 0)));
            return;
        }
        if (view == "gate-weapon")
        {
            var g = GateFrame.From(session.World.Definition.Entities.First(e => e.Model == "stargate"));
            camera.Position = g.World(new(1.6f, -.35f, 1.3f));
            camera.LookAt(g.World(new(1.15f, -.55f, 0)));
            return;
        }
        if (view.StartsWith("shadow-rotation"))
        {
            var g = session.World.Definition.Entities.First(e => e.Model == "stargate").Position;
            bool lab = session.World.Definition.Fixture == "graphics-lab";
            camera.Position = lab ? new Vector3(12, 3, 12) : g + new Vector3(0, 2, -5);
            float yaw = (lab ? 2.5f : 0) + (view.EndsWith("left") ? -.45f : view.EndsWith("right") ? .45f : (float)Math.Sin(time * .65) * .5f);
            camera.LookAt(camera.Position + new Vector3(MathF.Sin(yaw), -.10f, -MathF.Cos(yaw)) * 20);
            return;
        }
        if (view == "weapon-wall")
        {
            camera.Position = new(8, Movement.EyeHeight, 4.6f);
            camera.LookAt(new(8, .65f, 3));
            return;
        }
        var gate = session.World.Definition.Entities.First(e => e.Model == "stargate").Position;
        float ground = gate.Y + PlanetSettings.GateEmbedDepth;
        Vector3 target = new(gate.X, ground + 2.2f, gate.Z), offset = new(-7, 1, 14);
        if (view.StartsWith("horizon-"))
        {
            target = EventHorizonState.Centre(session.World.Definition.Entities.First(e => e.Model == "stargate"));
            offset = view switch
            {
                "horizon-side" => new(13, 1, 3),
                "horizon-wide" => new(8, 1, 12),
                "horizon-front" => new(0, 0, 8),
                "horizon-close" => new(0, 0, 1.1f),
                "horizon-oblique" => new(4, .2f, 2),
                "horizon-45" => new(5.657f, 0, 5.657f),
                "horizon-70" => new(7.518f, 0, 2.736f),
                "horizon-shadow" => new(5, 1.4f, 8),
                "horizon-rear" => new(0, 0, -8),
                "horizon-cross" => new(0, 0, (float)Math.Sin(time * .8) * 2.5f),
                _ => throw new ArgumentException("Unknown horizon inspection view.")
            };
            if (view is "horizon-side" or "horizon-wide")
                target += Vector3.UnitZ * 2;
            if (offset.LengthSquared() < .0001f)
                offset.Z = .01f;
        }
        else if (view == "gate-ground")
        {
            target = new(gate.X + 1, ground + .1f, gate.Z + 1.6f);
            offset = new(-.8f, 1.6f, 2.5f);
        }
        else if (view.StartsWith("character-", StringComparison.Ordinal))
        {
            target = new(-4, 1.1f, -4);
            offset = view switch
            {
                "character-front" => new(0, .1f, -2.5f),
                "character-side" => new(2.5f, .1f, 0),
                "character-hands" => new(.65f, .25f, -1.3f),
                _ => new(0, .1f, 2.5f)
            };
        }
        // Fixed seed-7319 regression view: keep the camera at the reported bank
        // even when a corrected generator moves the automatic cliff selection.
        else if (view == "river-bank")
        {
            target = new(96, 26.735f, -251);
            offset = new(-15, 7, 14);
        }
        else if (view == "weather-shelter")
        {
            var room = WinterRoom(session.World.Definition.Entities.First(e => e.Model == "stargate"));
            target = room + new Vector3(1.5f, 1.35f, -2);
            offset = new(0, .1f, 3.5f);
        }
        else if (view is "grass" or "grass-standing" or "grass-low" or "grass-distant" or "snow")
        {
            string key = session.World.Definition.Id + ":" + view;
            int count = session.Terrain?.Count ?? 0;
            if (cached.Key == key && cached.Count == count)
            {
                target = cached.Target;
                offset = new(0, .6f, 3);
            }
            else if (session.Terrain is not null)
            {
                var points = session.Terrain.Chunks.SelectMany(c => Enumerable.Range(0, 64).Where(x => x % 4 == 0).SelectMany(x => Enumerable.Range(0, 64).Where(z => z % 4 == 0).Select(z => (Chunk: c, X: x, Z: z))))
                    .Where(p => !float.IsFinite(p.Chunk.WaterHeight(p.X, p.Z)) && p.Chunk.Normal(p.X, p.Z).Y > .93f)
                    .Select(p => p.Chunk.Point(p.X, p.Z)).Where(p => Vector3.Distance(p, gate) > 22 && Vector3.Distance(p, gate) < 60)
                    .OrderBy(p => Vector3.DistanceSquared(p, gate + new Vector3(-26, 0, 20))).ToArray();
                if (points.Length > 0)
                {
                    target = points[0] + new Vector3(0, .25f, 0);
                    offset = new(0, .6f, 3);
                    cached = (key, count, target);
                }
            }
            if (view == "grass-standing") offset = new(0, 1.55f, 3);
            else if (view == "grass-low") offset = new(0, .08f, 3);
            else if (view == "grass-distant") offset = new(0, 8, 48);
        }
        else if (view.StartsWith("settlement") && session.World.Definition.Settlements is { Plots.Length: > 0 } settlement)
        {
            var p = settlement.Plots[0];
            var t = SettlementTemplates.Get(p.Template);
            target = p.Position + new Vector3(0, 1.5f, 0);
            if (view == "settlement-above")
            {
                var site = settlement.Sites[0];
                var plots = settlement.Plots.Where(p => p.Site == site.Id).ToArray();
                float span = Math.Max(60, plots.Max(p => Vector2.Distance(new(p.Position.X, p.Position.Z), new(site.Centre.X, site.Centre.Z))) * 2);
                target = site.Centre;
                offset = new Vector3(-.35f, .85f, -.55f) * span;
            }
            else if (view == "settlement-interior")
            {
                offset = new(0, 0, .8f);
                target += new Vector3(0, 0, -1);
            }
            else
            {
                offset = Vector3.Transform(new Vector3(-.7f, .2f, -1.3f) * Math.Max(t.Width, t.Depth), Matrix4x4.CreateRotationY(-p.Rotation * MathF.PI / 6));
                offset.Y = Math.Max(1, t.Height * .2f);
            }
        }
        else if (view == "buildings" && session.Changes.Pieces.Length > 0)
        {
            if (!ReferenceEquals(cachedBuilding.State, session.Changes))
            {
                var points = session.Changes.Pieces.SelectMany(p => StargateGame.Building.BuildingGeometry.Mesh(session.Changes, p)).SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
                var min = points.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min);
                var max = points.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max);
                cachedBuilding = (session.Changes, (min + max) / 2, new Vector3(-.85f, .7f, -1.15f) * Math.Max(15, Math.Max(max.X - min.X, max.Z - min.Z)));
            }
            target = cachedBuilding.Target;
            offset = cachedBuilding.Offset;
        }
        else if (view == "forest")
        {
            string key = session.World.Definition.Id + ":" + view;
            int marker = (session.Terrain?.Count ?? 0) * 257 + session.FarScene.Count;
            if (cached.Key != key || cached.Count != marker)
            {
                var candidates = new Dictionary<ChunkCoordinate, (int Trees, float Height)>();
                foreach (var tile in session.FarScene.Values)
                    candidates[tile.Coordinate] = (tile.Trees.Length, tile.CentreHeight);
                if (session.Terrain is { } terrain)
                    foreach (var c in terrain.Chunks)
                        candidates[c.Coordinate] = (TreeScenery.Resolve(c, session.World.Definition.Preset, session.World.Definition.Fixture).Count, c.Sample(c.Coordinate.X * 64 + 32, c.Coordinate.Z * 64 + 32));
                var stand = candidates.OrderByDescending(p => p.Value.Trees).ThenBy(p => p.Key).FirstOrDefault();
                if (stand.Value.Trees > 0)
                    cached = (key, marker, new Vector3(stand.Key.X * 64 + 32, stand.Value.Height + 6, stand.Key.Z * 64 + 32));
            }
            if (cached.Key == key)
                target = cached.Target;
            offset = new(35, 16, 42);
        }
        else if (view == "trees" || view.StartsWith("tree:", StringComparison.Ordinal))
        {
            string? selectedTree = view.StartsWith("tree:", StringComparison.Ordinal) ? view[5..] : null;
            string key = session.World.Definition.Id + ":" + view;
            int count = session.Terrain?.Count ?? 0;
            if (cachedTree.Key != key || cachedTree.Count != count)
                cachedTree = (key, count, session.Terrain?.Chunks.SelectMany(c => TreeScenery.Resolve(c, session.World.Definition.Preset, session.World.Definition.Fixture))
                    .Where(t => view == "trees" || t.Model == selectedTree)
                    .OrderBy(t => t.Model == "Leave-Tree-01a" || t.Model.StartsWith("BlueSpruce") ? 0 : 1)
                    .ThenBy(t => Vector3.DistanceSquared(t.Transform.Translation, gate)).ThenBy(t => t.Id).FirstOrDefault());
            if (cachedTree.Tree is { } tree)
            {
                float height = tree.Max.Y - tree.Min.Y;
                target = (tree.Min + tree.Max) / 2;
                offset = new Vector3(-.45f, .6f, 1.65f) * Math.Max(3, height);
            }
        }
        else if (view == "rocks")
        {
            string key = session.World.Definition.Id;
            int count = session.Terrain?.Count ?? 0;
            if (cachedRock.Key != key || cachedRock.Count != count)
                cachedRock = (key, count, session.Terrain?.Chunks.SelectMany(c => RockScenery.Resolve(c, session.World.Definition.Preset))
                .Where(r => r.Collidable && Vector3.DistanceSquared((r.Min + r.Max) / 2, gate) < 160 * 160)
                .OrderByDescending(r => r.Max.Y - r.Min.Y).FirstOrDefault());
            var rock = cachedRock.Rock;
            if (rock is not null)
            {
                target = (rock.Min + rock.Max) / 2;
                float span = Math.Max(rock.Max.X - rock.Min.X, rock.Max.Z - rock.Min.Z);
                offset = new Vector3(-1, .65f, 1.4f) * Math.Max(2, span * 1.1f);
            }
        }
        else if (view is "river" or "cliff")
        {
            string key = session.World.Definition.Id + ":" + view;
            int count = session.Terrain?.Chunks.Count() ?? 0;
            if (cached.Key == key && cached.Count == count)
                target = cached.Target;
            else
            {
                float best = float.NegativeInfinity;
                if (session.Terrain is not null)
                    foreach (var chunk in session.Terrain.Chunks)
                        for (int z = 2; z < 63; z += 3)
                            for (int x = 2; x < 63; x += 3)
                            {
                                var point = chunk.Point(x, z);
                                float distance = Vector2.Distance(new(point.X, point.Z), new(gate.X, gate.Z));
                                if (distance > 160 || distance < 20)
                                    continue;
                                float score = view == "river" ? chunk.WaterHeight(x, z) - point.Y : (1 - chunk.Normal(x, z).Y);
                                if (!float.IsFinite(score) || score < .04f)
                                    continue;
                                score -= distance * .0003f;
                                if (score <= best)
                                    continue;
                                best = score;
                                target = point + Vector3.UnitY * .2f;
                            }
                cached = (key, count, target);
            }
            offset = view == "river" ? new(4, 2.4f, 6) : new(-15, 7, 14);
        }
        else if (view == "sun" || view == "galaxy")
        {
            var definition = session.World.Definition;
            var environment = definition.Environment ?? PlanetEnvironmentDefinition.Create(definition.LocationId, definition.Seed, definition.Preset);
            var direction = view == "sun" ? environment.Sun(hour is { } h ? h / 24 : environment.Phase(session.EnvironmentSeconds)) : Vector3.Normalize(new Vector3(0, .6f, -.76f));
            if (Math.Abs(direction.Y) > .98f)
                direction = Vector3.Normalize(direction + new Vector3(.2f, 0, 0));
            target = gate + new Vector3(0, 15, 45) + direction * 20;
            offset = -direction * 20;
        }
        else if (view == "sky")
        {
            target = gate + new Vector3(0, 5, 0);
            offset = new(0, -2, 15);
        }
        else if (view is "lake" or "underwater")
        {
            string key = session.World.Definition.Id + ":" + view;
            int count = session.Terrain?.Count ?? 0;
            if (cached.Key == key && cached.Count == count)
                target = cached.Target;
            else
            {
                var wet = session.Terrain?.Chunks.SelectMany(c => Enumerable.Range(0, 64).Where(x => x % 4 == 0).SelectMany(x => Enumerable.Range(0, 64).Where(z => z % 4 == 0).Select(z => (Point: c.Point(x, z), Water: c.WaterHeight(x, z)))))
                    .Where(p => float.IsFinite(p.Water)).OrderByDescending(p => p.Water - p.Point.Y).FirstOrDefault();
                if (wet is { } p && p.Water - p.Point.Y > 1)
                    target = new(p.Point.X, p.Water, p.Point.Z);
                cached = (key, count, target);
            }
            offset = view == "underwater" ? new(0, -1.5f, 3) : new(12, 8, 16);
        }
        else if (view == "props")
        {
            target = session.World.Definition.Props.Select(p => p.Position).Aggregate(Vector3.Zero, (a, b) => a + b) / 4 + Vector3.UnitY * .3f;
            offset = new(-4, 2.8f, 7);
        }
        else if (view == "wide")
        {
            target.Y -= 1;
            offset = new(-25, 16, 32);
        }
        else if (view == "dhd-close")
        {
            target = session.World.Definition.Entities.First(e => e.Model == "dhd").Position + Vector3.UnitY * .95f;
            offset = new(0, 1.2f, 1.5f);
        }
        else if (view == "dhd-hover")
        {
            var centre = session.World.Definition.Entities.First(e => e.Model == "dhd").Position;
            target = centre + new Vector3((float)Math.Sin(time * 1.7) * .78f, .95f, (float)Math.Cos(time * 1.1) * .65f);
            offset = centre + new Vector3(0, 2.2f, 1.1f) - target;
        }
        else if (view == "dhd")
        {
            target = session.World.Definition.Entities.First(e => e.Model == "dhd").Position + Vector3.UnitY * .6f;
            offset = new(-2, 1.4f, 3);
        }
        else if (view != "gate")
            throw new ArgumentException("--view must be player, gate, dhd, river, cliff, or wide.");
        if (orbit)
            offset = Vector3.Transform(offset, Matrix4x4.CreateRotationY((float)Math.Sin(time * .3) * .4f));
        camera.Position = target + offset;
        if (traverse)
        {
            camera.Position = target + new Vector3((float)Math.Sin(time * .65) * 30, 3, (float)Math.Cos(time * .65) * 30);
            if (session.Terrain is not null && session.Terrain.TryGetChunk(ChunkCoordinate.At(camera.Position.X, camera.Position.Z), out var groundChunk))
                camera.Position = new(camera.Position.X, groundChunk.Sample(camera.Position.X, camera.Position.Z) + 1.6f, camera.Position.Z);
            target = new(target.X, camera.Position.Y - .1f, target.Z);
        }
        camera.LookAt(target);
    }
}
