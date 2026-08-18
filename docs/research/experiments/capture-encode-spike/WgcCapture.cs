using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ClipShiftSpike;

/// <summary>
/// Windows.Graphics.Capture, as the comparison arm #14 needs. Issue #2 chose DDA and ruled WGC out on
/// two grounds — the unsuppressable capture border and a documented per-frame managed object — so this
/// exists to put numbers on that rather than to be an alternative.
///
/// It deliberately uses the CsWinRT projection rather than hand-rolled ABI calls, because that is what
/// ClipShift would actually do, and because §11.6 of display-capture-api.md lists "whether the
/// projection allocates a fresh RCW per TryGetNextFrame" as unsettled. Hand-rolling the interop would
/// answer a question nobody asked. Only the three interop shims that have no projection are raw:
/// IGraphicsCaptureItemInterop, CreateDirect3D11DeviceFromDXGIDevice, and IDirect3DDxgiInterfaceAccess.
///
/// The project's standing CsWinRT constraint is respected throughout: interface pointers come from
/// MarshalInspectable/MarshalInterface or from a vtable QueryInterface, never from casting a
/// __ComObject.
/// </summary>
internal sealed unsafe class WgcCapture : ICapture
{
    private readonly Gpu _gpu;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly IDirect3DDevice _device;
    private readonly ConvertSource _source;

    private void* _stagingTex;
    private void* _stagingSrv;

    private readonly void*[] _srvKeys = new void*[8];
    private readonly void*[] _srvVals = new void*[8];
    private int _srvNext;

    public int Width { get; }
    public int Height { get; }
    public string ItemDisplayName { get; } = "";

    /// <summary>§6 of display-capture-api.md: whether the yellow capture border can be turned off.</summary>
    public string BorderProbe { get; } = "not attempted";
    public bool CursorCaptureDisabled { get; }

    public bool HasAnySurface => FramesDelivered > 0;

    public long FramesDelivered { get; private set; }
    public long NullPolls { get; private set; }

    // §11.2's sibling question for WGC: SystemRelativeTime rather than LastPresentTime.
    public long TimeAgeSamples { get; private set; }
    public long TimeAgeMinNs { get; private set; } = long.MaxValue;
    public long TimeAgeMaxNs { get; private set; } = long.MinValue;
    public double TimeAgeSumNs { get; private set; }

    public static int MonitorSlot = 4;

    private static readonly Guid IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public WgcCapture(Gpu gpu, nint monitorHandle, ConvertSource source)
    {
        _gpu = gpu;
        _source = source;

        if (!GraphicsCaptureSession.IsSupported())
            throw new SpikeException("Windows.Graphics.Capture reports IsSupported() == false on this machine");

        _item = CreateItemForMonitor(monitorHandle);
        ItemDisplayName = _item.DisplayName;
        Width = _item.Size.Width;
        Height = _item.Size.Height;
        if (Width % 2 != 0 || Height % 2 != 0)
            throw new SpikeException("odd capture dimensions; NV12 needs even width and height");

        void* dxgiDevice = Com.QI(gpu.Device, Iid.IDXGIDevice, "IDXGIDevice");
        try
        {
            Com.Check(CreateDirect3D11DeviceFromDXGIDevice((nint)dxgiDevice, out nint devPtr),
                "CreateDirect3D11DeviceFromDXGIDevice");
            _device = MarshalInspectable<IDirect3DDevice>.FromAbi(devPtr);
            Marshal.Release(devPtr);
        }
        finally { Com.Release(dxgiDevice); }

        // Free-threaded so TryGetNextFrame can be polled from the pacing thread without a dispatcher.
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32 { Width = Width, Height = Height });
        _session = _pool.CreateCaptureSession(_item);

        // §6: the border is the decisive problem #2 found. Record what actually happens rather than
        // assuming — IsBorderRequired exists from Windows 11 21H2 and is gated by a capability.
        try
        {
            if (ApiInformation.IsBorderPropertyPresent())
            {
                _session.IsBorderRequired = false;
                BorderProbe = _session.IsBorderRequired
                    ? "IsBorderRequired stayed true after being set false — border NOT suppressed"
                    : "IsBorderRequired accepted false — border suppressed";
            }
            else BorderProbe = "IsBorderRequired property not present on this build";
        }
        catch (Exception e) { BorderProbe = "IsBorderRequired threw: " + e.GetType().Name; }

        try { _session.IsCursorCaptureEnabled = false; CursorCaptureDisabled = true; }
        catch { CursorCaptureDisabled = false; }
        // Put the cursor back: #12 and the standing constraints record the cursor as captured.
        try { _session.IsCursorCaptureEnabled = true; } catch { }

        if (_source == ConvertSource.CopyResource)
        {
            _stagingTex = gpu.CreateBgraTexture(Width, Height);
            _stagingSrv = gpu.CreateSrv(_stagingTex, DxgiFormat.B8G8R8A8_UNORM);
        }

        _session.StartCapture();
    }

    private static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        void* interop;
        Guid interopIid = IGraphicsCaptureItemInterop;
        Com.Check(Com.QueryInterface((void*)factory.ThisPtr, &interopIid, &interop), "QI(IGraphicsCaptureItemInterop)");
        try
        {
            // IGraphicsCaptureItemInterop derives from IUnknown: CreateForWindow(3), CreateForMonitor(4).
            Guid itemIid = GraphicsCaptureItemIid;
            void* raw;
            Com.Check(((delegate* unmanaged[Stdcall]<void*, nint, Guid*, void**, int>)Com.Vtbl(interop)[MonitorSlot])
                (interop, monitor, &itemIid, &raw), "IGraphicsCaptureItemInterop::CreateForMonitor");
            var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi((nint)raw);
            Com.Release(raw);
            return item;
        }
        finally { Com.Release(interop); }
    }

    /// <summary>
    /// Drains everything the frame pool currently holds, converting each frame as it arrives — the
    /// same shape as the DDA path, so the two are measured identically.
    /// </summary>
    public int Drain(ConvertCallback convert, out bool accessLost)
    {
        accessLost = false;
        int images = 0;

        while (true)
        {
            Direct3D11CaptureFrame? frame = _pool.TryGetNextFrame();
            if (frame is null) { NullPolls++; return images; }

            using (frame)
            {
                FramesDelivered++;
                images++;
                RecordTimeAge(frame.SystemRelativeTime.Ticks * 100);

                void* texture = TextureFrom(frame.Surface);
                try
                {
                    if (_source == ConvertSource.CopyResource)
                    {
                        _gpu.CopyResource(_stagingTex, texture);
                        convert(_stagingSrv);
                    }
                    else convert(SrvFor(texture));
                }
                finally { Com.Release(texture); }
            }
        }
    }

    private static void* TextureFrom(IDirect3DSurface surface)
    {
        nint abi = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            void* access;
            Guid accessIid = IDirect3DDxgiInterfaceAccess;
            Com.Check(Com.QueryInterface((void*)abi, &accessIid, &access), "QI(IDirect3DDxgiInterfaceAccess)");
            try
            {
                Guid texIid = Iid.ID3D11Texture2D;
                void* texture;
                // IDirect3DDxgiInterfaceAccess derives from IUnknown: GetInterface(3).
                Com.Check(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Com.Vtbl(access)[3])
                    (access, &texIid, &texture), "IDirect3DDxgiInterfaceAccess::GetInterface");
                return texture;
            }
            finally { Com.Release(access); }
        }
        finally { Marshal.Release(abi); }
    }

    private void RecordTimeAge(long systemRelativeNs)
    {
        long ageNs = Clock.NowNs() - systemRelativeNs;
        TimeAgeSamples++;
        TimeAgeSumNs += ageNs;
        if (ageNs < TimeAgeMinNs) TimeAgeMinNs = ageNs;
        if (ageNs > TimeAgeMaxNs) TimeAgeMaxNs = ageNs;
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

    public void Dispose()
    {
        _session?.Dispose();
        _pool?.Dispose();
        for (int i = 0; i < _srvVals.Length; i++) Com.SafeRelease(ref _srvVals[i]);
        Com.SafeRelease(ref _stagingSrv);
        Com.SafeRelease(ref _stagingTex);
    }

    private static class ApiInformation
    {
        public static bool IsBorderPropertyPresent()
        {
            try
            {
                return Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");
            }
            catch { return false; }
        }
    }
}
