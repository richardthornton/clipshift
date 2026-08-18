using System.Runtime.InteropServices;

namespace ClipShiftSpike;

/// <summary>
/// NVENC driven directly over the function table, per issue #3. Encodes H.264 High 4:2:0 8-bit,
/// CONSTQP qp=20, zero B-frames, 1-second IDR — the settings locked by issue #10 — and writes a raw
/// elementary stream. No muxer: issue #19 rules the muxer out of the spike on the grounds that it is
/// the riskiest code in the project and contributes nothing to a performance number.
/// </summary>
internal sealed unsafe class Encoder : IDisposable
{
    private nint _lib;
    private NV_ENCODE_API_FUNCTION_LIST _fn;
    private void* _session;

    private readonly Stream _out;
    private readonly int _width, _height;

    private void*[] _nv12 = [];
    private void*[] _rtvLuma = [];
    private void*[] _rtvChroma = [];
    private void*[] _registered = [];
    private void*[] _bitstreams = [];

    /// <summary>
    /// In-flight ring depth. OBS's floor is 4 with B-frames off; #12 specifies ~8 frames (133 ms) for
    /// the app. The spike uses #12's number so backpressure behaves the way the app's will.
    /// </summary>
    public const int RingDepth = 8;

    private int _submitted;      // frames handed to NvEncEncodePicture
    private int _drained;        // frames whose bitstream has been collected

    public long BytesWritten { get; private set; }
    public int KeyFrames { get; private set; }
    public int BackpressureEvents { get; private set; }

    private readonly byte[] _copyBuffer = new byte[4 << 20];   // pre-sized once; never grows on the hot path

    public static bool Diagnostics;

    public Encoder(Gpu gpu, Stream output, int width, int height, int preset, int qp, int gopFrames)
    {
        _out = output;
        _width = width;
        _height = height;

        NvEncStructs.AssertLayout();

        if (!NativeLibrary.TryLoad("nvEncodeAPI64.dll", out _lib))
            throw new SpikeException("nvEncodeAPI64.dll not present — no NVIDIA encoder on this machine");

        var maxVerPtr = (delegate* unmanaged[Cdecl]<uint*, int>)NativeLibrary.GetExport(_lib, "NvEncodeAPIGetMaxSupportedVersion");
        uint maxVer;
        NvCheckRaw(maxVerPtr(&maxVer), "NvEncodeAPIGetMaxSupportedVersion");
        uint needed = (NvEncVer.MajorVersion << 4) | NvEncVer.MinorVersion;
        if (maxVer < needed)
            throw new SpikeException($"driver supports NVENC API {maxVer >> 4}.{maxVer & 0xf}, spike needs {NvEncVer.MajorVersion}.{NvEncVer.MinorVersion}");

        var createInstance = (delegate* unmanaged[Cdecl]<NV_ENCODE_API_FUNCTION_LIST*, int>)
            NativeLibrary.GetExport(_lib, "NvEncodeAPICreateInstance");
        _fn = default;
        _fn.version = NvEncVer.FunctionList;
        fixed (NV_ENCODE_API_FUNCTION_LIST* p = &_fn)
            NvCheckRaw(createInstance(p), "NvEncodeAPICreateInstance");

        OpenSession(gpu.Device);
        Initialise(preset, qp, gopFrames);
        AllocateSurfaces(gpu);
    }

    private void* Fn(int index) => (void*)_fn.fn[index];

    private void OpenSession(void* d3dDevice)
    {
        var p = new NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS
        {
            version = NvEncVer.OpenSessionEx,
            deviceType = 0,           // NV_ENC_DEVICE_TYPE_DIRECTX
            device = d3dDevice,
            apiVersion = NvEncVer.ApiVersion,
        };
        void* session;
        NvCheck(((delegate* unmanaged[Cdecl]<NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS*, void**, int>)
            Fn(NV_ENCODE_API_FUNCTION_LIST.OpenEncodeSessionEx))(&p, &session), "NvEncOpenEncodeSessionEx");
        _session = session;
    }

    private void Initialise(int preset, int qp, int gopFrames)
    {
        Guid codec = NvEncGuids.CodecH264;
        Guid presetGuid = NvEncGuids.Preset(preset);
        const uint tuningHighQuality = 1;

        // Start from the driver's own preset config rather than a hand-built one: the preset carries
        // per-generation defaults we have no business guessing at.
        var pc = new NV_ENC_PRESET_CONFIG { version = NvEncVer.PresetConfig };
        pc.presetCfg.version = NvEncVer.Config;
        NvCheck(((delegate* unmanaged[Cdecl]<void*, Guid, Guid, uint, NV_ENC_PRESET_CONFIG*, int>)
            Fn(NV_ENCODE_API_FUNCTION_LIST.GetEncodePresetConfigEx))
            (_session, codec, presetGuid, tuningHighQuality, &pc), "NvEncGetEncodePresetConfigEx");

        if (Diagnostics)
        {
            Console.WriteLine($"  [diag] presetCfg.version=0x{pc.presetCfg.version:X8} " +
                              $"rcParams.version=0x{pc.presetCfg.rcParams.version:X8} " +
                              $"gopLength={pc.presetCfg.gopLength} frameIntervalP={pc.presetCfg.frameIntervalP} " +
                              $"rcMode={pc.presetCfg.rcParams.rateControlMode}");
            var probe = new NV_ENC_PRESET_CONFIG { version = NvEncVer.PresetConfig };
            probe.presetCfg.version = NvEncVer.Config;
            int legacy = ((delegate* unmanaged[Cdecl]<void*, Guid, Guid, NV_ENC_PRESET_CONFIG*, int>)
                Fn(10 /* nvEncGetEncodePresetConfig */))(_session, codec, presetGuid, &probe);
            Console.WriteLine($"  [diag] legacy GetEncodePresetConfig -> {(NvEncStatus)legacy}, " +
                              $"rcParams.version=0x{probe.presetCfg.rcParams.version:X8} gopLength={probe.presetCfg.gopLength}");
        }

        // Layout check on the returned config. rcParams.version is deliberately *not* used for this:
        // the driver treats it as an [in] field and leaves it zero on the way out, so checking it
        // reads as layout drift when nothing is wrong. What the driver does stamp is the config's own
        // version, and gopLength/frameIntervalP sit past the 16-byte profileGUID — between them they
        // catch a shifted layout.
        if (pc.presetCfg.version != NvEncVer.Config || pc.presetCfg.gopLength == 0
            || pc.presetCfg.frameIntervalP is < 0 or > 16)
            throw new SpikeException(
                $"preset config looks wrong (version=0x{pc.presetCfg.version:X8}, gopLength={pc.presetCfg.gopLength}, " +
                $"frameIntervalP={pc.presetCfg.frameIntervalP}); NV_ENC_CONFIG layout does not match the driver's");

        NV_ENC_CONFIG cfg = pc.presetCfg;
        cfg.version = NvEncVer.Config;
        cfg.profileGUID = NvEncGuids.ProfileHigh;
        cfg.gopLength = (uint)gopFrames;
        cfg.frameIntervalP = 1;               // IPP — zero B-frames, per #10
        cfg.frameFieldMode = 1;               // FRAME
        cfg.mvPrecision = 0;                  // default

        cfg.rcParams.version = NvEncVer.RcParams;
        cfg.rcParams.rateControlMode = 0;     // NV_ENC_PARAMS_RC_CONSTQP
        cfg.rcParams.constQP = new NV_ENC_QP { qpInterP = (uint)qp, qpInterB = (uint)qp, qpIntra = (uint)qp };
        cfg.rcParams.averageBitRate = 0;
        cfg.rcParams.maxBitRate = 0;
        cfg.rcParams.multiPass = 0;           // NV_ENC_MULTI_PASS_DISABLED, per #10
        cfg.rcParams.lookaheadDepth = 0;
        cfg.rcParams.bitFields &= ~(1u << NV_ENC_RC_PARAMS.BitEnableLookahead);
        cfg.rcParams.bitFields &= ~(1u << NV_ENC_RC_PARAMS.BitEnableExtLookahead);
        cfg.rcParams.bitFields &= ~(1u << NV_ENC_RC_PARAMS.BitEnableTemporalAQ);
        cfg.rcParams.bitFields |= 1u << NV_ENC_RC_PARAMS.BitEnableAQ;   // spatial AQ on, per #10

        ref NV_ENC_CONFIG_H264 h264 = ref cfg.encodeCodecConfig.h264Config;
        h264.idrPeriod = (uint)gopFrames;
        h264.chromaFormatIDC = 1;             // 4:2:0
        h264.outputBitDepth = 8;
        h264.inputBitDepth = 8;
        h264.sliceMode = 3;                   // slices per frame
        h264.sliceModeData = 1;
        h264.maxNumRefFrames = 1;             // IPP with one reference
        h264.entropyCodingMode = 1;           // CABAC
        h264.bitFields |= 1u << NV_ENC_CONFIG_H264.BitRepeatSPSPPS;   // SPS/PPS on every IDR
        h264.bitFields &= ~(1u << NV_ENC_CONFIG_H264.BitDisableSPSPPS);

        // Limited-range BT.709 in the SPS VUI, per #10. The container's `colr` box is the muxer's
        // half of that decision and is out of the spike's scope.
        ref NV_ENC_CONFIG_H264_VUI_PARAMETERS vui = ref h264.h264VUIParameters;
        vui.videoSignalTypePresentFlag = 1;
        vui.videoFormat = 5;                  // UNSPECIFIED
        vui.videoFullRangeFlag = 0;           // limited range
        vui.colourDescriptionPresentFlag = 1;
        vui.colourPrimaries = 1;              // BT.709
        vui.transferCharacteristics = 1;      // BT.709
        vui.colourMatrix = 1;                 // BT.709

        var init = new NV_ENC_INITIALIZE_PARAMS
        {
            version = NvEncVer.InitializeParams,
            encodeGUID = codec,
            presetGUID = presetGuid,
            encodeWidth = (uint)_width,
            encodeHeight = (uint)_height,
            darWidth = (uint)_width,
            darHeight = (uint)_height,
            frameRateNum = 60,                // the 60.000 fps grid of #12, not 60000/1001
            frameRateDen = 1,
            enableEncodeAsync = 0,            // synchronous; the spike polls rather than using events
            enablePTD = 1,
            tuningInfo = tuningHighQuality,
            encodeConfig = &cfg,
        };
        NvCheck(((delegate* unmanaged[Cdecl]<void*, NV_ENC_INITIALIZE_PARAMS*, int>)
            Fn(NV_ENCODE_API_FUNCTION_LIST.InitializeEncoder))(_session, &init), "NvEncInitializeEncoder");
    }

    private void AllocateSurfaces(Gpu gpu)
    {
        _nv12 = new void*[RingDepth];
        _rtvLuma = new void*[RingDepth];
        _rtvChroma = new void*[RingDepth];
        _registered = new void*[RingDepth];
        _bitstreams = new void*[RingDepth];

        for (int i = 0; i < RingDepth; i++)
        {
            _nv12[i] = gpu.CreateNv12Texture(_width, _height);
            _rtvLuma[i] = gpu.CreatePlaneRtv(_nv12[i], chroma: false);
            _rtvChroma[i] = gpu.CreatePlaneRtv(_nv12[i], chroma: true);

            var reg = new NV_ENC_REGISTER_RESOURCE
            {
                version = NvEncVer.RegisterResource,
                resourceType = 0,                 // NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX
                width = (uint)_width,
                height = (uint)_height,
                pitch = 0,                        // must be 0 for DirectX resources
                subResourceIndex = 0,
                resourceToRegister = _nv12[i],
                bufferFormat = 1,                 // NV_ENC_BUFFER_FORMAT_NV12
                bufferUsage = 0,                  // NV_ENC_INPUT_IMAGE
            };
            NvCheck(((delegate* unmanaged[Cdecl]<void*, NV_ENC_REGISTER_RESOURCE*, int>)
                Fn(NV_ENCODE_API_FUNCTION_LIST.RegisterResource))(_session, &reg), "NvEncRegisterResource");
            _registered[i] = reg.registeredResource;

            var bs = new NV_ENC_CREATE_BITSTREAM_BUFFER { version = NvEncVer.CreateBitstreamBuffer };
            NvCheck(((delegate* unmanaged[Cdecl]<void*, NV_ENC_CREATE_BITSTREAM_BUFFER*, int>)
                Fn(NV_ENCODE_API_FUNCTION_LIST.CreateBitstreamBuffer))(_session, &bs), "NvEncCreateBitstreamBuffer");
            _bitstreams[i] = bs.bitstreamBuffer;
        }
    }

    public void GetInputTargets(out void* rtvLuma, out void* rtvChroma)
    {
        int slot = _submitted % RingDepth;
        rtvLuma = _rtvLuma[slot];
        rtvChroma = _rtvChroma[slot];
    }

    /// <summary>
    /// True when the in-flight ring is full. #12's rule is count and continue, never block: the caller
    /// folds the tick into a duplicate rather than stalling the pacing grid.
    /// </summary>
    public bool RingFull => _submitted - _drained >= RingDepth;

    public void NoteBackpressure() => BackpressureEvents++;

    /// <summary>Submits the surface the last GetInputTargets handed out. Never blocks.</summary>
    public void Submit(long frameIndex)
    {
        int slot = _submitted % RingDepth;

        var map = new NV_ENC_MAP_INPUT_RESOURCE
        {
            version = NvEncVer.MapInputResource,
            registeredResource = _registered[slot],
        };
        NvCheck(((delegate* unmanaged[Cdecl]<void*, NV_ENC_MAP_INPUT_RESOURCE*, int>)
            Fn(NV_ENCODE_API_FUNCTION_LIST.MapInputResource))(_session, &map), "NvEncMapInputResource");

        var pic = new NV_ENC_PIC_PARAMS
        {
            version = NvEncVer.PicParams,
            inputWidth = (uint)_width,
            inputHeight = (uint)_height,
            inputPitch = 0,                      // 0 for DirectX resources, as at registration
            inputBuffer = map.mappedResource,
            outputBitstream = _bitstreams[slot],
            bufferFmt = 1,                       // NV12
            pictureStruct = 1,                   // NV_ENC_PIC_STRUCT_FRAME
            inputTimeStamp = (ulong)frameIndex,  // integer counter, never a wall-clock stamp (#12)
            inputDuration = 1,
        };
        int status = ((delegate* unmanaged[Cdecl]<void*, NV_ENC_PIC_PARAMS*, int>)
            Fn(NV_ENCODE_API_FUNCTION_LIST.EncodePicture))(_session, &pic);

        _pendingUnmap[slot] = map.mappedResource;
        _submitted++;

        if (status == (int)NvEncStatus.NeedMoreInput) return;   // cannot happen with B-frames off, but be explicit
        NvCheck(status, "NvEncEncodePicture");

        // Synchronous mode: doNotWait is documented to *possibly* return LOCK_BUSY here, and on this
        // driver it returns OUT_OF_MEMORY instead, so the opportunistic drain is not usable. See the
        // encoder-mode note in the spike's README.
        while (DrainOne(wait: true)) { }
    }

    /// <summary>
    /// Duplicate tick: the previous NV12 surface is re-rendered into the next slot rather than
    /// re-submitting the same registered resource, because two in-flight maps of one registered
    /// resource is not documented as legal. The cost is one NV12 copy, and only on duplicate ticks.
    /// </summary>
    public void CopyPreviousInto(Gpu gpu)
    {
        if (_submitted == 0) return;
        int dst = _submitted % RingDepth;
        int src = (_submitted - 1) % RingDepth;
        if (dst != src) gpu.CopyResource(_nv12[dst], _nv12[src]);
    }

    /// <summary>Blocks until one in-flight frame retires. Only called when the ring is full.</summary>
    public void DrainBlocking() => DrainOne(wait: true);

    private readonly void*[] _pendingUnmap = new void*[RingDepth];

    private bool DrainOne(bool wait)
    {
        if (_drained >= _submitted) return false;
        int slot = _drained % RingDepth;

        var lockParams = new NV_ENC_LOCK_BITSTREAM
        {
            version = NvEncVer.LockBitstream,
            outputBitstream = _bitstreams[slot],
            bitFields = wait ? 0u : 1u,          // doNotWait
        };
        int status = ((delegate* unmanaged[Cdecl]<void*, NV_ENC_LOCK_BITSTREAM*, int>)
            Fn(NV_ENCODE_API_FUNCTION_LIST.LockBitstream))(_session, &lockParams);
        if (!wait && (status == (int)NvEncStatus.LockBusy || status == (int)NvEncStatus.EncoderBusy)) return false;
        if (status != (int)NvEncStatus.Success)
            NvCheck(status, $"NvEncLockBitstream (slot {slot}, submitted {_submitted}, drained {_drained}, " +
                            $"wait {wait}, hwEncodeStatus {lockParams.hwEncodeStatus})");

        int size = (int)lockParams.bitstreamSizeInBytes;
        if (size > 0)
        {
            if (size > _copyBuffer.Length)
                throw new SpikeException($"bitstream frame of {size} bytes exceeds the pre-sized copy buffer");
            new ReadOnlySpan<byte>(lockParams.bitstreamBufferPtr, size).CopyTo(_copyBuffer);
            _out.Write(_copyBuffer, 0, size);
            BytesWritten += size;
            if (lockParams.pictureType is 2 or 3) KeyFrames++;   // I or IDR
        }

        NvCheck(((delegate* unmanaged[Cdecl]<void*, void*, int>)
            Fn(NV_ENCODE_API_FUNCTION_LIST.UnlockBitstream))(_session, _bitstreams[slot]), "NvEncUnlockBitstream");

        if (_pendingUnmap[slot] != null)
        {
            NvCheck(((delegate* unmanaged[Cdecl]<void*, void*, int>)
                Fn(NV_ENCODE_API_FUNCTION_LIST.UnmapInputResource))(_session, _pendingUnmap[slot]),
                "NvEncUnmapInputResource");
            _pendingUnmap[slot] = null;
        }
        _drained++;
        return true;
    }

    /// <summary>
    /// EOS then drain, per #3: a stop that skips this loses the in-flight tail silently on every
    /// recording.
    /// </summary>
    public void Finish()
    {
        var eos = new NV_ENC_PIC_PARAMS
        {
            version = NvEncVer.PicParams,
            encodePicFlags = 0x8,               // NV_ENC_PIC_FLAG_EOS
        };
        NvCheck(((delegate* unmanaged[Cdecl]<void*, NV_ENC_PIC_PARAMS*, int>)
            Fn(NV_ENCODE_API_FUNCTION_LIST.EncodePicture))(_session, &eos), "NvEncEncodePicture(EOS)");

        while (_drained < _submitted) { if (!DrainOne(wait: true)) break; }
        _out.Flush();
    }

    private void NvCheck(int status, string what)
    {
        if (status == (int)NvEncStatus.Success) return;
        string detail = "";
        if (_session != null)
        {
            var err = ((delegate* unmanaged[Cdecl]<void*, sbyte*>)Fn(NV_ENCODE_API_FUNCTION_LIST.GetLastErrorString))(_session);
            if (err != null) detail = ": " + new string(err);
        }
        throw new SpikeException($"{what} returned {(NvEncStatus)status}{detail}");
    }

    private static void NvCheckRaw(int status, string what)
    {
        if (status != (int)NvEncStatus.Success)
            throw new SpikeException($"{what} returned {(NvEncStatus)status}");
    }

    public void Dispose()
    {
        if (_session != null)
        {
            for (int i = 0; i < _registered.Length; i++)
            {
                if (_pendingUnmap[i] != null)
                    ((delegate* unmanaged[Cdecl]<void*, void*, int>)Fn(NV_ENCODE_API_FUNCTION_LIST.UnmapInputResource))(_session, _pendingUnmap[i]);
                if (_registered[i] != null)
                    ((delegate* unmanaged[Cdecl]<void*, void*, int>)Fn(NV_ENCODE_API_FUNCTION_LIST.UnregisterResource))(_session, _registered[i]);
                if (_bitstreams[i] != null)
                    ((delegate* unmanaged[Cdecl]<void*, void*, int>)Fn(NV_ENCODE_API_FUNCTION_LIST.DestroyBitstreamBuffer))(_session, _bitstreams[i]);
            }
            ((delegate* unmanaged[Cdecl]<void*, int>)Fn(NV_ENCODE_API_FUNCTION_LIST.DestroyEncoder))(_session);
            _session = null;
        }
        for (int i = 0; i < _nv12.Length; i++)
        {
            Com.SafeRelease(ref _rtvLuma[i]);
            Com.SafeRelease(ref _rtvChroma[i]);
            Com.SafeRelease(ref _nv12[i]);
        }
        if (_lib != 0) { NativeLibrary.Free(_lib); _lib = 0; }
    }
}
