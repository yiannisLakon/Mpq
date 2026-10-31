namespace Mpq;

/// <summary> MPQ's crypt table, filename hashing and block cipher. Fully specified, no I/O. </summary>
internal static class MpqCrypt {
	public const int HashTableOffset = 0;
	public const int HashNameA = 1;
	public const int HashNameB = 2;
	public const int HashFileKey = 3;

	static readonly uint[] table = Build();

	static uint[] Build() {
		var t = new uint[0x500];
		uint seed = 0x00100001;
		for (int i = 0; i < 0x100; i++) {
			for (int j = 0; j < 5; j++) {
				seed = (seed * 125 + 3) % 0x2AAAAB;
				uint hi = (seed & 0xFFFF) << 16;
				seed = (seed * 125 + 3) % 0x2AAAAB;
				t[i + j * 0x100] = hi | (seed & 0xFFFF);
			}
		}
		return t;
	}

	/// <summary>
	/// The MPQ filename hash. Names are hashed byte-wise, so callers must hand over a string
	/// whose chars are the archive's own bytes (see the Latin-1 decode in
	/// <see cref="MpqArchive"/>), not a Unicode-decoded name.
	/// </summary>
	public static uint Hash(string name, int type) {
		uint seed1 = 0x7FED7FED, seed2 = 0xEEEEEEEE;
		foreach (char raw in name) {
			byte c = Upper(raw);
			seed1 = table[type * 0x100 + c] ^ (seed1 + seed2);
			seed2 = c + seed1 + seed2 + (seed2 << 5) + 3;
		}
		return seed1;
	}

	/// <summary>
	/// StormLib's AsciiToUpperTable: '/' folds to '\', a-z fold to A-Z, and every byte from
	/// 0x80 up is left alone. <c>char.ToUpperInvariant</c> would wrongly fold those — 'é' to
	/// 'É' — and then never find a file whose name is not pure ASCII.
	/// </summary>
	static byte Upper(char raw) => raw switch {
		'/' => (byte)'\\',
		>= 'a' and <= 'z' => (byte)(raw - 32),
		_ => (byte)raw,
	};

	/// <summary> Decrypts in place, DWORD-wise. A prefix decrypts correctly on its own. </summary>
	public static void Decrypt(Span<byte> data, uint key) {
		uint seed = 0xEEEEEEEE;
		int dwords = data.Length / 4;
		for (int i = 0; i < dwords; i++) {
			seed += table[0x400 + (key & 0xFF)];
			var slot = data.Slice(i * 4, 4);
			uint plain = BitConverter.ToUInt32(slot) ^ (key + seed);
			key = ((~key << 21) + 0x11111111) | (key >> 11);
			seed = plain + seed + (seed << 5) + 3;
			BitConverter.TryWriteBytes(slot, plain);
		}
	}

	/// <summary> The per-file key, before any FIX_KEY adjustment. </summary>
	public static uint FileKey(string name) {
		int cut = name.LastIndexOf('\\');
		return Hash(cut < 0 ? name : name[(cut + 1)..], HashFileKey);
	}

	/// <summary> MPQ_FILE_FIX_KEY: the key is bound to the file's position and size. </summary>
	public static uint AdjustKey(uint key, uint blockPos, uint fileSize) => (key + blockPos) ^ fileSize;
}
