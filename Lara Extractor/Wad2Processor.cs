using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using TombLib.Wad;
using TombLib.Wad.TrLevels;

namespace Lara_Extractor
{
    public class Wad2Processor
    {
        private readonly string _inputLevelPath;
        private readonly string _outputWad2Path;
        private readonly Action<string> _logger;

        private const int TimeoutSeconds = 120;
        private const int ProgressLogIntervalSeconds = 5;

        public Wad2Processor(string inputLevelPath, string outputWad2Path, Action<string> logger)
        {
            _inputLevelPath = inputLevelPath;
            _outputWad2Path = outputWad2Path;
            _logger = logger;
        }

        public void ExtractWad2()
        {
            _logger("[Wad2] ========== STARTING WAD2 PIPELINE ==========");

            if (!File.Exists(_inputLevelPath))
            {
                _logger("[Wad2 Error] File not found.");
                return;
            }

            var fi = new FileInfo(_inputLevelPath);
            byte[] header = new byte[4];
            using (var fs = File.OpenRead(_inputLevelPath))
                _ = fs.Read(header, 0, 4);
            uint magic = BitConverter.ToUInt32(header, 0);

            string format = magic switch
            {
                0x00000020 => "TR1",
                0x0000002D => "TR2",
                0xFF180038 or 0xFF080038 or 0xFF180034 => "TR3",
                0x00345254 => "TR4/TR5",
                0x63345254 => "TRNG",
                _ => "UNKNOWN"
            };

            _logger($"[Wad2] File   : {fi.Name}");
            _logger($"[Wad2] Size   : {fi.Length / 1024:N0} KB");
            _logger($"[Wad2] Magic  : 0x{magic:X8} -> {format}");

            if (format == "UNKNOWN")
            {
                _logger("[Wad2 Error] Unrecognized format.");
                return;
            }

            Wad2? resultWad = null;

            // ── TR1: use the native parser (TombLib LoadLevel hangs on TR1) ──
            if (format == "TR1")
            {
                _logger("[Wad2] TR1: using native parser (bypassing TombLib LoadLevel).");
                var builder = new TR1Wad2Builder(_inputLevelPath, _logger);
                resultWad = builder.Build();
            }
            else if (format == "TR3" || format == "TR2")
            {
                _logger($"[Wad2] {format}: using native parser (bypassing TombLib ConvertTrLevel).");
                var builder = new TR3Wad2Builder(_inputLevelPath, _logger);
                resultWad = builder.Build();
            }
            else if (format == "TR4/TR5" || format == "TRNG")
            {
                if (format == "TRNG" || TRNGWad2Processor.IsTRNGLevel(_inputLevelPath))
                {
                    _logger("[Wad2] TRNG: detected Tomb Raider Next Generation level. Delegating to TRNGWad2Processor...");
                    var trngProcessor = new TRNGWad2Processor(_inputLevelPath, _outputWad2Path, _logger);
                    trngProcessor.ExtractWad2();
                    return;
                }

                // ── TR4/TR5 standard: use TombLib LoadLevel + ConvertTrLevel ──────────
                _logger($"[Wad2] {format}: using TombLib LoadLevel + ConvertTrLevel.");
                resultWad = RunViaTombLib();
            }

            if (resultWad == null)
            {
                _logger("[Wad2 Error] No Wad2 produced.");
                return;
            }

            SaveResult(resultWad);
        }

        private Wad2? RunViaTombLib()
        {
            _logger("[Wad2] TombLib: starting STA thread with Dispatcher pump...");

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
                _logger("[Wad2 Error] STA Dispatcher not ready.");
                return null;
            }

            // LoadLevel
            staDisp.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
            {
                try
                {
                    trLevel = new TrLevel();
                    trLevel.LoadLevel(_inputLevelPath, false);
                    _logger("[Wad2] [STA] LoadLevel OK.");
                    loadDone = true;
                }
                catch (EndOfStreamException ex)
                {
                    _logger($"[Wad2] [STA] EndOfStreamException (TR4/TR5 .NET10 bug - Safe to ignore): {ex.Message}");
                    loadDone = true;
                }
                catch (Exception ex)
                {
                    _logger($"[Wad2] [STA] LoadLevel error: {ex.GetType().Name}: {ex.Message}");
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
                    _logger($"[Wad2] LoadLevel in progress... {sw.Elapsed.TotalSeconds:F0}s");
                    if (sw.Elapsed.TotalSeconds >= TimeoutSeconds)
                    {
                        _logger($"[Wad2 Error] TIMEOUT LoadLevel after {TimeoutSeconds}s.");
                        staDisp.InvokeShutdown();
                        return null;
                    }
                }
            }

            if (thrownEx != null && trLevel == null)
            {
                _logger("[Wad2 Error] LoadLevel failed.");
                staDisp.InvokeShutdown();
                return null;
            }

            LogTrLevelContents(trLevel!);

            // ConvertTrLevel
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
                        _logger("[Wad2] [STA] ConvertTrLevel not found.");
                        convertDone = true;
                        return;
                    }

                    resultWad = (Wad2?)method.Invoke(null, new object[] { trLevel! });
                    _logger($"[Wad2] [STA] ConvertTrLevel OK. null={resultWad == null}");
                    convertDone = true;
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    _logger($"[Wad2] [STA] ConvertTrLevel error: {inner.GetType().Name}: {inner.Message}");
                    thrownEx = inner;
                    convertDone = true;
                }
                catch (Exception ex)
                {
                    _logger($"[Wad2] [STA] ConvertTrLevel error: {ex.GetType().Name}: {ex.Message}");
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
                    _logger($"[Wad2] ConvertTrLevel in progress... {swC.Elapsed.TotalSeconds:F0}s");
                    if (swC.Elapsed.TotalSeconds >= TimeoutSeconds)
                    {
                        _logger("[Wad2 Error] TIMEOUT ConvertTrLevel.");
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
            _logger("[Wad2] --- TrLevel Contents ---");
            try
            {
                var t = trLevel.GetType();
                string[] fields = { "Version", "Moveables", "StaticMeshes", "Meshes", "Animations", "Frames", "SpriteSequences" };
                foreach (var name in fields)
                {
                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f == null) continue;
                    var val = f.GetValue(trLevel);
                    if (val is System.Collections.ICollection c) _logger($"[Wad2]   {name} = [{c.Count}]");
                    else if (val is Array arr) _logger($"[Wad2]   {name} = Array[{arr.Length}]");
                    else _logger($"[Wad2]   {name} = {val}");
                }
            }
            catch { }
        }

        private void SaveResult(Wad2 wad)
        {
            _logger("[Wad2] --- Saving ---");
            _logger($"[Wad2]   Moveables : {wad.Moveables.Count}");
            _logger($"[Wad2]   Statics   : {wad.Statics.Count}");
            _logger($"[Wad2]   Sprites   : {wad.SpriteSequences.Count}");
            _logger($"[Wad2]   Version   : {wad.GameVersion}");

            string? dir = Path.GetDirectoryName(_outputWad2Path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            Wad2Writer.SaveToFile(wad, _outputWad2Path);
            _logger("[Wad2] ========== WAD2 SAVED OK ==========");
        }
    }
}
