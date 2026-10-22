using System;
using System.Collections.Generic;
using System.IO;

namespace Lara_Extractor
{
    /// <summary>
    /// PlayStation SPU-ADPCM decoder. Samples are stored as 16-byte blocks:
    ///   byte 0: low nibble = shift, high nibble = filter; byte 1: flags (bit 0 = end of sample, bit 1 = loop, bit 2 = loop start);
    ///   bytes 2-15: 28 4-bit samples, low nibble first.
    /// Tomb Raider PlayStation samples have no VAG header; level sounds play at 11025 Hz when the per-sound pitch
    /// modifier is 0 (the game computes an SPU pitch of 0x400, where 0x1000 is 44100 Hz).
    /// </summary>
    internal static class PSXAdpcm
    {
        public const int BlockSize = 16;
        public const int SamplesPerBlock = 28;
        public const int DefaultSampleRate = 11025;

        private static readonly int[] F0 = { 0, 60, 115, 98, 122 };
        private static readonly int[] F1 = { 0, 0, -52, -55, -60 };

        /// <summary>Length in bytes of the sample starting at <paramref name="start"/>, up to and including the block with the end flag.</summary>
        public static int FindSampleLength(byte[] data, int start, int limit)
        {
            int p = start;
            while (p + BlockSize <= limit)
            {
                bool end = (data[p + 1] & 1) != 0;
                p += BlockSize;
                if (end) break;
            }
            return p - start;
        }

        public static short[] Decode(byte[] data, int start, int length)
        {
            int blocks = length / BlockSize;
            var pcm = new short[blocks * SamplesPerBlock];
            int old = 0, older = 0, o = 0;

            for (int b = 0; b < blocks; b++)
            {
                int p = start + b * BlockSize;
                int shift = data[p] & 15;
                int filter = Math.Min(data[p] >> 4, 4);
                if (shift > 12) shift = 9;

                for (int i = 0; i < SamplesPerBlock; i++)
                {
                    int nibble = (data[p + 2 + i / 2] >> ((i & 1) * 4)) & 15;
                    int s = (short)(nibble << 12) >> shift;
                    s += (old * F0[filter] + older * F1[filter] + 32) >> 6;
                    s = Math.Clamp(s, short.MinValue, short.MaxValue);
                    pcm[o++] = (short)s;
                    older = old;
                    old = s;
                }
            }

            return pcm;
        }

        public static void WriteWav(string path, short[] pcm, int sampleRate)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            int dataSize = pcm.Length * 2;
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            w.Write(36 + dataSize);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16);
            w.Write((short)1);              // PCM
            w.Write((short)1);              // mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            w.Write(dataSize);
            foreach (short s in pcm) w.Write(s);
        }
    }
}
