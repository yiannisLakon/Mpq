using System.IO.Compression;
using System.Text;

namespace Mpq;

/// <summary>
/// Archive format, named by Zezula's numbering rather than the raw <c>wFormatVersion</c> field,
/// because the field is off by one and that has bitten everyone who touches this format.
/// </summary>
/// <remarks>
/// <see cref="MpqBuilder"/> writes <see cref="V1"/> and <see cref="V2"/> only, and refuses the
/// rest. That is not a gap: Warcraft III is <em>always</em> parsed as v1 whatever its header
/// claims, and MPQEditor's own StarCraft II preset creates v2 — so v1 and v2 between them cover
/// every game. v3 is an active trap (StormLib coerces any <c>.SC2Map</c> claiming version ≥ 3
/// to be read with the v4 layout), and v4 demands a header MD5 plus bit-packed HET/BET tables
/// that no consumer here needs.
/// </remarks>
public enum MpqFormat {
	/// <summary> <c>wFormatVersion</c> 0, 32-byte header, 4 KB sectors. Every Warcraft III map. </summary>
	V1 = 0,
	/// <summary> <c>wFormatVersion</c> 1, 44-byte header, 16 KB sectors. The StarCraft II target. </summary>
	V2 = 1,
	/// <summary> <c>wFormatVersion</c> 2 — never written, and never emit it for an SC2 extension. </summary>
	V3 = 2,
	/// <summary> <c>wFormatVersion</c> 3 — read by <see cref="MpqArchive"/>, never written. </summary>
	V4 = 3,
}

/// <summary> How one file's sectors are stored. </summary>
public enum MpqCompression {
	/// <summary>
	/// Already-compressed container formats are stored; everything else is zlib. This is
	/// MPQEditor's published rule for its StarCraft II preset, minus the methods its own UI
	/// does not offer.
	/// </summary>
	Auto,
	/// <summary> Stored verbatim: no <c>COMPRESS</c> flag, no sector offset table. </summary>
	None,
	/// <summary> zlib (MPQ mask <c>0x02</c>) — a real zlib stream, header and Adler-32 included. </summary>
	ZLib,
}

/// <summary> Per-file write options. </summary>
public sealed record MpqAddOptions {
	public MpqCompression Compression { get; init; } = MpqCompression.Auto;

	internal static readonly MpqAddOptions Default = new();
}

/// <summary> What <see cref="MpqBuilder.SaveAs"/> produced, after it verified its own output. </summary>
public readonly record struct MpqWriteResult(
	string Path, MpqFormat Format, int Files, long Bytes, long ArchiveBytes,
	int HashTableSize, TimeSpan Elapsed);

/// <summary>
/// Archive-internal names are raw bytes hashed byte-wise, not Unicode text. These convert
/// between a caller's <see cref="string"/> and the byte-per-char form the hash needs — the
/// exact inverse of the Latin-1 decode in <see cref="MpqArchive"/> and the UTF-8 fallback in
/// <see cref="MpqExtractor"/>.
/// </summary>
internal static class MpqNames {
	public const string ListFile = "(listfile)";

	/// <summary>
	/// Folds a logical name to the archive's byte form: separators normalised to '\', leading
	/// separators dropped, and non-ASCII encoded as UTF-8 — which is what SC2's editors write
	/// and what <see cref="MpqExtractor"/> decodes back.
	/// </summary>
	public static string Encode(string name) {
		MpqCompat.ThrowIfNullOrEmpty(name);
		string folded = name.Replace('/', '\\').TrimStart('\\');
		if (folded.Length == 0) throw new MpqException($"'{name}' is not a usable archive name.");
		return MpqCompat.IsAscii(folded) ? folded : Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(folded));
	}

	/// <summary>
	/// The form two names are equal in. MPQ hashes names through StormLib's
	/// <c>AsciiToUpperTable</c>, so <c>CASE.txt</c> and <c>case.txt</c> are one file — a
	/// builder that keys them separately writes two entries whose hashes collide, and one of
	/// them becomes unreachable. The fold is ASCII-only on purpose: <c>OrdinalIgnoreCase</c>
	/// would fold 'é' to 'É', which the hash does not, and the two would stop agreeing.
	/// </summary>
	public static string Canonical(string encoded) {
		Span<char> folded = encoded.Length <= 256 ? stackalloc char[encoded.Length] : new char[encoded.Length];
		for (int i = 0; i < encoded.Length; i++) {
			char c = encoded[i];
			folded[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
		}
		return new string(folded);
	}

	/// <summary> The bytes an already-encoded name occupies in <c>(listfile)</c>. </summary>
	public static byte[] Bytes(string encoded) => Encoding.Latin1.GetBytes(encoded);
}

/// <summary> The compressors the writer emits. zlib is the only one; see <see cref="Pick"/>. </summary>
internal static class MpqCompressor {
	public const byte MaskZLib = 0x02;

	/// <summary>
	/// MPQEditor's StarCraft II ruleset, trimmed to what we can emit. Containers that are
	/// already compressed are stored — zlib on a .png costs CPU and usually grows the file.
	/// </summary>
	static readonly string[] storedExtensions =
		[".mp3", ".ogg", ".ogv", ".jpg", ".jpeg", ".png", ".zip", ".mp4", ".webm", ".7z", ".gz"];

	public static MpqCompression Pick(string encodedName, MpqCompression requested) {
		if (requested != MpqCompression.Auto) return requested;
		string ext = Path.GetExtension(encodedName);
		foreach (string stored in storedExtensions)
			if (ext.Equals(stored, StringComparison.OrdinalIgnoreCase)) return MpqCompression.None;
		return MpqCompression.ZLib;
	}

	/// <summary>
	/// Compresses one sector into <paramref name="destination"/>, mask byte first, and reports
	/// the total length. Returns -1 when the result would not be smaller than the raw sector —
	/// the caller must then store the sector raw with no mask byte, which is the rule that keeps
	/// tiny and incompressible payloads from growing past their own sector bound.
	/// </summary>
	public static int Compress(ReadOnlySpan<byte> sector, Span<byte> destination) {
		destination[0] = MaskZLib;
		using var buffer = new MemoryStream(sector.Length);
		using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
			zlib.Write(sector);

		int payload = (int)buffer.Length;
		if (1 + payload >= sector.Length) return -1;
		buffer.GetBuffer().AsSpan(0, payload).CopyTo(destination[1..]);
		return 1 + payload;
	}
}
