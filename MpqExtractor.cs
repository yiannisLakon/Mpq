using System.Text;

namespace Mpq;

/// <summary>
/// What an extraction produced. <see cref="Skipped"/> is empty on a clean run; each element
/// explains one entry the reader refused, so callers can report a partial extraction instead
/// of mistaking it for success. <see cref="Warnings"/> is for oddities that cost no content.
/// </summary>
public readonly record struct MpqExtraction(
	int Files, long Bytes, IReadOnlyList<string> Skipped, IReadOnlyList<string> Warnings);

/// <summary>
/// In-process replacement for <c>MPQEditor.exe extract "&lt;mpq&gt;" "*" "&lt;dir&gt;" /fp</c>.
/// </summary>
public static class MpqExtractor {
	static readonly UTF8Encoding strictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: true);

	/// <summary>
	/// Extracts every named file into <paramref name="directory"/>, recreating the archive's
	/// internal '\'-separated tree on disk — that is what MPQEditor's <c>/fp</c> does.
	/// The archive's own metadata ((listfile), (attributes)) is not written out.
	/// </summary>
	/// <remarks>
	/// One unreadable entry does not abandon the archive: it is recorded in
	/// <see cref="MpqExtraction.Skipped"/> and the remaining files are still extracted.
	/// </remarks>
	public static MpqExtraction ExtractAll(string mpqPath, string directory) {
		using var archive = MpqArchive.Open(mpqPath);

		string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
		Directory.CreateDirectory(root);

		int files = 0;
		long bytes = 0;
		List<string> skipped = [];

		foreach ((string name, MpqEntry entry) in archive.Enumerate()) {
			if (MpqArchive.InternalFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

			try {
				string target = ResolveTarget(root, name);
				byte[] data = archive.Read(entry, name);

				string? parent = Path.GetDirectoryName(target);
				if (parent is not null) Directory.CreateDirectory(parent);
				File.WriteAllBytes(target, data);

				files++;
				bytes += data.Length;
			} catch (Exception ex) when (ex is MpqException or IOException or InvalidDataException) {
				// One corrupt or unsupported entry must not cost us the rest of the archive.
				// MpqException already names the entry; a decompressor's exception does not.
				skipped.Add(ex is MpqException ? ex.Message : $"'{name}': {ex.Message}");
			}
		}

		// Orphan blocks are leftovers no name reaches, so there is nothing to extract and no
		// content lost — MPQEditor ignores them too. Worth saying, not worth failing over.
		List<string> warnings = [];
		int unnamed = archive.UnnamedBlockCount();
		if (unnamed > 0)
			warnings.Add($"{unnamed} block(s) exist but no (listfile) name reaches them; nothing to extract for those.");

		return new MpqExtraction(files, bytes, skipped, warnings);
	}

	/// <summary>
	/// Maps an archive-internal name onto a path under <paramref name="root"/>, refusing any
	/// name that would escape it — an archive is untrusted input.
	/// </summary>
	static string ResolveTarget(string root, string name) {
		string relative = DiskName(name)
			.Replace('\\', Path.DirectorySeparatorChar)
			.Replace('/', Path.DirectorySeparatorChar)
			.TrimStart(Path.DirectorySeparatorChar);
		string target = Path.GetFullPath(Path.Combine(root, relative));

		if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			throw new MpqException($"'{name}' would be extracted outside the target directory.");

		return target;
	}

	/// <summary>
	/// Archive names arrive as raw bytes (one per char, see the Latin-1 decode in
	/// <see cref="MpqArchive"/>), which is what the hash needs but not always what the name
	/// means. SC2 editors write non-ASCII names as UTF-8, so decode them that way for disk and
	/// fall back to the bytes as-is when they are not valid UTF-8.
	/// </summary>
	static string DiskName(string name) {
		if (MpqCompat.IsAscii(name)) return name;   // the overwhelmingly common case

		byte[] raw = Encoding.Latin1.GetBytes(name);
		try {
			return strictUtf8.GetString(raw);
		} catch (DecoderFallbackException) {
			return name;
		}
	}
}
