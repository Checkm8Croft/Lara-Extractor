using Microsoft.Win32;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

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

            string outputDir = Path.Combine(Path.GetDirectoryName(filePath), "Extracted_Assets");
            string wad2FileName = Path.GetFileNameWithoutExtension(filePath) + ".wad2";
            string outputWad2Path = Path.Combine(outputDir, "Wads", wad2FileName);

            // Reset UI components
            ExtractionProgress.Value = 0;
            TxtLog.Clear();
            Log("[Engine] Starting comprehensive asset unpacking pipeline...");

            await Task.Run(async () => 
            {
                try
                {
                    // ==========================================
                    // PHASE 1: Raw Asset Extraction
                    // ==========================================
                    UpdateLog("[Pipeline] Phase 1: Launching AssetProcessor for raw data...");

                    AssetProcessor processor = new AssetProcessor(filePath, UpdateLog, UpdateProgress);
                    processor.UnpackAll();

                    UpdateLog("[Pipeline] AssetProcessor task completed. Releasing file handles...");

                    processor = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();

                    UpdateLog("[Pipeline] Waiting for OS file handles to clear safely...");
                    await Task.Delay(1500);

                    UpdateLog("--------------------------------------------------");

                    // ==========================================
                    // PHASE 2: 3D Geometry & WAD2 Extraction
                    // ==========================================
                    UpdateLog("[Pipeline] Phase 2: Launching Wad2Processor for 3D assets...");

                    Wad2Processor wad2Processor = new Wad2Processor(filePath, outputWad2Path, UpdateLog);
                    wad2Processor.ExtractWad2();

                    UpdateLog("[Pipeline] Comprehensive asset unpacking pipeline finished successfully!");
                    UpdateProgress(100);
                }
                catch (Exception ex)
                {
                    UpdateLog($"[CRITICAL ERROR] Extraction aborted: {ex.Message}");
                }
            });
        }
        private void Log(string message) => TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        
        private void UpdateLog(string message)
        {
            Dispatcher.Invoke(() => Log(message));
        }

        private void UpdateProgress(double percentage)
        {
            Dispatcher.Invoke(() => ExtractionProgress.Value = percentage);
        }
    }
}