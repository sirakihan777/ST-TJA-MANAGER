using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ST_Fumen_Manager_WPF.Models;
using ST_Fumen_Manager_WPF.Services;
using Microsoft.Win32;

namespace ST_Fumen_Manager_WPF
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<CourseItem> CourseItems { get; } = new();

        public List<string> GenreOptions { get; } = new()
        {
            "ポップス", "キッズ", "アニメ", "ボーカロイド™曲",
            "ゲームミュージック", "バラエティ", "クラシック", "ナムコオリジナル"
        };

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            CourseDataGrid.ItemsSource = CourseItems;
        }

        private async void SelectFolder_Click(object sender, RoutedEventArgs e)
        {
            string initialDir = ConfigService.LoadLastPath();

            var dialog = new OpenFolderDialog
            {
                Title = "TJA / WAV フォルダを選択してください",
                InitialDirectory = initialDir
            };

            if (dialog.ShowDialog() != true) return;

            string folderPath = dialog.FolderName;
            ConfigService.SaveLastPath(folderPath);

            CourseItems.Clear();
            UpdateStatus("解析・変換処理中...", "#FFFFFF");

            // UIがフリーズしないようバックグラウンドで変換と解析を実行
            await Task.Run(() =>
            {
                var summary = AudioConverter.BatchConvertAndCleanup(folderPath, (msg, color) =>
                {
                    Dispatcher.Invoke(() => UpdateStatus(msg, color));
                });

                var tjaFiles = Directory.GetFiles(folderPath, "*.tja", SearchOption.AllDirectories);
                var loadedCourses = new List<CourseItem>();

                foreach (var file in tjaFiles)
                {
                    try
                    {
                        var encoding = TjaParser.GetTjaEncoding(file);
                        
                        // WAVE行の自動更新もここで実行
                        string content = File.ReadAllText(file, encoding);
                        string updatedContent = TjaWriter.UpdateTjaWaveLines(content);
                        updatedContent = TjaWriter.CleanRedundantNewlines(updatedContent);
                        if (content != updatedContent)
                        {
                            TjaWriter.SaveWithEncodingFallback(file, updatedContent);
                        }

                        var courses = TjaParser.ParseTjaMultiCourse(file, encoding);
                        loadedCourses.AddRange(courses);
                    }
                    catch (Exception ex)
                    {
                        summary.TjaErrors.Add((file, ex.Message));
                    }
                }

                AudioConverter.WriteErrorLog(folderPath, summary);

                Dispatcher.Invoke(() =>
                {
                    foreach (var c in loadedCourses)
                    {
                        CourseItems.Add(c);
                    }

                    int errCount = summary.TotalErrorCount;
                    if (errCount > 0)
                    {
                        string note = summary.LogPath != null ? $" / ログ: {summary.LogPath}" : "";
                        UpdateStatus($"読み込み完了: {CourseItems.Count} コース / " +
                                     $"変換失敗 {summary.ConvertErrors.Count} / " +
                                     $"削除失敗 {summary.DeleteErrors.Count} / " +
                                     $"整理失敗 {summary.LegacyErrors.Count} / " +
                                     $"TJA更新失敗 {summary.TjaErrors.Count}{note}", "#FFA500");
                    }
                    else
                    {
                        UpdateStatus($"読み込み完了: {CourseItems.Count} コース", "#2EA043");
                    }
                });
            });
        }

        private void SaveRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is CourseItem item)
            {
                try
                {
                    TjaWriter.SaveSpecificCourseData(item.FilePath, item.RawCourseVal, item);
                    UpdateStatus($"保存完了: {item.Title} ({item.CourseName})", "#2EA043");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (CourseItems.Count == 0) return;

            var result = MessageBox.Show(
                $"全 {CourseItems.Count} コースを一括保存しますか？",
                "確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            UpdateStatus("保存中...", "#FFFF00");

            int success = 0;
            foreach (var item in CourseItems)
            {
                try
                {
                    TjaWriter.SaveSpecificCourseData(item.FilePath, item.RawCourseVal, item);
                    success++;
                }
                catch { }
            }

            UpdateStatus($"一括保存完了！ ({success}/{CourseItems.Count} 件)", "#2EA043");
        }

        private void UpdateStatus(string message, string hexColor)
        {
            StatusLabel.Text = message;
            try
            {
                StatusLabel.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hexColor)!;
            }
            catch { }
        }
    }
}