using System.Numerics;
using Marbots.Abstractions;
using ThreeNet;

namespace Marbots.Desktop.Office;

/// <summary>
/// The 3D office rendered with Three.Net: floor, rooms and furniture (Rodin models), one animated robot per bot
/// (rigged and animated in Blender) and delegation beams. It only reads <see cref="OfficeDirector"/>.
/// </summary>
public sealed class OfficeScene : IDisposable
{
    private const float Deg = MathF.PI / 180f;

    private readonly Scene _scene = new();
    private readonly PropLibrary _props;
    private readonly Dictionary<string, RobotActor> _actors = [];
    private readonly List<Node> _beams = [];
    private readonly Node _deskRow;
    private readonly Geometry _box;
    private readonly Material _beamMaterial;
    private int _deskCount = -1;

    public OfficeScene()
    {
        _props = new PropLibrary(_scene);
        _box = _scene.CreateBoxGeometry();
        _beamMaterial = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.45f, 0.55f, 1f, 1f), 0f, 0.4f) with
        {
            Emissive = new Vector3(0.45f, 0.55f, 1f),
            EmissiveIntensity = 3f,
        });
        _scene.Environment = _scene.Environment with
        {
            Background = new Vector4(0.80f, 0.83f, 0.90f, 1f),
            AmbientColor = new Vector3(0.80f, 0.84f, 0.95f),
            AmbientIntensity = 0.55f,
        };
        var sun = _scene.AddLight(Light.Directional(new Vector3(1f, 0.97f, 0.92f), 2.6f) with { CastShadow = true, ShadowNormalBias = 2f }, name: "sun");
        sun.Position = new Vector3(-12f, 22f, 14f);
        sun.LookAt(Vector3.Zero);
        var fill = _scene.AddLight(Light.Directional(new Vector3(0.6f, 0.7f, 1f), 0.6f), name: "fill");
        fill.Position = new Vector3(14f, 10f, -10f);
        fill.LookAt(Vector3.Zero);
        Camera = _scene.AddCamera(ThreeNet.Camera.Perspective(42f * Deg, 0.2f, 200f));
        BuildRoom();
        _deskRow = _scene.CreateNode(null, "desks");
    }

    public Scene Scene => _scene;
    public Node Camera { get; }

    /// <summary>The camera's vertical field of view, for projecting name tags.</summary>
    public static float FieldOfView => 42f * Deg;

    /// <summary>Whether the rigged robot model was found (otherwise simple stand-ins are used).</summary>
    public bool HasRobotModel => File.Exists(PropLibrary.AssetPath("robot.glb"));

    public IEnumerable<(string BotId, Node Node)> Robots => _actors.Select(a => (a.Key, a.Value.Root));

    /// <summary>Creates, updates and removes robots and desks to match the director, then advances animation.</summary>
    public void Update(OfficeDirector director, IReadOnlyDictionary<string, BotDefinition> bots, float dt)
    {
        var workers = director.Agents.Count(a => a.Id != WellKnown.BossManId);
        if (workers != _deskCount) BuildDesks(workers);

        foreach (var gone in _actors.Keys.Where(id => director.Find(id) is null).ToList())
        {
            _actors[gone].Dispose();
            _actors.Remove(gone);
        }
        foreach (var agent in director.Agents)
        {
            if (!_actors.TryGetValue(agent.Id, out var actor))
            {
                var color = bots.TryGetValue(agent.Id, out var b) ? ParseColor(b.Color) : new Vector3(0.3f, 0.4f, 0.9f);
                _actors[agent.Id] = actor = new RobotActor(_scene, agent.Id, color, agent.Id == WellKnown.BossManId);
            }
            actor.Apply(agent, dt);
        }
        _scene.UpdateAnimations(dt);
        UpdateBeams(director);
    }

    private void UpdateBeams(OfficeDirector director)
    {
        var i = 0;
        foreach (var link in director.Links)
        {
            if (director.Find(link.From) is not { } from || director.Find(link.To) is not { } to) continue;
            if (i == _beams.Count) _beams.Add(_scene.AddMesh(_box, _beamMaterial, name: "beam"));
            var a = new Vector3(from.Position.X, 1.25f, from.Position.Y);
            var c = new Vector3(to.Position.X, 1.25f, to.Position.Y);
            var d = c - a;
            var beam = _beams[i++];
            beam.Visible = true;
            beam.Position = (a + c) / 2;
            beam.Scale = new Vector3(0.05f, 0.05f, MathF.Max(d.Length(), 0.01f));
            beam.EulerAngles = new Vector3(0f, MathF.Atan2(d.X, d.Z), 0f);
        }
        for (; i < _beams.Count; i++) _beams[i].Visible = false;
    }

    private void BuildDesks(int workers)
    {
        foreach (var child in _deskRow.Children.ToList()) child.Remove();
        _deskCount = workers;
        for (var i = 0; i < workers; i++)
        {
            var p = OfficeLayout.Desk(i, workers);
            // The bot stands on the +Z side of its desk, facing the monitor.
            if (_props.Place("desk", _deskRow, new Vector3(p.X, 0f, p.Y - 0.85f), 0f, 1.5f) is null)
                Box(_deskRow, new Vector3(p.X, 0.37f, p.Y - 0.85f), new Vector3(1.5f, 0.74f, 0.8f), new Vector3(0.72f, 0.6f, 0.45f));
        }
    }

    private void BuildRoom()
    {
        var w = OfficeLayout.Width;
        var d = OfficeLayout.Depth;
        var floorOptions = MaterialOptions.Pbr(new Vector4(0.92f, 0.88f, 0.82f, 1f), 0f, 0.8f);
        var floorTexture = PropLibrary.AssetPath("floor.jpg");
        if (File.Exists(floorTexture)) floorOptions = floorOptions with { BaseColorMap = _scene.LoadTexture(floorTexture), UvScale = new Vector2(7f, 5f) };
        var floor = _scene.AddMesh(_scene.CreatePlaneGeometry(w, d), _scene.CreateMaterial(floorOptions), name: "floor");
        floor.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);

        var wall = new Vector3(0.93f, 0.93f, 0.96f);
        Box(null, new Vector3(0f, 1.6f, -d / 2 - 0.1f), new Vector3(w + 0.4f, 3.2f, 0.2f), wall);
        Box(null, new Vector3(-w / 2 - 0.1f, 1.6f, 0f), new Vector3(0.2f, 3.2f, d), wall);
        Box(null, new Vector3(w / 2 + 0.1f, 1.6f, 0f), new Vector3(0.2f, 3.2f, d), wall);
        Box(null, new Vector3(0f, 0.08f, -d / 2 + 0.02f), new Vector3(w, 0.16f, 0.06f), new Vector3(0.3f, 0.32f, 0.45f));

        foreach (var zone in OfficeLayout.Zones)
        {
            var rug = _scene.AddMesh(_scene.CreatePlaneGeometry(zone.Size.X, zone.Size.Y),
                _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(Vector3.Lerp(zone.Color, Vector3.One, 0.55f), 1f), 0f, 0.95f)), name: "rug-" + zone.Id);
            rug.EulerAngles = new Vector3(-MathF.PI / 2f, 0f, 0f);
            rug.Position = new Vector3(zone.Center.X, 0.01f, zone.Center.Y);
        }
        // Low glass partitions between the back stations.
        for (var i = 0; i < 3; i++)
        {
            var x = -7f + i * 7f;
            Box(null, new Vector3(x, 0.6f, -7.5f), new Vector3(0.06f, 1.2f, 5f), new Vector3(0.75f, 0.85f, 0.95f));
        }

        Furnish();
        Posters(d);
    }

    private void Furnish()
    {
        Vector3 At(string zone, float dx, float dz) { var c = OfficeLayout.Zone(zone).Center; return new Vector3(c.X + dx, 0f, c.Y + dz); }

        // Research desk: two workstations and a whiteboard.
        _props.Place("desk", null, At("web", -1.4f, -1.3f), 0f, 1.5f);
        _props.Place("desk", null, At("web", 1.4f, -1.3f), 0f, 1.5f);
        _props.Place("plant", null, At("web", -2.8f, 1.6f), 0.4f, 0.8f);

        // Library: shelves along the back wall.
        var palette = new[] { new Vector3(0.85f, 0.35f, 0.3f), new Vector3(0.3f, 0.45f, 0.85f), new Vector3(0.95f, 0.75f, 0.3f), new Vector3(0.35f, 0.7f, 0.5f), new Vector3(0.6f, 0.4f, 0.75f) };
        for (var s = 0; s < 3; s++)
        {
            var x = OfficeLayout.Zone("library").Center.X - 2f + s * 2f;
            Box(null, new Vector3(x, 1.1f, -9.7f), new Vector3(1.8f, 2.2f, 0.45f), new Vector3(0.55f, 0.42f, 0.3f));
            for (var shelf = 0; shelf < 4; shelf++)
                for (var b = 0; b < 7; b++)
                {
                    var h = 0.32f + (b * 7 + shelf * 3 + s) % 4 * 0.03f;
                    Box(null, new Vector3(x - 0.72f + b * 0.24f, 0.25f + shelf * 0.52f + h / 2, -9.5f), new Vector3(0.18f, h, 0.3f), palette[(b + shelf + s) % palette.Length]);
                }
        }

        // Workshop: a workbench with a terminal glow.
        Box(null, At("workshop", 0f, -1.4f) + new Vector3(0f, 0.45f, 0f), new Vector3(3.2f, 0.9f, 1f), new Vector3(0.45f, 0.47f, 0.52f));
        Glow(At("workshop", -0.8f, -1.6f) + new Vector3(0f, 1.15f, 0f), new Vector3(0.8f, 0.5f, 0.05f), new Vector3(0.95f, 0.6f, 0.2f));
        Glow(At("workshop", 0.8f, -1.6f) + new Vector3(0f, 1.15f, 0f), new Vector3(0.8f, 0.5f, 0.05f), new Vector3(0.3f, 0.95f, 0.5f));

        // Tool room: server racks with status lights.
        for (var r = 0; r < 4; r++)
        {
            var p = At("mcp", -2.25f + r * 1.5f, -1.6f);
            Box(null, p + new Vector3(0f, 1.0f, 0f), new Vector3(1.0f, 2.0f, 0.9f), new Vector3(0.12f, 0.13f, 0.17f));
            for (var led = 0; led < 6; led++)
                Glow(p + new Vector3(-0.3f + led % 3 * 0.3f, 0.5f + led / 3 * 0.9f, 0.46f), new Vector3(0.12f, 0.05f, 0.02f), led % 2 == 0 ? new Vector3(0.2f, 0.95f, 0.75f) : new Vector3(0.3f, 0.6f, 1f));
        }

        // Meeting room: table, whiteboard and plants.
        Box(null, At("meeting", 0f, 0.3f) + new Vector3(0f, 0.72f, 0f), new Vector3(4.2f, 0.08f, 1.6f), new Vector3(0.95f, 0.95f, 0.97f));
        Box(null, At("meeting", 0f, 0.3f) + new Vector3(0f, 0.36f, 0f), new Vector3(0.3f, 0.72f, 0.3f), new Vector3(0.3f, 0.3f, 0.35f));
        for (var c = 0; c < 3; c++)
        {
            _props.Place("chair", null, At("meeting", -1.4f + c * 1.4f, -0.9f), 0f, 0.7f);
            _props.Place("chair", null, At("meeting", -1.4f + c * 1.4f, 1.6f), MathF.PI, 0.7f);
        }
        _props.Place("whiteboard", null, At("meeting", -3.6f, 0.2f), MathF.PI / 2, 1.5f);

        // Manager's office: executive desk (screens towards the manager) and a plant.
        _props.Place("bossdesk", null, At("manager", 0f, 0.5f), MathF.PI, 2.2f);
        _props.Place("plant", null, At("manager", 3.2f, 1.8f), 1.2f, 0.9f);
        _props.Place("plant", null, At("manager", -3.2f, 1.8f), 2.2f, 0.9f);

        // Approval desk and waiting sofa; the coffee corner next to it.
        _props.Place("desk", null, At("approval", 0f, 1.6f), MathF.PI, 1.6f);
        _props.Place("sofa", null, At("approval", -2.6f, -1.6f), 0f, 2f);
        _props.Place("coffee", null, new Vector3(OfficeLayout.Width / 2 - 0.9f, 0f, 0.5f), -MathF.PI / 2, 1.5f);
        _props.Place("plant", null, new Vector3(-OfficeLayout.Width / 2 + 0.8f, 0f, -0.5f), 0f, 0.9f);
        _props.Place("plant", null, new Vector3(OfficeLayout.Width / 2 - 0.8f, 0f, 3.5f), 0.7f, 0.9f);
    }

    private void Posters(float depth)
    {
        var frame = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.15f, 0.15f, 0.18f, 1f), 0f, 0.6f));
        (string File, float X, float W, float H)[] posters = [("poster1.jpg", -10.5f, 1.5f, 2f), ("poster3.jpg", 3.5f, 2.4f, 1.8f), ("poster2.jpg", 10.5f, 1.5f, 2f)];
        foreach (var (file, x, w, h) in posters)
        {
            var path = PropLibrary.AssetPath(file);
            if (!File.Exists(path)) continue;
            var y = 2.0f;
            var back = _scene.AddMesh(_box, frame, name: "frame");
            back.Position = new Vector3(x, y, -depth / 2 + 0.02f);
            back.Scale = new Vector3(w + 0.1f, h + 0.1f, 0.04f);
            var art = _scene.AddMesh(_scene.CreatePlaneGeometry(w, h),
                _scene.CreateMaterial(MaterialOptions.Pbr(Vector4.One, 0f, 0.7f) with { BaseColorMap = _scene.LoadTexture(path) }), name: "poster");
            art.Position = new Vector3(x, y, -depth / 2 + 0.05f);
        }
    }

    private Node Box(Node? parent, Vector3 center, Vector3 size, Vector3 color)
    {
        var node = _scene.AddMesh(_box, _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(color, 1f), 0f, 0.7f)), parent, "box");
        node.Position = center;
        node.Scale = size;
        return node;
    }

    private void Glow(Vector3 center, Vector3 size, Vector3 color)
    {
        var node = _scene.AddMesh(_box, _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(color, 1f), 0f, 0.4f) with { Emissive = color, EmissiveIntensity = 2.5f }), name: "glow");
        node.Position = center;
        node.Scale = size;
    }

    internal static Vector3 ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return new Vector3(0.3f, 0.4f, 0.9f);
        return new Vector3((v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    public void Dispose()
    {
        foreach (var a in _actors.Values) a.Dispose();
        _scene.Dispose();
    }
}

/// <summary>
/// Static props: each GLB is imported once into a hidden prototype and placed with <see cref="Node.Clone"/>, so copies
/// share GPU buffers. Missing assets return null and callers fall back to primitives.
/// </summary>
internal sealed class PropLibrary(Scene scene)
{
    private readonly Node _prototypes = CreateHidden(scene);
    private readonly Dictionary<string, (Node Root, Vector3 Size)?> _cache = [];

    public static string AssetPath(string file) => Path.Combine(AppContext.BaseDirectory, "Assets", "Office", file);

    private static Node CreateHidden(Scene scene)
    {
        var n = scene.CreateNode(null, "prototypes");
        n.Visible = false;
        return n;
    }

    /// <summary>Places <paramref name="name"/> with its widest side <paramref name="length"/> metres, on the floor.</summary>
    public Node? Place(string name, Node? parent, Vector3 position, float yaw, float length)
    {
        if (!_cache.TryGetValue(name, out var proto))
        {
            proto = null;
            var path = AssetPath(name + ".glb");
            if (File.Exists(path))
            {
                try
                {
                    var holder = scene.CreateNode(_prototypes, name);
                    var import = scene.LoadGltf(path, holder);
                    var b = scene.GetBounds(import.Root);
                    proto = (holder, b.Max - b.Min);
                }
                catch (ThreeNetException) { }
            }
            _cache[name] = proto;
        }
        if (proto is not { } p) return null;
        var pivot = scene.CreateNode(parent, name);
        pivot.Position = position;
        pivot.EulerAngles = new Vector3(0f, yaw, 0f);
        var copy = p.Root.Clone(pivot);
        copy.Visible = true;
        copy.Scale = new Vector3(length / MathF.Max(MathF.Max(p.Size.X, p.Size.Z), 1e-3f));
        return pivot;
    }
}
