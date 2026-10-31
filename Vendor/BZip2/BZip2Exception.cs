// Vendored from SharpZipLib (https://github.com/icsharpcode/SharpZipLib), MIT licence.
// Copyright (c) 2000-2018 SharpZipLib Contributors. See LICENSE.SharpZipLib.txt.
// Local changes: namespace moved to Mpq.Vendor.BZip2; IChecksum interface dropped
// (concrete BZip2Crc used directly); BZip2Exception reduced to a local IOException
// subclass so System.Runtime.Serialization is not needed. Decompression only.

using System.IO;

namespace Mpq.Vendor.BZip2;

/// <summary> Raised on malformed bzip2 data. Replaces SharpZipLib's serializable exception. </summary>
public sealed class BZip2Exception(string message):IOException(message);
