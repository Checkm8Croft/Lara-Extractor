using Microsoft.Win32;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Lara_Extractor
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Log("System initialized. Ready to unpack Tomb Raider levels.");
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Tomb Raider Levels (*.phd;*.tr2;*.tr4;*.trc)|*.phd;*.tr2;*.tr4;*.trc;*.dat|All files (*.*)|*.*",
                Title = "Select a Tomb Raider Level"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                TxtFilePath.Text = openFileDialog.FileName;
                Log($"Target loaded: {Path.GetFileName(openFileDialog.FileName)} ({new FileInfo(openFileDialog.FileName).Length / 1024 / 1024} MB)");
            }
        }

        private async void BtnExtract_Click(object sender, RoutedEventArgs e)
        {
            string filePath = TxtFilePath.Text;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show("Please select a valid level file first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string outputDir      = Path.Combine(Path.GetDirectoryName(filePath) ?? "", "Extracted_Assets");
            string wad2FileName   = Path.GetFileNameWithoutExtension(filePath) + ".wad2";
            string outputWad2Path = Path.Combine(outputDir, "Wads", wad2FileName);

            ExtractionProgress.Value = 0;
            TxtLog.Clear();
            Log("[Engine] Starting comprehensive asset unpacking pipeline...");

            await Task.Run(async () =>
            {
                try
                {
                    UpdateLog("[Pipeline] Phase 1: Launching AssetProcessor for raw data...");
                    var processor = new AssetProcessor(filePath, UpdateLog, UpdateProgress);
                    processor.UnpackAll();
                    processor = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(1500);
                    UpdateLog("--------------------------------------------------");

                    UpdateLog("[Pipeline] Phase 2: Launching Wad2Processor for 3D assets...");
                    var wad2Processor = new Wad2Processor(filePath, outputWad2Path, UpdateLog);
                    wad2Processor.ExtractWad2();

                    UpdateLog("[Pipeline] Pipeline finished successfully!");
                    UpdateProgress(100);
                }
                catch (Exception ex)
                {
                    UpdateLog($"[CRITICAL ERROR] {ex.GetType().Name}: {ex.Message}");
                    // Full stack trace for debugging
                    var lines = (ex.StackTrace ?? "").Split('\n');
                    foreach (var line in lines)
                    {
                        var l = line.Trim();
                        if (!string.IsNullOrEmpty(l))
                            UpdateLog($"[STACK] {l}");
                    }
                    if (ex.InnerException != null)
                        UpdateLog($"[INNER] {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
            });
        }

        private void Log(string message)
            => TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");

        private void UpdateLog(string message)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() => Log(message)));
        }

        private void UpdateProgress(double percentage)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                (Action)(() => ExtractionProgress.Value = percentage));
        }
    }
}
