namespace Mpq;

/// <summary>
/// Blizzard's adaptive Huffman coding, MPQ compression mask <c>0x01</c>. Decoder only; see
/// <c>Vendor/Huffman/README.md</c> for provenance and for the validation run behind it.
/// </summary>
/// <remarks>
/// The tree is adaptive (Faller-Gallager-Knuth), so there is no table in the stream: the first
/// byte selects one of nine starting weight distributions and the tree reshapes itself as bytes
/// are decoded. Nothing about the payload is therefore seekable, and a truncated stream decodes
/// to a prefix rather than to an error.
/// <para>
/// In practice this mask arrives combined with ADPCM — <c>0x41</c> or <c>0x81</c> — where Huffman
/// is the outer layer and its output is the <see cref="MpqAdpcm"/> stream.
/// </para>
/// </remarks>
public static class MpqHuffman {
	/// <summary>
	/// Decodes one Huffman stream into <paramref name="destination"/> and returns the number of
	/// bytes written, which is less than its length when the stream ends first.
	/// </summary>
	/// <param name="source"> The Huffman stream, starting at its distribution-selector byte. </param>
	/// <param name="destination"> Sized to the expected decoded length. Never grown. </param>
	/// <exception cref="InvalidDataException"> The stream is malformed. </exception>
	public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination) =>
		Vendor.Huffman.HuffmanCodec.Decompress(source, destination);
}
