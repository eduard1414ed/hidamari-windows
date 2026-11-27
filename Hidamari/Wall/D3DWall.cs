using System.IO.Compression;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Hidamari;

/// The Direct3D drawing of the wall: the prototype's shader (Wall/wall.hlsl, the HLSL copy of the Mac's wall.metal),
/// its numbers (Core/WallLighting.cs) and its pictures (shared/textures, the Mac's Metal textures byte for byte).
/// One for the whole app; every screen's wall draws with it (D3DRenderer). Like MetalWall on the Mac.
///
/// It draws only when asked; nothing runs between frames. Pictures are read only when a frame needs them (the crowns
/// for dapples, the plant for the window scenes, the frost only in the window scene with frost) and let go when no wall
/// needs them any more.
///
/// One graphics device and one DirectComposition device for all walls (and the card). Every use goes through `Lock`:
/// the still frames are drawn on a worker thread, the moving ones on each screen's pacing thread.
sealed class D3DWall : IDisposable
{
    public readonly object Lock = new();
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public Vortice.DirectComposition.IDCompositionDevice Composition { get; }
    /// "graphics card NAME" or "software (WARP)"
    public string Kind { get; }
    public FeatureLevel Level { get; }
    public long ShaderMs { get; }

    readonly ID3D11VertexShader vs;
    readonly ID3D11PixelShader ps;
    readonly string source;
    readonly ID3D11Buffer constants;
    readonly ID3D11SamplerState repeat, clamp;
    /// Mac update 1.4: the Tree's picture repeats mirrored past its edges (MIRRORED_REPEAT); the photo plant's pixels
    /// for its pieces are read linearly, without smaller copies (the sketch's rigLoad()).
    readonly ID3D11SamplerState mirror, pieceSampler;

    // ---------- Mac update 1.4 ----------
    /// The window and the blinds while they move: the shader compiled with WALL_MOVING = 1 (wall.hlsl), made the first
    /// time it is needed. Still frames never use it, so they are exactly the still code.
    ID3D11PixelShader? psMoving;
    /// The Tree's shadow under the current sun: the pass that draws it, its mesh, the last few pictures.
    ID3D11VertexShader? treeVs; ID3D11PixelShader? treePs; ID3D11BlendState? multiply;
    ID3D11Buffer? treeGrid, treeIndex; ID3D11ShaderResourceView? treeGridView; int treeIndexCount;
    ID3D11Buffer? passConstants;
    readonly List<(TreeMapKey key, ID3D11Texture2D tex, ID3D11RenderTargetView rtv, ID3D11ShaderResourceView view)> treeMaps = new();
    /// The window's plant in the wind: the passes that draw it, the plants' pieces, the last few pictures.
    ID3D11VertexShader? rigVs, ficusVs; ID3D11PixelShader? rigPs, ficusPs;
    ID3D11BlendState[]? darkestIn;
    readonly Dictionary<int, WindowLeaves.Rig> rigs = new();
    readonly List<(LeafPose key, ID3D11Texture2D tex, ID3D11RenderTargetView rtv, ID3D11ShaderResourceView view)> posed = new();
    /// The ficus's wind steps from the last frame (WindowLeaves.Memos): the same picture with far less work.
    readonly WindowLeaves.Memos ficusMemos = new();
    ID3D11Buffer? shapes; ID3D11ShaderResourceView? shapesView; int shapesFloats;
    ID3D11Buffer? profile; ID3D11ShaderResourceView? profileView;
    readonly ID3D11BlendState mixing;
    readonly ID3D11RasterizerState bothSides;
    readonly ID3D11ShaderResourceView blank;
    readonly ID3D11Texture2D blankTexture;
    readonly Dictionary<string, (ID3D11Texture2D tex, ID3D11ShaderResourceView view)> pictures = new();
    /// What each wall's last frame read, so a picture is let go only when nobody needs it.
    readonly Dictionary<object, HashSet<string>> needed = new();

    /// For the checks: draw with the software device even where there is a graphics card (HIDAMARI_D3D=warp).
    static bool ForceWarp => Environment.GetEnvironmentVariable("HIDAMARI_D3D") == "warp";

    public D3DWall()
    {
        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var flags = DeviceCreationFlags.BgraSupport;
        ID3D11Device? dev = null; ID3D11DeviceContext? ctx = null; FeatureLevel level = default;
        string kind = "software (WARP)";
        if (!ForceWarp)
        {
            var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out dev, out level, out ctx);
            if (r.Failure || dev == null) Log.Write($"d3d: no graphics card device ({r}), trying the software one (WARP)");
            else kind = $"graphics card {AdapterName(dev)}";
        }
        if (dev == null)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out dev, out level, out ctx).CheckError();
            kind = "software (WARP)";
        }
        Device = dev!;
        Context = ctx!;
        Level = level;
        Kind = kind;
        using (var mt = Context.QueryInterfaceOrNull<ID3D11Multithread>()) mt?.SetMultithreadProtected(true);
        using (var dxgi = Device.QueryInterface<IDXGIDevice>())
            Composition = Vortice.DirectComposition.DComp.DCompositionCreateDevice<Vortice.DirectComposition.IDCompositionDevice>(dxgi);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var s = typeof(D3DWall).Assembly.GetManifestResourceStream("wall.hlsl")!)
        using (var reader = new StreamReader(s)) source = reader.ReadToEnd();
        vs = Device.CreateVertexShader(ShaderCompiler.Compile(source, "wallVertex", "vs_4_0"));
        ps = Device.CreatePixelShader(ShaderCompiler.Compile(source, "wallFragment", "ps_4_0"));
        ShaderMs = sw.ElapsedMilliseconds;

        constants = Device.CreateBuffer(new BufferDescription(16 * BlockGroups, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        // the prototype's textures: linear filtering between pixels and between levels (LINEAR_MIPMAP_LINEAR)
        repeat = Device.CreateSamplerState(SamplerDescription.LinearWrap);
        clamp = Device.CreateSamplerState(SamplerDescription.LinearClamp);
        var md = SamplerDescription.LinearWrap;
        md.AddressU = md.AddressV = md.AddressW = TextureAddressMode.Mirror;
        mirror = Device.CreateSamplerState(md);
        // no smaller copies: the level is held at 0
        var pd = SamplerDescription.LinearClamp;
        pd.MaxLOD = 0;
        pieceSampler = Device.CreateSamplerState(pd);
        // new = frame · a + what is there · (1 − a), a = the blend factor: the second motion of a crossfade (Mac update 1.1)
        mixing = Device.CreateBlendState(new BlendDescription(Blend.BlendFactor, Blend.InverseBlendFactor, Blend.One, Blend.Zero));
        // the prototype's triangle (-1,-1), (3,-1), (-1,3) turns anticlockwise, which Direct3D by default takes for its
        // back and does not draw (WebGL and Metal draw both sides): nothing is culled
        bothSides = Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        // stands in for a picture the frame does not read (WebGL's photo texture starts the same: 1×1 white)
        unsafe
        {
            uint white = 0xFFFFFFFF;
            blankTexture = Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, 1, 1, 1, 1), new SubresourceData(&white, 4));
        }
        blank = Device.CreateShaderResourceView(blankTexture);
    }

    static string AdapterName(ID3D11Device dev)
    {
        try
        {
            using var dxgi = dev.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            return adapter.Description.Description.TrimEnd('\0');
        }
        catch { return "?"; }
    }

    /// The device is gone (a new driver, the card restarted, removed or switched): DXGI_ERROR_DEVICE_REMOVED and its kin.
    public static bool IsLost(int hr) => hr is unchecked((int)0x887A0005) or unchecked((int)0x887A0007) or unchecked((int)0x887A0006) or unchecked((int)0x887A0020);

    /// Why the device went, when it did (0 while it is well).
    public int RemovedReason { get { try { return Device.DeviceRemovedReason.Code; } catch { return unchecked((int)0x887A0005); } } }

    // ---------- pictures ----------

    public static HashSet<string> PictureNames(WallUniformValues u)
    {
        var o = new HashSet<string>();
        if (u.ReadsCrowns) { o.Add("crown0"); o.Add("crown1"); }
        if (u.ReadsPlant)
        {
            if (u.Plant == PlantPicture.Ficus) o.Add("ficus");
            if (u.Plant == PlantPicture.Photo2) o.Add("photo2");
            if (u.Plant == PlantPicture.Photo5) o.Add("photo5");
            if (u.Plant == PlantPicture.Vase) o.Add("vase");
        }
        if (u.ReadsFrost) o.Add("frost");
        if (u.ReadsTree) { o.Add("tree"); o.Add("treedepth"); }
        // Mac update 1.4: the window's plant in the wind is drawn from its pieces ("leaves" keeps the drawn pictures)
        if (u.ReadsPlant && u.Leaves != null)
        {
            o.Add("leaves");
            int n = u.Plant == PlantPicture.Photo2 ? 2 : u.Plant == PlantPicture.Photo5 ? 5 : 0;
            if (n != 0) { o.Add($"rigphoto{n}"); o.Add($"rigid{n}"); }
        }
        return o;
    }

    /// .htex (tools/make-metal-textures.swift on the Mac): "HTX1", width, height, channels (1, 2 or 4), levels as 32-bit
    /// little-endian numbers, then each level's pixels, rows from the top, level 0 first. Here gzip-packed.
    ID3D11ShaderResourceView Picture(string name)
    {
        if (pictures.TryGetValue(name, out var p)) return p.view;
        byte[] data;
        using (var s = typeof(D3DWall).Assembly.GetManifestResourceStream($"textures/{name}.htex.gz") ?? throw new FileNotFoundException(name))
        using (var z = new GZipStream(s, CompressionMode.Decompress))
        using (var m = new MemoryStream()) { z.CopyTo(m); data = m.ToArray(); }
        if (data.Length < 20 || data[0] != 'H' || data[1] != 'T' || data[2] != 'X' || data[3] != '1') throw new InvalidDataException($"{name} is not a texture");
        int Num(int i) => BitConverter.ToInt32(data, 4 + 4 * i);
        int w = Num(0), h = Num(1), channels = Num(2), levels = Num(3);
        var subs = new SubresourceData[levels];
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            int offset = 20, lw = w, lh = h;
            for (int level = 0; level < levels; level++)
            {
                int size = lw * lh * channels;
                if (offset + size > data.Length) throw new InvalidDataException($"{name} is cut short");
                subs[level] = new SubresourceData(handle.AddrOfPinnedObject() + offset, (uint)(lw * channels));
                offset += size;
                lw = Math.Max(1, lw / 2); lh = Math.Max(1, lh / 2);
            }
            // the map of pieces holds numbers, read exactly (Mac update 1.4)
            var format = name.StartsWith("rigid") ? Format.R8G8_UInt : channels == 1 ? Format.R8_UNorm : channels == 2 ? Format.R8G8_UNorm : Format.R8G8B8A8_UNorm;
            var desc = new Texture2DDescription(format, (uint)w, (uint)h, 1, (uint)levels,
                BindFlags.ShaderResource, ResourceUsage.Immutable);
            var tex = Device.CreateTexture2D(desc, subs.AsSpan());
            var view = Device.CreateShaderResourceView(tex);
            pictures[name] = (tex, view);
            return view;
        }
        finally { handle.Free(); }
    }

    /// Records what `user`'s latest frame reads and lets go of pictures no wall reads any more.
    public void SetNeeded(HashSet<string> names, object user)
    {
        lock (Lock) { needed[user] = names; Trim(); }
    }

    /// A wall that is gone reads nothing.
    public void Forget(object user)
    {
        lock (Lock) { needed.Remove(user); Trim(); }
    }

    void Trim()
    {
        var keep = needed.Values.SelectMany(n => n).ToHashSet();
        foreach (var name in pictures.Keys.Where(n => !keep.Contains(n)).ToList())
        {
            pictures[name].view.Dispose();
            pictures[name].tex.Dispose();
            pictures.Remove(name);
        }
        // the Tree's shadow pictures and mesh go with the tree (about 11 MB a picture, 6 MB the mesh), the plant in the
        // wind with the moving window, the pieces with their photo (Mac update 1.4)
        if (!keep.Contains("tree"))
        {
            foreach (var m in treeMaps) { m.view.Dispose(); m.rtv.Dispose(); m.tex.Dispose(); }
            treeMaps.Clear();
            treeGridView?.Dispose(); treeGrid?.Dispose(); treeIndex?.Dispose();
            treeGridView = null; treeGrid = null; treeIndex = null;
        }
        if (!keep.Contains("leaves"))
        {
            foreach (var m in posed) { m.view.Dispose(); m.rtv.Dispose(); m.tex.Dispose(); }
            posed.Clear();
        }
        foreach (var n in rigs.Keys.Where(n => !keep.Contains($"rigphoto{n}")).ToList()) rigs.Remove(n);
    }

    /// Megabytes of pictures held now (for the journal).
    public double PicturesMB
    {
        get
        {
            lock (Lock)
            {
                double sum = 0;
                foreach (var (n, p) in pictures) { var d = p.tex.Description; sum += d.Width * d.Height * Bytes(d.Format) * (d.MipLevels > 1 ? 4.0 / 3 : 1); }
                foreach (var m in treeMaps) { var d = m.tex.Description; sum += d.Width * d.Height * 4.0 / 3; }
                foreach (var m in posed) { var d = m.tex.Description; sum += d.Width * d.Height * 4 * 4.0 / 3; }
                return sum / 1048576;
            }
        }
    }

    // ---------- Mac update 1.4: the passes drawn before the frame ----------

    /// The shader with WALL_MOVING = 1 (the window and the blinds in motion), compiled once, when first needed.
    string MovingSource => "#define WALL_MOVING 1\n" + source;

    ID3D11PixelShader MovingShader()
    {
        if (psMoving != null) return psMoving;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        psMoving = Device.CreatePixelShader(ShaderCompiler.Compile(MovingSource, "wallFragment", "ps_4_0"));
        Log.Write($"d3d: the moving window's shader compiled in {sw.ElapsedMilliseconds} ms");
        return psMoving;
    }

    /// 32 bytes for the passes' own uniforms (TreeMapUniforms, RigPlace in wall.hlsl), register b1.
    unsafe void UploadPass(ReadOnlySpan<float> eight)
    {
        passConstants ??= Device.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        var m = Context.Map(passConstants, MapMode.WriteDiscard);
        fixed (float* p = eight) Buffer.MemoryCopy(p, (void*)m.DataPointer, 32, eight.Length * 4);
        Context.Unmap(passConstants, 0);
    }

    static ShaderResourceViewDescription FloatsView(Format format, int elements) => new()
    {
        Format = format,
        ViewDimension = ShaderResourceViewDimension.Buffer,
        Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)elements },
    };

    /// An immutable buffer of floats the shaders read as Buffer<float> or Buffer<float2>.
    unsafe (ID3D11Buffer buf, ID3D11ShaderResourceView view) Floats(float[] data, Format format, int perElement)
    {
        fixed (float* p = data)
        {
            var buf = Device.CreateBuffer(new BufferDescription((uint)(data.Length * 4), BindFlags.ShaderResource, ResourceUsage.Immutable),
                new SubresourceData(p));
            return (buf, Device.CreateShaderResourceView(buf, FloatsView(format, data.Length / perElement)));
        }
    }

    /// The instances of the plant's pieces or shapes, rewritten for every draw (Buffer<float> in wall.hlsl).
    unsafe ID3D11ShaderResourceView Shapes(float[] data)
    {
        if (shapes == null || shapesFloats < data.Length)
        {
            shapesView?.Dispose(); shapes?.Dispose();
            shapesFloats = Math.Max(data.Length, 4096);
            shapes = Device.CreateBuffer(new BufferDescription((uint)(shapesFloats * 4), BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            shapesView = Device.CreateShaderResourceView(shapes, FloatsView(Format.R32_Float, shapesFloats));
        }
        var m = Context.Map(shapes, MapMode.WriteDiscard);
        fixed (float* p = data) Buffer.MemoryCopy(p, (void*)m.DataPointer, shapesFloats * 4L, data.Length * 4L);
        Context.Unmap(shapes, 0);
        return shapesView!;
    }

    /// A picture the passes draw into and the frame then reads, with all its smaller copies (made after drawing).
    (ID3D11Texture2D tex, ID3D11RenderTargetView rtv, ID3D11ShaderResourceView view) PassTarget(Format format, int w, int h)
    {
        var tex = Device.CreateTexture2D(new Texture2DDescription(format, (uint)w, (uint)h, 1, 0,
            BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
        return (tex, Device.CreateRenderTargetView(tex), Device.CreateShaderResourceView(tex));
    }

    /// Nothing a pass draws into may still be bound for reading (Direct3D would refuse the target).
    void UnbindPictures()
    {
        var none = new ID3D11ShaderResourceView?[6];
        Context.PSSetShaderResources(0, none!);
        Context.VSSetShaderResources(0, new ID3D11ShaderResourceView?[2]!);
    }

    /// The sketch's picture of the Tree's shadow: the photo with a 240 px margin, twice as fine (MetalWall.treeMapSize).
    const int TreeMapW = 2 * (970 + 2 * 240), TreeMapH = 2 * (900 + 2 * 240);
    /// How many shadow pictures and posed plants are kept: one per screen shape, and two while two motions are crossfaded.
    const int Kept = 3;

    /// The mesh of the sketch's initTree3D(): a grid over the whole picture, a point every 3 photo pixels; drawn as
    /// strips, one per row, with the same triangles as the sketch's (a, b, c), (b, d, c) (MetalWall.treeMeshLocked()).
    unsafe void TreeMesh()
    {
        if (treeGrid != null) return;
        int tw = 970 + 2 * 240, th = 900 + 2 * 240, step = 3;
        int nx = (tw + step - 1) / step + 1, ny = (th + step - 1) / step + 1;
        var uv = new float[nx * ny * 2];
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                uv[(j * nx + i) * 2] = (float)i / (float)(nx - 1);
                uv[(j * nx + i) * 2 + 1] = (float)j / (float)(ny - 1);
            }
        var idx = new List<uint>((ny - 1) * (2 * nx + 1));
        for (int j = 0; j < ny - 1; j++)
        {
            for (int i = 0; i < nx; i++) { idx.Add((uint)(j * nx + i)); idx.Add((uint)((j + 1) * nx + i)); }
            idx.Add(uint.MaxValue);                                   // a new strip for the next row
        }
        (treeGrid, treeGridView) = Floats(uv, Format.R32G32_Float, 2);
        var ia = idx.ToArray();
        fixed (uint* p = ia)
            treeIndex = Device.CreateBuffer(new BufferDescription((uint)(ia.Length * 4), BindFlags.IndexBuffer, ResourceUsage.Immutable), new SubresourceData(p));
        treeIndexCount = ia.Length;
    }

    /// The Tree's shadow picture for `key`, drawn first if it is not one of the last few (MetalWall.treeMap()).
    ID3D11ShaderResourceView TreeMap(TreeMapKey key)
    {
        int i = treeMaps.FindIndex(m => m.key == key);
        if (i >= 0) { var m = treeMaps[i]; treeMaps.RemoveAt(i); treeMaps.Add(m); return m.view; }
        var layers = Picture("tree"); var depth = Picture("treedepth");
        if (treeVs == null)
        {
            treeVs = Device.CreateVertexShader(ShaderCompiler.Compile(source, "treeMapVertex", "vs_4_0"));
            treePs = Device.CreatePixelShader(ShaderCompiler.Compile(source, "treeMapFragment", "ps_4_0"));
            // shadows multiply: blendFunc(ZERO, SRC_COLOR)
            multiply ??= Device.CreateBlendState(new BlendDescription(Blend.Zero, Blend.SourceColor, Blend.Zero, Blend.SourceAlpha));
        }
        TreeMesh();
        ID3D11Texture2D tex; ID3D11RenderTargetView rtv; ID3D11ShaderResourceView view;
        if (treeMaps.Count >= Kept) { (_, tex, rtv, view) = treeMaps[0]; treeMaps.RemoveAt(0); }
        else (tex, rtv, view) = PassTarget(Format.R8_UNorm, TreeMapW, TreeMapH);

        var c = Context;
        UnbindPictures();
        c.OMSetRenderTargets(rtv);
        c.ClearRenderTargetView(rtv, new Color4(1, 1, 1, 1));
        c.RSSetViewport(new Viewport(0, 0, TreeMapW, TreeMapH));
        c.RSSetState(bothSides);
        c.IASetInputLayout(null);
        c.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        c.IASetIndexBuffer(treeIndex, Format.R32_UInt, 0);
        c.VSSetShader(treeVs);
        c.PSSetShader(treePs);
        c.VSSetShaderResource(0, treeGridView);
        c.VSSetShaderResource(1, depth);
        c.PSSetShaderResource(0, layers);
        c.PSSetSampler(0, mirror);
        c.OMSetBlendState(multiply);
        double tw = 970 + 2 * 240, th = 900 + 2 * 240;
        for (int which = 0; which <= 1; which++)
        {
            // a point d metres from the wall: its shadow moves by d times the sun's slant (away from the sun)
            UploadPass(new[] { (float)(-key.OffX / (key.K * tw)), (float)(key.OffY / (key.K * th)), (float)(1 / tw), (float)(1 / th),
                               (float)key.T, (float)key.Wind, which, 0f });
            c.VSSetConstantBuffer(1, passConstants);
            c.PSSetConstantBuffer(1, passConstants);
            c.DrawIndexed((uint)treeIndexCount, 0, 0);
        }
        c.OMSetBlendState(null);
        c.UnsetRenderTargets();
        c.VSSetShaderResources(0, new ID3D11ShaderResourceView?[2]!);
        c.GenerateMips(view);
        treeMaps.Add((key, tex, rtv, view));
        return view;
    }

    WindowLeaves.Rig Rig(int n)
    {
        if (rigs.TryGetValue(n, out var r)) return r;
        using var s = typeof(D3DWall).Assembly.GetManifestResourceStream($"textures/rig{n}.json") ?? throw new FileNotFoundException($"rig{n}");
        using var reader = new StreamReader(s);
        return rigs[n] = WindowLeaves.Rig.FromJson(reader.ReadToEnd());
    }

    /// The window's plant posed for `key`, drawn first if it is not one of the last few; null when this plant does not
    /// move (it then stays the still picture). MetalWall.posedPlant().
    ID3D11ShaderResourceView? PosedPlant(LeafPose key, PlantPicture plant)
    {
        int n = plant == PlantPicture.Photo2 ? 2 : plant == PlantPicture.Photo5 ? 5 : 0;
        if (n == 0 && plant != PlantPicture.Ficus) return null;
        int i = posed.FindIndex(m => m.key == key);
        if (i >= 0) { var m = posed[i]; posed.RemoveAt(i); posed.Add(m); return m.view; }
        ID3D11ShaderResourceView? photo = null, ids = null;
        if (n != 0) { photo = Picture($"rigphoto{n}"); ids = Picture($"rigid{n}"); }
        if (ficusVs == null)
        {
            var moving = MovingSource;
            rigVs = Device.CreateVertexShader(ShaderCompiler.Compile(moving, "rigVertex", "vs_4_0"));
            rigPs = Device.CreatePixelShader(ShaderCompiler.Compile(moving, "rigFragment", "ps_4_0"));
            ficusVs = Device.CreateVertexShader(ShaderCompiler.Compile(moving, "ficusVertex", "vs_4_0"));
            ficusPs = Device.CreatePixelShader(ShaderCompiler.Compile(moving, "ficusFragment", "ps_4_0"));
            // the pieces: one depth layer per channel, darkest wins (blendEquation(MIN), colorMask per layer)
            darkestIn = new[] { ColorWriteEnable.Red, ColorWriteEnable.Green, ColorWriteEnable.Blue }.Select(mask =>
            {
                var d = new BlendDescription(Blend.One, Blend.One, Blend.One, Blend.One);
                d.RenderTarget[0].BlendOperation = BlendOperation.Min;
                d.RenderTarget[0].BlendOperationAlpha = BlendOperation.Min;
                d.RenderTarget[0].RenderTargetWriteMask = mask;
                return Device.CreateBlendState(d);
            }).ToArray();
            // the ficus: every shape multiplies what is there (the canvas's 'multiply' over white)
            multiply ??= Device.CreateBlendState(new BlendDescription(Blend.Zero, Blend.SourceColor, Blend.Zero, Blend.SourceAlpha));
            (profile, profileView) = Floats(WindowLeaves.LeafProfile, Format.R32_Float, 1);
        }
        ID3D11Texture2D tex; ID3D11RenderTargetView rtv; ID3D11ShaderResourceView view;
        if (posed.Count >= Kept) { (_, tex, rtv, view) = posed[0]; posed.RemoveAt(0); }
        else (tex, rtv, view) = PassTarget(Format.R8G8B8A8_UNorm, 1024, 1024);

        var c = Context;
        UnbindPictures();
        c.OMSetRenderTargets(rtv);
        c.ClearRenderTargetView(rtv, new Color4(1, 1, 1, 1));
        c.RSSetViewport(new Viewport(0, 0, 1024, 1024));
        c.RSSetState(bothSides);
        c.IASetInputLayout(null);
        c.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        if (n != 0)
        {
            var rig = Rig(n);
            c.VSSetShader(rigVs);
            c.PSSetShader(rigPs);
            c.PSSetShaderResource(0, photo!);
            c.PSSetShaderResource(1, ids!);
            c.PSSetSampler(0, pieceSampler);
            int layerCount = n == 2 ? WindowLeaves.Plant2Layers : 3;
            for (int layer = 0; layer < layerCount; layer++)
            {
                var inst = rig.Pose(key.T + WindowLeaves.Layers[layer].Off, key.Ms, 31 * layer + 3);
                var place = WindowLeaves.Place(n, rig, layer);
                UploadPass(new[] { place[0], place[1], place[2], place[3], (float)rig.W, (float)rig.H, 0f, 0f });
                c.VSSetShaderResource(0, Shapes(inst));
                c.VSSetConstantBuffer(1, passConstants);
                c.PSSetConstantBuffer(1, passConstants);
                c.OMSetBlendState(darkestIn![layer]);
                c.DrawInstanced(4, (uint)rig.Count, 0, 0);
            }
        }
        else
        {
            var all = WindowLeaves.Ficus(key.T, key.Ms, ficusMemos);
            c.VSSetShader(ficusVs);
            c.PSSetShader(ficusPs);
            c.VSSetShaderResource(0, Shapes(all));
            c.PSSetShaderResource(0, profileView!);
            c.OMSetBlendState(multiply);
            c.DrawInstanced(4, (uint)(all.Length / WindowLeaves.FicusStride), 0, 0);
        }
        c.OMSetBlendState(null);
        c.UnsetRenderTargets();
        c.VSSetShaderResources(0, new ID3D11ShaderResourceView?[2]!);
        c.GenerateMips(view);
        posed.Add((key, tex, rtv, view));
        return view;
    }

    static int Bytes(Format f) => f == Format.R8_UNorm ? 1 : f is Format.R8G8_UNorm or Format.R8G8_UInt ? 2 : 4;

    // ---------- drawing ----------

    /// Groups of four floats in wall.hlsl's WallUniforms (Mac update 1.4: twelve, the new scenes' numbers last).
    const int BlockGroups = 12;

    /// The uniform block of wall.hlsl (WallUniforms): twelve groups of four 32-bit floats.
    static float[] Block(WallUniformValues u, int ox, int oy)
    {
        float F(double x) => (float)x;
        return new[]
        {
            F(u.Res[0]), F(u.Res[1]), F(u.WallH), F(u.Tex),
            F(u.Src), F(u.Corner), F(u.Phase), F(u.Calm),
            F(u.Depth[0]), F(u.Depth[1]), F(u.Depth[2]), F(u.WT),
            F(u.PlantRect[0]), F(u.PlantRect[1]), F(u.PlantRect[2]), F(u.PlantRect[3]),
            F(u.L1[0]), F(u.L1[1]), F(u.L1[2]), F(u.Rough),
            F(u.C1[0]), F(u.C1[1]), F(u.C1[2]), F(u.FrostBottom),
            F(u.Win1[0]), F(u.Win1[1]), F(u.Win1[2]), F(u.Win1[3]),
            F(u.WallLin[0]), F(u.WallLin[1]), F(u.WallLin[2]), u.Scene,
            F(u.Amb[0]), F(u.Amb[1]), F(u.Amb[2]), u.Layers,
            u.Weather, F(u.T), F(u.Wind), F(u.WindLeaves),
            u.Mirror ? 1 : 0, ox, oy, 0,
            F(u.PhaseDay), F(u.GlassNight), F(u.Rainbow), 0,
        };
    }

    unsafe void Upload(float[] block)
    {
        var m = Context.Map(constants, MapMode.WriteDiscard);
        fixed (float* p = block) Buffer.MemoryCopy(p, (void*)m.DataPointer, 16 * BlockGroups, block.Length * 4);
        Context.Unmap(constants, 0);
    }

    /// Puts one frame into `target` at (`ox`, `oy`), the frame's size its size. With `from`, the frame is `from` and `u`
    /// mixed, `mix` the share of `u` (a crossfade of two motions): `from` is drawn first, `u` over it. Call within Lock.
    public void Encode(WallUniformValues u, WallUniformValues? from, double mix, ID3D11RenderTargetView target, int ox, int oy)
    {
        bool mixingNow = from != null && mix < 1;
        var names = PictureNames(u);
        if (mixingNow) names.UnionWith(PictureNames(from!));
        var crown0 = names.Contains("crown0") ? Picture("crown0") : blank;
        var crown1 = names.Contains("crown1") ? Picture("crown1") : blank;
        // the plant's picture: the still one, or (Mac update 1.4) the plant posed in the wind, for each motion of a crossfade
        ID3D11ShaderResourceView PlantView(WallUniformValues v)
        {
            if (!v.ReadsPlant) return blank;
            // a plant whose pieces cannot be read stays still rather than leaving the frame undrawn
            if (v.Leaves is LeafPose key)
            {
                try { if (PosedPlant(key, v.Plant) is { } t) return t; }
                catch (Exception e) when (e is not SharpGen.Runtime.SharpGenException) { Log.Write($"d3d: the plant in the wind could not be drawn: {e.Message}"); }
            }
            return v.Plant switch
            {
                PlantPicture.Photo2 => Picture("photo2"),
                PlantPicture.Photo5 => Picture("photo5"),
                PlantPicture.Ficus => Picture("ficus"),
                PlantPicture.Vase => Picture("vase"),
                _ => blank,
            };
        }
        var fromPlant = mixingNow ? PlantView(from!) : blank;
        var plant = PlantView(u);
        var frost = names.Contains("frost") ? Picture("frost") : blank;
        // the Tree: its shadow under this sun is drawn first (and the old motion's too, while two are crossfaded)
        var tree = u.ReadsTree ? Picture("tree") : blank;
        ID3D11ShaderResourceView treeMap = blank, fromTreeMap = blank;
        if (u.ReadsTree && u.Tree is TreeMapKey tk)
        {
            if (mixingNow && from!.Tree is TreeMapKey fk) fromTreeMap = TreeMap(fk);
            treeMap = TreeMap(tk);
        }
        var first = mixingNow ? from! : u;

        var c = Context;
        c.OMSetRenderTargets(target);
        c.RSSetViewport(new Viewport(ox, oy, (float)u.Res[0], (float)u.Res[1]));
        c.RSSetState(bothSides);
        c.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        c.IASetInputLayout(null);
        c.VSSetShader(vs);
        c.PSSetShader(first.Moving ? MovingShader() : ps);
        c.PSSetConstantBuffer(0, constants);
        c.PSSetShaderResource(0, crown0);
        c.PSSetShaderResource(1, crown1);
        c.PSSetShaderResource(2, mixingNow ? fromPlant : plant);
        c.PSSetShaderResource(3, frost);
        c.PSSetShaderResource(4, tree);
        c.PSSetShaderResource(5, mixingNow ? fromTreeMap : treeMap);
        c.PSSetSampler(0, repeat);
        c.PSSetSampler(1, clamp);
        c.PSSetSampler(2, mirror);
        c.OMSetBlendState(null);
        Upload(Block(first, ox, oy));
        c.Draw(3, 0);
        if (mixingNow)
        {
            float a = (float)Math.Max(0, mix);
            c.PSSetShader(u.Moving ? MovingShader() : ps);
            c.PSSetShaderResource(2, plant);
            c.PSSetShaderResource(5, treeMap);
            c.OMSetBlendState(mixing, new Color4(a, a, a, a));
            Upload(Block(u, ox, oy));
            c.Draw(3, 0);
            c.OMSetBlendState(null);
        }
        c.UnsetRenderTargets();
    }

    /// Puts one frame into `dest` (a DirectComposition surface's texture) at (`ox`, `oy`). With `readBack` the frame is
    /// drawn off the screen first, copied in, and its pixels returned (rows from the top, blue, green, red, 255): the
    /// checks then see exactly what the screen got. Call within Lock.
    public byte[]? Frame(WallUniformValues u, WallUniformValues? from, double mix, ID3D11Texture2D dest, int ox, int oy, bool readBack)
    {
        if (!readBack)
        {
            using var rtv = Device.CreateRenderTargetView(dest);
            Encode(u, from, mix, rtv, ox, oy);
            return null;
        }
        int w = (int)u.Res[0], h = (int)u.Res[1];
        using var target = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget));
        using (var rtv = Device.CreateRenderTargetView(target)) Encode(u, from, mix, rtv, 0, 0);
        Context.CopySubresourceRegion(dest, 0, (uint)ox, (uint)oy, 0, target, 0, null);
        return Read(target, w, h);
    }

    /// Waits until the graphics card has finished everything asked of it so far (a still frame's time is then its real
    /// cost, not only the time to ask for it). Takes Lock while it asks, not while it waits.
    public void Finish()
    {
        ID3D11Query q;
        lock (Lock)
        {
            q = Device.CreateQuery(new QueryDescription(QueryType.Event));
            Context.End(q);
            Context.Flush();
        }
        using (q)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                lock (Lock) { if (Context.IsDataAvailable(q)) return; }
                Thread.Sleep(1);
            }
        }
    }

    byte[] Read(ID3D11Texture2D target, int w, int h)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.None,
            ResourceUsage.Staging, CpuAccessFlags.Read));
        Context.CopyResource(staging, target);
        var m = Context.Map(staging, 0, MapMode.Read);
        try
        {
            var o = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Marshal.Copy(m.DataPointer + (nint)(y * m.RowPitch), o, y * w * 4, w * 4);
            // the surface ignores alpha; the pictures in memory have 255 there
            for (int i = 3; i < o.Length; i += 4) o[i] = 255;
            return o;
        }
        finally { Context.Unmap(staging, 0); }
    }

    // ---------- frames off the screen ----------

    /// A frame drawn off the screen and read back: the checks, the own wall's small pictures. Rows from the top,
    /// blue, green, red, 255 (as the walls' pictures in memory).
    public byte[] Image(WallUniformValues u, WallUniformValues? from = null, double mix = 1)
    {
        int w = (int)u.Res[0], h = (int)u.Res[1];
        lock (Lock)
        {
            using var target = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget));
            using var rtv = Device.CreateRenderTargetView(target);
            var token = new object();
            try
            {
                needed[token] = PictureNames(u);
                Encode(u, from, mix, rtv, 0, 0);
                return Read(target, w, h);
            }
            finally { needed.Remove(token); Trim(); }
        }
    }

    public void Dispose()
    {
        lock (Lock)
        {
            foreach (var (_, p) in pictures) { p.view.Dispose(); p.tex.Dispose(); }
            pictures.Clear();
            blank.Dispose(); blankTexture.Dispose();
            mixing.Dispose(); bothSides.Dispose(); repeat.Dispose(); clamp.Dispose(); constants.Dispose(); vs.Dispose(); ps.Dispose();
            foreach (var m in treeMaps) { m.view.Dispose(); m.rtv.Dispose(); m.tex.Dispose(); }
            foreach (var m in posed) { m.view.Dispose(); m.rtv.Dispose(); m.tex.Dispose(); }
            treeMaps.Clear(); posed.Clear();
            foreach (var d in new IDisposable?[] { mirror, pieceSampler, psMoving, treeVs, treePs, multiply, treeGridView, treeGrid, treeIndex,
                                                    passConstants, rigVs, rigPs, ficusVs, ficusPs, shapesView, shapes, profileView, profile })
                d?.Dispose();
            foreach (var b in darkestIn ?? Array.Empty<ID3D11BlendState>()) b.Dispose();
            try { Composition.Dispose(); } catch { }
            try { Context.ClearState(); Context.Flush(); } catch { }
            Context.Dispose();
            Device.Dispose();
        }
    }
}

/// Compiles HLSL with Windows' own compiler (d3dcompiler_47.dll, part of Windows 10 and 11), as the Mac app compiles
/// wall.metal when Metal starts. Called straight, so no other library is needed.
static class ShaderCompiler
{
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int D3DCompile(byte[] src, nuint size, string? name, IntPtr defines, IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string target, uint flags1, uint flags2,
        out IntPtr code, out IntPtr errors);

    const uint OptimizationLevel3 = 1 << 15;

    public static byte[] Compile(string source, string entry, string target)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        int hr = D3DCompile(bytes, (nuint)bytes.Length, "wall.hlsl", IntPtr.Zero, IntPtr.Zero, entry, target, OptimizationLevel3, 0, out var code, out var errors);
        string? messages = errors != IntPtr.Zero ? Text(errors) : null;
        if (errors != IntPtr.Zero) Release(errors);
        if (hr < 0 || code == IntPtr.Zero) throw new InvalidOperationException($"the shader did not compile ({entry}, 0x{hr:X8}): {messages}");
        try { return Bytes(code); }
        finally { Release(code); }
    }

    // ID3DBlob: IUnknown (3), GetBufferPointer, GetBufferSize
    static unsafe byte[] Bytes(IntPtr blob)
    {
        var vt = *(IntPtr**)blob;
        var ptr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)vt[3])(blob);
        var size = ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)vt[4])(blob);
        var o = new byte[(int)size];
        Marshal.Copy(ptr, o, 0, o.Length);
        return o;
    }

    static string Text(IntPtr blob) => System.Text.Encoding.UTF8.GetString(Bytes(blob)).TrimEnd('\0');

    static unsafe void Release(IntPtr unknown)
    {
        var vt = *(IntPtr**)unknown;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vt[2])(unknown);
    }
}
