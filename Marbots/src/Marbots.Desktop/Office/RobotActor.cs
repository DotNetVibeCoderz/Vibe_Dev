using System.Numerics;
using ThreeNet;

namespace Marbots.Desktop.Office;

/// <summary>
/// One bot's body: the rigged robot (its own glTF instance so its clips drive only this skeleton), a ring in the bot's
/// colour and a status lamp above the head. Clips are cross-faded when the activity changes.
/// </summary>
internal sealed class RobotActor : IDisposable
{
    private const float FadeSeconds = 0.25f;
    private readonly Scene _scene;
    private readonly Node _body;
    private readonly Material _lamp;
    private readonly Node _lampNode;
    private readonly Dictionary<BotActivity, AnimationPlayer> _players = [];
    private readonly List<AnimationClip> _clips = [];
    private BotActivity _current = BotActivity.Idle;
    private float _time;
    private float _yaw;

    public RobotActor(Scene scene, string botId, Vector3 color, bool manager)
    {
        _scene = scene;
        Root = scene.CreateNode(null, "bot-" + botId);
        var scale = manager ? 1.15f : 1f;

        var ring = scene.AddMesh(scene.CreateCylinderGeometry(0.42f * scale, 0.42f * scale, 0.025f, 40),
            scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(color, 1f), 0f, 0.5f) with { Emissive = color, EmissiveIntensity = 0.9f }), Root, "ring");
        ring.Position = new Vector3(0f, 0.015f, 0f);

        _body = scene.CreateNode(Root, "body");
        _body.Scale = new Vector3(scale);
        var path = PropLibrary.AssetPath("robot.glb");
        if (File.Exists(path) && TryLoadRobot(path)) { }
        else BuildStandIn(color);

        _lamp = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.4f, 0.9f, 0.6f, 1f), 0f, 0.3f) with { Emissive = new Vector3(0.4f, 0.9f, 0.6f), EmissiveIntensity = 2f });
        _lampNode = scene.AddMesh(scene.CreateSphereGeometry(0.07f, 16, 8), _lamp, Root, "lamp");
        _lampNode.Position = new Vector3(0f, 1.32f * scale, 0f);
    }

    public Node Root { get; }

    private bool TryLoadRobot(string path)
    {
        try
        {
            var before = _scene.Animations.Select(a => a.Id).ToHashSet();
            _scene.LoadGltf(path, _body);
            foreach (var clip in _scene.Animations.Where(a => !before.Contains(a.Id)))
            {
                BotActivity? activity = clip.Name.ToLowerInvariant() switch
                {
                    "idle" => BotActivity.Idle,
                    "walk" => BotActivity.Walking,
                    "typing" => BotActivity.Typing,
                    "thinking" => BotActivity.Thinking,
                    "wave" => BotActivity.Waving,
                    "talk" => BotActivity.Talking,
                    _ => null,
                };
                _clips.Add(clip);
                if (activity is null) continue;
                _players[activity.Value] = clip.Play(loop: true, speed: 1f, weight: activity == BotActivity.Idle ? 1f : 0f);
            }
            return true;
        }
        catch (ThreeNetException)
        {
            return false;
        }
    }

    /// <summary>A capsule body and round head when the robot model is not installed.</summary>
    private void BuildStandIn(Vector3 color)
    {
        var shell = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.93f, 0.94f, 0.97f, 1f), 0.1f, 0.35f));
        var accent = _scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(color, 1f), 0.1f, 0.4f));
        var torso = _scene.AddMesh(_scene.CreateCylinderGeometry(0.28f, 0.24f, 0.5f, 24), shell, _body, "torso");
        torso.Position = new Vector3(0f, 0.45f, 0f);
        var head = _scene.AddMesh(_scene.CreateSphereGeometry(0.3f, 24, 16), shell, _body, "head");
        head.Position = new Vector3(0f, 0.92f, 0f);
        var visor = _scene.AddMesh(_scene.CreateBoxGeometry(0.4f, 0.14f, 0.1f), accent, _body, "visor");
        visor.Position = new Vector3(0f, 0.95f, 0.25f);
    }

    public void Apply(OfficeAgent agent, float dt)
    {
        _time += dt;
        Root.Position = new Vector3(agent.Position.X, 0f, agent.Position.Y);
        // Model faces +Z; the director's heading 0 faces -Z.
        var target = MathF.PI - agent.Heading;
        var diff = MathF.IEEERemainder(target - _yaw, MathF.Tau);
        _yaw += diff * MathF.Min(1f, dt * 10f);
        Root.EulerAngles = new Vector3(0f, _yaw, 0f);

        var activity = agent.Activity;
        if (_players.Count > 0)
        {
            if (!_players.ContainsKey(activity)) activity = activity == BotActivity.Talking && _players.ContainsKey(BotActivity.Typing) ? BotActivity.Typing : BotActivity.Idle;
            _current = activity;
            var step = dt / FadeSeconds;
            foreach (var (a, player) in _players)
                player.Weight = Math.Clamp(player.Weight + (a == _current ? step : -step), 0f, 1f);
        }
        else
        {
            // Stand-in: bob while walking, sway while working.
            var bob = activity == BotActivity.Walking ? MathF.Abs(MathF.Sin(_time * 9f)) * 0.06f : MathF.Sin(_time * 2f) * 0.01f;
            _body.Position = new Vector3(0f, bob, 0f);
            _body.EulerAngles = new Vector3(activity is BotActivity.Typing or BotActivity.Thinking ? 0.12f : 0f, 0f, 0f);
        }

        var (rgb, pulse) = activity switch
        {
            BotActivity.Typing => (new Vector3(0.35f, 0.5f, 1f), 3f),
            BotActivity.Thinking => (new Vector3(1f, 0.75f, 0.2f), 2f),
            BotActivity.Waving => (new Vector3(1f, 0.3f, 0.35f), 5f),
            BotActivity.Talking => (new Vector3(0.6f, 0.45f, 1f), 3f),
            BotActivity.Walking => (new Vector3(0.6f, 0.85f, 1f), 0f),
            _ => (new Vector3(0.35f, 0.85f, 0.55f), 0f),
        };
        var intensity = pulse > 0 ? 1.6f + MathF.Sin(_time * pulse) * 1.2f : 1.2f;
        _lamp.Update(o => o with { BaseColor = new Vector4(rgb, 1f), Emissive = rgb, EmissiveIntensity = intensity });
    }

    public void Dispose()
    {
        foreach (var p in _players.Values) p.Stop();
        foreach (var c in _clips) c.Destroy();
        Root.Remove();
    }
}
