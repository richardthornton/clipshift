# Builds the WAV artifacts for issue #11 - does Resolve free read the containers
# and formats the audio decision depends on?
#
# Usage: build-artifacts.ps1 <output-directory>
#
# Content is a 1 kHz click on every second boundary (same generator as #15) so a
# placement error would be visible, except for the >4 GiB file where a plain sine
# keeps generation fast.

param([Parameter(Mandatory=$true)][string]$Out)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$clicks = "aevalsrc=0.6*sin(2*PI*1000*t)*lt(mod(t\,1)\,0.03):s=48000:c=stereo"
$dur = 30

function Enc($name, $extra) {
    $path = Join-Path $Out $name
    $cmd = "ffmpeg -hide_banner -loglevel warning -y -f lavfi -i `"$clicks`" -t $dur $extra `"$path`""
    Write-Host "-> $name"
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $name" }
}

# 1. Control - plain RIFF, 16-bit stereo. This is the shape #15 already verified.
Enc "ctl-riff-16-stereo.wav" "-rf64 never -c:a pcm_s16le -ac 2"

# 2. The Q5(c) steady state - RIFF carrying a JUNK reservation for a later ds64.
#    If Resolve chokes on this, option (c) fails in its COMMON case, which would be
#    far worse than RF64 failing.
Enc "junk-riff-16-stereo.wav" "-rf64 auto -c:a pcm_s16le -ac 2"

# 3. RF64 parser test - RF64 FourCC and a real ds64 from byte zero, small file.
Enc "rf64-small-16-stereo.wav" "-rf64 always -c:a pcm_s16le -ac 2"

# 4. The locked mic format - 24-bit mono.
Enc "mic-24-mono.wav" "-rf64 never -c:a pcm_s24le -ac 1"

# 5. BWF bext with TimeReference = 14:30:00.000 local as a sample count
#    (52200 s x 48000 = 2,505,600,000 - deliberately above 2^31 so a signed-32
#    bug in the reader shows up as a wrong or negative timecode).
Enc "bext-tc-16-stereo.wav" "-rf64 never -c:a pcm_s16le -ac 2 -write_bext 1 -metadata time_reference=2505600000"

# 6. The real case: 24-bit stereo past the 4 GiB RIFF ceiling.
#    4h15m x 288,000 B/s = 4,406,400,000 bytes > UINT32_MAX.
#    Sine rather than clicks - 735M samples of expression evaluation is slow.
Write-Host "-> rf64-big-24-stereo.wav (4.1 GiB, this one takes a minute)"
$big = Join-Path $Out "rf64-big-24-stereo.wav"
cmd /c "ffmpeg -hide_banner -loglevel warning -y -f lavfi -i `"sine=f=1000:r=48000:d=15300`" -ac 2 -rf64 always -c:a pcm_s24le `"$big`""
if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for the big file" }

Get-ChildItem $Out -Filter *.wav | Select-Object Name, Length | Format-Table -AutoSize
