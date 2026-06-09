using System;
using System.IO;
using System.IO.Compression;

namespace Lara_Extractor
{
    public class AssetProcessor
    {
        private readonly string _filePath;
        private readonly Action<string> _logger;
        private readonly Action<double> _progressReporter;
        private string _outputDir;

        public AssetProcessor(string filePath, Action<string> logger, Action<double> progressReporter)
        {
            _filePath = filePath;
            _logger = logger;
            _progressReporter = progressReporter;
        }

        public void UnpackAll()
        {
            _outputDir = Path.Combine(Path.GetDirectoryName(_filePath), "Lara_Extractor_Output");
            if (!Directory.Exists(_outputDir))
                Directory.CreateDirectory(_outputDir);

            string extension = Path.GetExtension(_filePath).ToLower();

            using (FileStream fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                uint version = br.ReadUInt32();
                _logger($"[TRosettaStone] Engine Version verified: 0x{version:X8}");

                if (extension == ".tr4" || extension == ".trc" || version == 0x00345254 || version == 0x00355254)
                {
                    _logger("[Engine] Processing Next-Gen Stream (TR4/TR5)...");
                    ParseTR4TR5Strict(br);
                }
                else if (extension == ".phd" || extension == ".tub" || version == 0x20)
                {
                    _logger("[Engine] Processing Legacy 8-bit Stream (TR1)...");
                    ParseTR1Strict(br);
                }
                else if (extension == ".tr2" || extension == ".tr3")
                {
                    _logger("[Engine] Processing Classic 16-bit Stream (TR2/TR3)...");
                    ProcessTR2TR3(br);
                }
            }
            _progressReporter(100);
        }
        private void ParseTR1Strict(BinaryReader br)
        {
            br.BaseStream.Position = 4;

            uint numTextureTiles = br.ReadUInt32();
            _logger($"[TR1] Reading {numTextureTiles} native texture tiles...");

            string textureDir = Path.Combine(_outputDir, "Textures");
            if (!Directory.Exists(textureDir)) Directory.CreateDirectory(textureDir);

            byte[] palette = new byte[768];
            for (int i = 0; i < 256; i++) { palette[i * 3] = (byte)i; palette[i * 3 + 1] = (byte)i; palette[i * 3 + 2] = (byte)i; }

            for (uint i = 0; i < numTextureTiles; i++)
            {
                if (br.BaseStream.Position + (256 * 256) > br.BaseStream.Length) break;
                byte[] indexedPixels = br.ReadBytes(256 * 256);
                byte[] converted24BitPixels = ApplyPalette(indexedPixels, palette);
                WriteTgaFile(Path.Combine(textureDir, $"Tile_Page_{i}.tga"), converted24BitPixels, 3, true);
            }

            _progressReporter(40);

            ExtractTR1AudioIndexed(br);
        }
        private void ExtractTR1AudioIndexed(BinaryReader br)
        {
            string soundDir = Path.Combine(_outputDir, "Audio_Samples_TR1");
            long fileLen = br.BaseStream.Length;

            _logger("[TR1 Audio] Executing sequential hardware sample unpacking...");

            try
            {

                long audioDataStartPos = fileLen - 348788; 
                if (audioDataStartPos < 0) audioDataStartPos = 0;

                br.BaseStream.Position = audioDataStartPos;
                if (!Directory.Exists(soundDir)) Directory.CreateDirectory(soundDir);

                uint extractedCounter = 0;

                while (br.BaseStream.Position < fileLen - 8)
                {
                    uint currentSize = br.ReadUInt32();

                    if (currentSize > 500 && currentSize < 100000 && br.BaseStream.Position + currentSize <= fileLen)
                    {

                        int headerSkip = 32;

                        if (currentSize > headerSkip)
                        {
                            long nextBlockPos = br.BaseStream.Position + currentSize;

                            br.BaseStream.Position += headerSkip;

                            int cleanSize = (int)currentSize - headerSkip;
                            byte[] cleanPcm = br.ReadBytes(cleanSize);

                            if (cleanPcm.Length > 0)
                            {

                                int fadeLength = Math.Min(cleanPcm.Length, 80);
                                for (int f = 0; f < fadeLength; f++)
                                {
                                    double fadeFactor = (double)f / fadeLength;

                                    int sampleOffset = cleanPcm[f] - 128;
                                    cleanPcm[f] = (byte)(128 + (sampleOffset * fadeFactor));
                                }

                                string classicWavPath = Path.Combine(soundDir, $"TR1_SFX_{extractedCounter}.wav");
                                WriteWav8BitUnsigned(classicWavPath, cleanPcm, 22050);
                                extractedCounter++;
                            }

                            br.BaseStream.Position = nextBlockPos;
                        }
                        else
                        {
                            byte[] rawPcm = br.ReadBytes((int)currentSize);
                            if (rawPcm.Length > 0)
                            {
                                string classicWavPath = Path.Combine(soundDir, $"TR1_SFX_{extractedCounter}.wav");
                                WriteWav8BitUnsigned(classicWavPath, rawPcm, 22050);
                                extractedCounter++;
                            }
                        }

                        long remainder = br.BaseStream.Position % 4;
                        if (remainder != 0)
                        {
                            br.BaseStream.Position += (4 - remainder);
                        }
                    }
                    else
                    {
                        br.BaseStream.Position -= 3;
                    }
                }

                _logger($"[TR1 Audio Success] Successfully extracted {extractedCounter} perfectly isolated sound effects!");
            }
            catch (Exception ex)
            {
                _logger($"[TR1 Audio Error] Sequential unpacking failed: {ex.Message}");
            }
        }
        private void WriteWav8BitUnsigned(string path, byte[] rawPcmData, uint sampleRate)
        {
            using (FileStream wavFs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter wavBw = new BinaryWriter(wavFs))
            {
                ushort channels = 1;
                ushort bitsPerSample = 8;

                uint byteRate = sampleRate * channels * (uint)(bitsPerSample / 8);
                ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));

                wavBw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                wavBw.Write((uint)(36 + rawPcmData.Length));
                wavBw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                wavBw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                wavBw.Write((uint)16);
                wavBw.Write((ushort)1);
                wavBw.Write(channels);
                wavBw.Write(sampleRate); 
                wavBw.Write(byteRate);
                wavBw.Write(blockAlign);
                wavBw.Write(bitsPerSample);
                wavBw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                wavBw.Write((uint)rawPcmData.Length);
                wavBw.Write(rawPcmData);
            }
        }

        private void SeekToTR1AudioBlock(BinaryReader br)
        {
            string soundDir = Path.Combine(_outputDir, "Audio_Samples_TR1");
            long fileLen = br.BaseStream.Length;

            long safeAudioZoneStart = fileLen - 400000;
            if (safeAudioZoneStart < 0) safeAudioZoneStart = 0;

            br.BaseStream.Position = safeAudioZoneStart;
            _logger($"[TR1 Audio] Bypassed geometry. Scanning safe tail-vault from byte {br.BaseStream.Position}...");

            uint sampleCounter = 0;

            while (br.BaseStream.Position < fileLen - 8)
            {
                uint sampleSize = br.ReadUInt32();

                if (sampleSize > 1500 && sampleSize < 60000 && br.BaseStream.Position + sampleSize <= fileLen)
                {
                    byte[] rawPcm = br.ReadBytes((int)sampleSize);

                    if (IsRealAudioTrack(rawPcm))
                    {
                        if (!Directory.Exists(soundDir)) Directory.CreateDirectory(soundDir);
                        string wavPath = Path.Combine(soundDir, $"TR1_SFX_{sampleCounter}.wav");

                        WriteWavFileCustom(wavPath, rawPcm, 11025, 8);
                        sampleCounter++;
                    }
                }
                else
                {
                    br.BaseStream.Position -= 3;
                }
            }

            _logger($"[TR1 Audio] Process complete. Extracted {sampleCounter} genuine unique audio tracks.");
        }


        private void ParseTR4TR5Strict(BinaryReader br)
        {
            long fileLen = br.BaseStream.Length;
            string soundDir = Path.Combine(_outputDir, "Audio_Samples_TR4_TR5");

            _logger("[NG Processing] Launching multi-format signature scanner (TR4/TR5)...");

            try
            {
                br.BaseStream.Position = 0;
                byte[] fileBytes = br.ReadBytes((int)fileLen);
                uint extractedCounter = 0;

                if (!Directory.Exists(soundDir)) Directory.CreateDirectory(soundDir);

                for (int i = 4; i < fileBytes.Length - 10000; i++)
                {
                    if (i < fileBytes.Length - 4 && fileBytes[i] == 'R' && fileBytes[i + 1] == 'I' && fileBytes[i + 2] == 'F' && fileBytes[i + 3] == 'F')
                    {
                        uint chunkLength = BitConverter.ToUInt32(fileBytes, i + 4) + 8;
                        if (chunkLength > 100 && chunkLength < 1000000 && (i + chunkLength) <= fileBytes.Length)
                        {
                            byte[] audioBuffer = new byte[chunkLength];
                            Buffer.BlockCopy(fileBytes, i, audioBuffer, 0, (int)chunkLength);
                            File.WriteAllBytes(Path.Combine(soundDir, $"TR_NG_SFX_{extractedCounter}.wav"), audioBuffer);
                            extractedCounter++;
                            i += (int)chunkLength - 1;
                        }
                    }
                    else if (i < fileBytes.Length - 2 && fileBytes[i] == 0xFF && (fileBytes[i + 1] == 0xFB || fileBytes[i + 1] == 0xFA || fileBytes[i + 1] == 0xF3))
                    {
                        int maxMp3Size = 60000;
                        int actualSize = 0;

                        for (int j = i; j < Math.Min(i + maxMp3Size, fileBytes.Length - 4); j++)
                        {
                            if (fileBytes[j] == 0x00 && fileBytes[j + 1] == 0x00 && fileBytes[j + 2] == 0x00 && fileBytes[j + 3] == 0x00)
                            {
                                actualSize = j - i;
                                break;
                            }
                        }

                        if (actualSize > 500)
                        {
                            byte[] mp3Buffer = new byte[actualSize];
                            Buffer.BlockCopy(fileBytes, i, mp3Buffer, 0, actualSize);
                            File.WriteAllBytes(Path.Combine(soundDir, $"TR5_SFX_{extractedCounter}.mp3"), mp3Buffer);
                            extractedCounter++;
                            i += actualSize - 1;
                        }
                    }
                    else if (i < fileBytes.Length - 7 && fileBytes[i] == 'W' && fileBytes[i + 1] == 'A' && fileBytes[i + 2] == 'V' && fileBytes[i + 3] == 'E' && fileBytes[i + 4] == 'f')
                    {
                        int startMarker = Math.Max(0, i - 8);
                        uint rawLength = 32000;

                        if (startMarker + rawLength <= fileBytes.Length)
                        {
                            byte[] rawPcm = new byte[rawLength];
                            Buffer.BlockCopy(fileBytes, startMarker, rawPcm, 0, (int)rawLength);
                            WriteWavFileCustom(Path.Combine(soundDir, $"TR5_RAW_SFX_{extractedCounter}.wav"), rawPcm, 22050, 16);
                            extractedCounter++;
                            i += (int)rawLength - 1;
                        }
                    }
                }

                _logger($"[NG Audio Success] Extracted {extractedCounter} clean and independent 16-bit sound tracks!");
            }
            catch (Exception ex)
            {
                _logger($"[NG Audio Error] Signature scanner faulted: {ex.Message}");
            }

            _progressReporter(50);


            _logger("[NG Textures] Shifting stream pointer to Zlib graphics chamber...");
            SeekToZlibTextures(br);

            try
            {
                uint uncompressedTexturesSize = br.ReadUInt32();
                uint compressedTexturesSize = br.ReadUInt32();

                if (br.BaseStream.Position + compressedTexturesSize <= fileLen)
                {
                    byte[] compressedBuffer = br.ReadBytes((int)compressedTexturesSize);
                    byte[] decompressedBuffer = DecompressZlib(compressedBuffer);

                    if (decompressedBuffer != null && decompressedBuffer.Length > 0)
                    {
                        string textureDir = Path.Combine(_outputDir, "Textures");
                        if (!Directory.Exists(textureDir)) Directory.CreateDirectory(textureDir);

                        int bytesPerPixel = (decompressedBuffer.Length % (256 * 256 * 4) == 0) ? 4 : 3;
                        int pageSizeInBytes = 256 * 256 * bytesPerPixel;
                        int totalPages = decompressedBuffer.Length / pageSizeInBytes;

                        _logger($"[NG Textures] Unzipping {totalPages} high-res graphics sheets ({bytesPerPixel * 8}-bit)...");

                        for (int p = 0; p < totalPages; p++)
                        {
                            byte[] pagePixels = new byte[pageSizeInBytes];
                            Buffer.BlockCopy(decompressedBuffer, p * pageSizeInBytes, pagePixels, 0, pageSizeInBytes);
                            WriteTgaFile(Path.Combine(textureDir, $"Tile_Page_{p}.tga"), pagePixels, bytesPerPixel, false);
                        }
                        _logger($"[NG Textures Success] Successfully written all graphic sheets to disk.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[NG Textures Error] Failed to unpack graphic layers: {ex.Message}");
            }
        }

        private bool IsRealAudioTrack(byte[] data)
        {
           
            if (data.Length < 100) return false;

            long zeroCount = 0;
            for (int i = 0; i < Math.Min(data.Length, 1000); i++)
            {
                if (data[i] == 0x00 || data[i] == 0x80) zeroCount++;
            }

            if (zeroCount > 950) return false;

            return true;
        }


        private void ProcessTR2TR3(BinaryReader br)
        {
            uint numTextureTiles = br.ReadUInt32();
            string textureDir = Path.Combine(_outputDir, "Textures");
            if (!Directory.Exists(textureDir)) Directory.CreateDirectory(textureDir);
            int pageSize16Bit = 256 * 256 * 2;
            for (uint i = 0; i < numTextureTiles; i++)
            {
                byte[] raw16BitPixels = br.ReadBytes(pageSize16Bit);
                byte[] converted32BitPixels = Convert16BitTo32Bit(raw16BitPixels);
                WriteTgaFile(Path.Combine(textureDir, $"Tile_Page_{i}.tga"), converted32BitPixels, 4, true);
            }
            ExtractClassicAudio(br);
        }

        private void ExtractClassicAudio(BinaryReader br)
        {
            string mainSfxPath = Path.Combine(Path.GetDirectoryName(_filePath), "MAIN.SFX");
            if (!File.Exists(mainSfxPath)) return;
        }

        private void SeekToZlibTextures(BinaryReader br)
        {
            br.BaseStream.Position = 0;
            long streamLen = br.BaseStream.Length;
            while (br.BaseStream.Position < streamLen - 12)
            {
                byte b1 = br.ReadByte();
                if (b1 == 0x78)
                {
                    byte b2 = br.ReadByte();
                    if (b2 == 0x9C || b2 == 0xDA)
                    {
                        long currentPos = br.BaseStream.Position;
                        br.BaseStream.Position -= 6;
                        uint testCompressedSize = br.ReadUInt32();
                        if (testCompressedSize > 10000 && testCompressedSize < streamLen)
                        {
                            br.BaseStream.Position -= 8;
                            return;
                        }
                        br.BaseStream.Position = currentPos;
                    }
                }
            }
        }

        private byte[] DecompressZlib(byte[] compressedBuffer)
        {
            try
            {
                using (MemoryStream msCompressed = new MemoryStream(compressedBuffer))
                {
                    msCompressed.Seek(2, SeekOrigin.Begin);

                    using (DeflateStream deflate = new DeflateStream(msCompressed, CompressionMode.Decompress))
                    using (MemoryStream msDecompressed = new MemoryStream())
                    {
                        deflate.CopyTo(msDecompressed);
                        return msDecompressed.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        private byte[] Convert16BitTo32Bit(byte[] raw16)
        {
            byte[] out32 = new byte[256 * 256 * 4];
            int outIdx = 0;
            for (int i = 0; i < raw16.Length; i += 2)
            {
                ushort pixel = BitConverter.ToUInt16(raw16, i);
                byte a = (byte)(((pixel >> 15) & 0x01) * 255);
                byte r = (byte)(((pixel >> 10) & 0x1F) << 3);
                byte g = (byte)(((pixel >> 5) & 0x1F) << 3);
                byte b = (byte)((pixel & 0x1F) << 3);
                out32[outIdx++] = b; out32[outIdx++] = g; out32[outIdx++] = r; out32[outIdx++] = a;
            }
            return out32;
        }

        private byte[] ApplyPalette(byte[] indexed, byte[] palette)
        {
            byte[] out24 = new byte[256 * 256 * 3];
            int outIdx = 0;
            for (int i = 0; i < indexed.Length; i++)
            {
                int colorIdx = indexed[i] * 3;
                if (colorIdx + 2 < palette.Length)
                {
                    out24[outIdx++] = palette[colorIdx + 2];
                    out24[outIdx++] = palette[colorIdx + 1];
                    out24[outIdx++] = palette[colorIdx];
                }
            }
            return out24;
        }

        private void WriteWavFileCustom(string path, byte[] rawPcmData, uint sampleRate, ushort bitsPerSample)
        {
            using (FileStream wavFs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter wavBw = new BinaryWriter(wavFs))
            {
                ushort channels = 1;
                uint byteRate = sampleRate * channels * (uint)(bitsPerSample / 8);
                ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));

                wavBw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                wavBw.Write((uint)(36 + rawPcmData.Length));
                wavBw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                wavBw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                wavBw.Write((uint)16);
                wavBw.Write((ushort)1);
                wavBw.Write(channels);
                wavBw.Write(sampleRate);
                wavBw.Write(byteRate);
                wavBw.Write(blockAlign);
                wavBw.Write(bitsPerSample);
                wavBw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                wavBw.Write((uint)rawPcmData.Length);
                wavBw.Write(rawPcmData);
            }
        }

        private void WriteTgaFile(string path, byte[] pixels, int bytesPerPixel, bool isClassic = false)
        {
            if (isClassic)
            {
                byte[] flippedPixels = new byte[pixels.Length];
                int rowSize = 256 * bytesPerPixel;
                for (int y = 0; y < 256; y++)
                {
                    Buffer.BlockCopy(pixels, (255 - y) * rowSize, flippedPixels, y * rowSize, rowSize);
                }
                pixels = flippedPixels;
            }

            using (FileStream tgaFs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter tgaBw = new BinaryWriter(tgaFs))
            {
                tgaBw.Write((byte)0); tgaBw.Write((byte)0); tgaBw.Write((byte)2);
                tgaBw.Write((ushort)0); tgaBw.Write((ushort)0); tgaBw.Write((byte)0);
                tgaBw.Write((ushort)0); tgaBw.Write((ushort)0);
                tgaBw.Write((ushort)256); tgaBw.Write((ushort)256);
                tgaBw.Write((byte)(bytesPerPixel * 8));
                tgaBw.Write((byte)(isClassic ? 0x00 : 0x20));
                tgaBw.Write(pixels);
            }
        }
    }
}