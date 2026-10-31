# Vendored PKWARE DCL ("implode"/explode) decompressor

- **Source:** [`amcthunder84/mpqfs`](https://github.com/amcthunder84/mpqfs) — a C# port, by Antonio
  Martin, of [`diasurgical/mpqfs`](https://github.com/diasurgical/mpqfs); its tables come from
  StormLib's `explode.c`
- **Taken:** 2026-10-30, from `main`
- **Licence:** MIT — repo `LICENSE` ("Copyright (c) 2025 mpqfs contributors") *and* a per-file
  `SPDX-License-Identifier: MIT` header on each file taken. Both read directly, not from a badge.
  See [`LICENSE.mpqfs.txt`](LICENSE.mpqfs.txt)

## Why it is here

PKWARE DCL is the one MPQ compression with **no BCL equivalent**, and it is not optional:
`War2Dat.mpq`'s own `(listfile)` is DCL-compressed, so that archive cannot be enumerated at all
without a decoder. This library ships with zero `PackageReference`s and its consumers publish Native AOT, so a
NuGet dependency for ~300 LOC of table-driven bit twiddling is not worth it.

## Files

| File | Upstream path |
|---|---|
| `PkwareExplode.cs` | `PkwareExplode.cs` |
| `PkwareTables.cs` | `PkwareTables.cs` |

`PkwareImplode.cs` is **not** vendored — the reader does not compress.

## Local changes

- 📦 Namespace moved to `Mpq.Vendor.Pkware`.
- 🧹 Upstream's `MpqException(MpqError.DecompressionFailed, …)` replaced by
  `InvalidDataException`, so nothing outside the BCL is dragged in alongside. The public
  entry point is [`MpqPkware`](../../MpqPkware.cs), one directory up.
- ✂️ Decompression only.

Otherwise byte-faithful.

## How we know it is correct

`mpqfs` is young — created 2026-08-21, and it ships **no tests**. That is the whole reason the
second implementation exists. Both were run over every PKWARE sector in
Warcraft II's `War2Dat.mpq`:

- `Foole.Mpq`'s `PKLibDecompress.cs` + `BitStream.cs` — "(C) 2006 Foole", explicit per-file
  X11/MIT grant, twenty years in production inside
  [`barrett777/Heroes.ReplayParser`](https://github.com/barrett777/Heroes.ReplayParser). Table-driven
  Shannon-Fano decode over a `Stream`.
- this one — linear code scan over a `ref struct` bit reader, `Span` in and `Span` out.

Across `War2Dat.mpq`, the four protected StarCraft II maps and one conformance fixture:
**7,267 of 7,267 DCL sectors byte-identical, zero disagreements, zero failures on either side**,
and every one of those sectors filled its expected length exactly. Two implementations that share
no code agreeing on 25.7 MB of real output is better evidence than either carries alone.

Agreeing is not the same as being right, so there is also an **external oracle**:
`Biz.Bizadm.MPQ.NET`'s `v1-pkware.mpq` fixture (MIT, Copyright (c) 2026 SeongSikChae) ships a
`.expected.json` declaring every entry's SHA-256. **7 of 7 entries byte-exact.** It also covers
the carrier `War2Dat.mpq` does not — the `MPQ_FILE_IMPLODE` form with no mask byte — so both
shapes are now tested. The numbers and the method are in
[the repository README](../../README.md#the-pkware-decoder-specifically).

**What that does not cover:** every sector in every corpus is literal mode **0** (binary). The
ASCII literal path (`compType == 1`, the `AsciiBits`/`AsciiCode` tables) is **unexercised** — it
reads as sound, but nothing here has run it. `Foole`'s implementation has no ASCII path at all, so
there was nothing to diff it against either, and the fixture author did not write one.

## Why this one and not Foole's

Foole's is the battle-tested one and was the better default, so the reasons to depart from it:

1. **Foole cannot do ASCII literal mode** — `DecodeLit` throws `NotImplementedException` for it.
   A general-purpose codec has to have the path, and adding one would mean editing the vendored
   code, which forfeits the very "twenty years in production" property that recommended it.
2. **Shape.** `Explode(ReadOnlySpan<byte>, Span<byte>)` drops straight into the sector loop's
   existing `Span<byte> destination`. Foole needs a `MemoryStream` per sector plus its own output
   array, then a copy into the destination — two allocations per sector in a loop that otherwise
   rents from `ArrayPool`.
3. **End-of-stream.** This one checks DCL's explicit end marker (length code 15 with all eight
   extra bits set). Foole has no such check and relies on the output buffer filling up or the
   input running dry; that is safe only because MPQ always knows the uncompressed size.

It is also 1.09× faster (142.5 vs 130.6 MB/s over the same 7,007 sectors) — a tiebreaker, not a
reason.

## Updating

Re-copy the two upstream files and re-apply the three changes above; keep the SPDX headers
intact. Then re-run the dual-decoder diff described in the results doc — the only thing standing
behind this code is that run.
