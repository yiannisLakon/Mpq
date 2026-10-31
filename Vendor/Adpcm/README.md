# Vendored Blizzard IMA ADPCM decompressor

- **Source:** [`SeongSikChae/Biz.Bizadm.MPQ.NET`](https://github.com/SeongSikChae/Biz.Bizadm.MPQ.NET)
  — `src/Biz.Bizadm.MPQ.NET/Compression/Legacy/AdpcmCodec.cs`, itself a port of StormLib's
  `adpcm/adpcm.cpp` (`CompressADPCM` / `DecompressADPCM`)
- **Taken:** 2026-10-30, from `main`
- **Licence:** MIT twice over, both read from the actual files rather than a badge — the repo
  root `LICENSE` ("Copyright (c) 2026 SeongSikChae", see [`LICENSE.bizadm.txt`](LICENSE.bizadm.txt))
  and, for the StormLib code it is a port of, the grant reproduced in the upstream
  `THIRD-PARTY-NOTICES.md` ("Copyright (c) 1999-2013 Ladislav Zezula", see
  [`LICENSE.StormLib.txt`](LICENSE.StormLib.txt))

## Why it is here

Masks `0x40` (mono) and `0x80` (stereo), almost always chained behind Huffman as `0x41` or
`0x81`. It is Blizzard's sound format: 407 of `War2Dat.mpq`'s 1,186 entries, the game's speech
and effects. No BCL equivalent, and it is not ordinary IMA ADPCM either — the step table, the
`0x80` / `0x81` control bytes and the per-channel alternation are Blizzard's own.

## Files

| File | Upstream path |
|---|---|
| `AdpcmCodec.cs` | `src/Biz.Bizadm.MPQ.NET/Compression/Legacy/AdpcmCodec.cs` |

## Local changes

- 📦 Namespace moved to `Mpq.Vendor.Adpcm`, and a provenance header added above the upstream one.
  Nothing else.

The compressor stays in the file, as in `Vendor/Huffman/`. The public entry point one directory
up, [`MpqAdpcm`](../../MpqAdpcm.cs), exposes only `Decompress`.

## ⚠️ Two things about this codec that no size check catches

**The channel count is not in the stream.** The header is a zero byte, a bit shift, and one
16-bit seed sample *per channel* — so a stereo stream decoded as mono consumes one seed where it
should consume two and then runs one predictor where it should alternate between two. The output
is still exactly the right length. It just sounds wrong. The mask bit is the only thing that
distinguishes them, which is why `Decompress` takes `channels` instead of sniffing it.

**It is lossy.** The decoded PCM is not the bytes that were compressed, so it cannot be validated
by round-tripping an original. It is, however, deterministic, which is what makes the hashes
below meaningful.

## How we know it is correct

**An external oracle, and for a lossy codec it is the only kind that counts.**
`Biz.Bizadm.MPQ.NET`'s fixtures ship a sibling `.expected.json` per archive declaring every
entry's SHA-256, and those hashes come from **StormLib itself** — the fixture generator under
upstream `tools/` P/Invokes `SFileReadFile` and hashes what the reference implementation returns.
Matching them means producing the same samples StormLib produces:

| Fixture entry | Mask | Channels | Size | Result |
|---|---|---|---|---|
| `v4-mixed.mpq` `sound\mono.wav` | `0x41` | 1 | 22,094 | ✅ byte-exact |
| `v4-mixed.mpq` `sound\stereo.wav` | `0x81` | 2 | 44,144 | ✅ byte-exact |
| `v1-legacy-masks.mpq` `sound\stereo.wav` | `0x81` | 2 | 44,144 | ✅ byte-exact |

Both channel counts, both composite masks, and `stereo.wav` hashes the same in two differently
built archives. Decoding either file with the wrong channel count changes the hash and nothing
else, so these three rows are the evidence that the mask-bit-to-channel-count mapping is right.

**And a second implementation.** `Foole.Mpq`'s `MpqWavCompression.cs` — "(C) 2006 Foole",
per-file X11/MIT grant, twenty years in production inside
[`barrett777/Heroes.ReplayParser`](https://github.com/barrett777/Heroes.ReplayParser) — ran
beside this one over every ADPCM sector of
Warcraft II's `War2Dat.mpq`: **5,439 of 5,439 sectors
byte-identical, zero disagreements**, every one filling its expected length exactly, checked per
sector. The two differ only in dead code: Foole treats any byte with the high bit set as a
control marker, where StormLib and this port treat only `0x80` and `0x81` as markers. No encoder
emits anything else, so the paths never diverge on real data.

## Updating

Re-copy the upstream file and re-apply the namespace change. Then re-run the fixture conformance
and the dual-decoder diff described in
[the repository README](../../README.md#-validation) — that run is the only thing
standing behind this code.
