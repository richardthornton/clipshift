using System.Text;

namespace ClipShiftSpike;

/// <summary>
/// D3D11 device plus the BGRA→NV12 colour-convert pass. The conversion renders straight into the
/// encoder's own NV12 texture through two render-target views on its planes, so the only GPU work per
/// frame is the convert itself — there is no full-frame copy unless the caller asks for one
/// (<c>--copy</c>), which exists purely so #14 can measure OBS's unconditional CopyResource against
/// the SRV-direct path the DDA docs point at.
/// </summary>
internal sealed unsafe class Gpu : IDisposable
{
    public void* Device;
    public void* Context;
    public void* Adapter;

    private void* _vs;
    private void* _psY;
    private void* _psUv;
    private void* _sampler;

    private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    private const uint D3D11_BIND_RENDER_TARGET = 0x20;
    private const int D3D11_RTV_DIMENSION_TEXTURE2D = 4;
    private const int D3D11_SRV_DIMENSION_TEXTURE2D = 4;
    private const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3D11_FORMAT_SUPPORT_RENDER_TARGET = 0x4000;

    private const string Hlsl = """
        Texture2D<float4> Src : register(t0);
        SamplerState Smp : register(s0);

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            float2 t = float2((id << 1) & 2, id & 2);
            o.uv  = t;
            o.pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        // Limited-range BT.709, matching the colour decision in issue #10. Values are treated as
        // non-linear R'G'B' throughout: no linearisation, which is what video expects.
        static const float3 Kr_Kg_Kb = float3(0.2126, 0.7152, 0.0722);

        float PSLuma(VSOut i) : SV_Target
        {
            float3 c = Src.Sample(Smp, i.uv).rgb;
            float  y = dot(c, Kr_Kg_Kb);
            return (16.0 + 219.0 * y) / 255.0;
        }

        // Rendered at half resolution with a linear sampler, so each chroma site lands exactly on the
        // corner shared by its 2x2 luma block and the bilinear tap is an exact box average.
        float2 PSChroma(VSOut i) : SV_Target
        {
            float3 c = Src.Sample(Smp, i.uv).rgb;
            float  y = dot(c, Kr_Kg_Kb);
            float  u = (c.b - y) / 1.8556;
            float  v = (c.r - y) / 1.5748;
            return float2((128.0 + 224.0 * u) / 255.0, (128.0 + 224.0 * v) / 255.0);
        }
        """;

    public Gpu(void* adapter)
    {
        Adapter = adapter;
        void* device, context;
        int featureLevel;
        int* levels = stackalloc int[2] { 0xb100, 0xb000 };   // 11_1, 11_0
        Com.Check(Native.D3D11CreateDevice(
            adapter, D3D_DRIVER_TYPE_UNKNOWN, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, 2, D3D11_SDK_VERSION, &device, &featureLevel, &context), "D3D11CreateDevice");
        Device = device;
        Context = context;

        // The capture thread and the encode submission share the immediate context.
        void* mt = Com.QI(Device, Iid.ID3D11Multithread, "ID3D11Multithread");
        ((delegate* unmanaged[Stdcall]<void*, int, int>)Com.Vtbl(mt)[V.Multithread_SetMultithreadProtected])(mt, 1);
        Com.Release(mt);

        CompileShaders();
        CreateSampler();
    }

    public bool Nv12IsRenderTargetable()
    {
        uint support;
        int hr = ((delegate* unmanaged[Stdcall]<void*, int, uint*, int>)Com.Vtbl(Device)[V.Device_CheckFormatSupport])
            (Device, DxgiFormat.NV12, &support);
        return hr >= 0 && (support & D3D11_FORMAT_SUPPORT_RENDER_TARGET) != 0;
    }

    private void CompileShaders()
    {
        byte[] srcBytes = Encoding.ASCII.GetBytes(Hlsl);
        fixed (byte* src = srcBytes)
        {
            void* vsBlob = CompileOne(src, srcBytes.Length, "VSMain", "vs_5_0");
            void* pyBlob = CompileOne(src, srcBytes.Length, "PSLuma", "ps_5_0");
            void* pcBlob = CompileOne(src, srcBytes.Length, "PSChroma", "ps_5_0");
            try
            {
                void* shader;
                Com.Check(((delegate* unmanaged[Stdcall]<void*, void*, nuint, void*, void**, int>)
                    Com.Vtbl(Device)[V.Device_CreateVertexShader])
                    (Device, BlobPtr(vsBlob), BlobSize(vsBlob), null, &shader), "CreateVertexShader");
                _vs = shader;

                Com.Check(((delegate* unmanaged[Stdcall]<void*, void*, nuint, void*, void**, int>)
                    Com.Vtbl(Device)[V.Device_CreatePixelShader])
                    (Device, BlobPtr(pyBlob), BlobSize(pyBlob), null, &shader), "CreatePixelShader(luma)");
                _psY = shader;

                Com.Check(((delegate* unmanaged[Stdcall]<void*, void*, nuint, void*, void**, int>)
                    Com.Vtbl(Device)[V.Device_CreatePixelShader])
                    (Device, BlobPtr(pcBlob), BlobSize(pcBlob), null, &shader), "CreatePixelShader(chroma)");
                _psUv = shader;
            }
            finally
            {
                Com.Release(vsBlob); Com.Release(pyBlob); Com.Release(pcBlob);
            }
        }
    }

    private static void* CompileOne(byte* src, int len, string entry, string target)
    {
        void* code;
        void* errors;
        int hr = Native.D3DCompile(src, (nuint)len, "spike.hlsl", null, null, entry, target, 0, 0, &code, &errors);
        if (hr < 0)
        {
            string msg = errors != null
                ? new string((sbyte*)BlobPtr(errors), 0, (int)BlobSize(errors))
                : "no compiler output";
            Com.Release(errors);
            throw new SpikeException($"D3DCompile({entry}) failed: {msg}", hr);
        }
        Com.Release(errors);
        return code;
    }

    private static void* BlobPtr(void* blob)
        => ((delegate* unmanaged[Stdcall]<void*, void*>)Com.Vtbl(blob)[V.Blob_GetBufferPointer])(blob);

    private static nuint BlobSize(void* blob)
        => ((delegate* unmanaged[Stdcall]<void*, nuint>)Com.Vtbl(blob)[V.Blob_GetBufferSize])(blob);

    private void CreateSampler()
    {
        // D3D11_SAMPLER_DESC, laid out by hand: filter MIN_MAG_MIP_LINEAR (0x15), CLAMP addressing (3).
        Span<byte> desc = stackalloc byte[52];
        desc.Clear();
        fixed (byte* d = desc)
        {
            *(int*)(d + 0) = 0x15;                 // Filter
            *(int*)(d + 4) = 3;                    // AddressU = CLAMP
            *(int*)(d + 8) = 3;                    // AddressV
            *(int*)(d + 12) = 3;                   // AddressW
            *(float*)(d + 16) = 0f;                // MipLODBias
            *(uint*)(d + 20) = 1;                  // MaxAnisotropy
            *(int*)(d + 24) = 1;                   // ComparisonFunc = NEVER
            *(float*)(d + 44) = 0f;                // MinLOD
            *(float*)(d + 48) = float.MaxValue;    // MaxLOD
            void* sampler;
            Com.Check(((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)
                Com.Vtbl(Device)[V.Device_CreateSamplerState])(Device, d, &sampler), "CreateSamplerState");
            _sampler = sampler;
        }
    }

    public void* CreateNv12Texture(int width, int height)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.NV12,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = 0,   // DEFAULT
            BindFlags = D3D11_BIND_RENDER_TARGET,
        };
        void* tex;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, D3D11_TEXTURE2D_DESC*, void*, void**, int>)
            Com.Vtbl(Device)[V.Device_CreateTexture2D])(Device, &desc, null, &tex), "CreateTexture2D(NV12)");

        // OBS pins its encoder surfaces; eviction mid-session would be a stall we would misread as
        // an encoder cost.
        ((delegate* unmanaged[Stdcall]<void*, uint, void>)Com.Vtbl(tex)[V.Resource_SetEvictionPriority])
            (tex, 0x80000000 /* DXGI_RESOURCE_PRIORITY_MAXIMUM */);
        return tex;
    }

    public void* CreateBgraTexture(int width, int height)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNORM,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = 0,
            BindFlags = D3D11_BIND_SHADER_RESOURCE,
        };
        void* tex;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, D3D11_TEXTURE2D_DESC*, void*, void**, int>)
            Com.Vtbl(Device)[V.Device_CreateTexture2D])(Device, &desc, null, &tex), "CreateTexture2D(BGRA)");
        return tex;
    }

    /// <summary>
    /// A 1x1 black BGRA source, used for #12's counted black lead-in — the ticks between T0 (the
    /// record instant) and the first real surface. Converting from it yields legal limited-range
    /// black rather than an undefined surface.
    /// </summary>
    public void* CreateBlackSrv()
    {
        uint black = 0xFF000000;
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNORM,
            SampleCount = 1, SampleQuality = 0, Usage = 1 /* IMMUTABLE */,
            BindFlags = D3D11_BIND_SHADER_RESOURCE,
        };
        // D3D11_SUBRESOURCE_DATA { pSysMem, SysMemPitch, SysMemSlicePitch }
        Span<byte> init = stackalloc byte[16];
        init.Clear();
        void* tex;
        fixed (byte* i = init)
        {
            *(void**)(i + 0) = &black;
            *(uint*)(i + 8) = 4;
            Com.Check(((delegate* unmanaged[Stdcall]<void*, D3D11_TEXTURE2D_DESC*, void*, void**, int>)
                Com.Vtbl(Device)[V.Device_CreateTexture2D])(Device, &desc, i, &tex), "CreateTexture2D(black)");
        }
        void* srv = CreateSrv(tex, DxgiFormat.B8G8R8A8_UNORM);
        Com.Release(tex);
        return srv;
    }

    public void* CreateSrv(void* texture, int format)
    {
        var desc = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = format,
            ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D,
            MostDetailedMip = 0,
            MipLevels = 1,
        };
        void* srv;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, void*, D3D11_SHADER_RESOURCE_VIEW_DESC*, void**, int>)
            Com.Vtbl(Device)[V.Device_CreateShaderResourceView])(Device, texture, &desc, &srv),
            "CreateShaderResourceView");
        return srv;
    }

    /// <summary>
    /// RTV on one plane of an NV12 texture. D3D11 selects the plane from the view format: R8 is the
    /// luma plane, R8G8 the interleaved chroma plane.
    /// </summary>
    public void* CreatePlaneRtv(void* nv12, bool chroma)
    {
        var desc = new D3D11_RENDER_TARGET_VIEW_DESC
        {
            Format = chroma ? DxgiFormat.R8G8_UNORM : DxgiFormat.R8_UNORM,
            ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D,
            MipSlice = 0,
        };
        void* rtv;
        Com.Check(((delegate* unmanaged[Stdcall]<void*, void*, D3D11_RENDER_TARGET_VIEW_DESC*, void**, int>)
            Com.Vtbl(Device)[V.Device_CreateRenderTargetView])(Device, nv12, &desc, &rtv),
            $"CreateRenderTargetView({(chroma ? "chroma" : "luma")} plane)");
        return rtv;
    }

    public void CopyResource(void* dst, void* src)
        => ((delegate* unmanaged[Stdcall]<void*, void*, void*, void>)Com.Vtbl(Context)[V.Ctx_CopyResource])
            (Context, dst, src);

    /// <summary>The whole per-frame GPU cost: two draws, no copy, no allocation.</summary>
    public void ConvertToNv12(void* srcSrv, void* rtvLuma, void* rtvChroma, int width, int height)
    {
        void* ctx = Context;
        void* nullRtv = null;

        ((delegate* unmanaged[Stdcall]<void*, int, void>)Com.Vtbl(ctx)[V.Ctx_IASetPrimitiveTopology])(ctx, 4); // TRIANGLELIST
        ((delegate* unmanaged[Stdcall]<void*, void*, void>)Com.Vtbl(ctx)[V.Ctx_IASetInputLayout])(ctx, null);
        ((delegate* unmanaged[Stdcall]<void*, void*, void**, uint, void>)Com.Vtbl(ctx)[V.Ctx_VSSetShader])(ctx, _vs, null, 0);
        void* sampler = _sampler;
        ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, void>)Com.Vtbl(ctx)[V.Ctx_PSSetSamplers])(ctx, 0, 1, &sampler);
        ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, void>)Com.Vtbl(ctx)[V.Ctx_PSSetShaderResources])(ctx, 0, 1, &srcSrv);

        // Luma pass, full resolution.
        Pass(ctx, _psY, rtvLuma, width, height);
        // Chroma pass, half resolution.
        Pass(ctx, _psUv, rtvChroma, width / 2, height / 2);

        // Leave nothing bound: the acquired DDA surface goes invalid the moment we release it, and a
        // stale SRV binding is exactly how that turns into a device-removed later.
        ((delegate* unmanaged[Stdcall]<void*, uint, void**, void*, void>)Com.Vtbl(ctx)[V.Ctx_OMSetRenderTargets])(ctx, 1, &nullRtv, null);
        void* nullSrv = null;
        ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, void>)Com.Vtbl(ctx)[V.Ctx_PSSetShaderResources])(ctx, 0, 1, &nullSrv);
    }

    private static void Pass(void* ctx, void* ps, void* rtv, int width, int height)
    {
        ((delegate* unmanaged[Stdcall]<void*, uint, void**, void*, void>)Com.Vtbl(ctx)[V.Ctx_OMSetRenderTargets])(ctx, 1, &rtv, null);
        var vp = new D3D11_VIEWPORT { TopLeftX = 0, TopLeftY = 0, Width = width, Height = height, MinDepth = 0, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<void*, uint, D3D11_VIEWPORT*, void>)Com.Vtbl(ctx)[V.Ctx_RSSetViewports])(ctx, 1, &vp);
        ((delegate* unmanaged[Stdcall]<void*, void*, void**, uint, void>)Com.Vtbl(ctx)[V.Ctx_PSSetShader])(ctx, ps, null, 0);
        ((delegate* unmanaged[Stdcall]<void*, uint, uint, void>)Com.Vtbl(ctx)[V.Ctx_Draw])(ctx, 3, 0);
    }

    public void Flush()
        => ((delegate* unmanaged[Stdcall]<void*, void>)Com.Vtbl(Context)[V.Ctx_Flush])(Context);

    public void Dispose()
    {
        Com.SafeRelease(ref _sampler);
        Com.SafeRelease(ref _psUv);
        Com.SafeRelease(ref _psY);
        Com.SafeRelease(ref _vs);
        Com.SafeRelease(ref Context);
        Com.SafeRelease(ref Device);
    }
}
