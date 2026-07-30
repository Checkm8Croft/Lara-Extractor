using System;
using System.IO;
using System.IO.Compression;

namespace Lara_Extractor
{
    /// <summary>
    /// Estrae texture e audio raw dai livelli TR1-TR5.
    /// Strutture da TRosettaStone 3 (https://opentomb.github.io/TRosettaStone3/).
    /// </summary>
    public class AssetProcessor
    {
        private readonly string _filePath;
        private readonly Action<string> _logger;
        private readonly Action<double> _progressReporter;
        private string _outputDir = "";

        public AssetProcessor(string filePath, Action<string> logger, Action<double> progressReporter)
        {
            _filePath = filePath;
            _logger = logger;
            _progressReporter = progressReporter;
        }

        public void UnpackAll()
        {
            _outputDir = Path.Combine(
                Path.GetDirectoryName(_filePath) ?? "",
                "Extracted_Assets");
            Directory.CreateDirectory(_outputDir);

            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);

            uint magic = br.ReadUInt32();
            string ext = Path.GetExtension(_filePath).ToLowerInvariant();
            _logger($"[Engine] Magic: 0x{magic:X8}  Ext: {ext.ToUpperInvariant()}");

            if (ext == ".tr4" || ext == ".trc" || magic == 0x00345254)
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
                _logger($"[Engine] Formato non riconosciuto (magic=0x{magic:X8}).");
            }

            _progressReporter(100);
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // TR1 â€” .PHD
        // Layout (TRosettaStone sezione 10.1):
        //   version(4) | numTiles(4) | Textile8[N*65536]
        //   | Rooms | FloorData | Meshes | Animations | ... (tutto il livello)
        //   | LightMap[8192] | Palette[256*3]   <-- palette VERSO LA FINE
        //   | CinematicFrames | DemoData | SoundMap[256*2]
        //   | NumSoundDetails(4) | SoundDetails[N*8]
        //   | NumSamples(4) | Samples[]          <-- audio embedded
        //   | NumSampleIndices(4) | SampleIndices[]
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void ParseTR1(BinaryReader br)
        {
            // â”€â”€ Leggi l'intero file in memoria per navigazione efficiente â”€â”€
            long fileLen = br.BaseStream.Length;
            br.BaseStream.Position = 0;
            byte[] file = br.ReadBytes((int)fileLen);

            uint numTiles = BitConverter.ToUInt32(file, 4);
            _logger($"[TR1 Tex] {numTiles} texture tiles (8-bit indexed).");

            // â”€â”€ Trova la palette: Ã¨ prima di NumSampleIndices â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // La struttura a fine file Ã¨:
            //   NumSamples(4) + Samples[NumSamples]
            //   NumSampleIndices(4) + SampleIndices[NumSampleIndices*4]
            // Cerca la prima firma RIFF per localizzare NumSamples
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

            if (firstRiff >= 4)
            {
                // Il uint32 prima di firstRiff Ã¨ NumSamples (byte count)
                uint numSampleBytes = BitConverter.ToUInt32(file, firstRiff - 4);

                // Dopo i sample ci sono NumSampleIndices + SampleIndices
                // Ricalcola la fine dei sample
                int samplesEnd = firstRiff + (int)numSampleBytes;

                if (samplesEnd + 4 <= file.Length)
                {
                    uint numSampleIndices = BitConverter.ToUInt32(file, samplesEnd);
                    int afterSampleIndices = samplesEnd + 4 + (int)numSampleIndices * 4;

                    // Questo dovrebbe essere la fine del file (o pochi byte di padding)
                    // La palette Ã¨ a: afterSampleIndices - (ma aspetta, l'indice viene DOPO i sample)
                    // Layout corretto: ... LightMap[8192] | Palette[768] | CinematicFrames | DemoData | SoundMap | SoundDetails | NumSamples | Samples | NumSampleIndices | SampleIndices
                    // Quindi la palette Ã¨ PRIMA di NumSamples, cioÃ¨ prima di firstRiff-4
                    // Andiamo a ritroso: prima di NumSamples(4) c'Ã¨ SoundDetails, prima ancora SoundMap ecc.
                    // Percorso piÃ¹ semplice: la palette Ã¨ a (firstRiff - 4) - NumSoundDetails*8 - 4 - SoundMap*2 - DemoData - CinematicFrames - 768 - 8192
                    // Troppo complesso; usiamo invece la ricerca diretta

                    // La palette TR1 Ã¨ 256 colori * 3 bytes = 768 bytes, tutti nel range 0..63 (VGA 6-bit)
                    // Cerchiamo il blocco di 768 byte dove tutti i valori sono <= 63
                    // Si trova subito dopo il LightMap (8192 bytes), quindi cerca groupi di 768 bytes con max<=63
                    int palOffset = FindTR1Palette(file, firstRiff);
                    if (palOffset >= 0)
                    {
                        pal8 = new byte[768];
                        Buffer.BlockCopy(file, palOffset, pal8, 0, 768);
                        // Scala i valori da 0..63 a 0..252
                        for (int i = 0; i < 768; i++)
                            pal8[i] = (byte)Math.Min(255, pal8[i] * 4);
                        _logger($"[TR1 Tex] Palette trovata a offset {palOffset}.");
                    }
                }
            }

            if (pal8 == null)
            {
                // Fallback: costruisci palette di default (grayscale)
                _logger("[TR1 Tex] Attenzione: palette non trovata, uso grayscale.");
                pal8 = new byte[768];
                for (int i = 0; i < 256; i++)
                    pal8[i * 3] = pal8[i * 3 + 1] = pal8[i * 3 + 2] = (byte)i;
            }

            // â”€â”€ Converti i tile 8-bit indexed usando la palette â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            string texDir = Path.Combine(_outputDir, "Textures");
            Directory.CreateDirectory(texDir);

            int tilesStart = 8; // dopo version(4) + numTiles(4)
            for (uint i = 0; i < numTiles; i++)
            {
                int offset = tilesStart + (int)i * 65536;
                byte[] indexed = new byte[65536];
                Buffer.BlockCopy(file, offset, indexed, 0, 65536);
                byte[] rgb24 = ApplyPalette8(indexed, pal8);
                // TR1: dati tile sono top-down, TGA con flipVertical=false e descriptor 0x20 (top-left)
                WriteTga(Path.Combine(texDir, $"Tile_{i:D3}.tga"), rgb24, 3, topLeftOrigin: true);
            }
            _logger($"[TR1 Tex] {numTiles} tiles salvate.");
            _progressReporter(40);

            // â”€â”€ Audio embedded â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Layout fine file: NumSamples(4 = byte count) + RIFF-WAV concatenati + NumSampleIndices(4) + SampleIndices[]
            // I sample NON hanno allineamento â€” sono contigui
            if (firstRiff >= 4)
            {
                uint numSampleBytes = BitConverter.ToUInt32(file, firstRiff - 4);
                _logger($"[TR1 Audio] Blocco sample a offset {firstRiff}, {numSampleBytes:N0} bytes.");

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

                    pos += total; // Nessun allineamento in TR1
                }

                _logger($"[TR1 Audio] Estratti {count} sample WAV.");
            }
            else
            {
                _logger("[TR1 Audio] Nessun sample RIFF trovato nel file.");
            }

            _progressReporter(80);
        }

        /// <summary>
        /// Cerca la palette TR1 (768 bytes con tutti i valori nel range 0..63)
        /// nella zona precedente al blocco audio (firstRiff).
        /// La palette si trova dopo il LightMap[8192] e prima di CinematicFrames.
        /// </summary>
        private static int FindTR1Palette(byte[] file, int searchBefore)
        {
            // Scansiona a ritroso dalla posizione audio cercando un blocco di 768 bytes
            // dove TUTTI i valori sono <= 63 (formato VGA 6-bit)
            int limit = Math.Max(0, searchBefore - 768);
            for (int i = searchBefore - 768; i >= limit - 8192; i--)
            {
                if (i < 0) break;
                bool valid = true;
                for (int j = 0; j < 768; j++)
                {
                    if (file[i + j] > 63) { valid = false; break; }
                }
                if (valid) return i;
            }
            return -1;
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // TR2/TR3 â€” .TR2
        // Layout: version(4) | Palette[256*3] | Palette16[256*4]
        //         | NumTiles(4) | Textile8[N*65536] | Textile16[N*131072]
        //         | ... livello ...
        // Audio: in MAIN.SFX esterno (da implementare separatamente)
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void ParseTR2TR3(BinaryReader br, bool isTR3)
        {
            string label = isTR3 ? "TR3" : "TR2";

            // Palette8 (768 bytes) e Palette16 (1024 bytes) vengono PRIMA dei tile
            byte[] pal8raw = br.ReadBytes(768);
            byte[] pal8    = new byte[768];
            for (int i = 0; i < 768; i++)
                pal8[i] = (byte)Math.Min(255, pal8raw[i] * 4); // scala 0..63 -> 0..252
            br.ReadBytes(1024); // Palette16: non usata

            uint numTiles = br.ReadUInt32();
            _logger($"[{label} Tex] {numTiles} tiles: Textile8 + Textile16.");

            // Salta Textile8 (non lo usiamo â€” usiamo Textile16 che ha piÃ¹ fedeltÃ )
            br.ReadBytes((int)numTiles * 65536);

            // Textile16: ARGB1555
            string texDir = Path.Combine(_outputDir, "Textures");
            Directory.CreateDirectory(texDir);

            for (uint i = 0; i < numTiles; i++)
            {
                byte[] raw16  = br.ReadBytes(131072); // 256*256*2
                byte[] bgra32 = Convert16To32(raw16);
                WriteTga(Path.Combine(texDir, $"Tile_{i:D3}.tga"), bgra32, 4, topLeftOrigin: false);
            }
            _logger($"[{label} Tex] {numTiles} tiles (16-bit) salvate.");
            _progressReporter(40);

            // MAIN.SFX â€” da implementare separatamente
            _logger($"[{label} Audio] Supporto MAIN.SFX in arrivo. Collocalo nella stessa cartella del livello.");
            _progressReporter(70);
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // TR4/TR5 â€” .TR4 / .TRC
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void ParseTR4TR5(BinaryReader br)
        {
            string label = Path.GetExtension(_filePath).ToLowerInvariant() == ".trc" ? "TR5" : "TR4";

            ushort numRoomTiles = br.ReadUInt16();
            ushort numObjTiles  = br.ReadUInt16();
            ushort numBumpTiles = br.ReadUInt16();
            _logger($"[{label} Tex] {numRoomTiles} room + {numObjTiles} obj + {numBumpTiles} bump tiles.");

            // Blocco 1: texture 32-bit BGRA
            uint uncomp32 = br.ReadUInt32();
            uint comp32   = br.ReadUInt32();
            byte[] compTex32 = br.ReadBytes((int)comp32);

            // Blocco 2: texture 16-bit â€” skip
            br.ReadUInt32();
            br.ReadBytes((int)br.ReadUInt32());

            // Blocco 3: misc â€” skip
            br.ReadUInt32();
            br.ReadBytes((int)br.ReadUInt32());

            // Blocco 4: level data â€” skip (usato da Wad2Processor via TombLib)
            br.ReadUInt32();
            br.ReadBytes((int)br.ReadUInt32());

            // Decomprimi e salva texture 32-bit
            try
            {
                byte[] texData  = DecompressZlib(compTex32);
                int    pageSize = 256 * 256 * 4; // BGRA32
                int    numPages = texData.Length / pageSize;

                string texDir = Path.Combine(_outputDir, "Textures");
                Directory.CreateDirectory(texDir);

                for (int p = 0; p < numPages; p++)
                {
                    byte[] page = new byte[pageSize];
                    Buffer.BlockCopy(texData, p * pageSize, page, 0, pageSize);
                    // TR4/TR5: BGRA â€” TGA usa BGR nativamente, quindi Ã¨ compatibile
                    WriteTga(Path.Combine(texDir, $"Tile_{p:D3}.tga"), page, 4, topLeftOrigin: true);
                }
                _logger($"[{label} Tex] {numPages} tiles 32-bit salvate.");
            }
            catch (Exception ex)
            {
                _logger($"[{label} Tex Errore] {ex.Message}");
            }
            _progressReporter(50);

            // Audio embedded dopo i 4 blocchi zlib
            ExtractTR4TR5Audio(br, label);
        }

        private void ExtractTR4TR5Audio(BinaryReader br, string label)
        {
            long remaining = br.BaseStream.Length - br.BaseStream.Position;
            if (remaining < 8)
            {
                _logger($"[{label} Audio] Nessun dato audio dopo il level block.");
                return;
            }

            byte[] block = br.ReadBytes((int)remaining);
            string audioDir = Path.Combine(_outputDir, "Audio");
            Directory.CreateDirectory(audioDir);

            int pos   = 0;
            int count = 0;

            // TR4: uint32 numSamples + uint32 SampleIndices[numSamples] + tr4_sample[]
            // tr4_sample: uint32 UncompSize + uint32 CompSize + byte[] WaveData (MS-ADPCM WAV)
            uint possibleNum = BitConverter.ToUInt32(block, 0);
            if (possibleNum > 0 && possibleNum <= 2000)
            {
                pos = 4 + (int)possibleNum * 4;
                _logger($"[{label} Audio] {possibleNum} sample indicizzati, dati a offset {pos}.");
            }

            while (pos < block.Length - 8)
            {
                // TR4 sample: ogni sample Ã¨ preceduto da UncompSize e CompSize
                // poi il file WAV compresso (MS-ADPCM) â€” ha giÃ  l'header RIFF interno
                if (block[pos] == 'R' && block[pos+1] == 'I' &&
                    block[pos+2] == 'F' && block[pos+3] == 'F')
                {
                    // TR4 retail: WAV diretto senza preambolo UncompSize/CompSize
                    uint riffSize = BitConverter.ToUInt32(block, pos + 4);
                    int  total    = (int)riffSize + 8;
                    if (pos + total > block.Length) break;

                    byte[] wav = new byte[total];
                    Buffer.BlockCopy(block, pos, wav, 0, total);
                    File.WriteAllBytes(Path.Combine(audioDir, $"SFX_{count:D3}.wav"), wav);
                    count++;
                    pos += total;
                }
                else
                {
                    // TR4 con UncompSize/CompSize prima del WAV
                    uint uncompSize = BitConverter.ToUInt32(block, pos);
                    uint compSize   = BitConverter.ToUInt32(block, pos + 4);

                    if (compSize > 0 && compSize < 2 * 1024 * 1024 && pos + 8 + compSize <= block.Length)
                    {
                        byte[] wavData = new byte[compSize];
                        Buffer.BlockCopy(block, pos + 8, wavData, 0, (int)compSize);
                        File.WriteAllBytes(Path.Combine(audioDir, $"SFX_{count:D3}.wav"), wavData);
                        count++;
                        pos += 8 + (int)compSize;
                    }
                    else
                    {
                        pos++;
                    }
                }
            }

            _logger($"[{label} Audio] Estratti {count} sample WAV.");
            _progressReporter(80);
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Utilities
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static byte[] DecompressZlib(byte[] data)
        {
            using var ms      = new MemoryStream(data, 2, data.Length - 2);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var output  = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }

        /// <summary>Tile 8-bit indexed â†’ RGB24 con palette TR1.</summary>
        private static byte[] ApplyPalette8(byte[] indexed, byte[] pal8)
        {
            byte[] rgb = new byte[indexed.Length * 3];
            for (int i = 0; i < indexed.Length; i++)
            {
                int ci = indexed[i] * 3;
                rgb[i * 3]     = pal8[ci];
                rgb[i * 3 + 1] = pal8[ci + 1];
                rgb[i * 3 + 2] = pal8[ci + 2];
            }
            return rgb;
        }

        /// <summary>Pixel 16-bit ARGB1555 â†’ BGRA32.</summary>
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

        /// <summary>
        /// Scrive un TGA non compresso (tipo 2).
        /// topLeftOrigin=true: bit 5 del descriptor = 1 (origin in alto a sinistra, dati as-is).
        /// topLeftOrigin=false: bit 5 = 0 (origin in basso a sinistra, viewer riflette verticalmente).
        /// </summary>
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
            // Image descriptor byte:
            //   bits 0-3: attribute bits per pixel
            //   bit 4:    reserved
            //   bit 5:    1 = top-left origin, 0 = bottom-left origin
            bw.Write((byte)(topLeftOrigin ? 0x20 : 0x00));
            bw.Write(pixels);
        }
    }
}
