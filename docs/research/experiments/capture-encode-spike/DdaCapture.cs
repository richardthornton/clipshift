namespace ClipShiftSpike;

internal enum Ownership { Hold, Release }
internal enum ConvertSource { SrvDirect, CopyResource }

internal unsafe delegate void ConvertCallback(void* srv);

/// <summary>What the pacing loop needs from a capture API. Both arms implement exactly this, so #14
/// measures the same loop with a different source underneath.</summary>
internal interface ICapture : IDisposable
{
    int Width { get; }
    int Height { get; }
    bool HasAnySurface { get; }
    int Drain(ConvertCallback convert, out bool accessLost);
}

/// <summary>
/// DXGI Desktop Duplication, per issue #2, with the two variant axes #14 has to compare.
///
/// The axes are not independent, which is worth stating rather than hiding behind a flag matrix: the
/// acquired surface goes invalid the instant ReleaseFrame returns, so under <c>Release</c> the pixels
/// have to be taken out of the surface before it dies — either by CopyResource into a staging texture
/// (what OBS does) or by running the colour convert there and then (which moves the cost rather than
/// removing it, since superseded frames get converted too). Only <c>Hold</c> + <c>SrvDirect</c> is the
/// path the DDA documentation actually points at: no full-frame copy at all.
/// </summary>
internal sealed unsafe class DdaCapture : ICapture
{
    private void* _dupl;
    private readonly Gpu _gpu;

    public Ownership Ownership { get; }
    public ConvertSource Source { get; }

    private void* _heldResource;
    private void* _stagingTex;
    private void* _stagingSrv;

    // DDA cycles a small set of surfaces, so keying SRVs on the texture pointer keeps the steady
    // state free of per-frame view creation.
    private readonly void*[] _srvKeys = new void*[8];
    private readonly void*[] _srvVals = new void*[8];
    private int _srvNext;

    public int Width { get; }
    public int Height { get; }
    public bool UsedDuplicateOutput1 { get; }
    public int DuplicateOutput1Hr { get; } = 1;   // 1 == not attempted
    public int CompositedUiOnlyHr { get; } = 1;   // 1 == not probed

    // §11.2 probe. We cannot name what LastPresentTime is anchored to, but we can measure whether its
    // offset from QPC-now is constant — a constant offset is harmless, a drifting one is not.
    public long PresentAgeSamples { get; private set; }
    public long PresentAgeMinNs { get; private set; } = long.MaxValue;
    public long PresentAgeMaxNs { get; private set; } = long.MinValue;
    public double PresentAgeSumNs { get; private set; }
    public long PointerOnlyUpdates { get; private set; }

    public long AccumulatedFramesLost { get; private set; }
    public long AcquireCount { get; private set; }
    public long TimeoutCount { get; private set; }

    public DdaCapture(Gpu gpu, void* output, Ownership ownership, ConvertSource source, bool probeCompositedUiOnly)
    {
        _gpu = gpu;
        Ownership = ownership;
        Source = source;

        void* output5 = null;
        Guid iid5 = Iid.IDXGIOutput5;
        int hr5 = Com.QueryInterface(output, &iid5, &output5);
        void* dupl = null;

        if (hr5 >= 0)
        {
            // Microsoft's own sample and OBS both pass a multi-entry list; a BGRA-only list is
            // rejected outright on this machine (recorded in DuplicateOutput1Hr), so the list has to
            // include the formats a fullscreen app might actually be presenting in.
            int* formats = stackalloc int[3]
            {
                DxgiFormat.B8G8R8A8_UNORM, DxgiFormat.R8G8B8A8_UNORM, DxgiFormat.R16G16B16A16_FLOAT,
            };
            const uint formatCount = 3;

            if (probeCompositedUiOnly)
            {
                // §11.9 probe: DXGI_OUTDUPL_COMPOSITED_UI_CAPTURE_ONLY is the sole DXGI_OUTDUPL_FLAG
                // member and has no Learn page at all — the URL 404s.
                void* probe = null;
                CompositedUiOnlyHr = ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, int*, void**, int>)
                    Com.Vtbl(output5)[V.Output5_DuplicateOutput1])(output5, gpu.Device, 1, formatCount, formats, &probe);
                if (CompositedUiOnlyHr >= 0)
                {
                    Com.Release(probe);
                    // It was accepted, so the duplication it produced must be dropped before opening
                    // the real one: DXGI allows only one duplication of an output per process.
                }
            }

            int hr = ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, int*, void**, int>)
                Com.Vtbl(output5)[V.Output5_DuplicateOutput1])(output5, gpu.Device, 0, formatCount, formats, &dupl);
            DuplicateOutput1Hr = hr;
            Com.Release(output5);
            if (hr >= 0) UsedDuplicateOutput1 = true;
            else if (hr != Hr.DXGI_ERROR_UNSUPPORTED) throw new SpikeException(Explain(hr), hr);
        }

        if (dupl == null)
        {
            void* output1 = Com.QI(output, Iid.IDXGIOutput1, "IDXGIOutput1");
            int hr1 = ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)
                Com.Vtbl(output1)[V.Output1_DuplicateOutput])(output1, gpu.Device, &dupl);
            Com.Release(output1);
            if (hr1 < 0) throw new SpikeException(Explain(hr1), hr1);
        }
        _dupl = dupl;

        DXGI_OUTDUPL_DESC desc;
        ((delegate* unmanaged[Stdcall]<void*, DXGI_OUTDUPL_DESC*, void>)Com.Vtbl(_dupl)[V.Dupl_GetDesc])(_dupl, &desc);
        Width = (int)desc.ModeDescWidth;
        Height = (int)desc.ModeDescHeight;

        if (Source == ConvertSource.CopyResource)
        {
            _stagingTex = gpu.CreateBgraTexture(Width, Height);
            _stagingSrv = gpu.CreateSrv(_stagingTex, DxgiFormat.B8G8R8A8_UNORM);
        }
    }

    private static string Explain(int hr) => hr switch
    {
        Hr.DXGI_ERROR_UNSUPPORTED =>
            "DuplicateOutput returned DXGI_ERROR_UNSUPPORTED — on a hybrid machine this is the documented "
            + "symptom of duplicating an output that is not driven by this device's adapter",
        Hr.E_ACCESSDENIED =>
            "DuplicateOutput returned E_ACCESSDENIED — a secure desktop (UAC prompt or lock screen) is up",
        _ => "DuplicateOutput failed",
    };

    /// <summary>
    /// Pulls every frame DXGI currently has, converting each one as it arrives, and returns how many
    /// desktop images were consumed.
    ///
    /// The convert happens at acquire time in every variant, and that is forced rather than chosen:
    /// DXGI requires ReleaseFrame before the next AcquireNextFrame, and the surface dies at release,
    /// so a speculative acquire that times out would destroy a surface being kept for the tick. What
    /// the variants then differ on is exactly what the documentation is about — how long the frame
    /// stays *owned*:
    ///   Hold    — ReleaseFrame is deferred until immediately before the next acquire (minimum
    ///             un-owned time, which is what the ReleaseFrame remarks actually ask for).
    ///   Release — ReleaseFrame fires as soon as the pixels are out (what OBS does).
    /// and on whether a full-frame CopyResource sits in front of the convert.
    /// </summary>
    public int Drain(ConvertCallback convert, out bool accessLost)
    {
        accessLost = false;
        int images = 0;

        while (true)
        {
            if (_heldResource != null)
            {
                ((delegate* unmanaged[Stdcall]<void*, int>)Com.Vtbl(_dupl)[V.Dupl_ReleaseFrame])(_dupl);
                ReleaseHeld();
            }

            DXGI_OUTDUPL_FRAME_INFO info;
            void* resource;
            int hr = ((delegate* unmanaged[Stdcall]<void*, uint, DXGI_OUTDUPL_FRAME_INFO*, void**, int>)
                Com.Vtbl(_dupl)[V.Dupl_AcquireNextFrame])(_dupl, 0, &info, &resource);

            if (hr == Hr.DXGI_ERROR_WAIT_TIMEOUT) { TimeoutCount++; return images; }
            if (hr == Hr.DXGI_ERROR_ACCESS_LOST || hr == Hr.DXGI_ERROR_SESSION_DISCONNECTED)
            {
                accessLost = true;
                return images;
            }
            Com.Check(hr, "AcquireNextFrame");

            AcquireCount++;
            if (info.AccumulatedFrames > 1) AccumulatedFramesLost += info.AccumulatedFrames - 1;

            bool hasImage = info.LastPresentTime != 0;
            if (hasImage)
            {
                if (info.LastPresentTime != _lastPresentTime)
                {
                    RecordPresentAge(info.LastPresentTime);
                    _lastPresentTime = info.LastPresentTime;
                }
                _everHadSurface = true;
                images++;

                void* texture = Com.QI(resource, Iid.ID3D11Texture2D, "ID3D11Texture2D from the acquired frame");
                if (Source == ConvertSource.CopyResource)
                {
                    _gpu.CopyResource(_stagingTex, texture);
                    convert(_stagingSrv);
                }
                else
                {
                    convert(SrvFor(texture));
                }
                Com.Release(texture);
            }
            else PointerOnlyUpdates++;

            if (Ownership == Ownership.Hold)
            {
                // Kept owned; the next iteration (or the next tick's drain) releases it.
                _heldResource = resource;
            }
            else
            {
                Com.Release(resource);
                ((delegate* unmanaged[Stdcall]<void*, int>)Com.Vtbl(_dupl)[V.Dupl_ReleaseFrame])(_dupl);
                _heldResource = null;
            }
        }
    }

    private long _lastPresentTime;

    /// <summary>True once at least one real desktop image has been converted.</summary>
    public bool HasAnySurface => _everHadSurface;

    private bool _everHadSurface;

    private void RecordPresentAge(long lastPresentQpc)
    {
        long ageNs = Clock.NowNs() - Clock.TicksToNs(lastPresentQpc);
        PresentAgeSamples++;
        PresentAgeSumNs += ageNs;
        if (ageNs < PresentAgeMinNs) PresentAgeMinNs = ageNs;
        if (ageNs > PresentAgeMaxNs) PresentAgeMaxNs = ageNs;
    }

    private void* SrvFor(void* texture)
    {
        for (int i = 0; i < _srvKeys.Length; i++)
            if (_srvKeys[i] == texture) return _srvVals[i];

        void* srv = _gpu.CreateSrv(texture, DxgiFormat.B8G8R8A8_UNORM);
        int slot = _srvNext++ % _srvKeys.Length;
        if (_srvVals[slot] != null) Com.Release(_srvVals[slot]);
        _srvKeys[slot] = texture;
        _srvVals[slot] = srv;
        return srv;
    }

    private void ReleaseHeld()
    {
        Com.SafeRelease(ref _heldResource);
    }

    public void Dispose()
    {
        if (_heldResource != null)
            ((delegate* unmanaged[Stdcall]<void*, int>)Com.Vtbl(_dupl)[V.Dupl_ReleaseFrame])(_dupl);
        ReleaseHeld();
        for (int i = 0; i < _srvVals.Length; i++) Com.SafeRelease(ref _srvVals[i]);
        Com.SafeRelease(ref _stagingSrv);
        Com.SafeRelease(ref _stagingTex);
        Com.SafeRelease(ref _dupl);
    }
}
