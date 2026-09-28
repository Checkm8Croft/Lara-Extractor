using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using TombLib.Wad;
using TombLib.Wad.TrLevels;

namespace Lara_Extractor
{
    /// <summary>
    /// Processor for Tomb Raider Next Generation (TRNG) level files (.tr4 with NGLE/TRNG extensions).
    /// Extracts 3D models, statics, animations, textures, and sounds into a TombLib Wad2 file.
    /// Based on TRNG Community Edition specifications (https://github.com/TombNextGeneration/TRNGCommunityEdition).
    /// </summary>
    public class TRNGWad2Processor
    {
        private readonly string _inputLevelPath;
        private readonly string _outputWad2Path;
        private readonly Action<string> _logger;

        private const int TimeoutSeconds = 120;
        private const int ProgressLogIntervalSeconds = 5;

        // Constants from TRNG Community Edition
        public const uint NG_LONG_CHECK  = 0x454C474E; // "NGLE" in little-endian (ASCII: 'N','G','L','E')
        public const ushort NG_SHORT_CHECK = 0x474E;     // "NG" in little-endian (ASCII: 'N','G')
        public const ushort NGTAG_END_SEQUENCE = 0xFFFF;

        // Common NG Tag identifiers
        public const ushort NGTAG_CONTROLLO_OPTIONS   = 0x01;
        public const ushort NGTAG_IMPORT_FILE         = 0x02;
        public const ushort NGTAG_SCRIPT_LEVEL        = 0x03;
        public const ushort NGTAG_SCRIPT_OPTIONS      = 0x04;
        public const ushort NGTAG_OLD_FMV             = 0x05;
        public const ushort NGTAG_OLD_EFFECTS         = 0x06;
        public const ushort NGTAG_PLUGIN_DATA         = 0x07;
        public const ushort NGTAG_OLD_ACTIONS         = 0x08;
        public const ushort NGTAG_SALVA_TIMER_OGGETTI = 0x09;
        public const ushort NGTAG_SAVEGAME_INFOS      = 0x0A;
        public const ushort NGTAG_SALVA_STATICS       = 0x0B;
        public const ushort NGTAG_PUSH_CLIMB          = 0x0C;
        public const ushort NGTAG_OLD_CONDITION       = 0x0D;
        public const ushort NGTAG_MIRRORS             = 0x0E;
        public const ushort NGTAG_ROOM_FLAGS          = 0x0F;
        public const ushort NGTAG_WEATHER_INTENSITY   = 0x10;
        public const ushort NGTAG_ANIM_SWAPPING       = 0x11;
        public const ushort NGTAG_EXTRA_AI_RECORDS    = 0x12;
        public const ushort NGTAG_ACTORS_INDICES      = 0x13;
        public const ushort NGTAG_CUTSCENE_CAMERA     = 0x14;
        public const ushort NGTAG_OCB_ITEMS           = 0x15;
        public const ushort NGTAG_DIARY_DATA          = 0x16;
        public const ushort NGTAG_STATUS_ORGANIZER    = 0x17;
        public const ushort NGTAG_INDICI_PFRAME       = 0x18;
        public const ushort NGTAG_STATUS_ANIM_RANGES  = 0x19;
        public const ushort NGTAG_ADAPTIVE_FARVIEW    = 0x1A;
        public const ushort NGTAG_SALVA_COORDINATE    = 0x1B;
        public const ushort NGTAG_PROGR_ACTIONS       = 0x1C;
        public const ushort NGTAG_STATUS_GTRIGGERS    = 0x1D;
        public const ushort NGTAG_ELEVATORS           = 0x1E;
        public const ushort NGTAG_PRINT_STRING        = 0x1F;
        public const ushort NGTAG_SWAP_MESH           = 0x20;
        public const ushort NGTAG_FLIP_MESH           = 0x21;
        public const ushort NGTAG_VAR_GLOBAL_TRNG     = 0x22;
        public const ushort NGTAG_SLOT_FLAGS_ARRAY    = 0x23;
        public const ushort NGTAG_VAR_LOCAL_TRNG      = 0x24;
        public const ushort NGTAG_KAYAK_EXTRA_DATA    = 0x25;
        public const ushort NGTAG_ASSIGNED_SLOT       = 0x26;
        public const ushort NGTAG_FROZEN_ITEMS        = 0x27;
        public const ushort NGTAG_FISH                = 0x28;
        public const ushort NGTAG_NO_COLL_ITEMS       = 0x29;
        public const ushort NGTAG_STATUS_TRIGGER_GROUP= 0x2A;
        public const ushort NGTAG_SAVE_LOCUST         = 0x2B;
        public const ushort NGTAG_VARIABLE_DATA       = 0x2C;
        public const ushort NGTAG_VAR_DATA_LARA       = 0x2D;
        public const ushort NGTAG_MINI_SHOT           = 0x2E;
        public const ushort NGTAG_NG_HUB_HEADERS      = 0x2F;

        // TRNG Extended Moveable Slots (from TRNG structures.h)
        public static readonly Dictionary<uint, string> TRNGSlotNames = new()
        {
            { 465, "MOTOR_BOAT" },
            { 466, "MOTOR_BOAT_LARA" },
            { 467, "RUBBER_BOAT" },
            { 468, "RUBBER_BOAT_LARA" },
            { 469, "MOTORBIKE_LARA" },
            { 470, "FONT_GRAPHICS" },
            { 471, "PARALLEL_BARS" },
            { 472, "PANEL_BORDER" },
            { 473, "PANEL_MIDDLE" },
            { 474, "PANEL_CORNER" },
            { 475, "PANEL_DIAGONAL" },
            { 476, "PANEL_STRIP" },
            { 477, "PANEL_HALF_BORDER1" },
            { 478, "PANEL_HALF_BORDER2" },
            { 479, "PANEL_MIDDLE_CORNER" },
            { 480, "TIGHT_ROPE" },
            { 481, "LASER_HEAD" },
            { 482, "LASER_HEAD_BASE" },
            { 483, "LASER_HEAD_TENTACLE" },
            { 484, "HYDRA" },
            { 485, "HYDRA_MISSILE" },
            { 486, "ENEMY_SUB_MARINE" },
            { 487, "ENEMY_SUB_MARINE_MIP" },
            { 488, "SUB_MARINE_MISSILE" },
            { 489, "FROG_MAN" },
            { 490, "FROG_MAN_HARPOON" },
            { 491, "FISH_EMITTER" },
            { 492, "KAYAK" },
            { 493, "KAYAK_LARA" },
            { 494, "CUSTOM_SPRITES" },
            { 495, "BRIDGE_TILT3" },
            { 496, "BRIDGE_TILT4" },
            { 497, "BRIDGE_CUSTOM" },
            { 498, "ROBOT_CLEANER" },
            { 499, "ROBOT_STAR_WARS" },
            { 500, "MECH_WARRIOR" },
            { 501, "MECH_WARRIOR_LARA" },
            { 502, "UW_PROPULSOR" },
            { 503, "UW_PROPULSOR_LARA" },
            { 504, "MINE_CART" },
            { 505, "MINE_CART_LARA" },
            { 506, "NEW_SLOT_5" },
            { 507, "NEW_SLOT_6" },
            { 508, "NEW_SLOT_7" },
            { 509, "NEW_SLOT_8" },
            { 510, "NEW_SLOT_9" },
            { 511, "NEW_SLOT_10" },
            { 512, "NEW_SLOT_11" },
            { 513, "NEW_SLOT_12" },
            { 514, "NEW_SLOT_13" },
            { 515, "NEW_SLOT_14" },
            { 516, "NEW_SLOT_15" },
            { 517, "NEW_SLOT_16" },
            { 518, "NEW_SLOT_17" },
            { 519, "NEW_SLOT_18" },
            { 520, "SLOT_NUMBER_OBJECTS" }
        };

        public class TRNGHeaderSummary
        {
            public bool HasNGHeader { get; set; }
            public uint HeaderSize { get; set; }
            public long HeaderOffset { get; set; }
            public int FieldCount { get; set; }
            public List<string> ImportedFiles { get; } = new();
            public List<ushort> TagTypes { get; } = new();
        }

        public TRNGWad2Processor(string inputLevelPath, string outputWad2Path, Action<string> logger)
        {
            _inputLevelPath = inputLevelPath;
            _outputWad2Path = outputWad2Path;
            _logger = logger;
        }

        /// <summary>
        /// Checks whether a given level file is a TRNG (Next Generation TR4) level.
        /// </summary>
        public static bool IsTRNGLevel(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var fi = new FileInfo(filePath);
                if (fi.Length < 16) return false;

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);

                // Check version magic
                uint magic = br.ReadUInt32();
                if (magic != 0x00345254) // Not TR4 magic
                    return false;

                // Check for NGLE footer at end of file
                fs.Seek(-8, SeekOrigin.End);
                uint endCheck = br.ReadUInt32();
                uint sizeNgHeader = br.ReadUInt32();

                if (endCheck == NG_LONG_CHECK && sizeNgHeader > 8 && sizeNgHeader <= fi.Length)
                {
                    // Check initial check word
                    fs.Seek(-sizeNgHeader, SeekOrigin.End);
                    ushort firstCheckWord = br.ReadUInt16();
                    if (firstCheckWord == NG_SHORT_CHECK)
                        return true;
                }
            }
            catch { /* ignore */ }

            return false;
        }

        /// <summary>
        /// Parses TRNG NG header summary if present.
        /// </summary>
        public static TRNGHeaderSummary ParseNGHeaderSummary(string filePath)
        {
            var summary = new TRNGHeaderSummary();
            try
            {
                if (!File.Exists(filePath)) return summary;
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);

                if (fs.Length < 16) return summary;

                fs.Seek(-8, SeekOrigin.End);
                uint endCheck = br.ReadUInt32();
                uint sizeNgHeader = br.ReadUInt32();

                if (endCheck != NG_LONG_CHECK || sizeNgHeader <= 8 || sizeNgHeader > fs.Length)
                    return summary;

                long startOffset = fs.Length - sizeNgHeader;
                fs.Seek(startOffset, SeekOrigin.Begin);

                ushort firstCheck = br.ReadUInt16();
                if (firstCheck != NG_SHORT_CHECK)
                    return summary;

                summary.HasNGHeader = true;
                summary.HeaderSize = sizeNgHeader;
                summary.HeaderOffset = startOffset;

                int dataWordsCount = (int)(sizeNgHeader - 2 - 8) / 2;
                if (dataWordsCount <= 0) return summary;

                ushort[] words = new ushort[dataWordsCount];
                for (int w = 0; w < dataWordsCount; w++)
                    words[w] = br.ReadUInt16();

                int idx = 0;
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

                    if (numberOfWords == 0 || (ushort)numberOfWords == NGTAG_END_SEQUENCE)
                        break;

                    if (idx >= words.Length) break;
                    ushort tagType = words[idx++];
                    summary.TagTypes.Add(tagType);
                    summary.FieldCount++;

                    int dataWords = numberOfWords - extraWords;
                    if (dataWords > 0 && idx + dataWords <= words.Length)
                    {
                        if (tagType == NGTAG_IMPORT_FILE && dataWords >= 46)
                        {
                            // Parse StrHeaderImportFile
                            // WORD Id, WORD TipoImport, WORD TipoFile, short NumeroFile, char NomeFile[80], DWORD SizeFile
                            int nameOffset = idx + 4; // after Id, TipoImport, TipoFile, NumeroFile (4 words = 8 bytes)
                            byte[] nameBytes = new byte[80];
                            Buffer.BlockCopy(words, nameOffset * 2, nameBytes, 0, 80);
                            string rawName = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' ');
                            if (!string.IsNullOrEmpty(rawName))
                                summary.ImportedFiles.Add(rawName);
                        }
                    }

                    idx += Math.Max(0, dataWords);
                }
            }
            catch { /* ignore */ }

            return summary;
        }

        /// <summary>
        /// Main extraction pipeline for TRNG Wad2.
        /// </summary>
        public void ExtractWad2()
        {
            _logger("[TRNG Wad2] ========== STARTING TRNG WAD2 PIPELINE ==========");

            if (!File.Exists(_inputLevelPath))
            {
                _logger("[TRNG Wad2 Error] Input level file not found.");
                return;
            }

            var fi = new FileInfo(_inputLevelPath);
            _logger($"[TRNG Wad2] Level File : {fi.Name} ({fi.Length / 1024:N0} KB)");

            var summary = ParseNGHeaderSummary(_inputLevelPath);
            if (summary.HasNGHeader)
            {
                _logger($"[TRNG Wad2] NG Header detected: Size = {summary.HeaderSize} bytes ({summary.FieldCount} chunks).");
                if (summary.ImportedFiles.Count > 0)
                {
                    _logger($"[TRNG Wad2] Embedded NG Files ({summary.ImportedFiles.Count}):");
                    foreach (var fn in summary.ImportedFiles)
                        _logger($"[TRNG Wad2]   - {fn}");
                }
            }
            else
            {
                _logger("[TRNG Wad2] Standard TR4 container (no extra NG footer detected).");
            }

            _logger("[TRNG Wad2] Initializing STA Dispatcher for TombLib level loader & converter...");
            var wad = RunViaTombLib();

            if (wad == null)
            {
                _logger("[TRNG Wad2 Error] Failed to generate Wad2 from TRNG level.");
                return;
            }

            SaveResult(wad);
        }

        private Wad2? RunViaTombLib()
        {
            return RunViaTombLibCore(_inputLevelPath);
        }

        private Wad2? RunViaTombLibCore(string inputPath)
        {
            TrLevel?   trLevel     = null;
            Wad2?      resultWad   = null;
            Exception? thrownEx    = null;
            bool       loadDone    = false;
            bool       convertDone = false;
            Dispatcher? staDisp    = null;
            var dispReady = new ManualResetEventSlim(false);

            var staThread = new Thread(() =>
            {
                staDisp = Dispatcher.CurrentDispatcher;
                dispReady.Set();
                Dispatcher.Run();
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            dispReady.Wait(5000);
            if (staDisp == null)
            {
                _logger("[TRNG Wad2 Error] STA Dispatcher failed to initialize.");
                return null;
            }

            // Phase 1: LoadLevel
            staDisp.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
            {
                try
                {
                    trLevel = new TrLevel();
                    trLevel.LoadLevel(inputPath, true);
                    _logger("[TRNG Wad2] [STA] TrLevel.LoadLevel succeeded.");
                    loadDone = true;
                }
                catch (EndOfStreamException ex)
                {
                    _logger($"[TRNG Wad2] [STA] EndOfStreamException (Ignored for TRNG stream end): {ex.Message}");
                    loadDone = true;
                }
                catch (Exception ex)
                {
                    _logger($"[TRNG Wad2] [STA] TrLevel.LoadLevel error: {ex.GetType().Name}: {ex.Message}");
                    thrownEx = ex;
                    loadDone = true;
                }
            }));

            var sw = Stopwatch.StartNew();
            while (!loadDone)
            {
                Thread.Sleep(ProgressLogIntervalSeconds * 1000);
                if (!loadDone)
                {
                    _logger($"[TRNG Wad2] LoadLevel in progress... {sw.Elapsed.TotalSeconds:F0}s");
                    if (sw.Elapsed.TotalSeconds >= TimeoutSeconds)
                    {
                        _logger($"[TRNG Wad2 Error] TIMEOUT loading level after {TimeoutSeconds}s.");
                        staDisp.InvokeShutdown();
                        return null;
                    }
                }
            }

            if (trLevel == null)
            {
                _logger("[TRNG Wad2 Error] TrLevel instance is null.");
                staDisp.InvokeShutdown();
                return null;
            }

            LogTrLevelContents(trLevel);

            // Phase 2: ConvertTrLevel
            staDisp.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
            {
                try
                {
                    var opsType = typeof(TrLevel).Assembly
                        .GetType("TombLib.Wad.TrLevels.TrLevelOperations");
                    var method = opsType?.GetMethod("ConvertTrLevel",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    if (method == null)
                    {
                        _logger("[TRNG Wad2 Error] ConvertTrLevel method not found in TombLib.");
                        convertDone = true;
                        return;
                    }

                    resultWad = (Wad2?)method.Invoke(null, new object[] { trLevel });
                    _logger($"[TRNG Wad2] [STA] ConvertTrLevel completed. (Wad2 created = {resultWad != null})");
                    convertDone = true;
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    _logger($"[TRNG Wad2] [STA] ConvertTrLevel invocation error: {inner.GetType().Name}: {inner.Message}");
                    thrownEx = inner;
                    convertDone = true;
                }
                catch (Exception ex)
                {
                    _logger($"[TRNG Wad2] [STA] ConvertTrLevel error: {ex.GetType().Name}: {ex.Message}");
                    thrownEx = ex;
                    convertDone = true;
                }
                finally
                {
                    staDisp?.InvokeShutdown();
                }
            }));

            var swC = Stopwatch.StartNew();
            while (!convertDone)
            {
                Thread.Sleep(ProgressLogIntervalSeconds * 1000);
                if (!convertDone)
                {
                    _logger($"[TRNG Wad2] ConvertTrLevel in progress... {swC.Elapsed.TotalSeconds:F0}s");
                    if (swC.Elapsed.TotalSeconds >= TimeoutSeconds)
                    {
                        _logger("[TRNG Wad2 Error] TIMEOUT ConvertTrLevel.");
                        staDisp.InvokeShutdown();
                        return null;
                    }
                }
            }

            staThread.Join(5000);
            return thrownEx == null ? resultWad : null;
        }

        private void LogTrLevelContents(TrLevel trLevel)
        {
            _logger("[TRNG Wad2] --- TrLevel Contents Summary ---");
            try
            {
                var t = trLevel.GetType();
                string[] fields = { "Version", "Moveables", "StaticMeshes", "Meshes", "Animations", "Frames", "SpriteSequences" };
                foreach (var name in fields)
                {
                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f == null) continue;
                    var val = f.GetValue(trLevel);
                    if (val is System.Collections.ICollection c)
                        _logger($"[TRNG Wad2]   {name} = [{c.Count}]");
                    else if (val is Array arr)
                        _logger($"[TRNG Wad2]   {name} = Array[{arr.Length}]");
                    else
                        _logger($"[TRNG Wad2]   {name} = {val}");
                }
            }
            catch { /* ignore */ }
        }

        private void SaveResult(Wad2 wad)
        {
            _logger("[TRNG Wad2] --- Saving Wad2 Package ---");
            _logger($"[TRNG Wad2]   Moveables       : {wad.Moveables.Count}");
            _logger($"[TRNG Wad2]   Static Meshes   : {wad.Statics.Count}");
            _logger($"[TRNG Wad2]   Sprite Sequences: {wad.SpriteSequences.Count}");
            _logger($"[TRNG Wad2]   Game Version    : {wad.GameVersion}");

            // Log any extended TRNG moveables found
            int trngCustomCount = 0;
            foreach (var kvp in wad.Moveables)
            {
                uint slotId = kvp.Key.TypeId;
                if (TRNGSlotNames.TryGetValue(slotId, out string? name) && slotId >= 465)
                {
                    _logger($"[TRNG Wad2 Extended Slot] Slot {slotId} -> {name}");
                    trngCustomCount++;
                }
            }
            if (trngCustomCount > 0)
                _logger($"[TRNG Wad2] Detected {trngCustomCount} TRNG extended moveable slot(s).");

            string? dir = Path.GetDirectoryName(_outputWad2Path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            Wad2Writer.SaveToFile(wad, _outputWad2Path);
            _logger($"[TRNG Wad2] ========== TRNG WAD2 SAVED TO: {_outputWad2Path} ==========");
        }
    }
}
