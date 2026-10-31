# Mpq

**A managed MPQ (MoPaQ) reader and writer for .NET — no native `StormLib.dll`, no NuGet
dependencies, Native-AOT friendly.**

MPQ is the archive format Blizzard shipped in everything from Diablo and Warcraft II through to
StarCraft II maps and Warcraft III `.w3x`. This library opens those archives, extracts them, and
writes new ones — in process, in managed code.

- 🪶 **Zero `PackageReference`s.** Every codec the BCL lacks is vendored under `Vendor/`, each
  with its own licence. See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
- ⚙️ **`IsAotCompatible=true`** — the trim and AOT analysers are on, so a consumer can publish
  Native AOT without a warning.
- 🧱 **Reads more than it writes, on purpose.** Header formats v3 and v4 are refused by the
  writer by design — see [Why no v3/v4 writing](#-why-the-writer-stops-at-v2).
- 📏 **Validated against three independent oracles** over ~80,000 real archive entries, not
  against a unit-test suite. There is **no test project** — see
  [What this is not](#-what-this-is-not).

> ✅ **Targets `net11.0`, `net8.0` and `net6.0`**, from one set of sources and with zero
> packages on every one of them. .NET Framework (net48 and earlier) is still out: reaching it
> needs `netstandard2.0`, which needs the `System.Memory` package for `Span<T>` and
> `ArrayPool<T>` — and that would end the zero-dependency claim above. See
> [Roadmap](#-roadmap).

---

## 🧩 Support matrix

### Header formats

| `wFormatVersion` | Also called | Read | Write | Note |
|---|---|:--:|:--:|---|
| **0** | `MpqFormat.V1` | ✅ | ✅ | 32-byte header, 4 KB sectors. Every Warcraft III map. |
| **1** | `MpqFormat.V2` | ✅ | ✅ | 44-byte header, 16 KB sectors. The StarCraft II target. |
| **2** | `MpqFormat.V3` | — | ❌ | Refused by the writer with a named exception. |
| **3** | `MpqFormat.V4` | ✅ | ❌ | Read through the **classic** hash/block tables; HET/BET ignored. A v4 source opened for rewrite is written back as v2. |

The reader scans for the header at **512-byte boundaries** and shunts past a `MPQ\x1B` user-data
block, so archives with an installer stub or an SC2 map header in front of them open normally.

### Compressions

Every compression Blizzard used before CASC, read. The writer emits one.

| Mask | Codec | Read | Write | Provided by |
|---|---|:--:|:--:|---|
| `0x02` | **zlib** | ✅ | ✅ | BCL (`System.IO.Compression`) |
| `0x10` | **bzip2** | ✅ | — | vendored SharpZipLib |
| `0x08` | **PKWARE DCL** ("explode") | ✅ | — | vendored `mpqfs` |
| `IMPLODE` flag | **PKWARE DCL**, no mask byte | ✅ | — | same decoder, separate carrier |
| `0x01` | **adaptive Huffman** | ✅ | — | vendored, StormLib-derived |
| `0x40` / `0x80` | **ADPCM** mono / stereo | ✅ | — | vendored, StormLib-derived |
| `0x41` / `0x81` | **Huffman + ADPCM** chains | ✅ | — | composite, order fixed in code |
| any other mask | `0x0A` PKWARE+zlib, `0x20` sparse, `0x12` LZMA, … | ❌ | ❌ | throws `MpqException` naming the entry and the mask |

**Writing is zlib or stored, and that is deliberate** — zlib was already the recommended default,
MPQ Editor's own StarCraft II ruleset offers nothing else, and a bzip2 source round-trips
correctly as zlib. Two fallbacks matter more than the codec choice: a sector that does not shrink
is stored raw with no mask byte, and if **no** sector in a file shrank the `COMPRESS` flag is
cleared outright — because a reader decides stored-versus-compressed by size, and a
`COMPRESS`-flagged file that never shrank is how a 3-byte payload turns into garbage.

### Archive features

| Feature | Read | Write |
|---|:--:|:--:|
| Encrypted hash & block tables | ✅ | ✅ (always) |
| Encrypted file entries, incl. `FIX_KEY` derivation | ✅ | — |
| `(listfile)` | ✅ | ✅ |
| Sector offset tables, including the `SECTOR_CRC` slot | ✅ | ✅ |
| `SINGLE_UNIT` entries | ✅ | ❌ (stripped — see below) |
| Raw-stored entries with no offset table | ✅ | ✅ |
| `(attributes)` | ignored | ❌ (see below) |
| Per-file encryption | ✅ | ❌ |

`(attributes)` and `SINGLE_UNIT` are **not** written, each because emitting it is a documented way
to corrupt an archive rather than because it was expensive: `(attributes)` is indexed by block
number, so any reindex silently mismatches; and StormLib strips `SINGLE_UNIT` from any format-0
archive, then expects a sector offset table that was never written — the mechanism behind the
corrupted-`.w3n` reports.

---

## 🚀 Usage

### Read and enumerate

```csharp
using Mpq;

using var archive = MpqArchive.Open(@"Maps\MyMap.SC2Map");

Console.WriteLine($"format v{archive.FormatVersion}, " +
                  $"{archive.BlockCount} blocks, {archive.SectorSize} B sectors");

foreach ((string name, MpqEntry entry) in archive.Enumerate())
    Console.WriteLine($"{name,-50} {entry.Size,10:N0} B  {entry.Flags}");

if (archive.Contains("MapInfo"))
    byte[] mapInfo = archive.Read("MapInfo");
```

`Files` is the `(listfile)` as read; `UnnamedBlockCount()` reports blocks the listfile does not
name. `Read` throws `MpqException` — naming the entry — rather than returning wrong bytes.

### Extract a whole archive

```csharp
MpqExtraction result = MpqExtractor.ExtractAll(
    @"Maps\MyMap.SC2Map", @"out\MyMap");

Console.WriteLine($"{result.Files:N0} files, {result.Bytes:N0} bytes");

foreach (string skipped in result.Skipped) Console.WriteLine($"skipped: {skipped}");
foreach (string warning in result.Warnings) Console.WriteLine($"warning: {warning}");
```

It recreates the archive's internal `\`-separated tree on disk (what MPQ Editor's `/fp` does), and
skips the archive's own metadata. **One unreadable entry does not abandon the archive** — it lands
in `Skipped` and the rest still extract, so a partial extraction cannot be mistaken for success.

### Write a new archive

```csharp
using var builder = MpqBuilder.Create(MpqFormat.V2);

builder.AddFile(@"MapInfo", @"src\MapInfo")
       .Add(@"Triggers\main.galaxy", File.ReadAllBytes(@"src\main.galaxy"))
       .AddDirectory(@"src\Assets", archivePrefix: @"Assets")
       .Add("raw.bin", payload, new MpqAddOptions { Compression = MpqCompression.None });

MpqWriteResult written = builder.SaveAs(@"out\MyMap.SC2Map");

Console.WriteLine($"{written.Files} files, {written.ArchiveBytes:N0} B, " +
                  $"hash table {written.HashTableSize}, {written.Elapsed.TotalSeconds:F1}s");
```

### Mutate an existing archive

```csharp
using var builder = MpqBuilder.Open(@"out\MyMap.SC2Map");  // format inherited; v4 → v2

builder.Remove("Triggers\\old.galaxy");
builder.Add("Triggers\\main.galaxy", newSource);

builder.SaveAs(@"out\MyMap.SC2Map");   // temp file, verified, then replaced atomically

MpqWriteResult compacted = MpqBuilder.Compact(@"out\MyMap.SC2Map");
```

**There is no in-place mutation, and that is the design.** StormLib's allocator never reuses the
hole a replaced file leaves, so editing in place grows an archive without bound and leaves a
half-written one on any failure. Every `SaveAs` instead:

1. 📝 writes a **temp file**;
2. 🔎 checks the structural invariants over what it just wrote;
3. 🔁 **reopens it with `MpqArchive`** — which shares nothing with the writer but the format;
4. 🧮 compares **every file byte for byte** against the source it was built from;
5. ♻️ only then replaces the target **atomically**.

Compaction therefore needs no code of its own: a save *is* a compaction.

### Names are keyed the way MPQ keys them

MPQ's hash folds `a-z` to `A-Z`, so `CASE.txt` and `case.txt` are **one file**. The builder keys
entries by that fold, not by `Dictionary` equality — keying them apart would write two entries
whose hashes collide and leave one unreachable. The fold is **ASCII-only** on purpose:
`OrdinalIgnoreCase` would fold a non-ASCII byte the MPQ hash leaves alone, and the two would stop
agreeing. Archive-internal names are raw bytes, decoded **Latin-1**, not UTF-8.

---

## 📏 Validation

There is no unit-test suite behind this library. What stands behind it is **differential runs
against independent implementations over real archives**, with per-sector length checks rather
than per-file ones (comparing a decoded entry's array length to the block table is circular — the
array was allocated from it).

### Reading

| Corpus | Scale | Oracle | Result |
|---|---|---|---|
| **StarCraft II** maps and mods | **826 archives**, ~**80,759 entries** | Ladik's **MPQ Editor** | extracted **byte-identically** |
| `War2Dat.mpq` (Warcraft II data) | **1,186 entries**, all encrypted, only **88** named by the archive's own `(listfile)` | — | **89 / 1,186** extracted through this API — every entry it can name. See the note below. |
| Warcraft III `.w3x` maps | **11 maps** | — | all **11** open, unchanged |
| Protected SC2 maps | **36 encrypted entries** across **5** maps | — | **36 / 36** extracted |

> 🔑 **`War2Dat.mpq`, and why 89 is not 1,186.** An MPQ entry's decryption key is derived from
> its **filename**, so an entry no `(listfile)` names cannot be read at all — the block is there,
> the bytes are there, the key is not. That archive names 88 of its 1,186 blocks, and 89 is those
> 88 plus `(attributes)`.
>
> **The codecs are not what is missing.** A harness that recovers keys by trial against the
> sector-offset table reached all 1,186 and decoded **13,147 of 13,147** sectors to their exact
> expected length, zero short fills — so every compression this archive uses is covered end to
> end. What is missing is the key recovery itself, which is **not in this library**: it is
> [roadmap item 1](#-roadmap), and those 1,097 entries are what it is worth.

### Writing

| Check | Oracle | Result |
|---|---|---|
| Round-trip fidelity | the library's own byte-for-byte reopen | **3,274 files, zero byte differences** |
| Structural soundness | native **StormLib** | opens, enumerates, verifies and **compacts** every archive written, with `MPQ_FLAG_MALFORMED` clear |
| Third extractor | **MPQ Editor** | identical **SHA-256** per entry |

`SFileCompactArchive` is the sharp one of those three: it rebuilds every table from scratch, so it
fails loudly on exactly the corruption a hand-rolled writer causes.

### The PKWARE decoder, specifically

PKWARE DCL is the one MPQ compression with no BCL equivalent, and its upstream port ships no
tests — so it got two layers:

- 🔀 **Two independent implementations** run over every DCL sector in the corpus:
  **7,267 of 7,267 byte-identical, zero disagreements.** Reaching `War2Dat.mpq`'s share of those
  needed the key-recovery harness from the note above, so the sector count is larger than this
  API alone can enumerate; the agreement it measures is between the two decoders either way.
- 🎯 **A conformance fixture** with declared SHA-256s (generated by P/Invoking StormLib itself):
  **7 / 7 entries byte-exact**, covering both the `0x08` mask and the no-mask-byte `IMPLODE`
  carrier.

**What that does not cover:** every DCL sector in every corpus uses literal mode **0** (binary).
The ASCII literal path is **unexercised** — it reads as sound, but nothing here has run it.

### The legacy sound codecs

Huffman and ADPCM were checked against fixture hashes **produced by StormLib's own
`SFileReadFile`**, which makes agreement with the reference implementation rather than with a
second guess — and additionally diffed against a second implementation over **5,439 / 5,439**
sectors of `War2Dat.mpq`, zero disagreements (reached through the same key-recovery harness, for
the same reason). ADPCM is **lossy**, so the fixture hashes are the
only kind of check that counts for it; the channel count is not in the stream and is taken from
the mask bit, which those hashes also pin down.

Oracles named in full, with licences, in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
Each `Vendor/*/README.md` carries that codec's own numbers.

---

## 🚫 What this is not

Stated plainly rather than buried:

- ❌ **No test project.** None. The validation above is reproducible *runs*, not a suite you can
  invoke with `dotnet test`. A regression would be caught by re-running them against a game
  install — which means it would not be caught in CI, because there is no CI either.
- ❌ **No .NET Framework.** `net11.0`, `net8.0` and `net6.0` are covered (so .NET 7 and 9/10
  consumers resolve the net6.0 and net8.0 assets); net48 and earlier are not, and would cost a
  `PackageReference` to reach.
- ❌ **Not on NuGet.** Reference the project or vendor the folder.
- ❌ **No v3/v4 writing**, no LZMA, no sparse compression, no patch-file (`PATCH`) application,
  no per-file encryption on write, no `(attributes)`.
- ❌ **No ASCII-literal DCL coverage** — see above.

### 🧭 Why the writer stops at v2

Not a gap — a finding that made the writer *smaller*:

- Warcraft III is parsed as **v1 whatever its header claims**.
- MPQ Editor's own StarCraft II preset creates **v2**, and SC2 accepts v2.
- **v3 is worse than unnecessary:** StormLib coerces any SC2 archive claiming version ≥ 3 into the
  **v4** layout, so a genuine v3 header gets read with the wrong one.

So the v4 header MD5 and the bit-packed HET/BET tables drop out of the critical path entirely.
`Create` refuses v3 and v4 with a named exception.

---

## 🗺️ Roadmap

Nothing here is promised, and none of it is started.

| | Item | Why it matters |
|---|---|---|
| 1 | **Filename / key recovery for unnamed blocks** | the largest gap by far: an entry no `(listfile)` names has no key, so it is unreadable however well the codecs work. In `War2Dat.mpq` that is **1,097 of 1,186 entries** — the whole of Warcraft II's data beyond the 88 it names |
| 2 | **`netstandard2.0` / `net48`** | all that is left of the multi-targeting gap; it costs a `System.Memory` `PackageReference`, which is why it has not been taken |
| 3 | **A test project** | turn the validation runs into something `dotnet test` can execute without a game install |
| 4 | PKWARE **ASCII literal mode** coverage | the one decoder path nothing has run |
| 5 | `0x0A` (PKWARE + zlib) and `0x20` (sparse) | the two remaining pre-CASC masks the reader refuses |

---

## ⚖️ Licence

**MIT** — see [`LICENSE`](LICENSE).

Vendored codecs are MIT too, each under its own copyright; every one is credited, with its licence
text and upstream provenance, in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

MPQ is Blizzard Entertainment's format. This project is independent of, and not endorsed by,
Blizzard Entertainment, and contains no Blizzard code or assets.
