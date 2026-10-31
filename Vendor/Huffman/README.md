# Vendored Blizzard adaptive-Huffman decompressor

- **Source:** [`SeongSikChae/Biz.Bizadm.MPQ.NET`](https://github.com/SeongSikChae/Biz.Bizadm.MPQ.NET)
  — `src/Biz.Bizadm.MPQ.NET/Compression/Legacy/HuffmanCodec.cs`, itself a port of StormLib's
  `huffman/huff.cpp` (authors Ladislav Zezula and ShadowFlare) and the `Compress_huff` /
  `Decompress_huff` wrappers in `SCompression.cpp`
- **Taken:** 2026-10-30, from `main`
- **Licence:** MIT twice over, both read from the actual files rather than a badge — the repo
  root `LICENSE` ("Copyright (c) 2026 SeongSikChae", see [`LICENSE.bizadm.txt`](LICENSE.bizadm.txt))
  and, for the StormLib code it is a port of, the grant reproduced in the upstream
  `THIRD-PARTY-NOTICES.md` ("Copyright (c) 1999-2013 Ladislav Zezula", see
  [`LICENSE.StormLib.txt`](LICENSE.StormLib.txt))

## Why it is here

Mask `0x01`. On its own it is rare, but combined with ADPCM as `0x41` or `0x81` it is how every
Blizzard game before StarCraft II stored sound: 407 of `War2Dat.mpq`'s 1,186 entries, a third of
the archive, are unreadable without it. There is no BCL equivalent — this is not deflate with a
static table but a Faller-Gallager-Knuth adaptive tree seeded from one of nine weight
distributions that MPQ carries in code, not in the stream.

## Files

| File | Upstream path |
|---|---|
| `HuffmanCodec.cs` | `src/Biz.Bizadm.MPQ.NET/Compression/Legacy/HuffmanCodec.cs` |

## Local changes

- 📦 Namespace moved to `Mpq.Vendor.Huffman`, and a provenance header added above the upstream
  one. Nothing else.

The compressor stays in the file even though the reader never calls it. Decompression here is not
a separate file as it was for PKWARE: `HuffmanTree` is shared between the two directions, so
removing the encode side means cutting into vendored code rather than declining to copy a file.
The public entry point one directory up, [`MpqHuffman`](../../MpqHuffman.cs), exposes only
`Decompress`.

## How we know it is correct

**An external oracle, which is better than the cross-check.** `Biz.Bizadm.MPQ.NET`'s test
fixtures ship a sibling `.expected.json` per archive declaring every entry's SHA-256, and those
hashes are produced by **StormLib itself** — the fixture generator under upstream `tools/`
P/Invokes `SFileReadFile` and hashes what the reference implementation returns. So matching them
is agreement with StormLib, not with a second guess:

| Fixture entry | Mask | Size | Result |
|---|---|---|---|
| `v1-legacy-masks.mpq` `text\huffman.txt` | `0x01` | 17,100 | ✅ byte-exact |
| `v1-legacy-masks.mpq` `sound\stereo.wav` | `0x81` | 44,144 | ✅ byte-exact |
| `v4-mixed.mpq` `sound\stereo.wav` | `0x81` | 44,144 | ✅ byte-exact |
| `v4-mixed.mpq` `sound\mono.wav` | `0x41` | 22,094 | ✅ byte-exact |

That covers compression type 0 (plain Huffman, where the tree rebalances after every byte) and
types 6/7/8 (the ADPCM distributions, where it rebalances only on a new symbol) — the two
different behaviours in the file.

**And a second implementation anyway.** `Foole.Mpq`'s `MpqHuffman.cs` — "(C) 2006 Foole", explicit
per-file X11/MIT grant, twenty years in production inside
[`barrett777/Heroes.ReplayParser`](https://github.com/barrett777/Heroes.ReplayParser) — was run
beside this one over every Huffman sector of
Warcraft II's `War2Dat.mpq`: **5,439 of 5,439 sectors
byte-identical, zero disagreements**. Foole has no compression-type-0 path at all (it throws), so
the fixture above is the only thing standing behind that case; every `War2Dat.mpq` sector is
type 6, 7 or 8.

Every one of those 5,439 sectors also filled its expected length **exactly** — checked per
sector, against the length the block table implies for that sector, because comparing a decoded
entry's array length to the block table is circular (the array is allocated from it).

## Updating

Re-copy the upstream file and re-apply the namespace change. Then re-run the fixture conformance
and the dual-decoder diff described in
[the repository README](../../README.md#-validation) — that run is the only thing
standing behind this code.
