namespace Mpq;

/// <summary>
/// PKWARE Data Compression Library "implode", the one MPQ compression with no BCL equivalent.
/// Decoder only; see <c>Vendor/Pkware/README.md</c> for provenance and for the validation run
/// that stands behind it.
/// </summary>
/// <remarks>
/// Two shapes carry DCL inside an MPQ, and the caller must know which it has:
/// <list type="bullet">
/// <item> <c>MPQ_FILE_COMPRESS</c> (0x200) — every sector starts with a compression mask byte,
///   and mask <c>0x08</c> means the rest of the sector is a DCL stream. Pass the sector
///   <em>without</em> that mask byte. </item>
/// <item> <c>MPQ_FILE_IMPLODE</c> (0x100) — there is no mask byte at all; the whole sector is a
///   DCL stream. Pass it as-is. </item>
/// </list>
/// A DCL stream itself opens with two bytes: literal mode (0 binary, 1 ASCII) and dictionary
/// size in bits (4, 5 or 6). Both are read from <paramref name="source"/>, so callers never
/// supply them.
/// <para>
/// ⚠️ Either flag only says the sector <em>may</em> be compressed. An incompressible sector is
/// written raw and its stored length comes out >= the sector size, with no DCL stream in it at
/// all — so the stored-vs-compressed size check that <c>COMPRESS</c> already gets applies to
/// <c>IMPLODE</c> just the same. Skipping it feeds raw bytes to the decoder, which then rejects
/// them as a bad literal mode.
/// </para>
/// </remarks>
public static class MpqPkware {
	/// <summary>
	/// Explodes one DCL stream into <paramref name="destination"/> and returns the number of
	/// bytes written. Stops early, rather than throwing, when the stream ends before the
	/// destination is full — which is how MPQ's final short sector is encoded.
	/// </summary>
	/// <param name="source"> The DCL stream, starting at its literal-mode byte. </param>
	/// <param name="destination"> Sized to the sector's uncompressed length. Never grown. </param>
	/// <exception cref="InvalidDataException"> The stream is malformed. </exception>
	public static int Explode(ReadOnlySpan<byte> source, Span<byte> destination) =>
		Vendor.Pkware.PkwareExplode.Explode(source, destination);

	/// <summary> Convenience overload for callers that want the bytes rather than a buffer. </summary>
	/// <param name="uncompressedSize"> The size the block table records for this sector. </param>
	public static byte[] Explode(ReadOnlySpan<byte> source, int uncompressedSize) {
		var output = new byte[uncompressedSize];
		int written = Explode(source, output);
		return written == output.Length ? output : output[..written];
	}
}
