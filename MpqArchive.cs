using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Mpq;

/// <summary> Anything the reader refuses to do, with the offending entry named. </summary>
public sealed class MpqException(string message):Exception(message);

[Flags]
public enum MpqFlags:uint {
	Implode = 0x0000_0100,
	Compress = 0x0000_0200,
	Encrypted = 0x0001_0000,
	FixKey = 0x0002_0000,
	PatchFile = 0x0010_0000,
	SingleUnit = 0x0100_0000,
	DeleteMarker = 0x0200_0000,
	SectorCrc = 0x0400_0000,
	Exists = 0x8000_0000,
}

public readonly record struct MpqEntry(uint Position, uint CompressedSize, uint Size, MpqFlags Flags);

/// <summary>
/// A read-only MPQ archive: header v0/v1/v3, classic hash and block tables, and every
/// compression Blizzard's own games put in one — zlib, bzip2, PKWARE DCL, Huffman and ADPCM,
/// alone or chained. Everything outside that subset (LZMA, sparse, patch files) throws
/// <see cref="MpqException"/> naming the entry, rather than producing wrong bytes.
/// </summary>
public sealed class MpqArchive:IDisposable {
	const uint magicMpq = 0x1A51504D;   // 'MPQ\x1A'
	const uint magicUser = 0x1B51504D;  // 'MPQ\x1B'

	readonly FileStream stream;
	readonly long mpqPos;               // every offset in the archive is relative to this
	readonly int sectorSize;
	readonly MpqEntry[] blocks;
	readonly Dictionary<ulong, int> byName = [];   // (nameA,nameB) -> block index

	public int FormatVersion { get; }
	public int SectorSize => sectorSize;
	public int BlockCount => blocks.Length;

	/// <summary> Names from (listfile). Empty when the archive carries none. </summary>
	public IReadOnlyList<string> Files { get; }

	MpqArchive(FileStream fs) {
		stream = fs;
		mpqPos = LocateHeader(fs, out int version, out int blockShift,
			out long hashPos, out long blockPos, out uint hashCount, out uint blockCount);
		FormatVersion = version;
		sectorSize = 512 << blockShift;

		// Hash table: maps name hashes to block indices.
		var hashRaw = Read(hashPos, checked((int)(hashCount * 16)));
		MpqCrypt.Decrypt(hashRaw, MpqCrypt.Hash("(hash table)", MpqCrypt.HashFileKey));

		// Block table: where each file's bytes are and how they are stored.
		var blockRaw = Read(blockPos, checked((int)(blockCount * 16)));
		MpqCrypt.Decrypt(blockRaw, MpqCrypt.Hash("(block table)", MpqCrypt.HashFileKey));

		blocks = new MpqEntry[blockCount];
		for (int i = 0; i < blockCount; i++) {
			var s = blockRaw.AsSpan(i * 16);
			blocks[i] = new MpqEntry(
				BinaryPrimitives.ReadUInt32LittleEndian(s),
				BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
				BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
				(MpqFlags)BinaryPrimitives.ReadUInt32LittleEndian(s[12..]));
		}

		for (int i = 0; i < hashCount; i++) {
			var s = hashRaw.AsSpan(i * 16);
			uint nameA = BinaryPrimitives.ReadUInt32LittleEndian(s);
			uint nameB = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
			uint index = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
			if (index >= blockCount) continue;   // 0xFFFFFFFF empty, 0xFFFFFFFE deleted
			byName.TryAdd((ulong)nameA << 32 | nameB, (int)index);
		}

		Files = ReadListFile();
	}

	public static MpqArchive Open(string path) =>
		new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));

	/// <summary> Scans 512-byte boundaries for the header, following a user-data shunt. </summary>
	static long LocateHeader(FileStream fs, out int version, out int blockShift,
		out long hashPos, out long blockPos, out uint hashCount, out uint blockCount) {
		Span<byte> buf = stackalloc byte[32];
		for (long probe = 0; probe + 32 <= fs.Length; probe += 0x200) {
			fs.Position = probe;
			fs.ReadExactly(buf);
			uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buf);

			if (magic == magicUser) {
				// Shunt: the real header sits at probe + HeaderOffset, and that becomes MpqPos.
				uint headerOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]);
				long real = probe + headerOffset;
				if (real + 32 > fs.Length) continue;
				fs.Position = real;
				fs.ReadExactly(buf);
				if (BinaryPrimitives.ReadUInt32LittleEndian(buf) != magicMpq) continue;
				ParseHeader(buf, out version, out blockShift,
					out hashPos, out blockPos, out hashCount, out blockCount);
				return real;
			}

			if (magic == magicMpq) {
				ParseHeader(buf, out version, out blockShift,
					out hashPos, out blockPos, out hashCount, out blockCount);
				return probe;
			}
		}
		throw new MpqException($"No MPQ header found in '{fs.Name}'.");
	}

	/// <summary>
	/// Only the common first 32 bytes are needed. v2 adds high words for the table offsets;
	/// v3/v4 add HET/BET and MD5s, which a reader does not need while the classic tables exist
	/// — and on every SC2 archive measured, they do.
	/// </summary>
	static void ParseHeader(ReadOnlySpan<byte> h, out int version, out int blockShift,
		out long hashPos, out long blockPos, out uint hashCount, out uint blockCount) {
		version = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
		blockShift = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
		hashPos = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
		blockPos = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
		hashCount = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
		blockCount = BinaryPrimitives.ReadUInt32LittleEndian(h[28..]);
		if (blockShift is 0 or > 23) throw new MpqException($"Bad sector-size shift {blockShift}.");
	}

	byte[] Read(long relative, int count) {
		var buf = new byte[count];
		ReadInto(relative, buf);
		return buf;
	}

	void ReadInto(long relative, Span<byte> destination) {
		stream.Position = mpqPos + relative;
		stream.ReadExactly(destination);
	}

	public bool Contains(string name) => byName.ContainsKey(NameKey(name));

	static ulong NameKey(string name) =>
		(ulong)MpqCrypt.Hash(name, MpqCrypt.HashNameA) << 32 | MpqCrypt.Hash(name, MpqCrypt.HashNameB);

	IReadOnlyList<string> ReadListFile() {
		if (!Contains("(listfile)")) return [];
		// Latin-1, not UTF-8: MPQ names are raw bytes, hashed byte-wise, and MPQEditor writes
		// them to disk through the ANSI API. A byte-preserving decode both finds the entry and
		// reproduces the on-disk name byte for byte. Decoding as UTF-8 does neither.
		string text = Encoding.Latin1.GetString(Read("(listfile)"));
		return [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
			StringSplitOptions.TrimEntries)];
	}

	public byte[] Read(string name) {
		if (!byName.TryGetValue(NameKey(name), out int index))
			throw new MpqException($"'{name}' is not in the archive.");
		return Read(blocks[index], name);
	}

	public byte[] Read(MpqEntry e, string name) {
		if ((e.Flags & MpqFlags.Exists) == 0) throw new MpqException($"'{name}' has no EXISTS flag.");
		if ((e.Flags & MpqFlags.PatchFile) != 0) throw new MpqException($"'{name}' is a patch file, which is not supported.");

		uint key = 0;
		bool encrypted = (e.Flags & MpqFlags.Encrypted) != 0;
		if (encrypted) {
			key = MpqCrypt.FileKey(name);
			if ((e.Flags & MpqFlags.FixKey) != 0) key = MpqCrypt.AdjustKey(key, e.Position, e.Size);
		}

		return (e.Flags & MpqFlags.SingleUnit) != 0
			? ReadSingleUnit(e, name, key, encrypted)
			: ReadSectors(e, name, key, encrypted);
	}

	byte[] ReadSingleUnit(MpqEntry e, string name, uint key, bool encrypted) {
		int stored = checked((int)e.CompressedSize);
		var output = new byte[e.Size];
		var pool = ArrayPool<byte>.Shared;
		byte[] rented = pool.Rent(stored);
		try {
			var raw = rented.AsSpan(0, stored);
			ReadInto(e.Position, raw);
			if (encrypted) MpqCrypt.Decrypt(raw, key);

			// Stored-vs-compressed is decided by size, not by the flag: ~3,000 SC2 entries
			// carry COMPRESS yet hold raw bytes, and IMPLODE behaves the same way.
			if ((e.Flags & (MpqFlags.Compress | MpqFlags.Implode)) != 0 && e.CompressedSize < e.Size)
				Decompress(rented, stored, output, name, Imploded(e));
			else
				raw[..Math.Min(stored, output.Length)].CopyTo(output);
		} finally {
			pool.Return(rented);
		}
		return output;
	}

	byte[] ReadSectors(MpqEntry e, string name, uint key, bool encrypted) {
		int count = (int)((e.Size + (uint)sectorSize - 1) / (uint)sectorSize);
		if (count == 0) return [];

		// A file with neither COMPRESS nor IMPLODE is stored raw and has NO offset table;
		// its sectors are simply consecutive. SC2 does this for .mp3/.ogg assets.
		if ((e.Flags & (MpqFlags.Compress | MpqFlags.Implode)) == 0) {
			var stored = Read(e.Position, checked((int)e.Size));
			if (encrypted)
				for (int i = 0; i < count; i++) {
					int at = i * sectorSize;
					MpqCrypt.Decrypt(stored.AsSpan(at, Math.Min(sectorSize, stored.Length - at)),
						key + (uint)i);
				}
			return stored;
		}

		// count+1 offsets bound every sector. A SECTOR_CRC table adds a trailing slot we never
		// need, and decrypting a prefix of the table is valid, so the extra slot is simply not read.
		var offsetsRaw = Read(e.Position, (count + 1) * 4);
		if (encrypted) MpqCrypt.Decrypt(offsetsRaw, key - 1);
		var offsets = new uint[count + 1];
		for (int i = 0; i <= count; i++)
			offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(offsetsRaw.AsSpan(i * 4));

		int widest = 0;
		for (int i = 0; i < count; i++) {
			if (offsets[i + 1] < offsets[i])
				throw new MpqException($"'{name}' has non-monotonic sector offsets " +
					$"(flags=0x{(uint)e.Flags:X8} size={e.Size} csize={e.CompressedSize} " +
					$"sectors={count} off0={offsets[0]} off1={offsets[1]}).");
			widest = Math.Max(widest, (int)(offsets[i + 1] - offsets[i]));
		}

		var output = new byte[e.Size];
		var pool = ArrayPool<byte>.Shared;
		byte[] rented = pool.Rent(Math.Max(widest, 1));
		try {
			int written = 0;
			for (int i = 0; i < count; i++) {
				int stored = (int)(offsets[i + 1] - offsets[i]);
				int expected = Math.Min(sectorSize, (int)e.Size - written);

				if (stored == 0) { written += expected; continue; }   // zero-filled

				var raw = rented.AsSpan(0, stored);
				ReadInto(e.Position + offsets[i], raw);
				if (encrypted) MpqCrypt.Decrypt(raw, key + (uint)i);

				// Same stored-vs-compressed reconciliation as the single-unit path, and for the
				// same reason: COMPRESS and IMPLODE both only say a sector *may* be compressed.
				var destination = output.AsSpan(written, expected);
				if (stored < expected)
					Decompress(rented, stored, destination, name, Imploded(e));
				else
					raw[..expected].CopyTo(destination);

				written += expected;
			}
		} finally {
			pool.Return(rented);
		}
		return output;
	}

	/// <summary>
	/// IMPLODE and COMPRESS are two different carriers, and an entry that sets both is imploded
	/// — which is the order StormLib tests them in too.
	/// </summary>
	static bool Imploded(MpqEntry e) => (e.Flags & MpqFlags.Implode) != 0;

	/// <summary>
	/// Applies one sector's compression. Unencrypted SC2 sectors only ever use zlib or bzip2;
	/// the protected maps' encrypted entries and all of Warcraft II use PKWARE DCL, and Warcraft
	/// II's sound adds Huffman and ADPCM.
	/// </summary>
	/// <param name="raw"> Buffer holding <paramref name="stored"/> bytes of sector. </param>
	/// <param name="implode">
	/// The IMPLODE carrier: no mask byte, the whole sector is a DCL stream. COMPRESS instead
	/// spends the first byte on a mask.
	/// </param>
	static void Decompress(byte[] raw, int stored, Span<byte> destination, string name, bool implode) {
		if (implode) {
			MpqPkware.Explode(raw.AsSpan(0, stored), destination);
			return;
		}

		byte mask = raw[0];
		var payload = raw.AsSpan(1, stored - 1);
		switch (mask) {
			case 0x02:
				// MPQ's 0x02 is zlib-wrapped (header + Adler-32), so ZLibStream, not DeflateStream.
				using (var source = new MemoryStream(raw, 1, stored - 1, writable: false))
				using (var zlib = new ZLibStream(source, CompressionMode.Decompress))
					zlib.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
				return;   // a short sector stays zero-filled, as StormLib leaves it
			case 0x10:
				// MPQ's 0x10 payload is an ordinary bzip2 stream, 'BZh' header and all.
				using (var source = new MemoryStream(raw, 1, stored - 1, writable: false))
				using (var bz = new Vendor.BZip2.BZip2InputStream(source) { IsStreamOwner = false })
					bz.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
				return;
			case 0x08:
				MpqPkware.Explode(payload, destination);
				return;
			case 0x01:
				MpqHuffman.Decompress(payload, destination);
				return;
			case 0x40:
				MpqAdpcm.Decompress(payload, destination, channels: 1);
				return;
			case 0x80:
				MpqAdpcm.Decompress(payload, destination, channels: 2);
				return;
			case 0x41:
				HuffmanThenAdpcm(payload, destination, channels: 1);
				return;
			case 0x81:
				HuffmanThenAdpcm(payload, destination, channels: 2);
				return;
			default:
				throw new MpqException($"'{name}' uses compression mask 0x{mask:X2}, which is not supported.");
		}
	}

	/// <summary>
	/// Masks 0x41 and 0x81, Blizzard's sound chain. A mask is a set, and MPQ undoes the chain in
	/// the reverse of the order it applied it, so Huffman is the outer layer and what comes out
	/// of it is the ADPCM stream — never the other way round. Both halves are lossless about
	/// *length*, so getting the order backwards still fills the sector exactly; it just fills it
	/// with noise, which no size check can catch.
	/// </summary>
	static void HuffmanThenAdpcm(ReadOnlySpan<byte> payload, Span<byte> destination, int channels) {
		// ADPCM turns one byte into one 16-bit sample, so its own stream never exceeds half the
		// PCM it decodes to and the sector's length is a safe bound for the Huffman output.
		var pool = ArrayPool<byte>.Shared;
		byte[] rented = pool.Rent(destination.Length);
		try {
			int adpcm = MpqHuffman.Decompress(payload, rented);
			MpqAdpcm.Decompress(rented.AsSpan(0, adpcm), destination, channels);
		} finally {
			pool.Return(rented);
		}
	}

	/// <summary> Every named entry that the hash table actually resolves to a block. </summary>
	public IEnumerable<(string Name, MpqEntry Entry)> Enumerate() {
		foreach (string name in Files)
			if (byName.TryGetValue(NameKey(name), out int i)) yield return (name, blocks[i]);
	}

	/// <summary> The archive's own metadata, which carries no map content and is never extracted. </summary>
	public static readonly string[] InternalFiles =
		["(listfile)", "(attributes)", "(signature)", "(user data)"];

	/// <summary>
	/// Blocks that exist but no (listfile) name resolves to — the archive's own metadata aside.
	/// Non-zero means an extraction would be incomplete, so callers must treat it as a failure
	/// rather than ignore it.
	/// </summary>
	public int UnnamedBlockCount() {
		var named = new HashSet<int>();
		foreach (string name in Files.Concat(InternalFiles))
			if (byName.TryGetValue(NameKey(name), out int i)) named.Add(i);

		int unnamed = 0;
		for (int i = 0; i < blocks.Length; i++)
			if ((blocks[i].Flags & MpqFlags.Exists) != 0 && !named.Contains(i)) unnamed++;
		return unnamed;
	}

	public void Dispose() => stream.Dispose();
}
