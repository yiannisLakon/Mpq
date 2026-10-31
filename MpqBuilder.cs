using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace Mpq;

/// <summary>
/// Builds an MPQ archive, format v1 or v2, and refuses everything else — see
/// <see cref="MpqFormat"/> for why that covers every game this library serves.
/// </summary>
/// <remarks>
/// <para>
/// The builder is <em>rewrite-only</em>. There is no in-place mutation, by design: StormLib's
/// own allocator never reuses the hole a replaced file leaves, so editing in place grows an
/// archive without bound and gives a half-written archive on any failure. Here every save
/// produces a fresh, dense file, which also makes compaction fall out for free — a save
/// <em>is</em> a compaction.
/// </para>
/// <para>
/// <see cref="SaveAs"/> never hands back an archive it has not re-read. It writes a temporary
/// file, reopens it with <see cref="MpqArchive"/> — an independent code path that shares nothing
/// with the writer but the format — compares every single file byte for byte against what was
/// added, checks the structural invariants, and only then replaces the target. A writer that is
/// wrong in a way the reader does not notice produces an archive that still opens with one file
/// unreachable through the hash table, so this check is the whole defence against losing data
/// quietly.
/// </para>
/// <para>
/// Not written, deliberately, because no consumer of this library reads them and each one is a
/// documented way to corrupt an archive: <c>(attributes)</c> (its arrays are indexed by block
/// number, so any reindex silently mismatches), <c>(signature)</c>, per-file encryption,
/// <c>SINGLE_UNIT</c> (StormLib strips it from any v1 archive and then expects the sector table
/// that was never written — the mechanism behind the corrupted-<c>.w3n</c> reports), and
/// <c>SECTOR_CRC</c>.
/// </para>
/// </remarks>
public sealed class MpqBuilder:IDisposable {
	const uint magicMpq = 0x1A51504D;       // 'MPQ\x1A'
	const uint magicUser = 0x1B51504D;      // 'MPQ\x1B'
	const uint hashFree = 0xFFFFFFFF;
	const uint hashDeleted = 0xFFFFFFFE;
	const uint maxHashTableSize = 0x80000;

	readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
	readonly List<string> order = [];
	MpqArchive? owned;
	readonly string? openedPath;

	/// <summary> Bytes before the MPQ header — a <c>.w3x</c>'s HM3W block, or an SC2 shunt. </summary>
	readonly byte[] prefix;

	public MpqFormat Format { get; }

	/// <summary> Bytes per sector: 4 KB for v1, 16 KB for v2, matching what each game writes. </summary>
	public int SectorSize => Format == MpqFormat.V1 ? 0x1000 : 0x4000;

	int HeaderSize => Format == MpqFormat.V1 ? 0x20 : 0x2C;

	/// <summary> Content files staged for the next save, excluding the generated <c>(listfile)</c>. </summary>
	public int Count => entries.Count;

	MpqBuilder(MpqFormat format, byte[] prefix, MpqArchive? owned, string? openedPath = null) {
		if (format is not (MpqFormat.V1 or MpqFormat.V2))
			throw new MpqException($"Only MPQ format v1 and v2 can be written, not {format}. " +
				"v3 is a trap (StormLib reads any SC2 archive claiming version >= 3 with the v4 " +
				"layout) and v4 needs a header MD5 and HET/BET tables no consumer here reads.");
		Format = format;
		this.prefix = prefix;
		this.owned = owned;
		this.openedPath = openedPath;
	}

	/// <summary> An empty archive at the given format version. </summary>
	public static MpqBuilder Create(MpqFormat format) => new(format, [], null);

	/// <summary>
	/// Stages every file of an existing archive, so an edit preserves what it does not touch.
	/// Nothing is read here; each file is decompressed during <see cref="SaveAs"/>.
	/// </summary>
	/// <remarks>
	/// Refuses an archive with blocks no <c>(listfile)</c> name reaches. The builder could only
	/// drop those, and dropping files is exactly the failure this class exists to prevent.
	/// </remarks>
	public static MpqBuilder From(MpqArchive source, MpqFormat? format = null) =>
		Stage(new MpqBuilder(format ?? FormatOf(source), [], null), source);

	/// <summary>
	/// Opens an archive for the open → mutate → save-as cycle, keeping its format version and
	/// any bytes that precede the MPQ header. Dispose the builder to close the archive.
	/// </summary>
	public static MpqBuilder Open(string archivePath, MpqFormat? format = null) {
		var archive = MpqArchive.Open(archivePath);
		try {
			return Stage(new MpqBuilder(format ?? FormatOf(archive),
				ReadPrefix(archivePath), archive, Path.GetFullPath(archivePath)), archive);
		} catch {
			archive.Dispose();
			throw;
		}
	}

	static MpqFormat FormatOf(MpqArchive source) => source.FormatVersion switch {
		0 => MpqFormat.V1,
		1 => MpqFormat.V2,
		// A v3/v4 source is readable but not writable at its own version. v2 is what SC2
		// accepts and what MPQEditor's own SC2 preset creates, so that is where it lands.
		_ => MpqFormat.V2,
	};

	static MpqBuilder Stage(MpqBuilder builder, MpqArchive source) {
		int unnamed = source.UnnamedBlockCount();
		if (unnamed > 0)
			throw new MpqException($"{unnamed} block(s) in the source archive exist but no " +
				"(listfile) name reaches them; rewriting it would drop them. Recover the names first.");

		foreach ((string name, MpqEntry entry) in source.Enumerate()) {
			if (MpqArchive.InternalFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
			// Names already carry the archive's bytes one per char, so they are staged as-is.
			builder.Stage(name, new ArchiveSource(source, name, entry.Size), MpqAddOptions.Default);
		}
		return builder;
	}

	/// <summary>
	/// Reproduces the reader's 512-byte-boundary header scan for the sole purpose of measuring
	/// how many bytes precede the header, so they can be copied through verbatim.
	/// </summary>
	/// <remarks>
	/// Duplicated from <see cref="MpqArchive"/>'s <c>LocateHeader</c>, which does not expose the
	/// position it found. Worth folding into one <c>MpqPos</c> property once the reader and
	/// writer branches are merged.
	/// </remarks>
	static byte[] ReadPrefix(string archivePath) {
		using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
		Span<byte> buf = stackalloc byte[32];
		for (long probe = 0; probe + 32 <= fs.Length; probe += 0x200) {
			fs.Position = probe;
			fs.ReadFully(buf);
			uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buf);
			long at = -1;

			if (magic == magicUser) {
				long real = probe + BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]);
				if (real + 32 > fs.Length) continue;
				fs.Position = real;
				fs.ReadFully(buf);
				if (BinaryPrimitives.ReadUInt32LittleEndian(buf) == magicMpq) at = real;
			} else if (magic == magicMpq) {
				at = probe;
			}

			if (at < 0) continue;
			if (at == 0) return [];
			var head = new byte[at];
			fs.Position = 0;
			fs.ReadFully(head);
			return head;
		}
		throw new MpqException($"No MPQ header found in '{archivePath}'.");
	}

	public bool Contains(string name) =>
		entries.ContainsKey(MpqNames.Canonical(MpqNames.Encode(name)));

	/// <summary> Archive names of everything staged, in the order it will be written. </summary>
	public IReadOnlyList<string> Names => [.. order.Select(key => entries[key].Name)];

	/// <summary> Adds or replaces a file from memory. Replacing is the default, not an error. </summary>
	public MpqBuilder Add(string name, ReadOnlySpan<byte> data, MpqAddOptions? options = null) =>
		Stage(MpqNames.Encode(name), new MemorySource(data.ToArray()), options ?? MpqAddOptions.Default);

	/// <summary>
	/// Adds or replaces a file from a stream. The stream is read to memory immediately, because
	/// the save path reads every payload twice — once to write, once to verify.
	/// </summary>
	public MpqBuilder Add(string name, Stream data, MpqAddOptions? options = null) {
		ArgumentNullException.ThrowIfNull(data);
		using var buffer = new MemoryStream();
		data.CopyTo(buffer);
		return Stage(MpqNames.Encode(name), new MemorySource(buffer.ToArray()),
			options ?? MpqAddOptions.Default);
	}

	/// <summary> Adds or replaces a file from disk, read lazily during <see cref="SaveAs"/>. </summary>
	public MpqBuilder AddFile(string name, string diskPath, MpqAddOptions? options = null) {
		var info = new FileInfo(diskPath);
		if (!info.Exists) throw new FileNotFoundException($"'{diskPath}' does not exist.", diskPath);
		return Stage(MpqNames.Encode(name), new FileSource(info), options ?? MpqAddOptions.Default);
	}

	/// <summary>
	/// Adds every file under <paramref name="diskRoot"/>, recursively, naming each by its path
	/// relative to that root with '\' separators and <paramref name="archivePrefix"/> in front.
	/// This is the directory-recursion contract the SC2 repack path expects.
	/// </summary>
	public MpqBuilder AddDirectory(string diskRoot, string archivePrefix = "",
		MpqAddOptions? options = null) {
		string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(diskRoot));
		if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"'{diskRoot}' does not exist.");

		string head = archivePrefix.Replace('/', '\\').Trim('\\');
		foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
			string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '\\');
			AddFile(head.Length == 0 ? relative : $"{head}\\{relative}", file, options);
		}
		return this;
	}

	/// <summary> Drops a staged file. False when no such name was staged. </summary>
	public bool Remove(string name) {
		string key = MpqNames.Canonical(MpqNames.Encode(name));
		if (!entries.Remove(key)) return false;
		order.Remove(key);
		return true;
	}

	/// <summary>
	/// Stages one payload under the name MPQ would consider it to have. A second add under a
	/// name that differs only in case replaces the first, because the archive cannot tell them
	/// apart — the later name is the one written.
	/// </summary>
	MpqBuilder Stage(string encoded, Source source, MpqAddOptions options) {
		if (encoded.Equals(MpqNames.ListFile, StringComparison.OrdinalIgnoreCase))
			throw new MpqException("(listfile) is generated on save and cannot be added directly.");
		string key = MpqNames.Canonical(encoded);
		if (!entries.ContainsKey(key)) order.Add(key);
		entries[key] = new Entry(encoded, source, options);
		return this;
	}

	// ─── saving ──────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Writes the archive, verifies it against what was staged, and only then puts it at
	/// <paramref name="path"/>. An existing target is replaced atomically; a failure anywhere
	/// leaves the target exactly as it was.
	/// </summary>
	/// <exception cref="MpqException"> The written archive did not read back identically, or a
	/// structural invariant failed. The target is untouched in both cases. </exception>
	public MpqWriteResult SaveAs(string path) {
		var clock = Stopwatch.StartNew();
		string target = Path.GetFullPath(path);
		string? directory = Path.GetDirectoryName(target);
		if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

		string temp = target + ".mpqtmp" + Environment.ProcessId.ToString("x");
		Plan plan;
		try {
			plan = WriteTo(temp);
			Validate(plan, new FileInfo(temp).Length - prefix.Length);
			VerifyReadBack(temp, plan);
		} catch {
			TryDelete(temp);
			throw;
		}

		long archiveBytes = new FileInfo(temp).Length;
		// Saving over the archive we opened: every payload has been read and verified by now,
		// so the handle can go. File.Replace cannot touch a file we still hold open.
		if (owned is not null && openedPath is not null &&
			string.Equals(openedPath, target, StringComparison.OrdinalIgnoreCase)) {
			owned.Dispose();
			owned = null;
		}

		if (File.Exists(target)) {
			string backup = target + ".mpqbak" + Environment.ProcessId.ToString("x");
			File.Replace(temp, target, backup, ignoreMetadataErrors: true);
			TryDelete(backup);
		} else {
			File.Move(temp, target);
		}

		long bytes = 0;
		foreach (var entry in entries.Values) bytes += entry.Source.Length;
		return new MpqWriteResult(target, Format, entries.Count, bytes, archiveBytes,
			plan.HashSlots.Length / 16, clock.Elapsed);
	}

	/// <summary>
	/// Rewrites an archive in place through a full read, verify and atomic replace — which is
	/// what compaction is, once the writer never reuses holes.
	/// </summary>
	public static MpqWriteResult Compact(string archivePath) {
		using var builder = Open(archivePath);
		return builder.SaveAs(archivePath);
	}

	/// <summary> Everything the writer decided, kept so it can be checked before it is trusted. </summary>
	sealed record Plan(string[] Names, uint[] Positions, uint[] CompressedSizes, uint[] Sizes,
		MpqFlags[] Flags, byte[] HashSlots, long HashTablePos, long BlockTablePos, int HeaderSize);

	Plan WriteTo(string temp) {
		// (listfile) is written last but must name itself, so the name list is fixed up front.
		string[] names = [.. order.Select(key => entries[key].Name), MpqNames.ListFile];
		int count = names.Length;
		var positions = new uint[count];
		var compressed = new uint[count];
		var sizes = new uint[count];
		var flags = new MpqFlags[count];

		using var fs = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
		if (prefix.Length > 0) fs.Write(prefix);
		long mpqPos = prefix.Length;
		fs.Position = mpqPos + HeaderSize;

		for (int i = 0; i < count - 1; i++) {
			var entry = entries[order[i]];
			positions[i] = Relative(fs.Position - mpqPos, names[i]);
			using var source = entry.Source.OpenRead();
			(compressed[i], sizes[i], flags[i]) = WriteFileData(fs, source, entry.Source.Length,
				MpqCompressor.Pick(names[i], entry.Options.Compression), names[i]);
		}

		// The listfile names every file including itself, which is what makes the archive
		// compactable and resizable later; losing it is irreversible.
		byte[] listFile = BuildListFile(names);
		positions[count - 1] = Relative(fs.Position - mpqPos, MpqNames.ListFile);
		using (var source = new MemoryStream(listFile, writable: false))
			(compressed[count - 1], sizes[count - 1], flags[count - 1]) =
				WriteFileData(fs, source, listFile.Length, MpqCompression.ZLib, MpqNames.ListFile);

		byte[] hashSlots = BuildHashTable(names);
		long hashPos = fs.Position - mpqPos;
		fs.Write(hashSlots);

		byte[] blockSlots = BuildBlockTable(positions, compressed, sizes, flags);
		long blockPos = fs.Position - mpqPos;
		fs.Write(blockSlots);

		long archiveSize = fs.Position - mpqPos;
		fs.Position = mpqPos;
		fs.Write(BuildHeader(archiveSize, hashPos, blockPos,
			(uint)(hashSlots.Length / 16), (uint)count));

		return new Plan(names, positions, compressed, sizes, flags, hashSlots, hashPos, blockPos,
			HeaderSize);
	}

	static uint Relative(long offset, string name) => offset <= uint.MaxValue
		? (uint)offset
		: throw new MpqException($"'{name}' would start past 4 GB; v1 and v2 archives cannot " +
			"address that. Split the archive or write fewer files.");

	byte[] BuildListFile(string[] names) {
		var text = new MemoryStream();
		foreach (string name in names) {
			text.Write(MpqNames.Bytes(name));
			text.Write("\r\n"u8);
		}
		return text.ToArray();
	}

	/// <summary>
	/// Writes one file's payload and reports its block-table triple. Compression is abandoned
	/// for the whole file when no sector shrank: the <c>COMPRESS</c> flag is cleared and the
	/// bytes are stored raw, because a reader decides stored-versus-compressed by size and a
	/// COMPRESS-flagged file whose sectors did not shrink is how tiny and incompressible
	/// payloads turn into garbage.
	/// </summary>
	(uint Compressed, uint Size, MpqFlags Flags) WriteFileData(FileStream fs, Stream source,
		long length, MpqCompression mode, string name) {
		if (length > uint.MaxValue)
			throw new MpqException($"'{name}' is {length} bytes; v1 and v2 archives cap a file at 4 GB.");
		uint size = (uint)length;

		// A zero-length file is a block with no payload at all. It must not carry COMPRESS:
		// there is no sector offset table to find.
		if (size == 0) return (0, 0, MpqFlags.Exists);

		if (mode == MpqCompression.None) {
			source.CopyTo(fs);
			return (size, size, MpqFlags.Exists);
		}

		int sectorSize = SectorSize;
		int count = (int)((size + (uint)sectorSize - 1) / (uint)sectorSize);
		long start = fs.Position;
		var offsets = new uint[count + 1];
		offsets[0] = (uint)((count + 1) * 4);
		fs.Position = start + offsets[0];

		var pool = ArrayPool<byte>.Shared;
		byte[] raw = pool.Rent(sectorSize);
		byte[] packed = pool.Rent(sectorSize);
		bool anyShrank = false;
		try {
			for (int i = 0; i < count; i++) {
				int want = (int)Math.Min(sectorSize, size - (long)i * sectorSize);
				source.ReadFully(raw, 0, want);
				int got = MpqCompressor.Compress(raw.AsSpan(0, want), packed.AsSpan(0, want));
				if (got > 0) {
					fs.Write(packed, 0, got);
					anyShrank = true;
				} else {
					fs.Write(raw, 0, want);
					got = want;
				}
				offsets[i + 1] = offsets[i] + (uint)got;
			}
		} finally {
			pool.Return(raw);
			pool.Return(packed);
		}

		if (!anyShrank || offsets[count] >= size) {
			// Rewind and store it raw. Seeking the source back is cheap for every source here.
			fs.Position = start;
			fs.SetLength(start);
			source.Position = 0;
			source.CopyTo(fs);
			return (size, size, MpqFlags.Exists);
		}

		long end = fs.Position;
		fs.Position = start;
		var table = new byte[(count + 1) * 4];
		for (int i = 0; i <= count; i++)
			BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 4), offsets[i]);
		fs.Write(table);
		fs.Position = end;

		return (offsets[count], size, MpqFlags.Compress | MpqFlags.Exists);
	}

	/// <summary>
	/// The hash table: 0xFF everywhere (FREE), then one linear probe per name from
	/// <c>Hash(name, 0) &amp; mask</c>. Sizing keeps the load factor at or under ¾ <em>and</em>
	/// guarantees a FREE slot survives — a full table has no terminator, so a lookup wraps,
	/// gives up, and the file becomes invisible while the archive still opens.
	/// </summary>
	static byte[] BuildHashTable(string[] names) {
		uint size = HashTableSizeFor(names.Length);
		var slots = new byte[size * 16];
		slots.AsSpan().Fill(0xFF);
		uint mask = size - 1;

		for (int i = 0; i < names.Length; i++) {
			string name = names[i];
			uint nameA = MpqCrypt.Hash(name, MpqCrypt.HashNameA);
			uint nameB = MpqCrypt.Hash(name, MpqCrypt.HashNameB);
			uint start = MpqCrypt.Hash(name, MpqCrypt.HashTableOffset) & mask;

			bool placed = false;
			for (uint step = 0; step <= mask; step++) {
				uint slot = (start + step) & mask;
				var s = slots.AsSpan((int)slot * 16);
				uint index = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
				if (index is hashFree or hashDeleted) {
					BinaryPrimitives.WriteUInt32LittleEndian(s, nameA);
					BinaryPrimitives.WriteUInt32LittleEndian(s[4..], nameB);
					BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);   // locale 0, platform 0
					BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)i);
					placed = true;
					break;
				}
				if (BinaryPrimitives.ReadUInt32LittleEndian(s) == nameA &&
					BinaryPrimitives.ReadUInt32LittleEndian(s[4..]) == nameB)
					throw new MpqException($"'{name}' collides with an already-placed name " +
						"(same hash pair). One of the two would be unreachable.");
			}
			if (!placed) throw new MpqException($"The hash table filled up before '{name}'.");
		}

		MpqWriteCrypt.Encrypt(slots, MpqCrypt.Hash("(hash table)", MpqCrypt.HashFileKey));
		return slots;
	}

	static uint HashTableSizeFor(int count) {
		uint size = 4;
		while (size < maxHashTableSize && (count >= size || (long)count * 4 > (long)size * 3))
			size <<= 1;
		if (count >= size)
			throw new MpqException($"{count} files need a hash table larger than the " +
				$"{maxHashTableSize} slots a v1/v2 archive allows.");
		return size;
	}

	/// <summary>
	/// The block table, one live entry per file and no gaps. Stray non-EXISTS entries inside
	/// <c>dwBlockTableSize</c> open fine in editors and in-game but are rejected by third-party
	/// consumers, so the table is exactly as long as the file count.
	/// </summary>
	static byte[] BuildBlockTable(uint[] positions, uint[] compressed, uint[] sizes, MpqFlags[] flags) {
		var slots = new byte[positions.Length * 16];
		for (int i = 0; i < positions.Length; i++) {
			var s = slots.AsSpan(i * 16);
			BinaryPrimitives.WriteUInt32LittleEndian(s, positions[i]);
			BinaryPrimitives.WriteUInt32LittleEndian(s[4..], compressed[i]);
			BinaryPrimitives.WriteUInt32LittleEndian(s[8..], sizes[i]);
			BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)flags[i]);
		}
		MpqWriteCrypt.Encrypt(slots, MpqCrypt.Hash("(block table)", MpqCrypt.HashFileKey));
		return slots;
	}

	byte[] BuildHeader(long archiveSize, long hashPos, long blockPos, uint hashCount, uint blockCount) {
		var h = new byte[HeaderSize];
		BinaryPrimitives.WriteUInt32LittleEndian(h, magicMpq);
		BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)HeaderSize);
		BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), (uint)archiveSize);
		BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(12), (ushort)Format);
		// 512 << shift == SectorSize: 3 for 4 KB, 5 for 16 KB.
		BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(14),
			(ushort)(System.Numerics.BitOperations.Log2((uint)SectorSize) - 9));
		BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), (uint)hashPos);
		BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), (uint)blockPos);
		BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), hashCount);
		BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), blockCount);
		// v2's tail is the 64-bit table-position extension, and it is zero for every archive
		// that fits in 4 GB. The high words must still be present or the header is the wrong size.
		return h;
	}

	// ─── checking ────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// The structural invariants, pure arithmetic over what was just written. Each one is a way
	/// an archive opens cleanly and still loses a file, so they are checked on every save rather
	/// than in a test.
	/// </summary>
	static void Validate(Plan plan, long archiveLength) {
		int count = plan.Names.Length;
		uint hashSize = (uint)(plan.HashSlots.Length / 16);

		if (!System.Numerics.BitOperations.IsPow2(hashSize))
			throw new MpqException($"Hash table size {hashSize} is not a power of two; the probe " +
				"mask degenerates and files become invisible.");
		if (count >= hashSize)
			throw new MpqException($"{count} entries in {hashSize} hash slots leaves no FREE slot " +
				"to terminate a probe.");

		// Decrypt a copy so the probe can be walked exactly as a reader would.
		var slots = plan.HashSlots.AsSpan().ToArray();
		MpqCrypt.Decrypt(slots, MpqCrypt.Hash("(hash table)", MpqCrypt.HashFileKey));
		uint mask = hashSize - 1;

		for (uint i = 0; i < hashSize; i++) {
			uint index = BinaryPrimitives.ReadUInt32LittleEndian(slots.AsSpan((int)i * 16 + 12));
			if (index is hashFree or hashDeleted) continue;
			if (index >= count)
				throw new MpqException($"Hash slot {i} points at block {index} of {count}.");
			if ((plan.Flags[index] & MpqFlags.Exists) == 0)
				throw new MpqException($"Hash slot {i} points at block {index}, which has no EXISTS flag.");
		}

		// The lookup every reader and both games perform: probe from the name's own slot and
		// stop at FREE. Exactly one live match must be found, and it must be this file's block.
		for (int i = 0; i < count; i++) {
			string name = plan.Names[i];
			uint nameA = MpqCrypt.Hash(name, MpqCrypt.HashNameA);
			uint nameB = MpqCrypt.Hash(name, MpqCrypt.HashNameB);
			uint start = MpqCrypt.Hash(name, MpqCrypt.HashTableOffset) & mask;
			int found = 0, at = -1;
			for (uint step = 0; step <= mask; step++) {
				var s = slots.AsSpan((int)((start + step) & mask) * 16);
				uint index = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
				if (index == hashFree) break;
				if (index == hashDeleted) continue;
				if (BinaryPrimitives.ReadUInt32LittleEndian(s) == nameA &&
					BinaryPrimitives.ReadUInt32LittleEndian(s[4..]) == nameB) { found++; at = (int)index; }
			}
			if (found != 1 || at != i)
				throw new MpqException($"Probing for '{name}' found {found} live match(es) " +
					$"resolving to block {at}, not exactly one resolving to block {i}.");
		}

		// Byte ranges: inside the file, never overlapping each other, the header or the tables.
		var ranges = new List<(long Start, long End, string Name)>(count + 2);
		for (int i = 0; i < count; i++) {
			long end = (long)plan.Positions[i] + plan.CompressedSizes[i];
			if (end > archiveLength)
				throw new MpqException($"'{plan.Names[i]}' ends at {end}, past the archive's " +
					$"{archiveLength} bytes.");
			if (plan.CompressedSizes[i] > 0) ranges.Add((plan.Positions[i], end, plan.Names[i]));

			var f = plan.Flags[i];
			if ((f & MpqFlags.Exists) == 0)
				throw new MpqException($"'{plan.Names[i]}' has no EXISTS flag.");
			if ((f & MpqFlags.Implode) != 0 && (f & MpqFlags.Compress) != 0)
				throw new MpqException($"'{plan.Names[i]}' sets both IMPLODE and COMPRESS.");
			if ((f & MpqFlags.SectorCrc) != 0 && (f & MpqFlags.SingleUnit) != 0)
				throw new MpqException($"'{plan.Names[i]}' sets both SECTOR_CRC and SINGLE_UNIT.");
			if ((f & MpqFlags.FixKey) != 0 && (f & MpqFlags.Encrypted) == 0)
				throw new MpqException($"'{plan.Names[i]}' sets FIX_KEY without ENCRYPTED.");
			if ((f & MpqFlags.Compress) != 0 && plan.CompressedSizes[i] >= plan.Sizes[i])
				throw new MpqException($"'{plan.Names[i]}' is COMPRESS-flagged but did not shrink " +
					$"({plan.CompressedSizes[i]} >= {plan.Sizes[i]}); it must be stored raw instead.");
		}
		ranges.Add((plan.HashTablePos, plan.HashTablePos + plan.HashSlots.Length, "(hash table)"));
		ranges.Add((plan.BlockTablePos, plan.BlockTablePos + count * 16L, "(block table)"));

		ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
		for (int i = 0; i < ranges.Count; i++) {
			if (i > 0 && ranges[i].Start < ranges[i - 1].End)
				throw new MpqException($"'{ranges[i].Name}' overlaps '{ranges[i - 1].Name}'.");
		}
		if (ranges.Count > 0 && ranges[0].Start < plan.HeaderSize)
			throw new MpqException($"'{ranges[0].Name}' starts at {ranges[0].Start}, inside the header.");
	}

	/// <summary>
	/// Reopens the written archive with <see cref="MpqArchive"/> and compares every file byte
	/// for byte against the source it was built from. The reader shares no code with the writer,
	/// so agreement here means the archive is readable by something that was not written to
	/// agree with us.
	/// </summary>
	void VerifyReadBack(string temp, Plan plan) {
		using var check = MpqArchive.Open(temp);

		var listed = new HashSet<string>(check.Files, StringComparer.Ordinal);
		foreach (string name in plan.Names)
			if (!listed.Contains(name))
				throw new MpqException($"'{name}' was written but its own (listfile) does not name it.");
		if (listed.Count != plan.Names.Length)
			throw new MpqException($"(listfile) names {listed.Count} files; {plan.Names.Length} were written.");
		if (check.UnnamedBlockCount() != 0)
			throw new MpqException($"{check.UnnamedBlockCount()} written block(s) are not reachable by name.");

		foreach (string key in order) {
			(string name, Source source, _) = entries[key];
			if (!check.Contains(name))
				throw new MpqException($"'{name}' was written but cannot be found through the hash table.");
			byte[] actual = check.Read(name);
			if (actual.LongLength != source.Length)
				throw new MpqException($"'{name}' read back as {actual.Length} bytes, not {source.Length}.");
			if (!source.Matches(actual))
				throw new MpqException($"'{name}' read back with different bytes than it was written from.");
		}
	}

	static void TryDelete(string path) {
		try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	public void Dispose() => owned?.Dispose();

	// ─── staged payloads ─────────────────────────────────────────────────────────────────────

	readonly record struct Entry(string Name, Source Source, MpqAddOptions Options);

	/// <summary> A staged payload, readable twice: once to write, once to verify. </summary>
	abstract class Source {
		public abstract long Length { get; }
		public abstract Stream OpenRead();

		/// <summary> Compares without materialising a second copy of a large payload. </summary>
		public bool Matches(ReadOnlySpan<byte> actual) {
			using var source = OpenRead();
			var pool = ArrayPool<byte>.Shared;
			byte[] buffer = pool.Rent(1 << 20);
			try {
				int at = 0;
				while (at < actual.Length) {
					int got = source.Read(buffer, 0, Math.Min(buffer.Length, actual.Length - at));
					if (got <= 0) return false;
					if (!buffer.AsSpan(0, got).SequenceEqual(actual.Slice(at, got))) return false;
					at += got;
				}
				return source.ReadByte() < 0;
			} finally {
				pool.Return(buffer);
			}
		}
	}

	sealed class MemorySource(byte[] data):Source {
		public override long Length => data.Length;
		public override Stream OpenRead() => new MemoryStream(data, writable: false);
	}

	sealed class FileSource(FileInfo info):Source {
		public override long Length => info.Length;
		public override Stream OpenRead() =>
			new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
	}

	/// <summary>
	/// A file still inside its source archive. Read on demand, twice, which costs a second
	/// decompression but keeps a whole archive's worth of payloads out of memory.
	/// </summary>
	sealed class ArchiveSource(MpqArchive source, string name, uint size):Source {
		public override long Length => size;
		public override Stream OpenRead() => new MemoryStream(source.Read(name), writable: false);
	}
}
