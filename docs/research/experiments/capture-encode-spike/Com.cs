using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClipShiftSpike;

/// <summary>
/// Minimal COM plumbing. Everything is a raw <c>void*</c> to the interface pointer and every call
/// goes through the vtable by index — no RCWs, no <c>__ComObject</c>, no marshalling stubs. This is
/// deliberate: the spike has to be able to claim zero managed allocation on the hot path, and the
/// project's known CsWinRT interop constraint (never cast a <c>__ComObject</c>) makes the projection
/// route the wrong shape here anyway.
/// </summary>
internal static unsafe class Com
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void** Vtbl(void* obj) => *(void***)obj;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int QueryInterface(void* obj, Guid* iid, void** result)
        => ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Vtbl(obj)[0])(obj, iid, result);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AddRef(void* obj)
        => ((delegate* unmanaged[Stdcall]<void*, uint>)Vtbl(obj)[1])(obj);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Release(void* obj)
        => obj == null ? 0u : ((delegate* unmanaged[Stdcall]<void*, uint>)Vtbl(obj)[2])(obj);

    public static void SafeRelease(ref void* obj)
    {
        if (obj != null) { Release(obj); obj = null; }
    }

    /// <summary>QueryInterface that throws with the interface name on failure.</summary>
    public static void* QI(void* obj, in Guid iid, string what)
    {
        void* result;
        Guid local = iid;
        int hr = QueryInterface(obj, &local, &result);
        if (hr < 0) throw new SpikeException($"QueryInterface({what}) failed", hr);
        return result;
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new SpikeException(what + " failed", hr);
    }
}

internal sealed class SpikeException : Exception
{
    public int Hr { get; }

    public SpikeException(string message, int hr = 0)
        : base(hr == 0 ? message : $"{message} (0x{hr:X8})")
        => Hr = hr;
}
