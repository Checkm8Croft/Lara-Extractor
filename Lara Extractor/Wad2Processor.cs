using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using TombLib.Wad;
using TombLib.Wad.TrLevels;

namespace Lara_Extractor
{
    public class Wad2Processor
    {
        private readonly string _inputLevelPath;
        private readonly string _outputWad2Path;
        private readonly Action<string> _logger;
        private const int TimeoutSeconds = 90;

        public Wad2Processor(string inputLevelPath, string outputWad2Path, Action<string> logger)
        {
            _inputLevelPath = inputLevelPath;
            _outputWad2Path = outputWad2Path;
            _logger = logger;
        }

        public void ExtractWad2()
        {
            _logger("[Wad2] Starting pipeline...");

            if (!File.Exists(_inputLevelPath))
            {
                _logger("[Wad2 Error] File not found.");
                return;
            }

            // ── Magic bytes ──────────────────────────────────────────────
            long fileSize = new FileInfo(_inputLevelPath).Length;
            byte[] header = new byte[4];
            using (var fs = File.OpenRead(_inputLevelPath))
                _ = fs.Read(header, 0, 4);
            uint magic = BitConverter.ToUInt32(header, 0);

            string formatName = magic switch
            {
                0x00000020 => "TR1 (.phd)",
                0x0000002D => "TR2 (.tr2)",
                0xFF180038 => "TR3 (.tr2)",
                0xFF080038 => "TR3 (.tr2)",
                0xFF180034 => "TR3 (.tr2)",
                0x00345254 => "TR4/TR5 (.tr4/.trc)",
                _ => "UNKNOWN"
            };

            _logger($"[Wad2] File  : {Path.GetFileName(_inputLevelPath)}");
            _logger($"[Wad2] Size  : {fileSize / 1024:N0} KB");
            _logger($"[Wad2] Magic : 0x{magic:X8} -> {formatName}");

            if (formatName == "UNKNOWN")
            {
                _logger("[Wad2 Error] Format not recognized. Aborted.");
                return;
            }

            TrLevel? trLevel = null;
            Exception? loadException = null;

            var thread = new Thread(() =>
            {
                try
                {
                    trLevel = new TrLevel();
                    trLevel.LoadLevel(_inputLevelPath, false);
                    _logger("[Wad2] LoadLevel completed without exceptions.");
                }
                catch (EndOfStreamException ex)
                {
                    // Crash attended in bug TombLib 1.11.1 / .NET 10 in SoundDetails.
                    // Moveables ans Statics are already loaded correctly.
                    _logger($"[Wad2] EndOfStreamException during LoadLevel (bug on TombLib 1.11.1/.NET 10) — continuing with partial data.");
                    _logger($"[Wad2] Detail: {ex.Message}");
                }
                catch (Exception ex)
                {
                    loadException = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            bool done = thread.Join(TimeSpan.FromSeconds(TimeoutSeconds));
            if (!done)
            {
                thread.Interrupt();
                _logger($"[Wad2 Error] Timeout: LoadLevel blocked after {TimeoutSeconds}s.");
                return;
            }

            if (loadException != null)
            {
                _logger($"[Wad2 Error] LoadLevel -> {loadException.GetType().Name}: {loadException.Message}");
                return;
            }

            if (trLevel == null)
            {
                _logger("[Wad2 Error] TrLevel not initialized.");
                return;
            }

            // Manual Conversion to Wad2 format
            Wad2? resultWad = null;
            Exception? convertException = null;

            var convertThread = new Thread(() =>
            {
                try
                {
                    var opsType = typeof(TrLevel).Assembly
                        .GetType("TombLib.Wad.TrLevels.TrLevelOperations");

                    var convertMethod = opsType?.GetMethod("ConvertTrLevel",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    if (convertMethod != null)
                    {
                        _logger("[Wad2] Invoking ConvertTrLevel via reflection...");
                        resultWad = (Wad2?)convertMethod.Invoke(null, new object[] { trLevel });
                        _logger("[Wad2] ConvertTrLevel completed.");
                    }
                    else
                    {
                        _logger("[Wad2 Error] ConvertTrLevel not found via reflection.");
                    }
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    convertException = tie.InnerException;
                }
                catch (Exception ex)
                {
                    convertException = ex;
                }
            });
            convertThread.SetApartmentState(ApartmentState.STA);
            convertThread.IsBackground = true;
            convertThread.Start();
            convertThread.Join(TimeSpan.FromSeconds(TimeoutSeconds));

            if (convertException != null)
            {
                _logger($"[Wad2 Error] ConvertTrLevel -> {convertException.GetType().Name}: {convertException.Message}");
                return;
            }

            if (resultWad == null)
            {
                _logger("[Wad2 Error] ConvertTrLevel returned null.");
                return;
            }

            SaveResult(resultWad);
        }

        private void SaveResult(Wad2 wad)
        {
            _logger($"[Wad2] Result: {wad.Moveables.Count} moveables, " +
                    $"{wad.Statics.Count} statics, {wad.SpriteSequences.Count} sprites.");

            if (wad.Moveables.Count == 0 && wad.Statics.Count == 0)
                _logger("[Wad2 Warning] Wad2 is empty (might be a cutscene or title screen).");

            string? outputDir = Path.GetDirectoryName(_outputWad2Path);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            _logger($"[Wad2] Writing: {Path.GetFileName(_outputWad2Path)}");
            Wad2Writer.SaveToFile(wad, _outputWad2Path);
            _logger($"[Wad2 OK] Saved to: {_outputWad2Path}");
        }
    }
}
