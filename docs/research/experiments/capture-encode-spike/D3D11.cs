using System.Runtime.InteropServices;

namespace ClipShiftSpike;

internal static class Iid
{
    public static readonly Guid IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IDXGIAdapter = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
    public static readonly Guid IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    public static readonly Guid IDXGIOutput5 = new("80a07424-ab52-42eb-833c-0c42fd282d98");
    public static readonly Guid ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    public static readonly Guid IDXGIResource = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
    public static readonly Guid ID3D11Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_TEXTURE2D_DESC
{
    public uint Width, Height, MipLevels, ArraySize;
    public int Format;              // DXGI_FORMAT
    public uint SampleCount, SampleQuality;
    public int Usage;               // D3D11_USAGE
    public uint BindFlags, CPUAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_VIEWPORT
{
    public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
}

/// <summary>D3D11_RENDER_TARGET_VIEW_DESC with the TEXTURE2D union arm.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_RENDER_TARGET_VIEW_DESC
{
    public int Format;
    public int ViewDimension;   // D3D11_RTV_DIMENSION_TEXTURE2D = 4
    public uint MipSlice;
    public uint Unused;         // union padding (ArraySlice / FirstArraySlice)
}

/// <summary>D3D11_SHADER_RESOURCE_VIEW_DESC with the TEXTURE2D union arm.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_SHADER_RESOURCE_VIEW_DESC
{
    public int Format;
    public int ViewDimension;   // D3D11_SRV_DIMENSION_TEXTURE2D = 4
    public uint MostDetailedMip;
    public uint MipLevels;
    public uint Unused0, Unused1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_DESC
{
    public uint ModeDescWidth, ModeDescHeight;
    public uint RefreshRateNumerator, RefreshRateDenominator;
    public int Format;
    public int ScanlineOrdering;
    public int Scaling;
    public int DesktopImageInSystemMemory;   // BOOL
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_FRAME_INFO
{
    public long LastPresentTime;
    public long LastMouseUpdateTime;
    public uint AccumulatedFrames;
    public int RectsCoalesced;
    public int ProtectedContentMaskedOut;
    public PointerPosition PointerPosition;
    public uint TotalMetadataBufferSize;
    public uint PointerShapeBufferSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointerPosition
{
    public int X, Y;
    public int Visible;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTPUT_DESC
{
    public unsafe fixed char DeviceName[32];
    public int Left, Top, Right, Bottom;
    public int AttachedToDesktop;
    public int Rotation;
    public nint Monitor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_ADAPTER_DESC1
{
    public unsafe fixed char Description[128];
    public uint VendorId, DeviceId, SubSysId;
    public uint Revision;
    public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public long AdapterLuid;
    public uint Flags;
}

internal static class DxgiFormat
{
    public const int R8G8B8A8_UNORM = 28;
    public const int NV12 = 103;
    public const int B8G8R8A8_UNORM = 87;
    public const int R16G16B16A16_FLOAT = 10;
    public const int R8_UNORM = 61;
    public const int R8G8_UNORM = 49;
}

internal static class Hr
{
    public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
    public const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
}

internal static unsafe partial class Native
{
    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(
        void* pAdapter, int driverType, nint software, uint flags,
        int* pFeatureLevels, uint featureLevels, uint sdkVersion,
        void** ppDevice, int* pFeatureLevel, void** ppImmediateContext);

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(Guid* riid, void** ppFactory);

    [LibraryImport("d3dcompiler_47.dll", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int D3DCompile(
        void* pSrcData, nuint srcDataSize, string? pSourceName,
        void* pDefines, void* pInclude, string pEntrypoint, string pTarget,
        uint flags1, uint flags2, void** ppCode, void** ppErrorMsgs);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWaitableTimerEx(nint attrs, string? name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(nint timer, long* dueTime, int period, nint routine, nint arg,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint ms);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryPerformanceCounter(long* value);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryPerformanceFrequency(long* value);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(long point, uint flags);

    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;
}

/// <summary>Vtable slot indices, taken from the interface declaration order in the SDK headers.</summary>
internal static class V
{
    // IDXGIFactory1 : IDXGIFactory : IDXGIObject : IUnknown
    public const int Factory1_EnumAdapters1 = 12;

    // IDXGIAdapter1 : IDXGIAdapter : IDXGIObject : IUnknown
    public const int Adapter_EnumOutputs = 7;
    public const int Adapter1_GetDesc1 = 10;

    // IDXGIOutput : IDXGIObject : IUnknown
    public const int Output_GetDesc = 7;
    // IDXGIOutput5 : ... : IDXGIOutput1 : IDXGIOutput
    public const int Output1_DuplicateOutput = 22;
    public const int Output5_DuplicateOutput1 = 26;

    // IDXGIOutputDuplication : IDXGIObject : IUnknown
    public const int Dupl_GetDesc = 7;
    public const int Dupl_AcquireNextFrame = 8;
    public const int Dupl_ReleaseFrame = 14;

    // ID3D11Device : IUnknown
    public const int Device_CreateTexture2D = 5;
    public const int Device_CreateShaderResourceView = 7;
    public const int Device_CreateRenderTargetView = 9;
    public const int Device_CreateVertexShader = 12;
    public const int Device_CreatePixelShader = 15;
    public const int Device_CreateSamplerState = 23;
    public const int Device_CheckFormatSupport = 29;
    public const int Device_GetImmediateContext = 40;

    // ID3D11DeviceContext : ID3D11DeviceChild : IUnknown
    public const int Ctx_PSSetShaderResources = 8;
    public const int Ctx_PSSetShader = 9;
    public const int Ctx_PSSetSamplers = 10;
    public const int Ctx_VSSetShader = 11;
    public const int Ctx_Draw = 13;
    public const int Ctx_IASetInputLayout = 17;
    public const int Ctx_IASetPrimitiveTopology = 24;
    public const int Ctx_OMSetRenderTargets = 33;
    public const int Ctx_RSSetState = 43;
    public const int Ctx_RSSetViewports = 44;
    public const int Ctx_CopyResource = 47;
    public const int Ctx_Flush = 111;

    // ID3D11Texture2D : ID3D11Resource : ID3D11DeviceChild : IUnknown
    public const int Resource_SetEvictionPriority = 8;
    public const int Texture2D_GetDesc = 10;

    // ID3D11Multithread : IUnknown
    public const int Multithread_SetMultithreadProtected = 4;

    // ID3DBlob : IUnknown
    public const int Blob_GetBufferPointer = 3;
    public const int Blob_GetBufferSize = 4;
}
