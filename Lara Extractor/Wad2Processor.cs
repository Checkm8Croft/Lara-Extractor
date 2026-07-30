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
            _logger("[Wad2] ========== AVVIO PIPELINE WAD2 ==========");

            if (!File.Exists(_inputLevelPath))
            {
                _logger("[Wad2 Errore] File non trovato.");
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
                _ => "UNKNOWN"
            };

            _logger($"[Wad2] File   : {fi.Name}");
            _logger($"[Wad2] Size   : {fi.Length / 1024:N0} KB");
            _logger($"[Wad2] Magic  : 0x{magic:X8} -> {format}");

            if (format == "UNKNOWN")
            {
                _logger("[Wad2 Errore] Formato non riconosciuto.");
                return;
            }

            Wad2? resultWad = null;

            // ── TR1: usa il parser nativo (TombLib LoadLevel si blocca per TR1) ──
            if (format == "TR1")
            {
                _logger("[Wad2] TR1: utilizzo parser nativo (bypass TombLib LoadLevel).");
                var builder = new TR1Wad2Builder(_inputLevelPath, _logger);
                resultWad = builder.Build();
            }
            else
            {
                // ── TR4/TR5: usa TombLib LoadLevel + ConvertTrLevel ──────────
                // ── TR2/TR3: usa TombLib (potrebbero bloccarsi come TR1 — da verificare) ──
                _logger($"[Wad2] {format}: utilizzo TombLib LoadLevel + ConvertTrLevel.");
                resultWad = RunViaTombLib();
            }

            if (resultWad == null)
            {
                _logger("[Wad2 Errore] Nessun Wad2 prodotto.");
                return;
            }

            SaveResult(resultWad);
        }

        private Wad2? RunViaTombLib()
        {
            _logger("[Wad2] TombLib: avvio thread STA con Dispatcher pump...");

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
                _logger("[Wad2 Errore] Dispatcher STA non pronto.");
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
                    _logger($"[Wad2] [STA] EndOfStreamException (bug TR4/TR5 .NET10): {ex.Message}");
                    loadDone = true;
                }
                catch (Exception ex)
                {
                    _logger($"[Wad2] [STA] LoadLevel errore: {ex.GetType().Name}: {ex.Message}");
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
                    _logger($"[Wad2] LoadLevel in corso... {sw.Elapsed.TotalSeconds:F0}s");
                    if (sw.Elapsed.TotalSeconds >= TimeoutSeconds)
                    {
                        _logger($"[Wad2 Errore] TIMEOUT LoadLevel dopo {TimeoutSeconds}s.");
                        staDisp.InvokeShutdown();
                        return null;
                    }
                }
            }

            if (thrownEx != null && trLevel == null)
            {
                _logger("[Wad2 Errore] LoadLevel fallito.");
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
                        _logger("[Wad2] [STA] ConvertTrLevel non trovato.");
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
                    _logger($"[Wad2] [STA] ConvertTrLevel errore: {inner.GetType().Name}: {inner.Message}");
                    thrownEx = inner;
                    convertDone = true;
                }
                catch (Exception ex)
                {
                    _logger($"[Wad2] [STA] ConvertTrLevel errore: {ex.GetType().Name}: {ex.Message}");
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
                    _logger($"[Wad2] ConvertTrLevel in corso... {swC.Elapsed.TotalSeconds:F0}s");
                    if (swC.Elapsed.TotalSeconds >= TimeoutSeconds)
                    {
                        _logger("[Wad2 Errore] TIMEOUT ConvertTrLevel.");
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
            _logger("[Wad2] --- Contenuto TrLevel ---");
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
            _logger("[Wad2] --- Salvataggio ---");
            _logger($"[Wad2]   Moveables : {wad.Moveables.Count}");
            _logger($"[Wad2]   Statics   : {wad.Statics.Count}");
            _logger($"[Wad2]   Sprites   : {wad.SpriteSequences.Count}");
            _logger($"[Wad2]   Version   : {wad.GameVersion}");

            string? dir = Path.GetDirectoryName(_outputWad2Path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            Wad2Writer.SaveToFile(wad, _outputWad2Path);
            _logger("[Wad2] ========== WAD2 SALVATO OK ==========");
        }
    }
}
