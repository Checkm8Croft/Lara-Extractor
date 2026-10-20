using System;
using System.IO;
using System.IO.Compression;

namespace Lara_Extractor
{
    /// <summary>
    /// TRosettaStone 3 (https://opentomb.github.io/TRosettaStone3/).
    /// </summary>
    public class AssetProcessor
    {
        private readonly string _filePath;
        private readonly Action<string> _logger;
        private readonly Action<double> _progressReporter;
        private readonly bool _extractTextures;
        private readonly bool _extractAudio;
        private string _outputDir = "";

        public AssetProcessor(string filePath, Action<string> logger, Action<double> progressReporter, string? outputDir = null, bool extractTextures = true, bool extractAudio = true)
        {
            _filePath = filePath;
            _logger = logger;
            _progressReporter = progressReporter;
            _outputDir = outputDir ?? "";
            _extractTextures = extractTextures;
            _extractAudio = extractAudio;
        }

        public void UnpackAll()
        {
            if (string.IsNullOrWhiteSpace(_outputDir))
            {
                _outputDir = Path.Combine(
                    Path.GetDirectoryName(_filePath) ?? "",
                    "Extracted_Assets");
            }
            Directory.CreateDirectory(_outputDir);

            if (Path.GetExtension(_filePath).Equals(".ten", StringComparison.OrdinalIgnoreCase))
            {
                new TENAssetProcessor(_filePath, _logger, _progressReporter, _outputDir,
                    _extractTextures, _extractAudio).UnpackAll();
                return;
            }

            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);

            uint magic = br.ReadUInt32();
            string ext = Path.GetExtension(_filePath).ToLowerInvariant();
            _logger($"[Engine] Magic: 0x{magic:X8}  Ext: {ext.ToUpperInvariant()}");

            if (magic == 0x63345254)
            {
                _logger("[Engine] TRNG detected. Decrypting level before parsing assets.");
                ParseTRNGDecrypted();
            }
            else if (ext == ".tr4" || ext == ".trc" || magic == 0x00345254 || magic == 0x00355254)
            {
                _logger("[Engine] TR4/TR5 detected.");
                ParseTR4TR5(br);
            }
            else if (magic == 0x00000020)
            {
                _logger("[Engine] TR1 detected.");
                ParseTR1(br);
            }
            else if (magic == 0x0000002D || magic == 0xFF180038 || magic == 0xFF080038 || magic == 0xFF180034)
            {
                bool isTR3 = magic != 0x0000002D;
                _logger($"[Engine] {(isTR3 ? "TR3" : "TR2")} detected.");
                ParseTR2TR3(br, isTR3);
            }
            else
            {
                _logger($"[Engine] Format not recognized (magic=0x{magic:X8}).");
            }

            _progressReporter(100);
        }

        private void ParseTRNGDecrypted()
        {
            string decryptedPath = Path.Combine(
                Path.GetTempPath(),
                $"LaraExtractor_TRNG_Assets_{Guid.NewGuid():N}.tr4");

            try
            {
                if (!TombLib.NG.NgEncryption.DecryptLevel(_filePath, decryptedPath))
                    throw new InvalidDataException("TRNG level decryption failed.");

                using var fs = new FileStream(decryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);
                if (br.ReadUInt32() != 0x00345254)
                    throw new InvalidDataException("Decrypted TRNG level has an invalid TR4 magic.");

                ParseTRNG(br);
            }
            catch (Exception ex)
            {
                _logger($"[TRNG Error] Decrypted asset parsing failed: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(decryptedPath)) File.Delete(decryptedPath); }
                catch { }
            }
        }

        private void ParseTR1(BinaryReader br)
        {
            long fileLen = br.BaseStream.Length;
            br.BaseStream.Position = 0;
            byte[] file = br.ReadBytes((int)fileLen);

            uint numTiles = BitConverter.ToUInt32(file, 4);
            _logger($"[TR1 Tex] {numTiles} texture tiles (8-bit indexed).");

            int firstRiff = -1;
            for (int i = 8; i < file.Length - 4; i++)
            {
                if (file[i] == 'R' && file[i+1] == 'I' && file[i+2] == 'F' && file[i+3] == 'F')
                {
                    firstRiff = i;
                    break;
                }
            }

            byte[]? pal8 = null;

            byte[]? tombLibPalette = null;
            try
            {
                tombLibPalette = TR1Wad2Builder.ReadPalette8FromFile(_filePath);
                pal8 = tombLibPalette;
                _logger("[TR1 Tex] Palette loaded from the structural TR1 parser.");
            }
            catch (Exception ex)
            {
                _logger($"[TR1 Tex] Warning: TombLib palette read failed ({ex.Message}), using grayscale.");
                pal8 = new byte[768];
                for (int i = 0; i < 256; i++)
                    pal8[i * 3] = pal8[i * 3 + 1] = pal8[i * 3 + 2] = (byte)i;
            }

            if (_extractTextures)
            {
                string texDir = Path.Combine(_outputDir, "Textures");
                Directory.CreateDirectory(texDir);

                int tilesStart = 8; 
                for (uint i = 0; i < numTiles; i++)
                {
                    int offset = tilesStart + (int)i * 65536;
                    byte[] indexed = new byte[65536];
                    Buffer.BlockCopy(file, offset, indexed, 0, 65536);
                    byte[] rgb24 = ApplyPalette8(indexed, pal8);
                    WriteTga(Path.Combine(texDir, $"Tile_{i:D3}.tga"), rgb24, 3, topLeftOrigin: true);
                }
                _logger($"[TR1 Tex] {numTiles} tiles saved.");
            }
            else
            {
                _logger("[TR1 Tex] Texture extraction skipped.");
            }
            _progressReporter(40);

            if (_extractAudio)
            {
                if (firstRiff >= 4)
                {
                    uint numSampleBytes = BitConverter.ToUInt32(file, firstRiff - 4);
                    _logger($"[TR1 Audio] Block sample at offset {firstRiff}, {numSampleBytes:N0} bytes.");

                    string audioDir = Path.Combine(_outputDir, "Audio");
                    Directory.CreateDirectory(audioDir);

                    int pos   = firstRiff;
                    int end   = firstRiff + (int)numSampleBytes;
                    int count = 0;

                    while (pos < end - 8 && pos < file.Length - 8)
                    {
                        if (file[pos] != 'R' || file[pos+1] != 'I' ||
                            file[pos+2] != 'F' || file[pos+3] != 'F')
                            break;

                        uint riffBodySize = BitConverter.ToUInt32(file, pos + 4);
                        int  total        = (int)riffBodySize + 8;
                        if (pos + total > file.Length) break;

                        byte[] wav = new byte[total];
                        Buffer.BlockCopy(file, pos, wav, 0, total);
                        File.WriteAllBytes(Path.Combine(audioDir, $"SFX_{count:D3}.wav"), wav);
                        count++;

                        pos += total;
                    }

                    _logger($"[TR1 Audio] Extracted {count} WAV samples.");
                }
                else
                {
                    _logger("[TR1 Audio] No RIFF samples found in the file.");
                }
            }
            else
            {
                _logger("[TR1 Audio] Audio extraction skipped.");
            }

            _progressReporter(80);
        }

        private static int FindTR1Palette(byte[] file)
        {
            int bestOffset = -1;
            int bestScore = -1; // Initialize bestScore to -1

            for (int i = file.Length - 768; i >= 0; i--)
            {
                bool valid = true;
                for (int j = 0; j < 768; j++)
                {
                    if (file[i + j] > 63) { valid = false; break; }
                }
                if (!valid) continue; // Skip if not valid

                var colors = new HashSet<int>();
                for (int j = 0; j < 768; j += 3)
                {
                    colors.Add(file[i + j] | (file[i + j + 1] << 8) | (file[i + j + 2] << 16));
                }

                int score = colors.Count;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = i;
                }
            }
            return bestOffset; // Return the best offset found
        }
        private void ParseTR2TR3(BinaryReader br, bool isTR3)
        {
            string label = isTR3 ? "TR3" : "TR2";

            byte[] pal8raw = br.ReadBytes(768);
            byte[] pal8    = new byte[768];
            for (int i = 0; i < 768; i++)
                pal8[i] = (byte)Math.Min(255, pal8raw[i] * 4);
            br.ReadBytes(1024); 

            uint numTiles = br.ReadUInt32();
            _logger($"[{label} Tex] {numTiles} tiles: Textile8 + Textile16.");

            br.ReadBytes((int)numTiles * 65536);

            if (_extractTextures)
            {
                string texDir = Path.Combine(_outputDir, "Textures");
                Directory.CreateDirectory(texDir);

                for (uint i = 0; i < numTiles; i++)
                {
                    byte[] raw16  = br.ReadBytes(131072); 
                    byte[] bgra32 = Convert16To32(raw16);
                    WriteTga(Path.Combine(texDir, $"Tile_{i:D3}.tga"), bgra32, 4, topLeftOrigin: false);
                }
                _logger($"[{label} Tex] {numTiles} tiles (16-bit) saved.");
            }
            else
            {
                _logger($"[{label} Tex] Texture extraction skipped.");
            }

            if (_extractAudio)
                ExtractTR4TR5Audio(br, label);
            else
                _logger($"[{label} Audio] Audio extraction skipped.");

            _progressReporter(70);
        }

        
        private static byte[] ReadChunk(BinaryReader br)
        {
            uint uncompSize = br.ReadUInt32();
            uint compSize   = br.ReadUInt32();
            uint sizeToRead = compSize != 0 ? compSize : uncompSize;
            if (sizeToRead == 0) return Array.Empty<byte>();
            return br.ReadBytes((int)sizeToRead);
        }

        private static void SkipChunk(BinaryReader br)
        {
            uint uncompSize = br.ReadUInt32();
            uint compSize   = br.ReadUInt32();
            uint sizeToRead = compSize != 0 ? compSize : uncompSize;
            if (sizeToRead > 0)
            {
                br.BaseStream.Seek(sizeToRead, SeekOrigin.Current);
            }
        }

        private void ParseTR4TR5(BinaryReader br)
        {
            string ext = Path.GetExtension(_filePath).ToLowerInvariant();
            string label = ext == ".trc" ? "TR5" : "TR4";

            ushort numRoomTiles = br.ReadUInt16();
            ushort numObjTiles  = br.ReadUInt16();
            ushort numBumpTiles = br.ReadUInt16();
            _logger($"[{label} Tex] {numRoomTiles} room + {numObjTiles} obj + {numBumpTiles} bump tiles.");

            // 1. 32-bit Textiles (room/obj/bump)
            byte[] compTex32 = ReadChunk(br);

            // 2. 16-bit Textiles (room/obj/bump)
            SkipChunk(br);

            // 3. 16-bit Misc Textiles (Sky / Font)
            SkipChunk(br);

            // 4. Level Data
            SkipChunk(br);

            if (_extractTextures && compTex32.Length > 0)
            {
                try
                {
                    byte[] texData = (compTex32.Length >= 2 && compTex32[0] == 0x78)
                        ? DecompressZlib(compTex32)
                        : compTex32;

                    int pageSize = 256 * 256 * 4; 
                    int numPages = texData.Length / pageSize;

                    string texDir = Path.Combine(_outputDir, "Textures");
                    Directory.CreateDirectory(texDir);

                    for (int p = 0; p < numPages; p++)
                    {
                        byte[] page = new byte[pageSize];
                        Buffer.BlockCopy(texData, p * pageSize, page, 0, pageSize);

                        WriteTga(Path.Combine(texDir, $"Tile_{p:D3}.tga"), page, 4, topLeftOrigin: true);
                    }
                    _logger($"[{label} Tex] {numPages} tiles 32-bit saved.");
                }
                catch (Exception ex)
                {
                    _logger($"[{label} Tex Error] {ex.Message}");
                }
            }
            else if (!_extractTextures)
            {
                _logger($"[{label} Tex] Texture extraction skipped.");
            }
            _progressReporter(50);

            if (_extractAudio)
            {
                ExtractTR4TR5Audio(br, label);
            }
            else
            {
                _logger($"[{label} Audio] Audio extraction skipped.");
            }
        }

        private void ParseTRNG(BinaryReader br)
        {
            _logger("[Engine] TRNG level parsing started...");

            ushort numRoomTiles = br.ReadUInt16();
            ushort numObjTiles  = br.ReadUInt16();
            ushort numBumpTiles = br.ReadUInt16();
            _logger($"[TRNG Tex] Header reports {numRoomTiles} room + {numObjTiles} obj + {numBumpTiles} bump tiles.");

            // 1. 32-bit Textiles (room/obj/bump)
            byte[] compTex32 = ReadChunk(br);

            // 2. 16-bit Textiles (room/obj/bump)
            byte[] compTex16 = ReadChunk(br);

            // 3. 16-bit Misc Textiles (Sky / Font)
            byte[] compMisc16 = ReadChunk(br);

            // 4. Level Data
            SkipChunk(br);

            if (_extractTextures)
            {
                string texDir = Path.Combine(_outputDir, "Textures");
                Directory.CreateDirectory(texDir);

                // 32-bit tiles
                if (compTex32.Length > 0)
                {
                    try
                    {
                        byte[] texData = (compTex32.Length >= 2 && compTex32[0] == 0x78)
                            ? DecompressZlib(compTex32)
                            : compTex32;

                        int pageSize = 256 * 256 * 4;
                        int numPages = texData.Length / pageSize;

                        for (int p = 0; p < numPages; p++)
                        {
                            byte[] page = new byte[pageSize];
                            Buffer.BlockCopy(texData, p * pageSize, page, 0, pageSize);
                            WriteTga(Path.Combine(texDir, $"Tile_{p:D3}.tga"), page, 4, topLeftOrigin: true);
                        }
                        _logger($"[TRNG Tex] {numPages} 32-bit texture tiles saved.");
                    }
                    catch (Exception ex)
                    {
                        _logger($"[TRNG Tex Error] 32-bit tiles: {ex.Message}");
                    }
                }

                // 16-bit misc tiles (Sky / Font)
                if (compMisc16.Length > 0)
                {
                    try
                    {
                        byte[] miscData = (compMisc16.Length >= 2 && compMisc16[0] == 0x78)
                            ? DecompressZlib(compMisc16)
                            : compMisc16;

                        int pageSize16 = 256 * 256 * 2;
                        int numMiscPages = miscData.Length / pageSize16;

                        for (int m = 0; m < numMiscPages; m++)
                        {
                            byte[] raw16 = new byte[pageSize16];
                            Buffer.BlockCopy(miscData, m * pageSize16, raw16, 0, pageSize16);
                            byte[] bgra32 = Convert16To32(raw16);
                            WriteTga(Path.Combine(texDir, $"Misc_Tile_{m:D3}.tga"), bgra32, 4, topLeftOrigin: true);
                        }
                        if (numMiscPages > 0)
                            _logger($"[TRNG Tex] {numMiscPages} 16-bit misc tiles (Sky/Font) converted and saved.");
                    }
                    catch (Exception ex)
                    {
                        _logger($"[TRNG Tex Error] Misc tiles: {ex.Message}");
                    }
                }
            }
            else
            {
                _logger("[TRNG Tex] Texture extraction skipped.");
            }
            _progressReporter(50);

            if (_extractAudio)
            {
                ExtractTR4TR5Audio(br, "TRNG");
            }
            else
            {
                _logger("[TRNG Audio] Audio extraction skipped.");
            }
            _progressReporter(75);

            // Phase 3: Extract any embedded files from TRNG Extra NG Header
            ExtractTRNGExtraNGData();
            _progressReporter(90);
        }

        private void ExtractTRNGExtraNGData()
        {
            try
            {
                var summary = TRNGWad2Processor.ParseNGHeaderSummary(_filePath);
                if (!summary.HasNGHeader)
                    return;

                _logger($"[TRNG NG-Data] Processing extra NG Header ({summary.HeaderSize} bytes, {summary.FieldCount} chunks)...");

                using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);

                fs.Seek(summary.HeaderOffset + 2, SeekOrigin.Begin); // skip initial check word (2 bytes)
                int dataWordsCount = (int)(summary.HeaderSize - 2 - 8) / 2;
                if (dataWordsCount <= 0) return;

                ushort[] words = new ushort[dataWordsCount];
                for (int w = 0; w < dataWordsCount; w++)
                    words[w] = br.ReadUInt16();

                int idx = 0;
                int importedAudioCount = 0;
                int importedTexCount = 0;
                int importedOtherCount = 0;

                while (idx < words.Length)
                {
                    ushort numWordsRaw = words[idx];
                    int numberOfWords;
                    int extraWords;

                    if ((numWordsRaw & 0x8000) != 0)
                    {
                        if (idx + 1 >= words.Length) break;
                        uint w1 = (uint)(words[idx++] & 0x7FFF);
                        uint w2 = words[idx++];
                        numberOfWords = (int)((w1 << 16) | w2);
                        extraWords = 3;
                    }
                    else
                    {
                        numberOfWords = words[idx++];
                        extraWords = 2;
                    }

                    if (numberOfWords == 0 || (ushort)numberOfWords == TRNGWad2Processor.NGTAG_END_SEQUENCE)
                        break;

                    if (idx >= words.Length) break;
                    ushort tagType = words[idx++];

                    int dataWords = numberOfWords - extraWords;
                    if (dataWords > 0 && idx + dataWords <= words.Length)
                    {
                        if (tagType == TRNGWad2Processor.NGTAG_IMPORT_FILE && dataWords >= 46)
                        {
                            ushort tipoFile = words[idx + 2];
                            byte[] nameBytes = new byte[80];
                            Buffer.BlockCopy(words, (idx + 4) * 2, nameBytes, 0, 80);
                            string rawName = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' ');

                            uint sizeFile = (uint)(words[idx + 44] | (words[idx + 45] << 16));
                            int contentWordStart = idx + 46;
                            int availableBytes = (dataWords - 46) * 2;

                            if (sizeFile > 0 && sizeFile <= availableBytes)
                            {
                                byte[] fileBytes = new byte[sizeFile];
                                Buffer.BlockCopy(words, contentWordStart * 2, fileBytes, 0, (int)sizeFile);

                                string safeFileName = string.IsNullOrWhiteSpace(rawName) ? $"Imported_{idx}.bin" : Path.GetFileName(rawName);
                                string fileExt = Path.GetExtension(safeFileName).ToLowerInvariant();

                                if (_extractAudio && (tipoFile == 2 || fileExt == ".wav" || fileExt == ".ogg" || fileExt == ".mp3"))
                                {
                                    string audioDir = Path.Combine(_outputDir, "Audio", "Imported");
                                    Directory.CreateDirectory(audioDir);
                                    File.WriteAllBytes(Path.Combine(audioDir, safeFileName), fileBytes);
                                    _logger($"[TRNG NG-Audio] Extracted imported sound: {safeFileName} ({sizeFile:N0} bytes)");
                                    importedAudioCount++;
                                }
                                else if (_extractTextures && (fileExt == ".tga" || fileExt == ".bmp" || fileExt == ".png" || fileExt == ".jpg"))
                                {
                                    string texDir = Path.Combine(_outputDir, "Textures", "Imported");
                                    Directory.CreateDirectory(texDir);
                                    File.WriteAllBytes(Path.Combine(texDir, safeFileName), fileBytes);
                                    _logger($"[TRNG NG-Tex] Extracted imported image: {safeFileName} ({sizeFile:N0} bytes)");
                                    importedTexCount++;
                                }
                                else
                                {
                                    string miscDir = Path.Combine(_outputDir, "Imported_Files");
                                    Directory.CreateDirectory(miscDir);
                                    File.WriteAllBytes(Path.Combine(miscDir, safeFileName), fileBytes);
                                    _logger($"[TRNG NG-Data] Extracted embedded file: {safeFileName} ({sizeFile:N0} bytes)");
                                    importedOtherCount++;
                                }
                            }
                        }
                    }

                    idx += Math.Max(0, dataWords);
                }

                if (importedAudioCount > 0 || importedTexCount > 0 || importedOtherCount > 0)
                {
                    _logger($"[TRNG NG-Data] Finished extracting NG files: Audio={importedAudioCount}, Textures={importedTexCount}, Other={importedOtherCount}.");
                }
            }
            catch (Exception ex)
            {
                _logger($"[TRNG NG-Data Error] {ex.Message}");
            }
        }

        private void ExtractTR4TR5Audio(BinaryReader br, string label)
        {
            long remaining = br.BaseStream.Length - br.BaseStream.Position;
            if (remaining < 4)
            {
                _logger($"[{label} Audio] No audio data found at end of level.");
                return;
            }

            string audioDir = Path.Combine(_outputDir, "Audio");
            Directory.CreateDirectory(audioDir);

            int count = 0;
            long startPos = br.BaseStream.Position;

            try
            {
                // According to TRosettaStone:
                // struct tr4_sample { uint32_t UncompressedSize; uint32_t CompressedSize; char WaveFile[]; };
                // Directly after LevelData chunk comes uint32_t NumSamples, followed by NumSamples tr4_sample structures.
                uint numSamples = br.ReadUInt32();
                _logger($"[{label} Audio] Sound header reports {numSamples} sample(s).");

                if (numSamples > 0 && numSamples <= 5000)
                {
                    bool parseOk = true;
                    for (uint i = 0; i < numSamples; i++)
                    {
                        if (br.BaseStream.Position + 8 > br.BaseStream.Length)
                        {
                            _logger($"[{label} Audio Warning] Stream ended unexpectedly before sample {i}.");
                            parseOk = false;
                            break;
                        }

                        uint uncompSize = br.ReadUInt32();
                        uint compSize   = br.ReadUInt32();

                        if (compSize == 0 || compSize > br.BaseStream.Length - br.BaseStream.Position)
                        {
                            _logger($"[{label} Audio Warning] Sample {i} has invalid size ({compSize} bytes).");
                            parseOk = false;
                            break;
                        }

                        byte[] sampleData = br.ReadBytes((int)compSize);

                        // If sampleData is zlib compressed (custom level variants), decompress to get the WAV
                        byte[] wavBytes = sampleData;
                        if (sampleData.Length >= 2 && sampleData[0] == 0x78 && (sampleData[1] == 0x9C || sampleData[1] == 0x01 || sampleData[1] == 0xDA))
                        {
                            try
                            {
                                byte[] decomp = DecompressZlib(sampleData);
                                if (decomp.Length >= 4 && decomp[0] == 'R' && decomp[1] == 'I' && decomp[2] == 'F' && decomp[3] == 'F')
                                {
                                    wavBytes = decomp;
                                }
                            }
                            catch { }
                        }

                        string wavPath = Path.Combine(audioDir, $"SFX_{i:D3}.wav");
                        File.WriteAllBytes(wavPath, wavBytes);
                        count++;
                    }

                    if (parseOk && count > 0)
                    {
                        // Read NumSampleIndices if present
                        if (br.BaseStream.Position + 4 <= br.BaseStream.Length)
                        {
                            uint numSampleIndices = br.ReadUInt32();
                            _logger($"[{label} Audio] {numSampleIndices} sample indices referenced in level.");
                        }

                        _logger($"[{label} Audio] Successfully extracted {count} WAV sound sample(s) according to TRosettaStone.");
                        _progressReporter(80);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[{label} Audio Warning] TRosettaStone direct audio parsing error: {ex.Message}. Falling back to RIFF scanner...");
            }

            // Fallback: If structured parsing did not succeed, scan for embedded RIFF/WAVE chunks
            if (count == 0)
            {
                _logger($"[{label} Audio] Scanning stream for embedded RIFF WAVE signatures...");
                br.BaseStream.Position = startPos;
                byte[] remainingBytes = br.ReadBytes((int)(br.BaseStream.Length - startPos));

                int pos = 0;
                while (pos <= remainingBytes.Length - 8)
                {
                    if (remainingBytes[pos] == 'R' && remainingBytes[pos + 1] == 'I' &&
                        remainingBytes[pos + 2] == 'F' && remainingBytes[pos + 3] == 'F')
                    {
                        uint riffBodySize = BitConverter.ToUInt32(remainingBytes, pos + 4);
                        int total = (int)riffBodySize + 8;
                        if (total > 8 && pos + total <= remainingBytes.Length)
                        {
                            byte[] wav = new byte[total];
                            Buffer.BlockCopy(remainingBytes, pos, wav, 0, total);
                            File.WriteAllBytes(Path.Combine(audioDir, $"SFX_{count:D3}.wav"), wav);
                            count++;
                            pos += total;
                            continue;
                        }
                    }
                    pos++;
                }

                _logger($"[{label} Audio] Fallback scan extracted {count} WAV sample(s).");
            }

            _progressReporter(80);
        }

       
        private static byte[] DecompressZlib(byte[] data)
        {
            using var ms      = new MemoryStream(data, 2, data.Length - 2);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var output  = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] ApplyPalette8(byte[] indexed, byte[] pal8)
        {
            byte[] rgb = new byte[indexed.Length * 3];
            for (int i = 0; i < indexed.Length; i++)
            {
                int ci = indexed[i] * 3;
                rgb[i * 3]     = pal8[ci + 2]; // B
                rgb[i * 3 + 1] = pal8[ci + 1]; // G
                rgb[i * 3 + 2] = pal8[ci];     // R
            }
            return rgb;
        }

        private static byte[] Convert16To32(byte[] raw16)
        {
            byte[] out32 = new byte[raw16.Length / 2 * 4];
            for (int i = 0, o = 0; i < raw16.Length; i += 2, o += 4)
            {
                ushort px    = BitConverter.ToUInt16(raw16, i);
                out32[o]     = (byte)((px        & 0x001F) << 3); // B
                out32[o + 1] = (byte)(((px >> 5)  & 0x1F) << 3); // G
                out32[o + 2] = (byte)(((px >> 10) & 0x1F) << 3); // R
                out32[o + 3] = (byte)(((px >> 15) & 0x01) * 255); // A
            }
            return out32;
        }

        private static void WriteTga(string path, byte[] pixels, int bpp, bool topLeftOrigin)
        {
            using var bw = new BinaryWriter(File.Open(path, FileMode.Create));
            bw.Write((byte)0);             // ID length
            bw.Write((byte)0);             // no colormap
            bw.Write((byte)2);             // uncompressed true-color
            bw.Write((ushort)0); bw.Write((ushort)0); bw.Write((byte)0); // colormap spec
            bw.Write((ushort)0); bw.Write((ushort)0); // X/Y origin
            bw.Write((ushort)256);         // width
            bw.Write((ushort)256);         // height
            bw.Write((byte)(bpp * 8));     // bits per pixel
            bw.Write((byte)(topLeftOrigin ? 0x20 : 0x00));
            bw.Write(pixels);
        }
    }
}
