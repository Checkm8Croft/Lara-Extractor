using K4os.Compression.LZ4;
using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lara_Extractor
{
    public sealed class TENAssetProcessor
    {
        private readonly string _filePath;
        private readonly Action<string> _logger;
        private readonly Action<double> _progressReporter;
        private readonly string _outputDir;
        private readonly bool _extractTextures;
        private readonly bool _extractAudio;

        public TENAssetProcessor(string filePath, Action<string> logger, Action<double> progressReporter,
            string outputDir, bool extractTextures, bool extractAudio)
        {
            _filePath = filePath;
            _logger = logger;
            _progressReporter = progressReporter;
            _outputDir = outputDir;
            _extractTextures = extractTextures;
            _extractAudio = extractAudio;
        }

        public void UnpackAll()
        {
            using var file = File.OpenRead(_filePath);
            using var reader = new BinaryReader(file);
            ValidateHeader(reader);
            byte[] media = ReadCompressedBlock(reader, "media");
            _logger($"[TEN] Media block decompressed: {media.Length:N0} bytes.");

            using var mediaStream = new MemoryStream(media, writable: false);
            using var mediaReader = new BinaryReader(mediaStream);
            if (_extractTextures) ExtractTextures(mediaReader);
            else _logger("[TEN Tex] Texture extraction skipped.");
            _progressReporter(50);
            if (_extractAudio) ExtractAudio(mediaReader);
            else _logger("[TEN Audio] Audio extraction skipped.");
            _progressReporter(100);
        }

        private static void ValidateHeader(BinaryReader reader)
        {
            if (reader.ReadUInt32() != 0x004E4554)
                throw new InvalidDataException("Invalid TEN header.");
            reader.ReadUInt32();
            reader.ReadInt32();
            reader.ReadInt32();
        }

        internal static byte[] ReadCompressedBlock(BinaryReader reader, string name)
        {
            long uncompressedSize = reader.ReadInt64();
            long compressedSize = reader.ReadInt64();
            if (uncompressedSize <= 0 || uncompressedSize > int.MaxValue || compressedSize <= 0 || compressedSize > int.MaxValue)
                throw new InvalidDataException($"Invalid TEN {name} block sizes.");
            byte[] compressed = reader.ReadBytes((int)compressedSize);
            if (compressed.Length != compressedSize)
                throw new EndOfStreamException($"TEN {name} block is truncated.");
            byte[] result = new byte[(int)uncompressedSize];
            using var chunkStream = new MemoryStream(compressed, writable: false);
            using var chunkReader = new BinaryReader(chunkStream);
            uint chunkCount = chunkReader.ReadUInt32();
            int outputOffset = 0;
            for (uint i = 0; i < chunkCount; i++)
            {
                int chunkUncompressed = checked((int)chunkReader.ReadUInt32());
                int chunkCompressed = checked((int)chunkReader.ReadUInt32());
                if (chunkUncompressed < 0 || chunkCompressed < 0 ||
                    chunkUncompressed > result.Length - outputOffset ||
                    chunkCompressed > chunkStream.Length - chunkStream.Position)
                    throw new InvalidDataException($"Invalid TEN {name} LZ4 chunk sizes.");

                byte[] chunk = chunkReader.ReadBytes(chunkCompressed);
                int written = LZ4Codec.Decode(chunk, 0, chunk.Length, result, outputOffset, chunkUncompressed);
                if (written != chunkUncompressed)
                    throw new InvalidDataException($"TEN {name} LZ4 chunk decoded incorrectly.");
                outputOffset += written;
            }

            if (outputOffset != result.Length)
                throw new InvalidDataException($"TEN {name} block decoded to {outputOffset} bytes instead of {result.Length}.");
            return result;
        }

        private void ExtractTextures(BinaryReader reader)
        {
            string textureDir = Path.Combine(_outputDir, "Textures");
            Directory.CreateDirectory(textureDir);

            string[] groupPrefixes = { "Room_Tile", "Moveable_Tile", "Static_Tile", "Animated_Tile" };
            int exportedCount = 0;

            for (int atlasGroup = 0; atlasGroup < 4; atlasGroup++)
            {
                int count = ReadCount(reader, 4096);
                for (int i = 0; i < count; i++)
                {
                    int width = reader.ReadInt32();
                    int height = reader.ReadInt32();
                    byte[] colorMap = ReadBlob(reader);
                    string baseName = $"{groupPrefixes[atlasGroup]}_{i:D3}";

                    try
                    {
                        SaveTexture(Path.Combine(textureDir, baseName), colorMap, width, height);
                        exportedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger($"[TEN Tex Warning] {baseName} ({width}x{height}): {ex.Message}");
                    }

                    // Optional NormalMap
                    if (reader.ReadBoolean())
                    {
                        byte[] normalMap = ReadBlob(reader);
                        try
                        {
                            SaveTexture(Path.Combine(textureDir, $"{baseName}_Normal"), normalMap, width, height);
                            exportedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger($"[TEN Tex Warning] {baseName}_Normal ({width}x{height}): {ex.Message}");
                        }
                    }

                    // Optional ORSHMap
                    if (reader.ReadBoolean())
                    {
                        byte[] orshMap = ReadBlob(reader);
                        try
                        {
                            SaveTexture(Path.Combine(textureDir, $"{baseName}_ORSH"), orshMap, width, height);
                            exportedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger($"[TEN Tex Warning] {baseName}_ORSH ({width}x{height}): {ex.Message}");
                        }
                    }

                    // Optional EmissiveMap
                    if (reader.ReadBoolean())
                    {
                        byte[] emissiveMap = ReadBlob(reader);
                        try
                        {
                            SaveTexture(Path.Combine(textureDir, $"{baseName}_Emissive"), emissiveMap, width, height);
                            exportedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger($"[TEN Tex Warning] {baseName}_Emissive ({width}x{height}): {ex.Message}");
                        }
                    }
                }
            }

            int spriteCount = ReadCount(reader, 4096);
            for (int i = 0; i < spriteCount; i++)
            {
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                byte[] spriteBlob = ReadBlob(reader);
                try
                {
                    SaveTexture(Path.Combine(textureDir, $"Sprite_{i:D3}"), spriteBlob, width, height);
                    exportedCount++;
                }
                catch (Exception ex)
                {
                    _logger($"[TEN Tex Warning] Sprite {i} ({width}x{height}): {ex.Message}");
                }
            }

            int skyWidth = reader.ReadInt32();
            int skyHeight = reader.ReadInt32();
            byte[] skyBlob = ReadBlob(reader);
            try
            {
                SaveTexture(Path.Combine(textureDir, "Sky"), skyBlob, skyWidth, skyHeight);
                exportedCount++;
            }
            catch (Exception ex)
            {
                _logger($"[TEN Tex Warning] Sky ({skyWidth}x{skyHeight}): {ex.Message}");
            }

            _logger($"[TEN Tex] {exportedCount} texture(s) successfully extracted.");
        }

        private void ExtractAudio(BinaryReader reader)
        {
            string audioDir = Path.Combine(_outputDir, "Audio");
            Directory.CreateDirectory(audioDir);
            ushort soundMapSize = reader.ReadUInt16();
            reader.ReadBytes(soundMapSize * 2);
            uint soundDetails = reader.ReadUInt32();
            reader.ReadBytes(checked((int)soundDetails * 8));
            uint sampleCount = reader.ReadUInt32();
            int extracted = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                int uncompressedSize = checked((int)reader.ReadUInt32());
                int compressedSize = checked((int)reader.ReadUInt32());
                byte[] sample = reader.ReadBytes(compressedSize);
                if (sample.Length != compressedSize) throw new EndOfStreamException("TEN sample data is truncated.");
                string extension = sample.Length >= 4 && sample[0] == 'R' && sample[1] == 'I' && sample[2] == 'F' && sample[3] == 'F' ? ".wav" : ".bin";
                File.WriteAllBytes(Path.Combine(audioDir, $"SFX_{i:D3}{extension}"), sample);
                extracted++;
            }
            _logger($"[TEN Audio] Extracted {extracted} sample(s).");
        }

        private static int ReadCount(BinaryReader reader, int max)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max) throw new InvalidDataException($"Invalid TEN count {count}.");
            return count;
        }

        private static byte[] ReadBlob(BinaryReader reader)
        {
            int length = checked((int)reader.ReadInt32());
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid TEN texture blob length.");
            byte[] data = reader.ReadBytes(length);
            if (data.Length != length) throw new EndOfStreamException("TEN texture blob is truncated.");
            return data;
        }

        private static void SaveTexture(string basePathNoExt, byte[] data, int expectedWidth, int expectedHeight)
        {
            if (data == null || data.Length == 0) return;

            // 1. Raw PNG format (\x89PNG)
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                File.WriteAllBytes(basePathNoExt + ".png", data);
                return;
            }

            // 2. DDS format (DDS )
            if (data.Length >= 128 && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ')
            {
                int height = BitConverter.ToInt32(data, 12);
                int width = BitConverter.ToInt32(data, 16);
                int fourCc = BitConverter.ToInt32(data, 84);

                if (width <= 0) width = expectedWidth;
                if (height <= 0) height = expectedHeight;

                byte[]? bgraPixels = null;
                if (fourCc == 0x35545844) // DXT5 / BC3
                {
                    bgraPixels = DecodeDxt5(data, width, height);
                }
                else if (fourCc == 0x31545844) // DXT1 / BC1
                {
                    bgraPixels = DecodeDxt1(data, width, height);
                }
                else if (fourCc == 0x32495441) // ATI2 / BC5 (Normal Map)
                {
                    bgraPixels = DecodeBc5(data, width, height);
                }

                if (bgraPixels != null)
                {
                    SaveBgraAsPng(basePathNoExt + ".png", bgraPixels, width, height);
                }
                else
                {
                    // Fallback to saving raw .dds file
                    File.WriteAllBytes(basePathNoExt + ".dds", data);
                }
                return;
            }

            // Fallback: save raw binary
            File.WriteAllBytes(basePathNoExt + ".bin", data);
        }

        private static void SaveBgraAsPng(string path, byte[] pixels, int width, int height)
        {
            try
            {
                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                bitmap.Freeze();
                using var stream = File.Create(path);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }
            catch
            {
                // Fallback to uncompressed TGA if PNG encoding encounters an issue
                string tgaPath = Path.ChangeExtension(path, ".tga");
                WriteBgraAsTga(tgaPath, pixels, width, height);
            }
        }

        private static void WriteBgraAsTga(string path, byte[] pixels, int width, int height)
        {
            using var writer = new BinaryWriter(File.Open(path, FileMode.Create));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)2);
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((byte)0);
            writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((ushort)width); writer.Write((ushort)height);
            writer.Write((byte)32); writer.Write((byte)0x20);
            writer.Write(pixels);
        }

        internal static byte[] DecodeDxt5(byte[] dds, int width, int height)
        {
            int blocksWide = (width + 3) / 4;
            int blocksHigh = (height + 3) / 4;
            byte[] pixels = new byte[width * height * 4];
            int source = 128;

            for (int blockY = 0; blockY < blocksHigh; blockY++)
            for (int blockX = 0; blockX < blocksWide; blockX++)
            {
                if (source + 16 > dds.Length) break;

                byte alpha0 = dds[source];
                byte alpha1 = dds[source + 1];
                ulong alphaBits = 0;
                for (int i = 0; i < 6; i++)
                    alphaBits |= (ulong)dds[source + 2 + i] << (8 * i);

                ushort color0 = BitConverter.ToUInt16(dds, source + 8);
                ushort color1 = BitConverter.ToUInt16(dds, source + 10);
                uint colorBits = BitConverter.ToUInt32(dds, source + 12);
                var colors = BuildDxtColors(color0, color1);

                for (int localY = 0; localY < 4; localY++)
                for (int localX = 0; localX < 4; localX++)
                {
                    int x = blockX * 4 + localX;
                    int y = blockY * 4 + localY;
                    if (x >= width || y >= height) continue;

                    int pixel = localY * 4 + localX;
                    int alphaIndex = (int)((alphaBits >> (3 * pixel)) & 7);
                    byte alpha = GetDxtAlpha(alpha0, alpha1, alphaIndex);
                    int colorIndex = (int)((colorBits >> (2 * pixel)) & 3);
                    int destination = (y * width + x) * 4;
                    pixels[destination] = colors[colorIndex].b;
                    pixels[destination + 1] = colors[colorIndex].g;
                    pixels[destination + 2] = colors[colorIndex].r;
                    pixels[destination + 3] = alpha;
                }

                source += 16;
            }

            return pixels;
        }

        internal static byte[] DecodeDxt1(byte[] dds, int width, int height)
        {
            int blocksWide = (width + 3) / 4;
            int blocksHigh = (height + 3) / 4;
            byte[] pixels = new byte[width * height * 4];
            int source = 128;

            for (int blockY = 0; blockY < blocksHigh; blockY++)
            for (int blockX = 0; blockX < blocksWide; blockX++)
            {
                if (source + 8 > dds.Length) break;

                ushort color0 = BitConverter.ToUInt16(dds, source);
                ushort color1 = BitConverter.ToUInt16(dds, source + 2);
                uint colorBits = BitConverter.ToUInt32(dds, source + 4);
                var colors = BuildDxtColors(color0, color1);

                for (int localY = 0; localY < 4; localY++)
                for (int localX = 0; localX < 4; localX++)
                {
                    int x = blockX * 4 + localX;
                    int y = blockY * 4 + localY;
                    if (x >= width || y >= height) continue;

                    int pixel = localY * 4 + localX;
                    int colorIndex = (int)((colorBits >> (2 * pixel)) & 3);
                    byte alpha = (color0 <= color1 && colorIndex == 3) ? (byte)0 : (byte)255;
                    int destination = (y * width + x) * 4;
                    pixels[destination] = colors[colorIndex].b;
                    pixels[destination + 1] = colors[colorIndex].g;
                    pixels[destination + 2] = colors[colorIndex].r;
                    pixels[destination + 3] = alpha;
                }

                source += 8;
            }

            return pixels;
        }

        private static byte[] DecodeBc5(byte[] dds, int width, int height)
        {
            int blocksWide = (width + 3) / 4;
            int blocksHigh = (height + 3) / 4;
            byte[] pixels = new byte[width * height * 4];
            int source = 128;

            for (int blockY = 0; blockY < blocksHigh; blockY++)
            for (int blockX = 0; blockX < blocksWide; blockX++)
            {
                if (source + 16 > dds.Length) break;

                byte r0 = dds[source];
                byte r1 = dds[source + 1];
                ulong rBits = 0;
                for (int i = 0; i < 6; i++) rBits |= (ulong)dds[source + 2 + i] << (8 * i);

                byte g0 = dds[source + 8];
                byte g1 = dds[source + 9];
                ulong gBits = 0;
                for (int i = 0; i < 6; i++) gBits |= (ulong)dds[source + 10 + i] << (8 * i);

                for (int localY = 0; localY < 4; localY++)
                for (int localX = 0; localX < 4; localX++)
                {
                    int x = blockX * 4 + localX;
                    int y = blockY * 4 + localY;
                    if (x >= width || y >= height) continue;

                    int pixel = localY * 4 + localX;
                    byte r = GetDxtAlpha(r0, r1, (int)((rBits >> (3 * pixel)) & 7));
                    byte g = GetDxtAlpha(g0, g1, (int)((gBits >> (3 * pixel)) & 7));

                    float normX = (r / 255.0f) * 2.0f - 1.0f;
                    float normY = (g / 255.0f) * 2.0f - 1.0f;
                    float normZ = MathF.Sqrt(MathF.Max(0f, 1.0f - normX * normX - normY * normY));
                    byte b = (byte)Math.Clamp((int)((normZ * 0.5f + 0.5f) * 255.0f), 0, 255);

                    int destination = (y * width + x) * 4;
                    pixels[destination] = b;
                    pixels[destination + 1] = g;
                    pixels[destination + 2] = r;
                    pixels[destination + 3] = 255;
                }

                source += 16;
            }

            return pixels;
        }

        private static (byte r, byte g, byte b)[] BuildDxtColors(ushort color0, ushort color1)
        {
            byte r0 = (byte)(((color0 >> 11) & 31) * 255 / 31);
            byte g0 = (byte)(((color0 >> 5) & 63) * 255 / 63);
            byte b0 = (byte)((color0 & 31) * 255 / 31);
            byte r1 = (byte)(((color1 >> 11) & 31) * 255 / 31);
            byte g1 = (byte)(((color1 >> 5) & 63) * 255 / 63);
            byte b1 = (byte)((color1 & 31) * 255 / 31);

            return color0 > color1
                ? new[] { (r0, g0, b0), (r1, g1, b1), ((byte)((2 * r0 + r1) / 3), (byte)((2 * g0 + g1) / 3), (byte)((2 * b0 + b1) / 3)), ((byte)((r0 + 2 * r1) / 3), (byte)((g0 + 2 * g1) / 3), (byte)((b0 + 2 * b1) / 3)) }
                : new[] { (r0, g0, b0), (r1, g1, b1), ((byte)((r0 + r1) / 2), (byte)((g0 + g1) / 2), (byte)((b0 + b1) / 2)), ((byte)0, (byte)0, (byte)0) };
        }

        private static byte GetDxtAlpha(byte alpha0, byte alpha1, int index)
        {
            if (index == 0) return alpha0;
            if (index == 1) return alpha1;
            if (alpha0 > alpha1)
                return index switch { 2 => (byte)((6 * alpha0 + alpha1) / 7), 3 => (byte)((5 * alpha0 + 2 * alpha1) / 7), 4 => (byte)((4 * alpha0 + 3 * alpha1) / 7), 5 => (byte)((3 * alpha0 + 4 * alpha1) / 7), 6 => (byte)((2 * alpha0 + 5 * alpha1) / 7), _ => (byte)((alpha0 + 6 * alpha1) / 7) };
            return index switch { 2 => (byte)((4 * alpha0 + alpha1) / 5), 3 => (byte)((3 * alpha0 + 2 * alpha1) / 5), 4 => (byte)((2 * alpha0 + 3 * alpha1) / 5), 5 => (byte)((alpha0 + 4 * alpha1) / 5), 6 => (byte)0, _ => (byte)255 };
        }
    }
}
