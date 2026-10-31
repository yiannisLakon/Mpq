# Third-party notices

`Mpq` ships with **zero `PackageReference`s** — every codec MPQ needs that the BCL does not
provide is vendored into `Vendor/`, under its own licence. All four are **MIT**.

Each entry below was checked against the licence file committed beside the code, not against a
badge or a memory of the upstream repo. The licence texts live at the paths in the last column.

| Codec | MPQ mask | Upstream | Licence | Text in this repo |
|---|---|---|---|---|
| **bzip2** (decompress) | `0x10` | [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) `v1.4.2` | MIT — *Copyright © 2000-2018 SharpZipLib Contributors* | [`Vendor/BZip2/LICENSE.SharpZipLib.txt`](Vendor/BZip2/LICENSE.SharpZipLib.txt) |
| **PKWARE DCL** (explode) | `0x08`, `IMPLODE` | [`amcthunder84/mpqfs`](https://github.com/amcthunder84/mpqfs) — C# port by Antonio Martin of [`diasurgical/mpqfs`](https://github.com/diasurgical/mpqfs); tables from StormLib's `explode.c` | MIT — *Copyright (c) 2025 mpqfs contributors* | [`Vendor/Pkware/LICENSE.mpqfs.txt`](Vendor/Pkware/LICENSE.mpqfs.txt) |
| **Adaptive Huffman** | `0x01` | [`SeongSikChae/Biz.Bizadm.MPQ.NET`](https://github.com/SeongSikChae/Biz.Bizadm.MPQ.NET), a port of **StormLib** `huffman/huff.cpp` (Ladislav Zezula, ShadowFlare) | MIT ×2 — *Copyright (c) 2026 SeongSikChae* **and** *Copyright (c) 1999-2013 Ladislav Zezula* | [`Vendor/Huffman/LICENSE.bizadm.txt`](Vendor/Huffman/LICENSE.bizadm.txt), [`Vendor/Huffman/LICENSE.StormLib.txt`](Vendor/Huffman/LICENSE.StormLib.txt) |
| **IMA ADPCM** (Blizzard variant) | `0x40`, `0x80` | [`SeongSikChae/Biz.Bizadm.MPQ.NET`](https://github.com/SeongSikChae/Biz.Bizadm.MPQ.NET), a port of **StormLib** `adpcm/adpcm.cpp` | MIT ×2 — *Copyright (c) 2026 SeongSikChae* **and** *Copyright (c) 1999-2013 Ladislav Zezula* | [`Vendor/Adpcm/LICENSE.bizadm.txt`](Vendor/Adpcm/LICENSE.bizadm.txt), [`Vendor/Adpcm/LICENSE.StormLib.txt`](Vendor/Adpcm/LICENSE.StormLib.txt) |

Each `Vendor/*/README.md` records, per codec: the upstream file paths copied, the date taken, the
local changes applied, and how that codec was validated.

---

## 📦 bzip2 — SharpZipLib

- 🔗 **Upstream:** <https://github.com/icsharpcode/SharpZipLib>, tag `v1.4.2`
- 📄 **Files:** `BZip2InputStream.cs`, `BZip2Constants.cs`, `BZip2Crc.cs`, `CrcUtilities.cs`
  (`BZip2Exception.cs` was rewritten locally as a plain `IOException` subclass)
- ✂️ **Local changes:** namespace → `Mpq.Vendor.BZip2`; decompression only; `IChecksum` dropped;
  `#nullable disable` added so the upstream bodies stay byte-faithful
- ⚖️ **Licence:** MIT. *Copyright © 2000-2018 SharpZipLib Contributors* —
  [`Vendor/BZip2/LICENSE.SharpZipLib.txt`](Vendor/BZip2/LICENSE.SharpZipLib.txt)

## 📦 PKWARE DCL — mpqfs

- 🔗 **Upstream:** <https://github.com/amcthunder84/mpqfs>, `main`
- 📄 **Files:** `PkwareExplode.cs`, `PkwareTables.cs` (`PkwareImplode.cs` deliberately not taken)
- ✂️ **Local changes:** namespace → `Mpq.Vendor.Pkware`; upstream's own exception type replaced
  by `InvalidDataException`. Otherwise byte-faithful; the per-file
  `SPDX-License-Identifier: MIT` headers are intact
- ⚖️ **Licence:** MIT, stated twice — the repo `LICENSE` (*Copyright (c) 2025 mpqfs contributors*,
  reproduced at [`Vendor/Pkware/LICENSE.mpqfs.txt`](Vendor/Pkware/LICENSE.mpqfs.txt)) and an SPDX
  header on each file taken
- 🧬 **Chain of authorship:** the decoder's tables originate in StormLib's `explode.c`; the C
  original is `diasurgical/mpqfs`, and the C# port taken here is Antonio Martin's

## 📦 Adaptive Huffman — Biz.Bizadm.MPQ.NET → StormLib

- 🔗 **Upstream:** <https://github.com/SeongSikChae/Biz.Bizadm.MPQ.NET>, `main` —
  `src/Biz.Bizadm.MPQ.NET/Compression/Legacy/HuffmanCodec.cs`
- 🧬 **Derived from:** StormLib `huffman/huff.cpp` (Ladislav Zezula, ShadowFlare) and the
  `Compress_huff` / `Decompress_huff` wrappers in `SCompression.cpp`
- ✂️ **Local changes:** namespace → `Mpq.Vendor.Huffman`, plus a provenance header. Nothing else.
  The compressor stays in the file unused — `HuffmanTree` is shared between both directions, so
  removing it would mean editing vendored code
- ⚖️ **Two licences apply, both MIT:**
  - the port's own, *Copyright (c) 2026 SeongSikChae* —
    [`Vendor/Huffman/LICENSE.bizadm.txt`](Vendor/Huffman/LICENSE.bizadm.txt)
  - StormLib's, as reproduced in the port's `THIRD-PARTY-NOTICES.md`,
    *Copyright (c) 1999-2013 Ladislav Zezula* —
    [`Vendor/Huffman/LICENSE.StormLib.txt`](Vendor/Huffman/LICENSE.StormLib.txt)

## 📦 IMA ADPCM — Biz.Bizadm.MPQ.NET → StormLib

- 🔗 **Upstream:** <https://github.com/SeongSikChae/Biz.Bizadm.MPQ.NET>, `main` —
  `src/Biz.Bizadm.MPQ.NET/Compression/Legacy/AdpcmCodec.cs`
- 🧬 **Derived from:** StormLib `adpcm/adpcm.cpp` (`CompressADPCM` / `DecompressADPCM`)
- ✂️ **Local changes:** namespace → `Mpq.Vendor.Adpcm`, plus a provenance header. Nothing else
- ⚖️ **Two licences apply, both MIT** — the same pair as Huffman:
  [`Vendor/Adpcm/LICENSE.bizadm.txt`](Vendor/Adpcm/LICENSE.bizadm.txt) and
  [`Vendor/Adpcm/LICENSE.StormLib.txt`](Vendor/Adpcm/LICENSE.StormLib.txt).
  The two `LICENSE.StormLib.txt` copies and the two `LICENSE.bizadm.txt` copies are byte-identical
  to their counterparts under `Vendor/Huffman/`

---

## 🔍 Not vendored, but used as oracles

These were run **beside** this library during validation and no code from them is in this repo.
They are named because the numbers in [`README.md`](README.md) are only meaningful with their
provenance attached.

| Oracle | What it is | Role |
|---|---|---|
| **StormLib** (`StormLib.dll`, native) | Ladislav Zezula's reference MPQ implementation, MIT | Opened, enumerated, verified and compacted every archive the writer produced |
| **Ladik's MPQ Editor** | Zezula's GUI/CLI tool, built on StormLib | Differential extraction baseline, and a third independent extractor |
| **`Foole.Mpq`** | "(C) 2006 Foole", per-file X11/MIT, long-running inside [`barrett777/Heroes.ReplayParser`](https://github.com/barrett777/Heroes.ReplayParser) | Second, independent implementation of PKWARE, Huffman and ADPCM, diffed sector by sector |
| **`Biz.Bizadm.MPQ.NET` test fixtures** | MIT, *Copyright (c) 2026 SeongSikChae*; each fixture ships a `.expected.json` of per-entry SHA-256s produced by P/Invoking StormLib's `SFileReadFile` | Conformance hashes for the legacy codecs, including the cases the game corpora contain none of |

## ⚖️ Blizzard

**MPQ (MoPaQ)** is Blizzard Entertainment's archive format. This library is an independent
implementation of it, built from the format's public documentation and from the MIT-licensed
sources credited above. It contains no Blizzard code and no Blizzard assets, and it is not
affiliated with or endorsed by Blizzard Entertainment.
