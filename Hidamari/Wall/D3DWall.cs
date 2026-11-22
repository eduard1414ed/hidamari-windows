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
    readonly ID3D11Buffer constants;
    readonly ID3D11SamplerState repeat, clamp;
    readonly ID3D11BlendState mixing;
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
        string source;
        using (var s = typeof(D3DWall).Assembly.GetManifestResourceStream("wall.hlsl")!)
        using (var reader = new StreamReader(s)) source = reader.ReadToEnd();
        vs = Device.CreateVertexShader(ShaderCompiler.Compile(source, "wallVertex", "vs_4_0"));
        ps = Device.CreatePixelShader(ShaderCompiler.Compile(source, "wallFragment", "ps_4_0"));
        ShaderMs = sw.ElapsedMilliseconds;

        constants = Device.CreateBuffer(new BufferDescription(16 * 11, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        // the prototype's textures: linear filtering between pixels and between levels (LINEAR_MIPMAP_LINEAR)
        repeat = Device.CreateSamplerState(SamplerDescription.LinearWrap);
        clamp = Device.CreateSamplerState(SamplerDescription.LinearClamp);
        // new = frame · a + what is there · (1 − a), a = the blend factor: the second motion of a crossfade (Mac update 1.1)
        mixing = Device.CreateBlendState(new BlendDescription(Blend.BlendFactor, Blend.InverseBlendFactor, Blend.One, Blend.Zero));
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
        }
        if (u.ReadsFrost) o.Add("frost");
        return o;
    }

    /// .htex (tools/make-metal-textures.swift on the Mac): "HTX1", width, height, channels (1 or 4), levels as 32-bit
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
            var desc = new Texture2DDescription(channels == 1 ? Format.R8_UNorm : Format.R8G8B8A8_UNorm, (uint)w, (uint)h, 1, (uint)levels,
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
    }

    /// Megabytes of pictures held now (for the journal).
    public double PicturesMB
    {
        get
        {
            lock (Lock)
            {
                double sum = 0;
                foreach (var (n, p) in pictures) { var d = p.tex.Description; sum += d.Width * d.Height * (d.Format == Format.R8_UNorm ? 1 : 4) * 4.0 / 3; }
                return sum / 1048576;
            }
        }
    }

    // ---------- drawing ----------

    /// The uniform block of wall.hlsl (WallUniforms): eleven groups of four 32-bit floats.
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
        };
    }

    unsafe void Upload(float[] block)
    {
        var m = Context.Map(constants, MapMode.WriteDiscard);
        fixed (float* p = block) Buffer.MemoryCopy(p, (void*)m.DataPointer, 16 * 11, block.Length * 4);
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
        var plant = blank;
        if (u.ReadsPlant && u.Plant == PlantPicture.Photo2) plant = Picture("photo2");
        if (u.ReadsPlant && u.Plant == PlantPicture.Photo5) plant = Picture("photo5");
        if (u.ReadsPlant && u.Plant == PlantPicture.Ficus) plant = Picture("ficus");
        var frost = names.Contains("frost") ? Picture("frost") : blank;

        var c = Context;
        c.OMSetRenderTargets(target);
        c.RSSetViewport(new Viewport(ox, oy, (float)u.Res[0], (float)u.Res[1]));
        c.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        c.IASetInputLayout(null);
        c.VSSetShader(vs);
        c.PSSetShader(ps);
        c.PSSetConstantBuffer(0, constants);
        c.PSSetShaderResource(0, crown0);
        c.PSSetShaderResource(1, crown1);
        c.PSSetShaderResource(2, plant);
        c.PSSetShaderResource(3, frost);
        c.PSSetSampler(0, repeat);
        c.PSSetSampler(1, clamp);
        c.OMSetBlendState(null);
        Upload(Block(mixingNow ? from! : u, ox, oy));
        c.Draw(3, 0);
        if (mixingNow)
        {
            float a = (float)Math.Max(0, mix);
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
            mixing.Dispose(); repeat.Dispose(); clamp.Dispose(); constants.Dispose(); vs.Dispose(); ps.Dispose();
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
