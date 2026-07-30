// Wad2Worker - processo separato net6.0-windows che usa TombLib nativa
// Riceve: args[0] = percorso file livello TR
//         args[1] = percorso output .wad2
// Exit code: 0 = successo, 1 = errore
// Stdout: messaggi di log (prefisso [LOG] o [ERR])

using System;
using System.IO;
using System.Threading;
using TombLib.Wad;

if (args.Length < 2)
{
    Console.WriteLine("[ERR] Uso: Wad2Worker.exe <inputLevel> <outputWad2>");
    Environment.Exit(1);
}

string inputPath  = args[0];
string outputPath = args[1];

if (!File.Exists(inputPath))
{
    Console.WriteLine($"[ERR] File non trovato: {inputPath}");
    Environment.Exit(1);
}

// Leggi magic bytes
byte[] hdr = new byte[4];
using (var fs = File.OpenRead(inputPath)) fs.Read(hdr, 0, 4);
uint magic = BitConverter.ToUInt32(hdr, 0);
Console.WriteLine($"[LOG] File: {Path.GetFileName(inputPath)}  Size: {new FileInfo(inputPath).Length / 1024:N0} KB  Magic: 0x{magic:X8}");

Exception? error = null;
Wad2? wad = null;

var thread = new Thread(() =>
{
    try
    {
        // IDialogHandler silenzioso - ignora tutti i dialogs
        wad = Wad2.ImportFromFile(inputPath, false, null);
    }
    catch (Exception ex)
    {
        error = ex;
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.IsBackground = true;
thread.Start();
bool done = thread.Join(TimeSpan.FromSeconds(120));

if (!done)
{
    Console.WriteLine("[ERR] Timeout: TombLib non ha completato entro 120s");
    Environment.Exit(1);
}

if (error != null)
{
    Console.WriteLine($"[ERR] {error.GetType().Name}: {error.Message}");
    if (error.InnerException != null)
        Console.WriteLine($"[ERR] Inner: {error.InnerException.Message}");
    Environment.Exit(1);
}

if (wad == null)
{
    Console.WriteLine("[ERR] Wad2.ImportFromFile ha restituito null");
    Environment.Exit(1);
}

Console.WriteLine($"[LOG] Conversione OK: {wad.Moveables.Count} moveables, {wad.Statics.Count} statics, {wad.SpriteSequences.Count} sprites");

string? outDir = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
    Directory.CreateDirectory(outDir);

Wad2Writer.SaveToFile(wad, outputPath);
Console.WriteLine($"[LOG] Salvato: {outputPath}");
Environment.Exit(0);
