// Vendored from amcthunder84/mpqfs (https://github.com/amcthunder84/mpqfs), MIT licence.
// Per-file SPDX-License-Identifier: MIT. Copyright (c) 2025 mpqfs contributors.
// See LICENSE.mpqfs.txt and README.md.
// Local changes: namespace moved to Mpq.Vendor.Pkware; the project's own
// MpqException/MpqError pair replaced by InvalidDataException, so nothing outside the BCL
// is vendored alongside. Decompression only — PkwareImplode.cs is not vendored.
// C# port of mpqfs (https://github.com/diasurgical/mpqfs)
// Port by Antonio Martin.
//
// Original: MIT License, Copyright (c) 2025 mpqfs contributors
// SPDX-License-Identifier: MIT
//
// PKWARE Data Compression Library (DCL) "explode" decompression.
//
// Diablo 1's DIABDAT.MPQ compresses its sectors with PKWARE DCL implode
// (block flag Implode, 0x00000100). This is the decompression side.
//
// The algorithm is an LZ77 variant that uses Shannon-Fano coding (not Huffman)
// for literals and length/distance values. The compressed stream starts with
// two bytes:
//   byte 0: compression type - 0 = binary (8-bit literals),
//                              1 = ASCII (variable-length literals via table)
//   byte 1: dictionary size  - 4 = 1024, 5 = 2048, 6 = 4096
//
// The lookup tables come from StormLib's explode.c, via mpqfs.

namespace Mpq.Vendor.Pkware;

internal static class PkwareExplode
{
    /// <summary>
    /// Bit reader that feeds bits out of the byte stream LSB first, which is
    /// how DCL codes are laid out.
    /// </summary>
    private ref struct BitStream
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private uint _bits;
        private int _avail;

        public BitStream(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
            _bits = 0;
            _avail = 0;
        }

        public int Available => _avail;
        public uint Buffered => _bits;

        /// <summary>Makes sure at least <paramref name="need"/> bits are buffered.</summary>
        public bool Fill(int need)
        {
            while (_avail < need)
            {
                if (_pos >= _data.Length) return false;
                _bits |= (uint)_data[_pos++] << _avail;
                _avail += 8;
            }
            return true;
        }

        public uint Peek(int n) => _bits & ((1u << n) - 1u);

        public void Drop(int n)
        {
            _bits >>= n;
            _avail -= n;
        }

        /// <summary>Reads and consumes n bits. Returns false when the input runs out.</summary>
        public bool TryRead(int n, out uint value)
        {
            if (n == 0) { value = 0; return true; }
            if (!Fill(n)) { value = 0; return false; }
            value = Peek(n);
            Drop(n);
            return true;
        }
    }

    // Shannon-Fano decoding walks the table comparing the low bits against each
    // code. That is O(n) per symbol, but the tables are small and this keeps the
    // implementation easy to check against the original.

    private static int DecodeDistance(ref BitStream bs)
    {
        if (!bs.Fill(8)) return -1;
        uint peek = bs.Peek(8);

        for (int i = 0; i < PkwareTables.DistBits.Length; i++)
        {
            int nbits = PkwareTables.DistBits[i];
            uint mask = (1u << nbits) - 1u;
            if ((peek & mask) == PkwareTables.DistCode[i])
            {
                bs.Drop(nbits);
                return i;
            }
        }

        return -1;
    }

    private static int DecodeLength(ref BitStream bs)
    {
        if (!bs.Fill(7)) return -1;
        uint peek = bs.Peek(7);

        for (int i = 0; i < PkwareTables.LenBits.Length; i++)
        {
            int nbits = PkwareTables.LenBits[i];
            uint mask = (1u << nbits) - 1u;
            if ((peek & mask) == PkwareTables.LenCode[i])
            {
                bs.Drop(nbits);
                return i;
            }
        }

        return -1;
    }

    private static int DecodeAsciiLiteral(ref BitStream bs)
    {
        // ASCII codes are at most 13 bits. A short read is not fatal here: a
        // shorter code may still match with the bits already buffered.
        bs.Fill(13);
        uint peek = bs.Buffered;

        for (int i = 0; i < PkwareTables.AsciiBits.Length; i++)
        {
            int nbits = PkwareTables.AsciiBits[i];
            if (nbits == 0 || bs.Available < nbits) continue;

            uint mask = (1u << nbits) - 1u;
            if ((peek & mask) == PkwareTables.AsciiCode[i])
            {
                bs.Drop(nbits);
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Decompresses PKWARE DCL data into <paramref name="destination"/> and
    /// returns the number of bytes written.
    /// </summary>
    public static int Explode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length < 2)
            throw new InvalidDataException("PKWARE stream too short.");

        byte compType = source[0];  // 0 = binary, 1 = ASCII
        byte dictBits = source[1];  // 4, 5 or 6

        if (compType > 1)
            throw new InvalidDataException($"Bad PKWARE literal mode ({compType}).");
        if (dictBits < 4 || dictBits > 6)
            throw new InvalidDataException($"Bad PKWARE dictionary size ({dictBits}).");

        var bs = new BitStream(source[2..]);
        int outPos = 0;

        while (outPos < destination.Length)
        {
            // One flag bit: 0 = literal, 1 = match.
            if (!bs.TryRead(1, out uint flag))
                break; // input exhausted; some encoders omit the end marker

            if (flag == 0)
            {
                int ch;
                if (compType == 1)
                {
                    ch = DecodeAsciiLiteral(ref bs);
                }
                else
                {
                    ch = bs.TryRead(8, out uint v) ? (int)v : -1;
                }

                if (ch < 0)
                    throw new InvalidDataException("Truncated PKWARE literal.");

                destination[outPos++] = (byte)ch;
                continue;
            }

            int lenIndex = DecodeLength(ref bs);
            if (lenIndex < 0)
                throw new InvalidDataException("Bad PKWARE length code.");

            uint extra = 0;
            int exBits = PkwareTables.ExLenBits[lenIndex];
            if (exBits != 0 && !bs.TryRead(exBits, out extra))
                throw new InvalidDataException("Truncated PKWARE length.");

            // End-of-stream marker: the largest encodable value, i.e. code 15
            // with all 8 extra bits set. Checked before computing the length,
            // matching the original PKWARE implementation.
            if (lenIndex == 15 && extra == 0xFF)
                break;

            uint matchLen = (uint)(PkwareTables.LenBase[lenIndex] + extra + 2);

            int distIndex = DecodeDistance(ref bs);
            if (distIndex < 0)
                throw new InvalidDataException("Bad PKWARE distance code.");

            // A 2-byte match reads only 2 low bits; anything longer reads
            // dictBits low bits.
            int lowBits = matchLen == 2 ? 2 : dictBits;
            if (!bs.TryRead(lowBits, out uint low))
                throw new InvalidDataException("Truncated PKWARE distance.");

            uint distance = ((uint)distIndex << lowBits) | low;

            // Distance is 0-based going backwards: 0 means "one byte back".
            if (distance + 1 > (uint)outPos)
                throw new InvalidDataException("PKWARE distance before start of output.");

            int copyFrom = outPos - (int)distance - 1;

            if (outPos + matchLen > destination.Length)
                throw new InvalidDataException("PKWARE output overflow.");

            // Copied one byte at a time on purpose: the ranges may overlap,
            // which is how run-length sequences are encoded.
            for (uint i = 0; i < matchLen; i++)
                destination[outPos++] = destination[copyFrom + (int)i];
        }

        return outPos;
    }
}

