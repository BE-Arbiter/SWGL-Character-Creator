using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SWGLCharacterCreator;

/// <summary>Renders a <see cref="GlmModel"/> with an orbit camera into a render target shown through ImGui.</summary>
public sealed class ModelViewer
{
    private readonly GraphicsDevice _gd;
    private readonly ImGuiRenderer _ui;
    private readonly ImageStore _images;
    private readonly BasicEffect _effect;
    private readonly Texture2D _white;
    private readonly List<(GlmSurface surface, Texture2D texture, VertexPositionNormalTexture[] work)> _draw = new();
    private GlaFile? _gla;
    private AnimDef? _anim;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _animStart;

    private RenderTarget2D? _rt;
    private IntPtr _rtId;
    private int _generation;

    private Vector3 _center;
    private float _radius = 40;
    private float _yaw = 0.5f, _pitch = 0.1f, _zoom = 1f;
    private Vector3 _pan;
    private Vector3 _bindCenter;
    private float _bindRadius = 40;
    private bool _recenter;

    /// <summary>Identifies what is currently loaded (null = nothing).</summary>
    public string? Key { get; private set; }

    public ModelViewer(GraphicsDevice gd, ImGuiRenderer ui, ImageStore images)
    {
        _gd = gd; _ui = ui; _images = images;
        _effect = new BasicEffect(gd) { TextureEnabled = true, LightingEnabled = true, VertexColorEnabled = false };
        _white = new Texture2D(gd, 1, 1);
        _white.SetData(new[] { new Color(200, 200, 200) });
    }

    public bool NeedsLoad(string key) => key != Key || _generation != _images.Generation;

    public void Clear() { Key = null; _draw.Clear(); _gla = null; }

    /// <summary>Plays <paramref name="anim"/> in a loop (null = bind pose).</summary>
    public void SetAnimation(AnimDef? anim)
    {
        _anim = anim;
        _animStart = _clock.Elapsed.TotalSeconds;
        _recenter = true; // frame the character at the first frame of the new animation
    }

    public double DebugTime;

    /// <summary>Animations of the loaded skeleton (used to find the neutral face and stand frames).</summary>
    public IReadOnlyList<AnimDef> Anims { get; set; } = Array.Empty<AnimDef>();
    public void SetCamera(float yaw, float pitch, float zoom = 1f, float panZ = 0f) { _yaw = yaw; _pitch = pitch; _zoom = zoom; _pan = new Vector3(0, 0, panZ); }

    public bool Animating => _anim != null && _gla != null;

    /// <summary>Builds the draw list: one texture per surface resolved through the skin map.</summary>
    public void Load(string key, GlmModel model, Dictionary<string, string> skin, GlaFile? gla)
    {
        _draw.Clear();
        _gla = gla;
        _generation = _images.Generation;
        foreach (var s in model.Surfaces)
        {
            if (s.Hidden || s.Indices.Length == 0) continue;
            Texture2D tex = _white;
            if (skin.TryGetValue(s.Name, out var shader))
            {
                if (shader.StartsWith("*off", StringComparison.OrdinalIgnoreCase) ||
                    shader.Equals("off", StringComparison.OrdinalIgnoreCase)) continue;
                var img = _images.Get(shader);
                if (img != null) tex = img.Texture;
            }
            _draw.Add((s, tex, (VertexPositionNormalTexture[])s.Verts.Clone()));
        }

        if (Key != key) { _yaw = 0.5f; _pitch = 0.1f; _zoom = 1f; _pan = Vector3.Zero; }
        Key = key;
        _bindCenter = _center = (model.Min + model.Max) / 2;
        _bindRadius = _radius = Math.Max((model.Max - model.Min).Length() / 2, 1f);
    }

    public void Orbit(System.Numerics.Vector2 delta)
    {
        _yaw -= delta.X * 0.01f;
        _pitch = Math.Clamp(_pitch + delta.Y * 0.01f, -1.4f, 1.4f);
    }

    /// <summary>Moves the camera target in the screen plane (right mouse drag).</summary>
    public void Pan(System.Numerics.Vector2 delta)
    {
        var dir = new Vector3(MathF.Cos(_yaw) * MathF.Cos(_pitch), MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch));
        var right = Vector3.Normalize(Vector3.Cross(-dir, Vector3.UnitZ));
        var up = Vector3.Cross(right, -dir);
        float scale = _radius * 2.6f * _zoom * 0.0016f;
        _pan += (-right * delta.X + up * delta.Y) * scale;
    }

    public void Zoom(float wheel) => _zoom = Math.Clamp(_zoom * MathF.Pow(0.9f, wheel), 0.2f, 4f);

    /// <summary>Updates the working vertices: bind pose, or the current frame of the playing animation.</summary>
    private void Skin()
    {
        System.Numerics.Matrix4x4[]? mats = null;
        if (_gla != null && _anim != null)
        {
            double t = _clock.Elapsed.TotalSeconds - _animStart + DebugTime;
            float fps = Math.Abs(_anim.Fps) < 0.01f ? 20f : Math.Abs(_anim.Fps);
            float frame = (float)((t * fps) % _anim.Length);
            var body = new FrameSource(frame, _anim.Length, _anim.Start);
            // Facial bones run their own animation: neutral (FACE_ALERT) unless a FACE_ animation is selected,
            // in which case the body holds a standing frame.
            FrameSource face;
            if (_anim.Name.StartsWith("FACE_", StringComparison.OrdinalIgnoreCase))
            {
                face = body;
                var stand = Anims.FirstOrDefault(a => a.Name == "BOTH_STAND1");
                body = new FrameSource(0, 1, stand?.Start ?? 0);
            }
            else
            {
                var alert = Anims.FirstOrDefault(a => a.Name == "FACE_ALERT");
                face = new FrameSource(0, 1, alert?.Start ?? 0);
            }
            mats = _gla.Pose(body, face);
        }

        foreach (var (s, _, work) in _draw)
        {
            if (mats == null) { Array.Copy(s.Verts, work, work.Length); continue; }
            for (int v = 0; v < work.Length; v++)
            {
                var src = s.Verts[v];
                var p = new System.Numerics.Vector3(src.Position.X, src.Position.Y, src.Position.Z);
                var n = new System.Numerics.Vector3(src.Normal.X, src.Normal.Y, src.Normal.Z);
                System.Numerics.Vector3 pos = default, nor = default;
                for (int k = 0; k < 4; k++)
                {
                    float w = s.Weight[v * 4 + k];
                    if (w <= 0) continue;
                    int b = s.Bone[v * 4 + k];
                    if (b < 0 || b >= mats.Length) continue;
                    pos += w * System.Numerics.Vector3.Transform(p, mats[b]);
                    nor += w * System.Numerics.Vector3.TransformNormal(n, mats[b]);
                }
                work[v].Position = new Vector3(pos.X, pos.Y, pos.Z);
                if (nor.LengthSquared() > 1e-8f) nor = System.Numerics.Vector3.Normalize(nor);
                work[v].Normal = new Vector3(nor.X, nor.Y, nor.Z);
        }
            }

        if (_recenter)
        {
            _recenter = false;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var (_, _, work) in _draw)
                foreach (var v in work) { min = Vector3.Min(min, v.Position); max = Vector3.Max(max, v.Position); }
            if (min.X <= max.X)
            {
                _center = (min + max) / 2;
                _radius = Math.Max(Math.Max((max - min).Length() / 2, _bindRadius * 0.6f), 1f);
            }
        }
    }

    /// <summary>Renders into the (re)sized target and returns its ImGui texture id.</summary>
    public IntPtr Render(int width, int height)
    {
        width = Math.Max(width, 16);
        height = Math.Max(height, 16);
        if (_rt == null || _rt.Width != width || _rt.Height != height)
        {
            if (_rt != null) { _ui.UnbindTexture(_rtId); _rt.Dispose(); }
            _rt = new RenderTarget2D(_gd, width, height, false, SurfaceFormat.Color, DepthFormat.Depth24);
            _rtId = _ui.BindTexture(_rt);
        }

        _gd.SetRenderTarget(_rt);
        _gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(40, 40, 48), 1f, 0);
        if (_draw.Count > 0) { Skin(); DrawModel(width, height); }
        _gd.SetRenderTarget(null);
        return _rtId;
    }

    /// <summary>Saves the current render as a png (diagnostics).</summary>
    public void SavePng(string path, int width, int height)
    {
        Render(width, height);
        using var fs = File.Create(path);
        _rt!.SaveAsPng(fs, _rt.Width, _rt.Height);
    }

    private void DrawModel(int width, int height)
    {
        // Quake axes: X forward, Y left, Z up. Yaw 0 looks at the model's front.
        float dist = _radius * 2.6f * _zoom;
        var dir = new Vector3(MathF.Cos(_yaw) * MathF.Cos(_pitch), MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch));
        var target = _center + _pan;
        var eye = target + dir * dist;

        _effect.World = Matrix.Identity;
        _effect.View = Matrix.CreateLookAt(eye, target, Vector3.UnitZ);
        _effect.Projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(40), width / (float)height,
            Math.Max(_radius * 0.05f, 0.1f), _radius * 20f);
        _effect.AmbientLightColor = new Vector3(0.45f);
        _effect.DirectionalLight0.Enabled = true;
        _effect.DirectionalLight0.Direction = -dir; // headlight
        _effect.DirectionalLight0.DiffuseColor = new Vector3(0.75f);
        _effect.DirectionalLight0.SpecularColor = Vector3.Zero;
        _effect.DirectionalLight1.Enabled = false;
        _effect.DirectionalLight2.Enabled = false;

        _gd.BlendState = BlendState.Opaque;
        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState = RasterizerState.CullNone;
        _gd.SamplerStates[0] = SamplerState.LinearWrap;

        foreach (var (s, tex, work) in _draw)
        {
            _effect.Texture = tex;
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, work, 0, work.Length,
                    s.Indices, 0, s.Indices.Length / 3);
            }
        }
    }
}
