using System.Runtime.InteropServices;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Vec2 = System.Numerics.Vector2;

namespace SWGLCharacterCreator;

/// <summary>Minimal ImGui.NET backend for MonoGame (DesktopGL).</summary>
public sealed class ImGuiRenderer
{
    private readonly GraphicsDevice _gd;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _raster;
    private readonly Dictionary<IntPtr, Texture2D> _textures = new();
    private VertexBuffer? _vb;
    private IndexBuffer? _ib;
    private int _vbSize, _ibSize;
    private int _scroll;
    private readonly List<char> _chars = new();
    private readonly Dictionary<Keys, ImGuiKey> _keyMap = new();

    private static readonly int VertSize = Marshal.SizeOf<ImDrawVert>();
    private static readonly VertexDeclaration VertDecl = new(
        VertSize,
        new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
        new VertexElement(8, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(16, VertexElementFormat.Color, VertexElementUsage.Color, 0));

    public ImGuiRenderer(Game game)
    {
        _gd = game.GraphicsDevice;
        ImGui.SetCurrentContext(ImGui.CreateContext());
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        unsafe { io.NativePtr->IniFilename = null; }

        _effect = new BasicEffect(_gd) { TextureEnabled = true, VertexColorEnabled = true, World = Matrix.Identity, View = Matrix.Identity };
        _raster = new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true, DepthClipEnable = true };

        BuildFont(io);
        SetupKeys();
        game.Window.TextInput += (_, e) => { if (e.Character >= 32 && e.Character != 127) _chars.Add(e.Character); };
        ImGui.StyleColorsDark();
        ImGui.GetStyle().WindowRounding = 0;
    }

    private int _nextTexId = 2;

    public IntPtr BindTexture(Texture2D tex)
    {
        var id = new IntPtr(_nextTexId++);
        _textures[id] = tex;
        return id;
    }

    public void UnbindTexture(IntPtr id) => _textures.Remove(id);

    private void BuildFont(ImGuiIOPtr io)
    {
        io.Fonts.AddFontDefault();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int w, out int h, out int bpp);
        var data = new byte[w * h * bpp];
        Marshal.Copy(pixels, data, 0, data.Length);
        var tex = new Texture2D(_gd, w, h, false, SurfaceFormat.Color);
        tex.SetData(data);
        var id = new IntPtr(1);
        _textures[id] = tex;
        io.Fonts.SetTexID(id);
        io.Fonts.ClearTexData();
    }

    private void SetupKeys()
    {
        var m = _keyMap;
        m[Keys.Tab] = ImGuiKey.Tab; m[Keys.Left] = ImGuiKey.LeftArrow; m[Keys.Right] = ImGuiKey.RightArrow;
        m[Keys.Up] = ImGuiKey.UpArrow; m[Keys.Down] = ImGuiKey.DownArrow; m[Keys.PageUp] = ImGuiKey.PageUp;
        m[Keys.PageDown] = ImGuiKey.PageDown; m[Keys.Home] = ImGuiKey.Home; m[Keys.End] = ImGuiKey.End;
        m[Keys.Delete] = ImGuiKey.Delete; m[Keys.Back] = ImGuiKey.Backspace; m[Keys.Enter] = ImGuiKey.Enter;
        m[Keys.Escape] = ImGuiKey.Escape; m[Keys.Space] = ImGuiKey.Space;
        m[Keys.A] = ImGuiKey.A; m[Keys.C] = ImGuiKey.C; m[Keys.V] = ImGuiKey.V; m[Keys.X] = ImGuiKey.X;
        m[Keys.Y] = ImGuiKey.Y; m[Keys.Z] = ImGuiKey.Z;
    }

    public void BeginFrame(GameTime gameTime)
    {
        var io = ImGui.GetIO();
        io.DeltaTime = Math.Max((float)gameTime.ElapsedGameTime.TotalSeconds, 1f / 1000f);
        io.DisplaySize = new Vec2(_gd.PresentationParameters.BackBufferWidth, _gd.PresentationParameters.BackBufferHeight);

        var ms = Mouse.GetState();
        io.AddMousePosEvent(ms.X, ms.Y);
        io.AddMouseButtonEvent(0, ms.LeftButton == ButtonState.Pressed);
        io.AddMouseButtonEvent(1, ms.RightButton == ButtonState.Pressed);
        io.AddMouseButtonEvent(2, ms.MiddleButton == ButtonState.Pressed);
        int wheel = ms.ScrollWheelValue;
        io.AddMouseWheelEvent(0, (wheel - _scroll) / 120f);
        _scroll = wheel;

        var ks = Keyboard.GetState();
        foreach (var (k, ik) in _keyMap) io.AddKeyEvent(ik, ks.IsKeyDown(k));
        io.AddKeyEvent(ImGuiKey.ModCtrl, ks.IsKeyDown(Keys.LeftControl) || ks.IsKeyDown(Keys.RightControl));
        io.AddKeyEvent(ImGuiKey.ModShift, ks.IsKeyDown(Keys.LeftShift) || ks.IsKeyDown(Keys.RightShift));
        io.AddKeyEvent(ImGuiKey.ModAlt, ks.IsKeyDown(Keys.LeftAlt) || ks.IsKeyDown(Keys.RightAlt));
        foreach (var c in _chars) io.AddInputCharacter(c);
        _chars.Clear();

        ImGui.NewFrame();
    }

    public void EndFrame()
    {
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    private void RenderDrawData(ImDrawDataPtr dd)
    {
        if (dd.CmdListsCount == 0) return;
        var lastViewport = _gd.Viewport;
        var lastScissor = _gd.ScissorRectangle;
        var lastBlend = _gd.BlendState;
        var lastDepth = _gd.DepthStencilState;
        var lastRaster = _gd.RasterizerState;
        var lastSampler = _gd.SamplerStates[0];

        _gd.BlendState = BlendState.NonPremultiplied;
        _gd.DepthStencilState = DepthStencilState.None;
        _gd.RasterizerState = _raster;
        _gd.SamplerStates[0] = SamplerState.LinearClamp;

        UpdateBuffers(dd);
        _gd.SetVertexBuffer(_vb);
        _gd.Indices = _ib;

        var io = ImGui.GetIO();
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0, io.DisplaySize.X, io.DisplaySize.Y, 0, -1, 1);

        int vtxOff = 0, idxOff = 0;
        for (int n = 0; n < dd.CmdListsCount; n++)
        {
            var cl = dd.CmdListsRange[n];
            for (int i = 0; i < cl.CmdBuffer.Size; i++)
            {
                var cmd = cl.CmdBuffer[i];
                if (cmd.ElemCount == 0) continue;
                _gd.ScissorRectangle = new Rectangle((int)cmd.ClipRect.X, (int)cmd.ClipRect.Y,
                    (int)(cmd.ClipRect.Z - cmd.ClipRect.X), (int)(cmd.ClipRect.W - cmd.ClipRect.Y));
                _effect.Texture = _textures[cmd.TextureId];
                foreach (var pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList,
                        vtxOff + (int)cmd.VtxOffset, idxOff + (int)cmd.IdxOffset, (int)cmd.ElemCount / 3);
                }
            }
            vtxOff += cl.VtxBuffer.Size;
            idxOff += cl.IdxBuffer.Size;
        }

        _gd.Viewport = lastViewport;
        _gd.ScissorRectangle = lastScissor;
        _gd.BlendState = lastBlend;
        _gd.DepthStencilState = lastDepth;
        _gd.RasterizerState = lastRaster;
        _gd.SamplerStates[0] = lastSampler;
    }

    private void UpdateBuffers(ImDrawDataPtr dd)
    {
        if (_vb == null || dd.TotalVtxCount > _vbSize)
        {
            _vb?.Dispose();
            _vbSize = (int)(dd.TotalVtxCount * 1.5f);
            _vb = new VertexBuffer(_gd, VertDecl, _vbSize, BufferUsage.None);
        }
        if (_ib == null || dd.TotalIdxCount > _ibSize)
        {
            _ib?.Dispose();
            _ibSize = (int)(dd.TotalIdxCount * 1.5f);
            _ib = new IndexBuffer(_gd, IndexElementSize.SixteenBits, _ibSize, BufferUsage.None);
        }

        var verts = new byte[dd.TotalVtxCount * VertSize];
        var idx = new byte[dd.TotalIdxCount * sizeof(ushort)];
        int vo = 0, io = 0;
        for (int n = 0; n < dd.CmdListsCount; n++)
        {
            var cl = dd.CmdListsRange[n];
            int vb = cl.VtxBuffer.Size * VertSize, ib = cl.IdxBuffer.Size * sizeof(ushort);
            Marshal.Copy(cl.VtxBuffer.Data, verts, vo, vb);
            Marshal.Copy(cl.IdxBuffer.Data, idx, io, ib);
            vo += vb; io += ib;
        }
        _vb.SetData(verts, 0, verts.Length);
        _ib.SetData(idx, 0, idx.Length);
    }
}
