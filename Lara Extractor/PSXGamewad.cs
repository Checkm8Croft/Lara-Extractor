using System;
using System.Collections.Generic;
using System.IO;

namespace Lara_Extractor
{
    /// <summary>
    /// PlayStation GAMEWAD.OBJ container (Tomb Raider: The Last Revelation / Chronicles).
    ///
    /// Layout (see scripts/gamewad.py and SPEC_PSX/CD.H in the TOMB5 project, confirmed on a TR4 GAMEWAD):
    ///   51 entries of { int32 offset, int32 size } at the start of the file, offsets are multiples of 2048.
    ///   Entry 0 = SETUP.MOD, entry 1 = cutscenes (CUTSEQ), entry 2 = title, the following ones are levels,
    ///   then loading screens (512x256, 16bpp) and a few small blobs.
    /// </summary>
    public sealed class PSXGamewad
    {
        public const int EntryCount = 51;
        public const int SectorSize = 2048;

        public readonly record struct Entry(int Index, long Offset, int Size);

        private readonly byte[] _data;
        public IReadOnlyList<Entry> Entries { get; }

        private PSXGamewad(byte[] data, List<Entry> entries)
        {
            _data = data;
            Entries = entries;
        }

        /// <summary>Returns null if the file does not look like a GAMEWAD.OBJ.</summary>
        public static PSXGamewad? TryOpen(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < SectorSize || info.Length > int.MaxValue)
                return null;

            byte[] data = File.ReadAllBytes(path);
            var entries = new List<Entry>();
            int validNonEmpty = 0;

            for (int i = 0; i < EntryCount; i++)
            {
                int offset = BitConverter.ToInt32(data, i * 8);
                int size = BitConverter.ToInt32(data, i * 8 + 4);

                if (offset < 0 || size < 0 || (offset % SectorSize) != 0 || (long)offset + size > data.Length)
                {
                    // Entries past the table must be sane; a single bad one means this is not a GAMEWAD.
                    return null;
                }

                if (size > 0) validNonEmpty++;
                entries.Add(new Entry(i, offset, size));
            }

            return validNonEmpty >= 3 ? new PSXGamewad(data, entries) : null;
        }

        public byte[] Read(Entry entry)
        {
            var result = new byte[entry.Size];
            Buffer.BlockCopy(_data, (int)entry.Offset, result, 0, entry.Size);
            return result;
        }
    }
}
