# Vendored bzip2 decompressor

- **Source:** [SharpZipLib](https://github.com/icsharpcode/SharpZipLib)
- **Version:** `v1.4.2` (verified by diffing every file against that tag — identical apart
  from the local changes below)
- **Licence:** MIT — see [`LICENSE.SharpZipLib.txt`](LICENSE.SharpZipLib.txt)
- **Taken:** 2026-10-30

## Why it is here

This library ships with **zero `PackageReference`s**, and its consumers publish Native AOT. 3.2% of the
sectors in StarCraft II archives are bzip2 (`0x10`); the rest are zlib, which the BCL
already provides. A NuGet reference for one decompressor is not worth the dependency.

## Files

| File | Upstream path |
|---|---|
| `BZip2InputStream.cs` | `src/ICSharpCode.SharpZipLib/BZip2/BZip2InputStream.cs` |
| `BZip2Constants.cs` | `src/ICSharpCode.SharpZipLib/BZip2/BZip2Constants.cs` |
| `BZip2Crc.cs` | `src/ICSharpCode.SharpZipLib/Checksum/BZip2Crc.cs` |
| `CrcUtilities.cs` | `src/ICSharpCode.SharpZipLib/Checksum/CrcUtilities.cs` |
| `BZip2Exception.cs` | rewritten locally (see below) |

## Local changes

- 📦 Namespace moved to `Mpq.Vendor.BZip2`.
- ✂️ **Decompression only** — `BZip2OutputStream`, `BZip2` helpers and the Zip/Tar/GZip
  code are not vendored.
- 🔌 `IChecksum` dropped; `BZip2Crc` is used as a concrete type.
- 🧹 `BZip2Exception` reduced to a plain `IOException` subclass, so
  `System.Runtime.Serialization` is not pulled in (it is AOT-hostile).
- 🤫 `#nullable disable` at the top of the upstream files, so they stay byte-faithful
  under this project's `<Nullable>enable</Nullable>`.

## Updating

Re-copy the four upstream files from a newer tag and re-apply the four changes above.
Keep the copyright headers intact.
