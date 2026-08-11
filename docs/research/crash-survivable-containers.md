# Crash-survivable containers for ClipShift

Research for [issue #8](https://github.com/richardthornton/clipshift/issues/8). Investigated against primary
sources only: the IETF Matroska and EBML RFCs, FFmpeg's own source and documentation, the OBS Studio
source and first-party engineering writeup, Apple's CAF specification, and Microsoft's Win32
documentation. Where a claim could not be settled from a primary source it is marked as such in
[Open questions](#open-questions).

Date: 2026-08-11.

---

## Recommendation

**Video file — fragmented MP4 written continuously, converted to a plain MP4 by an in-place "soft
remux" at stop.** This is exactly the design OBS shipped as *Hybrid MP4* in 30.2, and the mechanism is
small enough to reimplement: the whole finalisation step is about sixty lines in OBS's muxer. During
the session the file on disk is a valid fragmented MP4 with the initialisation `moov` at the head, so a
kill at any moment leaves a file that plays as-is. At stop, a 16-byte `free` placeholder near the head
is overwritten with an `mdat` header that swallows the entire fragmented body, and a complete
non-fragmented `moov` is appended. No media bytes are copied. The result is a conventional MP4.

**Audio files — RIFF/WAVE (PCM), but with the `RIFF` and `data` size fields patched in place on a short
cadence rather than only at stop.** WAV's trap is real: both size fields sit in the first ~50 bytes and
are written before any audio exists. The fix is cheap — two small in-place writes every second or two.
Under-declaring the size is safe (readers stop early); leaving the `0xFFFFFFFF` placeholder that
FFmpeg writes is not portably safe. Reserve an RF64 `ds64` slot at header time so a session that
crosses 4 GiB can be upgraded in place, because 48 kHz / 24-bit / stereo reaches ~3.86 GiB at exactly
four hours and breaks a few minutes later.

**Sync survives truncation, but only if `t=0` is a property of the bytes rather than of the stop
path.** Truncation only ever removes the tail, so byte 0 of every file still means what it meant
before the crash. What breaks alignment is deferring the start-offset to finalisation. OBS avoids this
deliberately; ClipShift must too. Details in [The sync interaction](#the-sync-interaction).

**Durability cadence — do not call `FlushFileBuffers` on a cadence.** The brief's requirement is that a
*killed process* leaves playable files. Windows' cache manager writes cached file data to disk
independently of the writing process; what loses cached data is "a sudden system failure (such as a
loss of power to the computer)", not process exit
([File Caching, Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching)).
Once bytes have gone through `WriteFile`, a `TerminateProcess` cannot take them back. The variable
worth minimising is therefore the *user-space* buffer occupancy, not the OS cache.

---

## 1. Why a standard MP4 dies with the process

ISO base media file format files are a tree of boxes. The media samples live in `mdat`; everything
needed to interpret them — track headers, sample sizes, sample-to-chunk mapping, chunk offsets,
sync-sample table, timing — lives in `moov`. A non-fragmented muxer cannot write `moov` until it knows
every sample's size and offset, so `moov` goes at the end.

OBS states the failure mode in one sentence in their engineering writeup:

> The `moov` sits at the end and is written when finalising the file, and it is required to be able to
> make sense of the data contained in the `mdat` box.
>
> — [Writing an MP4 Muxer for Fun and Profit, obsproject.com](https://obsproject.com/blog/obs-studio-hybrid-mp4)

A killed process therefore leaves `ftyp` + a partial `mdat` and nothing else. There is no index, no
codec configuration (`avcC`/`hvcC` live in `moov`), and no timing. Every byte of video is on disk and
none of it is addressable.

Recovery after the fact is possible but is *reconstruction*, not repair: tools scan the orphaned `mdat`
for NAL/OBU boundaries and rebuild a sample table, typically borrowing codec configuration and
timescales from a known-good "donor" file recorded with identical settings. That is guesswork about
frame durations and, critically, about the start offset — which is the part that matters for a
multi-file sync brief (see [The sync interaction](#the-sync-interaction)). This class of recovery is
out of scope as a *design*; it is what you fall back to when the design failed.

Worth noting for historical context: OBS's project lead rejected an automatic remux-everything approach
in 2017 partly on the grounds that "a means of MP4 recovery" would be the better answer
([PR #908](https://github.com/obsproject/obs-studio/pull/908)). Seven years later OBS shipped a muxer
that makes recovery unnecessary instead. That is the correct lesson.

## 2. Fragmented MP4

Fragmentation moves the metadata inline. The `moov` written at the head carries an `mvex` box declaring
that the movie is extended by fragments and carries *empty* sample tables; the actual samples arrive as
repeated `moof` + `mdat` pairs, each `moof` describing the samples in the `mdat` that follows it. OBS
summarises the consequence:

> everything up to the last fragment will still be readable
>
> — [obsproject.com](https://obsproject.com/blog/obs-studio-hybrid-mp4)

A truncated fragmented MP4 needs no repair. The head `moov` gives a parser the codec configuration and
timescales; it then walks `moof`/`mdat` pairs until the bytes run out and drops the incomplete tail.

Each track fragment also carries a `tfdt` box giving `baseMediaDecodeTime` — the absolute decode time of
the fragment's first sample. OBS writes it per fragment
([`mp4-mux.c:2305-2324`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-outputs/mp4-mux.c)),
computed as total track duration minus the samples not yet written. This is what makes fragments
self-locating: a fragment knows where it sits on the timeline without reference to anything before it.

The costs OBS enumerated, and which drove them to the hybrid design:

1. **Compatibility.** "Applications, editors, and players offered inconsistent support."
2. **Access latency.** "File browsers couldn't display duration on HDDs or network drives" — because
   there is no duration in the header; you must walk the file.
3. **Seeking.** "Players like Windows Media Player prevented seeking, treating files like livestreams
   rather than completed recordings."
   (all three: [obsproject.com](https://obsproject.com/blog/obs-studio-hybrid-mp4))

There is a fourth cost that matters for a four-hour file and is not in that list: without a global index
(`mfra` at the tail, or `sidx` at the head), seeking means a linear walk of `moof` headers. At a two
second fragment cadence a four-hour recording has ~7,200 fragments. FFmpeg builds this index by reading
the file. Scrubbing in an NLE is exactly the workload that punishes this.

FFmpeg exposes the relevant knobs, though it has no equivalent of the hybrid finalisation:

| Option | FFmpeg's description |
| --- | --- |
| `-movflags frag_keyframe` | "start a new fragment at each video keyframe" |
| `-movflags empty_moov` | writes an initial `moov` with no samples |
| `-movflags delay_moov` | "delay writing the initial moov until the first fragment is cut, or until the first fragment flush" |
| `-movflags global_sidx` | "write a global sidx index at the start of the file" |
| `-movflags default_base_moof` | "avoids writing the absolute base_data_offset field in tfhd atoms, but does so by using the new default-base-is-moof flag" |
| `-movflags faststart` | "Run a second pass moving the index (moov atom) to the beginning of the file" |
| `-frag_duration` / `-min_frag_duration` / `-frag_size` | fragment length in µs / minimum length in µs / bytes of payload |

— [FFmpeg Formats Documentation, "mov, mp4, ismv"](https://ffmpeg.org/ffmpeg-formats.html)

Two of those are traps for this use case. `faststart` is a **second pass over the whole file** — for a
four-hour 1080p60 recording at, say, 40 Mbps that is ~72 GB read and rewritten at stop. So is
`ffmpeg -c copy` remuxing a fragmented file to a plain one. Neither is acceptable as a stop-button
action. `global_sidx` requires seeking back to a reserved slot at the head, which is fine, but the
reserved space must be sized up front.

## 3. Matroska / MKV

Matroska is resilient because it never needs to go back and fix anything. It is now specified by
[RFC 9559](https://www.rfc-editor.org/rfc/rfc9559.html), which lists among the format's design goals:

> Error resilience (can recover playback even when the stream is damaged)
>
> — [RFC 9559 §1](https://www.rfc-editor.org/rfc/rfc9559.html)

The mechanism is EBML's unknown-size encoding. Per
[RFC 8794 §6.2](https://www.rfc-editor.org/rfc/rfc8794.html):

> An Element Data Size with all VINT_DATA bits set to one is reserved as an indicator that the size of
> the EBML Element is unknown.

and only Master Elements whose schema sets `unknownsizeallowed` may use it. RFC 9559 sets
`unknownsizeallowed: True` on both `Segment` (§5.1.1) and `Cluster` (§5.1.3). A parser ends an
unknown-sized element at whichever comes first of a valid sibling, a valid parent, a new Root Element,
or **end of file** (RFC 8794 §6.2). Truncation is therefore not an error condition — it is a defined
terminator.

FFmpeg's muxer relies on precisely this. It writes the Segment with the unknown-size marker at header
time, unconditionally, and only patches it at stop:

```c
put_ebml_id(pb, MATROSKA_ID_SEGMENT);
put_ebml_size_unknown(pb, 8);
mkv->segment_offset = avio_tell(pb);
```
— [`matroskaenc.c:2685-2687`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/matroskaenc.c)

```c
if (endpos - mkv->segment_offset < (1ULL << 56) - 1) {
    if ((ret64 = avio_seek(pb, mkv->segment_offset - 8, SEEK_SET)) < 0)
        ...
    put_ebml_length(pb, endpos - mkv->segment_offset, 8);
```
— [`matroskaenc.c:3397-3400`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/matroskaenc.c)

`put_ebml_size_unknown(pb, 8)` writes `0x01` followed by seven `0xFF` bytes
([`matroskaenc.c:331-337`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/matroskaenc.c)).
So for the entire duration of a recording the on-disk file is a **structurally valid, complete**
Matroska document that happens to be still growing. Kill the process and you have a legal file. No
repair step exists because none is needed.

Everything a demuxer requires — the EBML header, `Info` (with `TimestampScale`), and `Tracks` — is
written before the first Cluster, and each Cluster carries an absolute `Timestamp`
(RFC 9559 §5.1.3.1). `SeekHead` (§6.3) and `Cues` (§22) are indexes only; RFC 9559 §4.5 notes a parser
without them must "hunt and peck" through the file but can still seek.

**The loss window is larger than people assume.** FFmpeg buffers each Cluster entirely in memory
(`avio_open_dyn_buf`, [`matroskaenc.c:798-802`, `3157`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/matroskaenc.c))
and only emits it when a limit trips:

```c
// start a new cluster every 5 MB or 5 sec, or 32k / 1 sec for streaming or
// after 4k and on a keyframe
if (IS_SEEKABLE(pb, mkv)) {
    if (mkv->cluster_time_limit < 0)
        mkv->cluster_time_limit = 5000;
    if (mkv->cluster_size_limit < 0)
        mkv->cluster_size_limit = 5 * 1024 * 1024;
```
— [`matroskaenc.c:2742-2754`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/matroskaenc.c)

So an FFmpeg-written MKV risks up to 5 seconds or 5 MiB, whichever comes first, of *never-written*
data. At 40 Mbps (5 MB/s) the size limit dominates and the window is ~1 second; at lower bitrates the
time limit dominates and it is 5 seconds. Tunable via `-cluster_time_limit` / `-cluster_size_limit`.

## 4. What OBS actually does, and why

Two distinct mechanisms, added twelve years apart.

### 4.1 Automatic remux (2017)

[PR #908](https://github.com/obsproject/obs-studio/pull/908) proposed: if the user picks MP4, record
MKV and remux to MP4 after stopping, deleting the MKV. The MKV gives crash resilience; the MP4 gives
compatibility. Project lead jp9000 rejected making it unconditional — "Remuxing requires a lot of space
and a lot of time to process" — and asked for MP4 recovery instead. It shipped as an opt-in setting
(*Automatically remux to mp4*), off by default. This is the honest characterisation: it works, it costs
a full read+write of the file at stop, and it requires roughly double the free disk space at the moment
you can least afford a failure.

For a four-hour ClipShift session this is disqualifying on cost alone.

### 4.2 Hybrid MP4 (OBS 30.2, 2024)

OBS wrote their own MP4 muxer, roughly 3,000 lines of C, specifically to solve this. Their KB states the
outcome plainly:

> [Hybrid MP4/MOV files] remain recoverable even if writing the file is aborted, e.g. due to system
> crashes or power outages.
>
> — [Hybrid MP4 & Hybrid MOV Formats, obsproject.com](https://obsproject.com/kb/hybrid-mp4)

**The mechanism, verified against the source.** On the first fragment flush the muxer writes, in order:

```c
if (!mux->fragments_written) {
    mp4_write_ftyp(mux, true);
    /* Placeholder to write mdat header during soft-remux */
    mux->placeholder_offset = serializer_get_pos(s);
    mp4_write_free(mux);
}
...
// Write initial incomplete moov (because fragmentation)
if (!mux->fragments_written) {
    mp4_write_moov(mux, true);
```
— [`mp4-mux.c:2614-2637`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-outputs/mp4-mux.c)

The `free` box is exactly 16 bytes, sized so it can later become a 64-bit `mdat` header
(`u32 size=1` + `'mdat'` + `u64 largesize`):

```c
/* Write a 16-byte free box, so it can be replaced with a 64-bit size
 * box header (u32 + char[4] + u64) */
s_wb32(s, 16);
s_write(s, mux->flavor == FLAVOR_MOV ? "wide" : "free", 4);
s_wb64(s, 0);
```
— [`mp4-mux.c:138-149`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-outputs/mp4-mux.c)

So the **live** layout is:

```
ftyp (fragmented brands: iso6 …)
free (16 bytes)                <- placeholder_offset
moov (initialisation: mvex, empty sample tables, edit lists)
moof + mdat                    (fragment 1)
moof + mdat                    (fragment 2)
…
```

That is an ordinary fragmented MP4. Kill the process here and it plays.

At stop, `mp4_mux_finalise` flushes the last fragment, appends a full non-fragmented `moov`, rewrites
`ftyp` with non-fragmented brands, then overwrites the placeholder:

```c
serializer_seek(s, 0, SERIALIZE_SEEK_START);
mp4_write_ftyp(mux, false);

size_t data_size = data_end - mux->placeholder_offset;
serializer_seek(s, (int64_t)mux->placeholder_offset, SERIALIZE_SEEK_START);

/* If data is more than 4 GiB the mdat header becomes 16 bytes, hence
 * why we create a 16-byte placeholder "free" box at the start. */
if (data_size > UINT32_MAX) {
    s_wb32(s, 1); // 1 = use "largesize" field instead
    s_write(s, "mdat", 4);
    s_wb64(s, data_size); // largesize (64-bit)
} else {
    s_wb32(s, (uint32_t)data_size);
    s_write(s, "mdat", 4);
}
```
— [`mp4-mux.c:2955-3016`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-outputs/mp4-mux.c)

The trick worth naming, because it is the whole idea: **the new `mdat` swallows the initialisation
`moov` and every `moof` as opaque payload.** The final layout is `ftyp | mdat (huge) | moov`, with
exactly one visible `moov`. Sample offsets in that final `moov` are absolute file positions, and no
sample byte ever moved, so they are still correct. The `ftyp` rewrite is byte-length-preserving —
OBS pads with a dummy `obs1` brand specifically to keep the size identical
([`mp4-mux.c:111-116`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-outputs/mp4-mux.c)).

Total I/O at stop: write the `moov` (tens of MB for four hours), plus two small seeks. **Independent of
recording length.**

**Fragment cadence.** There is no time constant. OBS fragments on video keyframes: when a keyframe
arrives its PTS becomes the target, and the fragment flushes once every track has caught up to it.

```c
/* Set fragmentation PTS if packet is keyframe and PTS > 0 */
if (parsed_packet.keyframe && parsed_packet.pts > 0) {
    mux->next_frag_pts = packet_pts_usec(&parsed_packet);
}
```
— [`mp4-mux.c:2923-2926`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-outputs/mp4-mux.c)

So the unwritten window is one GOP on average and two GOPs worst case (the completed GOP waiting for
audio to catch up, plus the GOP in progress). At a 2-second keyframe interval that is **2–4 seconds**.

**Known caveat, first-party.** Chapter markers are not crash-survivable:

> Chapters are written during the finalisation process, i.e. they are not recoverable in the event of a
> crash.
>
> — [obsproject.com/kb/hybrid-mp4](https://obsproject.com/kb/hybrid-mp4)

This is the general rule in miniature: *anything only written at stop is lost.* ClipShift has no
chapters, but it must apply the same test to every piece of metadata it plans to write.

## 5. The audio files

### 5.1 What actually breaks in a truncated WAV

RIFF carries two sizes ahead of the data: the `RIFF` chunk size at byte offset 4, and the `data` chunk
size immediately before the samples. Both are 32-bit, both are written before a single sample exists,
and both are patched by seeking backwards at stop.

FFmpeg writes `0xFFFFFFFF` into both as a placeholder. The generic RIFF helper is unambiguous:

```c
int64_t ff_start_tag(AVIOContext *pb, const char *tag)
{
    ffio_wfourcc(pb, tag);
    avio_wl32(pb, -1);
    return avio_tell(pb);
}
```
— [`riffenc.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/riffenc.c)

and the WAV muxer writes `avio_wl32(pb, -1)` for the RIFF size too
([`wavenc.c:308-310`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/wavenc.c)). Everything
is fixed up in `wav_write_trailer`, and only if the output is seekable
([`wavenc.c:418-491`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/wavenc.c)).

So a killed FFmpeg WAV writer leaves a file declaring `RIFF` size `0xFFFFFFFF` and `data` size
`0xFFFFFFFF`. Whether that is readable depends entirely on the reader's charity. FFmpeg's own demuxer
is charitable:

```c
} else if (size > 0 && size != 0xFFFFFFFF) {
    ...
    wav->data_end = avio_tell(pb) + size;
    next_tag_ofs = wav->data_end + (size & 1);
} else {
    av_log(s, AV_LOG_WARNING, "Ignoring maximum wav data size, "
           "file may be invalid\n");
    next_tag_ofs = wav->data_end = INT64_MAX;
```
— [`wavdec.c:468-478`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/wavdec.c)

`INT64_MAX` means "read to EOF". FFmpeg will therefore open a killed WAV and recover all of it. A
stricter reader may instead trust `0xFFFFFFFF`, try to seek 4 GiB past EOF, and fail — or, worse,
report a bogus four-gigabyte duration to an NLE.

**The important asymmetry: under-declaring is safe, over-declaring is not.** A reader given a `data`
size smaller than the bytes present simply stops early. It has no reason to error. This is what makes
periodic patching the right answer rather than a hack.

**The 4 GiB wall is a live problem here, not a theoretical one.** Uncompressed PCM, two channels,
48 kHz, four hours:

| Format | Bytes/sec | 4 hours | vs. 4 GiB (4,294,967,296) |
| --- | --- | --- | --- |
| 16-bit | 192,000 | 2,764,800,000 (2.58 GiB) | fits, 36 % headroom |
| 24-bit | 288,000 | 4,147,200,000 (3.86 GiB) | fits — breaks at ~4 h 8 min |
| 32-bit float | 384,000 | 5,529,600,000 (5.15 GiB) | **exceeds** |

24-bit stereo clears four hours by about five percent. The brief says sessions "up to ~4 hours". That is
not margin, that is a coin flip, and it is being made twice because there are two audio files.

RF64 is the standard escape ([EBU Tech 3306](https://tech.ebu.ch/docs/tech/tech3306.pdf); I could not
extract quotable text from the PDF — see [Open questions](#open-questions)). FFmpeg's implementation is
readable and shows the in-place upgrade pattern precisely: in `RF64_AUTO` mode it reserves a `JUNK`
chunk at header time —

```c
if (wav->rf64 == RF64_AUTO) {
    /* reserve space for ds64 */
    ffio_wfourcc(pb, "JUNK");
    ...
    /* in RF64_AUTO mode, fmt + JUNK will be overwritten by ds64 + fmt */
```
— [`wavenc.c:336-343`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/wavenc.c)

— and at stop, if the file crossed `UINT32_MAX`, overwrites `RIFF` with `RF64` and turns the reservation
into a real `ds64` carrying 64-bit sizes ([`wavenc.c:467-491`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/wavenc.c)).
Note that this upgrade *also* only happens at stop: an FFmpeg WAV killed at 5 GiB is left claiming
`RIFF` with a `0xFFFFFFFF` size and no valid way to express its length at all.

### 5.2 The recommended WAV strategy

1. **Write RF64 from the first byte** (`RF64` signature + real `ds64`), or write `RIFF` with a reserved
   `JUNK` of `ds64` size and commit to upgrading in place the moment the file passes 4 GiB — during
   recording, not at stop. Writing RF64 unconditionally is simpler and removes a mid-session branch;
   the cost is that RF64 is slightly less universally recognised than RIFF.
2. **Patch the length fields on a cadence.** Every 1–2 seconds, seek back and rewrite the `RIFF`/`ds64`
   size and the `data` size with the count of *completely written, block-aligned* frames. Two writes of
   4 or 8 bytes. At 48 kHz/24-bit/stereo the seek-back distance is a few gigabytes but the OS cache
   holds the header page in memory for the whole session, so the cost is nil.
3. **Always round down to a whole sample frame.** A declared size that ends mid-frame produces a
   channel-swapped tail or a click, and — for a sync brief — an off-by-a-fraction-of-a-sample tail.
4. **Never leave `0xFFFFFFFF` on disk as the steady state.** It is a bet on the reader.
5. At stop, patch once more with the true size. Stop becomes an optimisation, not a requirement.

Result: worst-case audio loss is the patch interval, and the file is well-formed and self-consistent at
every instant in between.

### 5.3 Formats that degrade gracefully, for completeness

- **CAF (Apple Core Audio Format)** is the one container that specifies this case. Apple's spec: "An
  `mChunkSize` value of `-1` indicates that the size of the data section for this chunk is unknown. In
  this case, the Audio Data chunk must appear last in the file so that the end of the Audio Data chunk
  is the same as the end of the file."
  ([CAF Specification, Apple](https://developer.apple.com/library/archive/documentation/MusicAudio/Reference/CAFSpec/CAF_spec/CAF_spec.html))
  It has no 4 GiB limit and is designed to be finalised later. It is the technically superior choice and
  I would recommend it if the consumer were not "DaVinci Resolve / Premiere on Windows" — see
  [Open questions](#open-questions).
- **FLAC** frames are self-delimiting and each carries its own sample number, so a truncated FLAC loses
  only the final partial frame. The `STREAMINFO` total-sample count would be stale, which decoders
  tolerate. But FLAC costs CPU on a machine that is simultaneously game-rendering, NVENC-encoding, and
  OBS-streaming, and it is a lossless-compressed format an NLE must decode on every scrub. Not worth it
  here.
- **W64 (Sony Wave64)** solves the 4 GiB limit with 64-bit sizes but keeps them at the head, so it has
  exactly the same truncation trap as WAV and needs the same periodic patching. No advantage over RF64.
- **Raw headerless PCM** cannot be truncated into invalidity at all, but carries no sample rate, channel
  count, or bit depth, so an NLE cannot import it without the user typing those in. Rejected.

## 6. Flush and durability cadence

The question "how often must data reach disk" has two different answers depending on which failure you
are defending against, and conflating them leads to paying a large I/O cost for nothing.

**Process death (crash, unhandled exception, Task Manager kill, `TerminateProcess`).** This is what the
brief actually specifies. Anything already passed to `WriteFile` is safe: Windows' cache manager runs
continuously and its lazy writer "queues one-eighth of the pages that have not been flushed recently to
be written to disk" every second, entirely independently of the process that wrote them
([File Caching, Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching)).
No amount of process violence can un-write those bytes. **Only the application's own user-space buffer
is lost.**

**Machine death (BSOD, power loss).** The same Microsoft page names this as the case where cached data
is lost: "a sudden system failure (such as a loss of power to the computer) will happen before the
flush. In the latter instance, the cached data will be lost." Defending against it requires
`FlushFileBuffers` or `FILE_FLAG_WRITE_THROUGH`.

**What OBS chose.** OBS does neither. Its writer is a dedicated I/O thread draining a ring buffer with
plain `fwrite`, and there is no `fsync`, `fflush`, or `FlushFileBuffers` anywhere in the file:

```c
static const size_t DEFAULT_BUF_SIZE   = 256ULL * 1048576ULL; // 256 MiB
static const size_t DEFAULT_CHUNK_SIZE = 1048576;             // 1 MiB
```
— [`buffered-file-serializer.c:26-27`](https://github.com/obsproject/obs-studio/blob/master/libobs/util/buffered-file-serializer.c)
(the only file operations are `os_fopen` at line 335, `fwrite` at line 180, and `fclose` at line 202)

That is a deliberate, correct trade: defend against process death, do not pay for power loss. OBS's KB
claim that hybrid files survive "power outages" is optimistic on that last word — the *format* survives
a power outage, but whatever was in the 256 MiB buffer and the OS cache does not.

**Recommendation for ClipShift.**

- Do not call `FlushFileBuffers` on a cadence. At a 2-second fragment cadence over four hours that is
  7,200 forced flushes, each of which serialises the write path and can stall the encoder feed. It buys
  nothing against the failure the brief names.
- Do not use `FILE_FLAG_NO_BUFFERING` or `FILE_FLAG_WRITE_THROUGH`. Both trade throughput for a
  guarantee that was not asked for, and `NO_BUFFERING` additionally imposes sector-aligned buffers and
  offsets, which is a genuine complication for a muxer that seeks back to patch headers.
- Expose a config-file knob (`durability: process | power`) if a user records on a machine with an
  unstable power supply. Default to `process`.
- **Buffer capacity is not the loss window; buffer *occupancy at the moment of death* is.** A large
  ring buffer that normally sits near empty costs nothing in expectation — it only matters if the
  process dies during a disk stall, which is precisely when it is earning its keep. Size it for burst
  absorption (OBS uses 256 MiB) without guilt, but keep the writer thread aggressive so steady-state
  occupancy stays near zero. Note OBS's writer waits for 64 KiB before flushing a chunk unless shutting
  down ([`buffered-file-serializer.c:153`](https://github.com/obsproject/obs-studio/blob/master/libobs/util/buffered-file-serializer.c)),
  which bounds the idle-time residue at 64 KiB per stream.

## 7. The sync interaction

This is the part that is easy to get wrong, because "the file is playable" and "the files still line up"
are different properties.

### 7.1 Why truncation is structurally harmless

All three containers under discussion are written strictly forward from `t=0`. Truncation removes bytes
from the *tail*. Byte 0 of each file therefore still corresponds to exactly the instant it did before
the crash. **Alignment at `t=0` is preserved by construction** — provided nothing that *defines* `t=0`
is deferred to finalisation.

A direct corollary worth stating because it looks alarming and is not: after a hard kill the three files
will have **different durations**. The video may end 3 seconds before the microphone file, which may end
0.2 seconds before the loopback file, because they are buffered by different code paths at different
granularities. That is not desync. An NLE aligns imported clips at their own starts; three clips of
different lengths sharing a common `t=0` drop onto a timeline correctly. Ragged tails are cosmetic.

### 7.2 Where `t=0` is defined, and whether it survives

| Stream | What carries the start offset | Written where | Survives a kill? |
| --- | --- | --- | --- |
| MP4 / hybrid video | `elst` (edit list) `media_time` | inside the *initialisation* `moov`, at the head | **Yes** |
| fMP4 fragments | `tfdt` `baseMediaDecodeTime` | per fragment | **Yes** |
| MKV | `TimestampScale`, `CodecDelay`, per-Cluster `Timestamp` | `Info`/`Tracks` at head; each Cluster | **Yes** |
| WAV | *nothing* | — | **N/A — see below** |

**MP4 edit lists.** This is the sharpest finding. OBS writes the edit list into the *incomplete*
fragmented `moov` as well as the final one, and explicitly handles the case where the sample tables do
not exist yet:

```c
if (track->offsets.num) {
    struct sample_offset sample = track->offsets.array[0];
    dts_offset = sample.offset;
} else if (track->packets.size) {
    /* If no offset data exists yet (i.e. when writing the
     * incomplete moov in a fragmented file) use the raw
     * data from the current queued packets instead. */
    struct encoder_packet pkt;
    deque_peek_front(&track->packets, &pkt, sizeof(pkt));
    dts_offset = pkt.pts - pkt.dts;
}
```
— [`mp4-mux.c:1819-1841`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-outputs/mp4-mux.c)

The offset in question is the B-frame reorder delay for video and the encoder priming delay for audio —
both of which shift a stream against the others if lost. Because OBS puts it in the head `moov`, a
crashed file keeps it. **ClipShift must do the same: any start-offset correction belongs in the
initialisation `moov`, never only in the final one.**

The `elst`'s `segment_duration` field in that head `moov` is written as ~0, because the muxer emits the
initialisation `moov` before processing the first fragment's packets. This looks dangerous — a strict
reader could interpret a zero-length edit as an empty track. FFmpeg does not: for a single non-empty
edit it uses only `e->time` to derive `sc->time_offset` and ignores `e->duration`
([`mov.c:4977-5024`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/mov.c)), and it
auto-disables its advanced edit-list path on fragmented input entirely:

```c
/*
 * Advanced edit list support does not work with fragemented MP4s, which
 * have stsc, stsz, stco, and stts with zero entries in the moov atom.
 * In these files, trun atoms may be streamed in.
 */
if (!sc->stts_count && c->advanced_editlist) {
```
— [`mov.c:5593-5604`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/mov.c)

So the offset survives *and is applied* by FFmpeg. Whether Resolve and Premiere behave identically on a
crashed fragmented file is unverified ([Open questions](#open-questions)).

**WAV has no timestamp at all.** A WAV file's `t=0` is, definitionally, its first sample byte. There is
nowhere to record "this file starts 37 ms after the video". This is the single most important
consequence of the format choice for the sibling sync ticket:

> Any offset between an audio file and the video file must be expressed **in-band** — as leading
> silence written at the head of the WAV, at the moment recording starts — or it does not survive a
> crash.

If ClipShift instead records the offset in a sidecar, in a `bext` chunk written at stop, or by trimming
leading samples in the stop path, then a kill at hour 3 yields files that are individually playable and
collectively wrong, which is the worst outcome available: silent, plausible, undetectable until the
edit is half done.

### 7.3 The rules that follow

1. **Establish `t=0` at start, in the bytes.** Pick one capture-clock epoch for the session. Pad the
   head of every stream so its first frame/sample corresponds to that epoch, and write the padding
   immediately. Then byte 0 means the same thing in all three files and no metadata is load-bearing.
2. **Never establish or correct alignment in the stop path.** Apply OBS's chapter-marker lesson: if it
   is only written at finalisation, assume it is gone. Trimming, offset sidecars, and `bext`
   `TimeReference` all fail this test unless also written at start.
3. **Fill gaps in-band.** If an audio device glitches or a frame is dropped mid-session, write silence
   or duplicate/drop explicitly so the file remains a continuous timeline. A file whose timeline is only
   reconstructible from a log that was never flushed is not recoverable in any useful sense.
4. **Ragged tails are fine; ragged heads are fatal.** Nothing in this design can truncate a head, which
   is why the whole scheme works. Preserve that property.
5. **Append a running sidecar, don't write one at stop.** One line per second per stream (frames
   written, samples written, any gap fill), appended and left to the OS cache, costs nothing and gives
   post-crash forensics: you can prove the recovered files' durations and confirm no gaps without
   opening an NLE. Note this is *forensics*, not a dependency — the files must align without it.
6. **Beware recovery-by-reconstruction.** If a standard MP4 ever has to be rebuilt with a donor-file
   tool, the reconstructed track has no edit list and guessed frame durations. It may be playable and
   still be a frame or two out against the audio. This is a further argument for never being in that
   situation.

## 8. Practical loss window, summarised

Assumes 1080p60, ~40 Mbps video, 2-second keyframe interval, hard `TerminateProcess`.

| Scheme | Video lost | Audio lost | Recovery step needed | Cost at stop |
| --- | --- | --- | --- | --- |
| Standard MP4 | **everything** | — | donor-file reconstruction, guessed timing | write `moov` |
| MKV (FFmpeg defaults) | ~1–5 s (5 MiB / 5 s cluster) | ~1–5 s | none | seek + patch segment size |
| Fragmented MP4 | 2–4 s (1–2 GOPs) | 2–4 s | none | none |
| **Hybrid MP4 (OBS-style)** | **2–4 s (1–2 GOPs)** | — | none (file is a valid fMP4) | write `moov` + 2 seeks |
| MKV → remux to MP4 at stop | ~1–5 s | ~1–5 s | none | **full file read+write** |
| WAV, patched every 1 s | — | ≤1 s | none | 2 small writes |
| WAV, sizes only at stop | — | 0 s of *data*, but header is `0xFFFFFFFF` | reader-dependent; FFmpeg copes, others may not | 2 small writes |

Add to every row whatever is sitting in the application's own buffer at the moment of death — near zero
in steady state, up to the buffer capacity during a disk stall.

## 9. Recommendation for ClipShift, concretely

### Video file

Implement a minimal fragmented-MP4 muxer with OBS's soft-remux finalisation. Three things make this far
smaller than OBS's 3,000 lines:

- The video file has **exactly one track** (video only, per the standing constraints), so there is no
  multi-track interleaving, no per-track fragment synchronisation, no chapter track.
- No chapters, no encoder-config metadata, no MOV flavour.
- Only NVENC output needs supporting, so one `avcC`/`hvcC` path.

The core is: `ftyp`, a 16-byte `free` placeholder, an initialisation `moov` with `mvex` and an `elst`
carrying the reorder delay, then `moof`+`mdat` per GOP with `tfdt`. Finalisation appends a real `moov`,
rewrites `ftyp` at the same byte length, and turns the `free` into an `mdat` spanning the body.
Accumulate sample sizes and durations in memory as you go — for four hours at 60 fps that is 864,000
entries, a handful of MB, and it is what makes the final `moov` writable without re-reading the file.

Two alternatives, both worse:

- **`MFCreateFMPEG4MediaSink`** — "Creates a media sink for authoring fragmented MP4 files", Windows 8+
  ([Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nf-mfidl-mfcreatefmpeg4mediasink)).
  Native, no third-party dependency, and it gets you crash-survivable output. But it leaves you with a
  fragmented file: no soft remux, and the seek/compatibility costs OBS enumerated stay with you.
- **FFmpeg** (`libavformat` or a spawned `ffmpeg.exe`) with `-movflags frag_keyframe+empty_moov`. Same
  situation — FFmpeg has no soft-remux equivalent, and its two routes to a plain MP4 (`faststart` or
  `-c copy` remux) both rewrite the entire file. A spawned muxer process does have one genuine merit
  worth noting: it survives the death of the main app, so an OBS-style out-of-process muxer could still
  finalise cleanly when the UI process crashes. That hedges a *different* failure than the one specified
  and adds an IPC path to the hot loop; not worth it given the format already survives.

MKV is a perfectly good format and structurally the most robust of the three, but it loses on the
consumer requirement: NLE support is codec-dependent and inconsistent, and hybrid MP4 gets the same
resilience with none of that risk. Not recommended as the primary output; worth keeping as a
config-file option for users who prefer it.

### Audio files

RF64/WAVE, PCM, with periodic in-place size patching as in §5.2. Write RF64 (or a `JUNK`-reserved RIFF)
from the first byte, patch `RIFF`/`ds64` and `data` sizes every 1–2 seconds rounded down to whole
frames, patch once more at stop. Choose 24-bit only with RF64 in place — 24-bit stereo at 48 kHz reaches
3.86 GiB at four hours and will overflow plain RIFF on a slightly longer session.

### Sync

Establish `t=0` in the bytes at recording start (leading silence / leading padding to a common epoch),
never in the stop path. Put the video's reorder-delay `elst` in the initialisation `moov`. Fill
mid-session gaps in-band. Treat any sidecar as forensics only. Hand these constraints to the sync
ticket — this ticket does not settle the sync *mechanism*, only the constraints truncation imposes on
it.

---

## Open questions

Things I could not settle from primary sources, stated plainly:

1. **NLE ingest of a *crashed* (still-fragmented) MP4.** Adobe's own supported-formats page
   (`helpx.adobe.com/premiere-pro/using/supported-file-formats.html`) timed out repeatedly and I could
   not retrieve it; Blackmagic's Resolve manual was not checked. I verified that FFmpeg reads such a
   file and applies the edit-list offset correctly, and OBS asserts recoverability, but I have **not**
   verified that Resolve or Premiere import a truncated fragmented MP4 directly, nor that they apply
   the `elst` offset the same way FFmpeg does. **This should be prototyped, not assumed** — record 30
   seconds, kill the process, and import the result into both. It is a ten-minute experiment and it is
   the single highest-value unknown here.
2. **MKV support in Premiere.** Only Adobe community-forum threads were reachable, which are not
   primary. The signal there is that support exists but is codec-dependent (H.264 yes, HEVC in MKV no,
   Opus no). Unverified against Adobe documentation. This does not change the recommendation, since
   MKV is not the proposal.
3. **RF64 spec text.** [EBU Tech 3306](https://tech.ebu.ch/docs/tech/tech3306.pdf) downloaded but could
   not be rendered to text in this environment. All RF64 mechanics above are grounded in FFmpeg's
   `wavenc.c` implementation instead, which is primary but is one implementation's reading of the spec.
   Worth a five-minute confirmation read of the PDF before implementing the `JUNK`→`ds64` upgrade.
4. **ISO/IEC 14496-12 itself is paywalled** (iso.org returned HTTP 403). Box semantics above are
   grounded in OBS's and FFmpeg's implementations and in the section numbers OBS cites in its own
   comments (e.g. `/// 8.1.2 Free Space Box`, `/// 8.8.12 Track fragment decode time`). Nothing here
   depends on a contested reading of the spec, but the spec was not consulted directly.
5. **How Resolve and Premiere treat a WAV whose `data` size is `0xFFFFFFFF`.** Unverified. This is
   precisely why the recommendation is periodic patching rather than relying on charitable readers —
   the recommendation is designed so this question does not need answering.
6. **Whether `moov` write time at stop is perceptible.** For four hours at 60 fps the final `moov`
   holds ~864,000 video sample entries. OBS logs its size (`"Full moov size: %zu KiB"`) but I found no
   published figure. Likely tens of MB and sub-second, but it is a foreground operation on the stop
   button and should be measured.

## Sources

All primary. Source files were downloaded and read directly; line numbers refer to `master` as of
2026-08-11.

**Specifications**
- [RFC 9559 — Matroska Media Container Format](https://www.rfc-editor.org/rfc/rfc9559.html)
- [RFC 8794 — Extensible Binary Meta Language (EBML)](https://www.rfc-editor.org/rfc/rfc8794.html)
- [Apple Core Audio Format Specification 1.0](https://developer.apple.com/library/archive/documentation/MusicAudio/Reference/CAFSpec/CAF_spec/CAF_spec.html)
- [EBU Tech 3306 — RF64](https://tech.ebu.ch/docs/tech/tech3306.pdf) (not extractable, see Open questions)
- ISO/IEC 14496-12 — paywalled, not consulted directly

**OBS Studio**
- [Writing an MP4 Muxer for Fun and Profit](https://obsproject.com/blog/obs-studio-hybrid-mp4)
- [Hybrid MP4 & Hybrid MOV Formats (KB)](https://obsproject.com/kb/hybrid-mp4)
- [OBS Studio 30.2 release notes](https://obsproject.com/blog/obs-studio-30-2-release-notes)
- [PR #908 — UI: Automatically remux if user selects mp4](https://github.com/obsproject/obs-studio/pull/908)
- [`plugins/obs-outputs/mp4-mux.c`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-outputs/mp4-mux.c)
- [`plugins/obs-outputs/mp4-output.c`](https://github.com/obsproject/obs-studio/blob/master/plugins/obs-outputs/mp4-output.c)
- [`libobs/util/buffered-file-serializer.c`](https://github.com/obsproject/obs-studio/blob/master/libobs/util/buffered-file-serializer.c)

**FFmpeg**
- [FFmpeg Formats Documentation](https://ffmpeg.org/ffmpeg-formats.html)
- [`libavformat/matroskaenc.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/matroskaenc.c)
- [`libavformat/wavenc.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/wavenc.c)
- [`libavformat/wavdec.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/wavdec.c)
- [`libavformat/riffenc.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/riffenc.c)
- [`libavformat/mov.c`](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/mov.c)

**Microsoft**
- [File Caching](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching)
- [MFCreateFMPEG4MediaSink](https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nf-mfidl-mfcreatefmpeg4mediasink)
