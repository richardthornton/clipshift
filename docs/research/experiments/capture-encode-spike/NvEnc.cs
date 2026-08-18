using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClipShiftSpike;

// NVENC interop, transcribed from nv-codec-headers' nvEncodeAPI.h at SDK 13.1
// (https://github.com/FFmpeg/nv-codec-headers, include/ffnvcodec/nvEncodeAPI.h).
//
// Every struct here carries a compile-time-known size that is asserted against the header's computed
// layout in NvEncStructs.AssertLayout(). That check is the whole defence against the failure mode this
// interop actually has: a silently wrong offset does not crash, it feeds the driver garbage in a field
// it happens to tolerate, and the recording comes out subtly wrong. Assert loudly at startup instead.

internal static class NvEncVer
{
    public const uint MajorVersion = 13;
    public const uint MinorVersion = 1;
    public const uint ApiVersion = MajorVersion | (MinorVersion << 24);

    public static uint StructVersion(uint ver) => ApiVersion | (ver << 16) | (0x7u << 28);

    public static readonly uint FunctionList = StructVersion(2);
    public static readonly uint OpenSessionEx = StructVersion(1);
    public static readonly uint InitializeParams = StructVersion(7) | (1u << 31);
    public static readonly uint Config = StructVersion(9) | (1u << 31);
    public static readonly uint PresetConfig = StructVersion(5) | (1u << 31);
    public static readonly uint PicParams = StructVersion(7) | (1u << 31);
    public static readonly uint LockBitstream = StructVersion(2) | (1u << 31);
    public static readonly uint CreateBitstreamBuffer = StructVersion(1);
    public static readonly uint RegisterResource = StructVersion(5);
    public static readonly uint MapInputResource = StructVersion(4);
    public static readonly uint RcParams = StructVersion(1);
}

internal enum NvEncStatus
{
    Success = 0,
    NoEncodeDevice, UnsupportedDevice, InvalidEncoderDevice, InvalidDevice, DeviceNotExist,
    InvalidPtr, InvalidEvent, InvalidParam, InvalidCall, OutOfMemory, EncoderNotInitialized,
    UnsupportedParam, LockBusy, NotEnoughBuffer, InvalidVersion, MapFailed, NeedMoreInput,
    EncoderBusy, EventNotRegistered, Generic, IncompatibleClientKey, Unimplemented,
    ResourceRegisterFailed, ResourceNotRegistered, ResourceNotMapped, NeedMoreOutput,
}

internal static class NvEncGuids
{
    // GUIDs are written field-wise to match the C initialisers exactly.
    public static readonly Guid CodecH264 = new(0x6bc82762, 0x4e63, 0x4ca4, 0xaa, 0x85, 0x1e, 0x50, 0xf3, 0x21, 0xf6, 0xbf);
    public static readonly Guid ProfileHigh = new(0xe7cbc309, 0x4f7a, 0x4b89, 0xaf, 0x2a, 0xd5, 0x37, 0xc9, 0x2b, 0xe3, 0x10);
    public static readonly Guid PresetP1 = new(0xfc0a8d3e, 0x45f8, 0x4cf8, 0x80, 0xc7, 0x29, 0x88, 0x71, 0x59, 0x0e, 0xbf);
    public static readonly Guid PresetP4 = new(0x90a7b826, 0xdf06, 0x4862, 0xb9, 0xd2, 0xcd, 0x6d, 0x73, 0xa0, 0x86, 0x81);
    public static readonly Guid PresetP5 = new(0x21c6e6b4, 0x297a, 0x4cba, 0x99, 0x8f, 0xb6, 0xcb, 0xde, 0x72, 0xad, 0xe3);
    public static readonly Guid PresetP6 = new(0x8e75c279, 0x6299, 0x4ab6, 0x83, 0x02, 0x0b, 0x21, 0x5a, 0x33, 0x5c, 0xf5);
    public static readonly Guid PresetP7 = new(0x84848c12, 0x6f71, 0x4c13, 0x93, 0x1b, 0x53, 0xe2, 0x83, 0xf5, 0x79, 0x74);

    public static Guid Preset(int p) => p switch
    {
        4 => PresetP4, 5 => PresetP5, 6 => PresetP6, 7 => PresetP7,
        _ => throw new SpikeException($"unsupported preset p{p}; the spike offers p4..p7"),
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NV_ENC_QP { public uint qpInterP, qpInterB, qpIntra; }

[StructLayout(LayoutKind.Sequential)]
internal struct NV_ENC_RC_PARAMS
{
    public uint version;
    public uint rateControlMode;
    public NV_ENC_QP constQP;
    public uint averageBitRate, maxBitRate, vbvBufferSize, vbvInitialDelay;
    public uint bitFields;            // enableMinQP:1 .. reservedBitFields:15
    public NV_ENC_QP minQP, maxQP, initialRCQP;
    public uint temporallayerIdxMask;
    public unsafe fixed byte temporalLayerQP[8];
    public byte targetQuality, targetQualityLSB;
    public ushort lookaheadDepth;
    public byte lowDelayKeyFrameScale;
    public sbyte yDcQPIndexOffset, uDcQPIndexOffset, vDcQPIndexOffset;
    public uint qpMapMode, multiPass, alphaLayerBitrateRatio;
    public sbyte cbQPIndexOffset, crQPIndexOffset;
    public ushort reserved2;
    public uint lookaheadLevel;
    public unsafe fixed byte viewBitrateRatios[7];
    public byte reserved3;
    public uint reserved1;

    // bitFields bit positions
    public const int BitEnableMinQP = 0;
    public const int BitEnableMaxQP = 1;
    public const int BitEnableInitialRCQP = 2;
    public const int BitEnableAQ = 3;
    public const int BitEnableLookahead = 5;
    public const int BitDisableIadapt = 6;
    public const int BitDisableBadapt = 7;
    public const int BitEnableTemporalAQ = 8;
    public const int BitZeroReorderDelay = 9;
    public const int BitEnableNonRefP = 10;
    public const int BitStrictGOPTarget = 11;
    public const int ShiftAqStrength = 12;   // 4 bits
    public const int BitEnableExtLookahead = 16;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NV_ENC_CONFIG_H264_VUI_PARAMETERS
{
    public uint overscanInfoPresentFlag, overscanInfo;
    public uint videoSignalTypePresentFlag, videoFormat, videoFullRangeFlag;
    public uint colourDescriptionPresentFlag, colourPrimaries, transferCharacteristics, colourMatrix;
    public uint chromaSampleLocationFlag, chromaSampleLocationTop, chromaSampleLocationBot;
    public uint bitstreamRestrictionFlag, timingInfoPresentFlag, numUnitInTicks, timeScale;
    public unsafe fixed uint reserved[12];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NV_ENC_CONFIG_H264
{
    public uint bitFields;            // enableTemporalSVC:1 .. reservedBitFields:10
    public uint level, idrPeriod, separateColourPlaneFlag, disableDeblockingFilterIDC;
    public uint numTemporalLayers, spsId, ppsId;
    public uint adaptiveTransformMode, fmoMode, bdirectMode, entropyCodingMode, stereoMode;
    public uint intraRefreshPeriod, intraRefreshCnt, maxNumRefFrames, sliceMode, sliceModeData;
    public NV_ENC_CONFIG_H264_VUI_PARAMETERS h264VUIParameters;
    public uint ltrNumFrames, ltrTrustMode, chromaFormatIDC, maxTemporalLayers;
    public uint useBFramesAsRef, numRefL0, numRefL1, outputBitDepth, inputBitDepth, tfLevel;
    public unsafe fixed uint reserved1[264];
    public unsafe fixed ulong reserved2[64];

    public const int BitOutputAUD = 6;
    public const int BitDisableSPSPPS = 7;
    public const int BitRepeatSPSPPS = 12;
}

/// <summary>
/// The NV_ENC_CODEC_CONFIG union. Its true size is max(H264 1792, HEVC 1456, AV1 1552, reserved 1280)
/// = 1792, i.e. exactly the H.264 arm — so the H.264 arm alone lays the union out correctly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NV_ENC_CODEC_CONFIG { public NV_ENC_CONFIG_H264 h264Config; }

[StructLayout(LayoutKind.Sequential)]
internal struct NV_ENC_CONFIG
{
    public uint version;
    public Guid profileGUID;
    public uint gopLength;
    public int frameIntervalP;
    public uint monoChromeEncoding, frameFieldMode, mvPrecision;
    public NV_ENC_RC_PARAMS rcParams;
    public NV_ENC_CODEC_CONFIG encodeCodecConfig;
    public unsafe fixed uint reserved[278];
    public unsafe fixed ulong reserved2[64];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NV_ENC_PRESET_CONFIG
{
    public uint version, reserved;
    public NV_ENC_CONFIG presetCfg;
    public unsafe fixed uint reserved1[256];
    public unsafe fixed ulong reserved2[64];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_ENC_INITIALIZE_PARAMS
{
    public uint version;
    public Guid encodeGUID, presetGUID;
    public uint encodeWidth, encodeHeight, darWidth, darHeight, frameRateNum, frameRateDen;
    public uint enableEncodeAsync, enablePTD;
    public uint bitFields;
    public uint privDataSize, reserved;
    public void* privData;
    public NV_ENC_CONFIG* encodeConfig;
    public uint maxEncodeWidth, maxEncodeHeight;
    public fixed uint maxMEHintCountsPerBlock[8];   // 2 x NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE (16 bytes each)
    public uint tuningInfo, bufferFormat, numStateBuffers, outputStatsLevel;
    public fixed uint reserved1[284];
    public fixed ulong reserved2[64];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_ENC_PIC_PARAMS
{
    public uint version, inputWidth, inputHeight, inputPitch, encodePicFlags, frameIdx;
    public ulong inputTimeStamp, inputDuration;
    public void* inputBuffer;
    public void* outputBitstream;
    public void* completionEvent;
    public uint bufferFmt, pictureStruct, pictureType;
    private uint _padUnion;                        // the union is 8-aligned; a fixed byte buffer is not
    public fixed byte codecPicParams[1544];        // NV_ENC_CODEC_PIC_PARAMS
    public fixed uint meHintCountsPerBlock[8];
    public void* meExternalHints;
    public fixed uint reserved2[7];
    private uint _pad0;                            // alignment before reserved5
    public fixed ulong reserved5[2];
    public void* qpDeltaMap;
    public uint qpDeltaMapSize, reservedBitFields;
    public fixed ushort meHintRefPicDist[2];
    public int diffPicNumHint;
    public void* alphaBuffer;
    public void* meExternalSbHints;
    public uint meSbHintsCount, stateBufferIdx;
    public void* outputReconBuffer;
    public fixed uint reserved3[284];
    public fixed ulong reserved6[57];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_ENC_LOCK_BITSTREAM
{
    public uint version;
    public uint bitFields;                        // doNotWait:1, ltrFrame:1, getRCStats:1
    public void* outputBitstream;
    public uint* sliceOffsets;
    public uint frameIdx, hwEncodeStatus, numSlices, bitstreamSizeInBytes;
    public ulong outputTimeStamp, outputDuration;
    public void* bitstreamBufferPtr;
    public uint pictureType, pictureStruct, frameAvgQP, frameSatd;
    public uint ltrFrameIdx, ltrFrameBitmap, temporalId, intraMBCount, interMBCount;
    public int averageMVX, averageMVY;
    public uint alphaLayerSizeInBytes, outputStatsPtrSize, reserved;
    public void* outputStatsPtr;
    public uint frameIdxDisplay;
    public fixed uint reserved1[219];
    public fixed ulong reserved2[63];
    public fixed uint reservedInternal[8];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_ENC_REGISTER_RESOURCE
{
    public uint version, resourceType, width, height, pitch, subResourceIndex;
    public void* resourceToRegister;
    public void* registeredResource;
    public uint bufferFormat, bufferUsage;
    public void* pInputFencePoint;
    public fixed uint chromaOffset[2];
    public fixed uint chromaOffsetIn[2];
    public fixed uint reserved1[244];
    public fixed ulong reserved2[61];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_ENC_MAP_INPUT_RESOURCE
{
    public uint version, subResourceIndex;
    public void* inputResource;
    public void* registeredResource;
    public void* mappedResource;
    public uint mappedBufferFmt;
    public fixed uint reserved1[251];
    public fixed ulong reserved2[63];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_ENC_CREATE_BITSTREAM_BUFFER
{
    public uint version, size, memoryHeap, reserved;
    public void* bitstreamBuffer;
    public void* bitstreamBufferPtr;
    public fixed uint reserved1[58];
    public fixed ulong reserved2[64];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS
{
    public uint version, deviceType;
    public void* device;
    public void* reserved;
    public uint apiVersion;
    public fixed uint reserved1[253];
    public fixed ulong reserved2[64];
}

/// <summary>NV_ENCODE_API_FUNCTION_LIST: version, reserved, then 43 entry points and reserved2[275].</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_ENCODE_API_FUNCTION_LIST
{
    public uint version, reserved;
    public fixed ulong fn[318];   // 43 entry points + reserved2[275]

    public const int OpenEncodeSession = 0;
    public const int GetEncodeGUIDCount = 1;
    public const int GetEncodeCaps = 7;
    public const int InitializeEncoder = 11;
    public const int CreateBitstreamBuffer = 14;
    public const int DestroyBitstreamBuffer = 15;
    public const int EncodePicture = 16;
    public const int LockBitstream = 17;
    public const int UnlockBitstream = 18;
    public const int MapInputResource = 25;
    public const int UnmapInputResource = 26;
    public const int DestroyEncoder = 27;
    public const int OpenEncodeSessionEx = 29;
    public const int RegisterResource = 30;
    public const int UnregisterResource = 31;
    public const int GetLastErrorString = 37;
    public const int GetEncodePresetConfigEx = 39;
}

internal static class NvEncStructs
{
    /// <summary>
    /// Sizes computed from nvEncodeAPI.h with MSVC x64 packing rules. If a C# declaration drifts from
    /// the header, this fires at startup rather than corrupting an encode session silently.
    /// </summary>
    public static unsafe void AssertLayout()
    {
        Check<NV_ENC_QP>(12);
        Check<NV_ENC_RC_PARAMS>(128);
        Check<NV_ENC_CONFIG_H264_VUI_PARAMETERS>(112);
        Check<NV_ENC_CONFIG_H264>(1792);
        Check<NV_ENC_CODEC_CONFIG>(1792);
        Check<NV_ENC_CONFIG>(3584);
        Check<NV_ENC_PRESET_CONFIG>(5128);
        Check<NV_ENC_INITIALIZE_PARAMS>(1800);
        Check<NV_ENC_PIC_PARAMS>(3360);
        Check<NV_ENC_LOCK_BITSTREAM>(1544);
        Check<NV_ENC_REGISTER_RESOURCE>(1536);
        Check<NV_ENC_MAP_INPUT_RESOURCE>(1544);
        Check<NV_ENC_CREATE_BITSTREAM_BUFFER>(776);
        Check<NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS>(1552);
        Check<NV_ENCODE_API_FUNCTION_LIST>(2552);

        // Field offsets that a wrong declaration would move without changing the total size.
        CheckOffset("NV_ENC_CONFIG.rcParams", OffsetOfRcParams(), 40);
        CheckOffset("NV_ENC_CONFIG.encodeCodecConfig", OffsetOfCodecConfig(), 168);
        CheckOffset("NV_ENC_PIC_PARAMS.codecPicParams", OffsetOfCodecPicParams(), 80);
        CheckOffset("NV_ENC_INITIALIZE_PARAMS.encodeConfig", OffsetOfEncodeConfig(), 88);
        CheckOffset("NV_ENC_INITIALIZE_PARAMS.tuningInfo", OffsetOfTuningInfo(), 136);
    }

    private static void Check<T>(int expected) where T : unmanaged
    {
        int actual = Unsafe.SizeOf<T>();
        if (actual != expected)
            throw new SpikeException($"struct layout drift: sizeof({typeof(T).Name}) is {actual}, header says {expected}");
    }

    private static void CheckOffset(string what, int actual, int expected)
    {
        if (actual != expected)
            throw new SpikeException($"struct layout drift: {what} at {actual}, header says {expected}");
    }

    private static unsafe int OffsetOfRcParams()
    {
        NV_ENC_CONFIG c = default;
        return (int)((byte*)&c.rcParams - (byte*)&c);
    }

    private static unsafe int OffsetOfCodecConfig()
    {
        NV_ENC_CONFIG c = default;
        return (int)((byte*)&c.encodeCodecConfig - (byte*)&c);
    }

    private static unsafe int OffsetOfCodecPicParams()
    {
        NV_ENC_PIC_PARAMS p = default;
        return (int)((byte*)&p.codecPicParams[0] - (byte*)&p);
    }

    private static unsafe int OffsetOfEncodeConfig()
    {
        NV_ENC_INITIALIZE_PARAMS p = default;
        return (int)((byte*)&p.encodeConfig - (byte*)&p);
    }

    private static unsafe int OffsetOfTuningInfo()
    {
        NV_ENC_INITIALIZE_PARAMS p = default;
        return (int)((byte*)&p.tuningInfo - (byte*)&p);
    }
}
