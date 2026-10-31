using System.Runtime.CompilerServices;

namespace Mpq;

/// <summary>
/// The handful of BCL helpers this library used that arrived after its lowest target framework,
/// re-implemented once and called on <em>every</em> target rather than only where the framework
/// lacks them.
/// </summary>
/// <remarks>
/// The tempting alternative — <c>#if</c>-guarded extension methods that substitute only on the
/// older targets — makes each target run different code on the decompression path. That is
/// precisely the failure mode worth designing out: an archive that extracts correctly on
/// net11.0 and subtly wrongly on net6.0 costs far more than a few lines of duplication. Worse,
/// a same-named extension method is silently shadowed by the framework's instance method wherever
/// that exists, so the substitution would be invisible at the call site. Hence the deliberately
/// distinct names here, no <c>#if</c> in this file, and no per-target branch anywhere in the
/// library: all three targets execute these same bodies.
/// </remarks>
static class MpqCompat {
	/// <summary>
	/// Fills <paramref name="buffer"/> or throws — <c>Stream.ReadExactly</c> (.NET 7) by another
	/// name, which is the only sane contract for reading a fixed-size record.
	/// </summary>
	/// <exception cref="EndOfStreamException"> The stream ended first. </exception>
	public static void ReadFully(this Stream stream, Span<byte> buffer) {
		int total = 0;
		while (total < buffer.Length) {
			int got = stream.Read(buffer[total..]);
			if (got <= 0) throw new EndOfStreamException();
			total += got;
		}
	}

	/// <inheritdoc cref="ReadFully(Stream, Span{byte})"/>
	public static void ReadFully(this Stream stream, byte[] buffer, int offset, int count) =>
		stream.ReadFully(buffer.AsSpan(offset, count));

	/// <summary>
	/// Fills as much of <paramref name="buffer"/> as the stream has and reports how much, without
	/// throwing at the end of it — <c>Stream.ReadAtLeast(…, throwOnEndOfStream: false)</c>
	/// (.NET 7). A decompressor that runs dry mid-sector leaves the tail as the caller left it,
	/// zero-filled, which is what StormLib does too.
	/// </summary>
	/// <returns> The bytes actually read; short only when the stream ended. </returns>
	public static int ReadUpTo(this Stream stream, Span<byte> buffer) {
		int total = 0;
		while (total < buffer.Length) {
			int got = stream.Read(buffer[total..]);
			if (got <= 0) break;
			total += got;
		}
		return total;
	}

	/// <summary> <c>System.Text.Ascii.IsValid(string)</c> (.NET 8). </summary>
	public static bool IsAscii(string value) {
		foreach (char c in value)
			if (c > 0x7F) return false;
		return true;
	}

	/// <summary> <c>ArgumentException.ThrowIfNullOrEmpty</c> (.NET 7). </summary>
	/// <exception cref="ArgumentNullException"> <paramref name="argument"/> is null. </exception>
	/// <exception cref="ArgumentException"> <paramref name="argument"/> is empty. </exception>
	public static void ThrowIfNullOrEmpty(string? argument,
		[CallerArgumentExpression(nameof(argument))] string? paramName = null) {
		if (argument is null) throw new ArgumentNullException(paramName);
		if (argument.Length == 0)
			throw new ArgumentException("The value cannot be an empty string.", paramName);
	}

	/// <summary> <c>ArgumentOutOfRangeException.ThrowIfLessThan</c> (.NET 8). </summary>
	public static void ThrowIfLessThan(int value, int other,
		[CallerArgumentExpression(nameof(value))] string? paramName = null) {
		if (value < other)
			throw new ArgumentOutOfRangeException(paramName, value,
				$"{paramName} ('{value}') must be greater than or equal to '{other}'.");
	}

	/// <summary> <c>ArgumentOutOfRangeException.ThrowIfGreaterThan</c> (.NET 8). </summary>
	public static void ThrowIfGreaterThan(int value, int other,
		[CallerArgumentExpression(nameof(value))] string? paramName = null) {
		if (value > other)
			throw new ArgumentOutOfRangeException(paramName, value,
				$"{paramName} ('{value}') must be less than or equal to '{other}'.");
	}
}
