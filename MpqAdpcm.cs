namespace Mpq;

/// <summary>
/// Blizzard's IMA ADPCM variant, MPQ compression masks <c>0x40</c> (mono) and <c>0x80</c>
/// (stereo). Decoder only; see <c>Vendor/Adpcm/README.md</c> for provenance and for the
/// validation run behind it.
/// </summary>
/// <remarks>
/// ⚠️ <b>The channel count is not in the stream.</b> Its header is a zero byte, a bit shift and
/// one 16-bit seed sample <em>per channel</em>, so a stereo stream read as mono consumes one
/// seed instead of two and then alternates the wrong predictor — output of exactly the right
/// length that merely sounds wrong. The mask bit is the only thing that says which it is, which
/// is why <paramref name="channels"/> is a required parameter rather than something sniffed.
/// <para>
/// ADPCM is also <b>lossy</b>: the decoded PCM is not the bytes that went in, so it cannot be
/// checked by round-tripping against an original. It is deterministic, though — the same stream
/// always decodes to the same samples — so it can be checked against a hash declared by another
/// implementation, and is.
/// </para>
/// </remarks>
public static class MpqAdpcm {
	/// <summary>
	/// Decodes one ADPCM stream into <paramref name="destination"/> as 16-bit little-endian PCM
	/// and returns the number of bytes written, which is less than its length when the stream
	/// ends first.
	/// </summary>
	/// <param name="source"> The ADPCM stream, starting at its leading zero byte. </param>
	/// <param name="destination"> Sized to the expected decoded length. Never grown. </param>
	/// <param name="channels"> 1 for mask <c>0x40</c>, 2 for mask <c>0x80</c>. </param>
	/// <exception cref="InvalidDataException"> The stream is shorter than its own header. </exception>
	public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, int channels) =>
		Vendor.Adpcm.AdpcmCodec.Decompress(source, destination, channels);
}
