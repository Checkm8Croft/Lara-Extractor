using Microsoft.Win32;
using System;
using System.IO;
using System.Media;
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
                Filter = "Tomb Raider Levels (*.phd;*.tr2;*.tr4;*.trc)|*.phd;*.tr2;*.tr4;*.trc|All files (*.*)|*.*",
                Title = "Select a Tomb Raider Level"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                TxtFilePath.Text = openFileDialog.FileName;
                string defaultOutputDir = Path.Combine(Path.GetDirectoryName(openFileDialog.FileName) ?? "", "Extracted_Assets");
                TxtOutputDir.Text = defaultOutputDir;

                Log($"Target loaded: {Path.GetFileName(openFileDialog.FileName)} ({new FileInfo(openFileDialog.FileName).Length / 1024 / 1024} MB)");
                Log($"Output destination: {defaultOutputDir}");
            }
        }

        private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog folderDialog = new OpenFolderDialog
            {
                Title = "Select Output Directory",
                InitialDirectory = !string.IsNullOrWhiteSpace(TxtOutputDir.Text) && Directory.Exists(TxtOutputDir.Text)
                    ? TxtOutputDir.Text
                    : (!string.IsNullOrWhiteSpace(TxtFilePath.Text) ? Path.GetDirectoryName(TxtFilePath.Text) : null)
            };

            if (folderDialog.ShowDialog() == true)
            {
                TxtOutputDir.Text = folderDialog.FolderName;
                Log($"Output destination changed to: {folderDialog.FolderName}");
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

            string outputDir = TxtOutputDir.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = Path.Combine(Path.GetDirectoryName(filePath) ?? "", "Extracted_Assets");
                TxtOutputDir.Text = outputDir;
            }

            bool extractTextures = ChkExtractTextures.IsChecked == true;
            bool extractAudio    = ChkExtractAudio.IsChecked == true;
            bool extractWad2     = ChkExtractWad2.IsChecked == true;

            if (!extractTextures && !extractAudio && !extractWad2)
            {
                MessageBox.Show("Please select at least one component to extract (Textures, Audio, or 3D Assets).", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string wad2FileName   = Path.GetFileNameWithoutExtension(filePath) + ".wad2";
            string outputWad2Path = Path.Combine(outputDir, "Wads", wad2FileName);

            BtnExtract.IsEnabled = false;
            try
            {
                ExtractionProgress.Value = 0;
                TxtLog.Clear();
                Log("[Engine] Starting comprehensive asset unpacking pipeline...");
                Log($"[Engine] Output directory: {outputDir}");
                Log($"[Engine] Selected: Textures={extractTextures}, Audio={extractAudio}, Wad2={extractWad2}");

                bool saveLog = ChkSaveLog.IsChecked == true;

                await Task.Run(async () =>
                {
                    try
                    {
                        if (extractTextures || extractAudio)
                        {
                            UpdateLog("[Pipeline] Phase 1: Launching AssetProcessor for raw data...");
                            var processor = new AssetProcessor(filePath, UpdateLog, UpdateProgress, outputDir, extractTextures, extractAudio);
                            processor.UnpackAll();
                            processor = null;
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            await Task.Delay(1000);
                            UpdateLog("--------------------------------------------------");
                        }
                        else
                        {
                            UpdateLog("[Pipeline] Phase 1: Textures and Audio skipped (unselected).");
                        }

                        if (extractWad2)
                        {
                            UpdateLog("[Pipeline] Phase 2: Launching Wad2Processor for 3D assets...");
                            var wad2Processor = new Wad2Processor(filePath, outputWad2Path, UpdateLog);
                            wad2Processor.ExtractWad2();
                        }
                        else
                        {
                            UpdateLog("[Pipeline] Phase 2: 3D assets (Wad2) skipped (unselected).");
                        }

                        UpdateLog("[Pipeline] Pipeline finished successfully!");
                        UpdateProgress(100);
                    }
                    catch (Exception ex)
                    {
                        UpdateLog($"[CRITICAL ERROR] {ex.GetType().Name}: {ex.Message}");
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

                if (saveLog)
                {
                    try
                    {
                        string logFileName = $"{Path.GetFileNameWithoutExtension(filePath)}_log.txt";
                        string logFilePath = Path.Combine(outputDir, logFileName);
                        Directory.CreateDirectory(outputDir);

                        string finalLog = TxtLog.Text + $"[{DateTime.Now:HH:mm:ss}] [Engine] Log file successfully saved to: {logFilePath}\n";
                        File.WriteAllText(logFilePath, finalLog);
                        Log($"[Engine] Log file successfully saved to: {logFilePath}");
                    }
                    catch (Exception logEx)
                    {
                        Log($"[Warning] Failed to save log file: {logEx.Message}");
                    }
                }
            }
            finally
            {
                BtnExtract.IsEnabled = true;
                SystemSounds.Asterisk.Play();
            }
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
