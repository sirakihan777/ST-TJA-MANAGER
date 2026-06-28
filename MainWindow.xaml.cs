using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
        public ObservableCollection<StfdbEntryItem> StfdbEntries { get; } = new();

        public List<string> GenreOptions { get; } = new()
        {
            "ポップス", "キッズ", "アニメ", "ボーカロイド™曲",
            "ゲームミュージック", "バラエティ", "クラシック", "ナムコオリジナル"
        };

        private string _currentFolderPath = "";
        private string _currentStfdbPath = "";
        private Encoding _currentStfdbEncoding = Encoding.GetEncoding("shift_jis");
        private string _currentStfdbNewline = "\r\n";
        private List<string> _stfdbFilesList = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            CourseDataGrid.ItemsSource = CourseItems;
            StfdbDataGrid.ItemsSource = StfdbEntries;

            // RunSanityCheck を安全化（例外を吸収してアプリ起動を妨げない）
            try
            {
                if (StfdbService.RunSanityCheck(out string testReport))
                {
                    Trace.WriteLine("[STFDB Sanity Check Passed]\n" + testReport);
                }
                else
                {
                    Trace.TraceError("[STFDB Sanity Check FAILED]\n" + testReport);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("[STFDB Sanity Check Exception] " + ex.Message);
            }
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
            _currentFolderPath = folderPath;
            ConfigService.SaveLastPath(folderPath);

            CourseItems.Clear();
            StfdbEntries.Clear();
            StfdbComboBox.ItemsSource = null;
            UpdateStatus("解析・変換処理中...", "#FFFFFF");

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

                var stfdbFiles = StfdbService.FindStfdbFiles(folderPath);

                Dispatcher.Invoke(() =>
                {
                    foreach (var c in loadedCourses)
                    {
                        CourseItems.Add(c);
                    }

                    _stfdbFilesList = stfdbFiles;
                    if (_stfdbFilesList.Count > 0)
                    {
                        StfdbComboBox.ItemsSource = _stfdbFilesList.Select(Path.GetFileName).ToList();
                        StfdbComboBox.SelectedIndex = 0;
                        StfdbStatusText.Text = $"{_stfdbFilesList.Count} 件の STFDB を検出しました";
                    }
                    else
                    {
                        StfdbComboBox.ItemsSource = new List<string> { "STFDBファイルが見つかりません" };
                        StfdbComboBox.SelectedIndex = 0;
                        StfdbStatusText.Text = "STFDBファイルが見つかりません";
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

        #region STFDB Handlers

        private void CommitStfdbGridEdit()
        {
            try
            {
                StfdbDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                StfdbDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch { }
        }

        private void StfdbComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = StfdbComboBox.SelectedIndex;
            if (idx < 0 || _stfdbFilesList.Count == 0 || idx >= _stfdbFilesList.Count)
            {
                StfdbEntries.Clear();
                _currentStfdbPath = "";
                return;
            }

            _currentStfdbPath = _stfdbFilesList[idx];
            var entries = StfdbService.LoadStfdbFile(_currentStfdbPath, out _currentStfdbEncoding, out _currentStfdbNewline);

            StfdbEntries.Clear();
            foreach (var entry in entries)
            {
                StfdbEntries.Add(entry);
            }

            StfdbStatusText.Text = $"Status: STFDB loaded: {StfdbEntries.Count} entries";
        }

        private void StfdbAddFromLoaded_Click(object sender, RoutedEventArgs e)
        {
            CommitStfdbGridEdit();
            if (string.IsNullOrEmpty(_currentStfdbPath) || !File.Exists(_currentStfdbPath))
            {
                MessageBox.Show("まず編集対象のSTFDBファイルを選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var existingPaths = StfdbEntries.Select(x => x.ResolvedTjaPath).ToList();
            var dialog = new StfdbAddTjaDialog(CourseItems, existingPaths)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                int addedCount = 0;
                foreach (var candidate in dialog.SelectedCandidates)
                {
                    string relPath = StfdbService.MakeRelativeTjaPath(_currentStfdbPath, candidate.FilePath);
                    var newEntry = new StfdbEntryItem
                    {
                        StfdbFilePath = _currentStfdbPath,
                        RawPath = relPath,
                        EditedPath = relPath,
                        ResolvedTjaPath = candidate.FilePath,
                        Exists = true,
                        Title = candidate.Title,
                        Genre = candidate.Genre,
                        Course = candidate.Course,
                        Level = candidate.Level
                    };

                    StfdbEntries.Add(newEntry);
                    addedCount++;
                }

                if (addedCount > 0)
                {
                    StfdbService.ValidateStfdbEntries(StfdbEntries);
                    StfdbService.RefreshRowNumbers(StfdbEntries);
                    StfdbStatusText.Text = $"{addedCount} 件のTJAを追加しました (未保存)";
                }
            }
        }

        private void StfdbAddFromFile_Click(object sender, RoutedEventArgs e)
        {
            CommitStfdbGridEdit();
            if (string.IsNullOrEmpty(_currentStfdbPath) || !File.Exists(_currentStfdbPath))
            {
                MessageBox.Show("まず編集対象のSTFDBファイルを選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? stfdbDir = Path.GetDirectoryName(_currentStfdbPath);
            var dialog = new OpenFileDialog
            {
                Title = "追加するTJAファイルを選択してください",
                Filter = "TJAファイル (*.tja)|*.tja|すべてのファイル (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = !string.IsNullOrEmpty(stfdbDir) ? stfdbDir : _currentFolderPath
            };

            if (dialog.ShowDialog() == true)
            {
                int addedCount = 0;
                foreach (string filePath in dialog.FileNames)
                {
                    string relPath = StfdbService.MakeRelativeTjaPath(_currentStfdbPath, filePath);
                    var newEntry = new StfdbEntryItem
                    {
                        StfdbFilePath = _currentStfdbPath,
                        RawPath = relPath,
                        EditedPath = relPath,
                        ResolvedTjaPath = filePath,
                        Exists = File.Exists(filePath)
                    };

                    if (newEntry.Exists)
                    {
                        StfdbService.ReadTjaSummaryForStfdb(newEntry);
                    }

                    StfdbEntries.Add(newEntry);
                    addedCount++;
                }

                if (addedCount > 0)
                {
                    StfdbService.ValidateStfdbEntries(StfdbEntries);
                    StfdbService.RefreshRowNumbers(StfdbEntries);
                    StfdbStatusText.Text = $"{addedCount} 件のTJAをファイルから追加しました (未保存)";
                }
            }
        }

        private void StfdbDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            CommitStfdbGridEdit();
            if (StfdbDataGrid.SelectedItem is not StfdbEntryItem selected)
            {
                MessageBox.Show("削除する行を選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                "選択したTJA参照をSTFDBから削除します。\n実際のTJAファイルは削除されません。\n続行しますか？",
                "削除確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                StfdbEntries.Remove(selected);
                StfdbService.ValidateStfdbEntries(StfdbEntries);
                StfdbService.RefreshRowNumbers(StfdbEntries);
                StfdbStatusText.Text = "選択行を削除しました (未保存)";
            }
        }

        private void StfdbMoveUp_Click(object sender, RoutedEventArgs e)
        {
            CommitStfdbGridEdit();
            int index = StfdbDataGrid.SelectedIndex;
            if (index > 0)
            {
                var item = StfdbEntries[index];
                StfdbEntries.RemoveAt(index);
                StfdbEntries.Insert(index - 1, item);
                StfdbDataGrid.SelectedIndex = index - 1;
                StfdbDataGrid.ScrollIntoView(item);
                StfdbService.RefreshRowNumbers(StfdbEntries);
            }
        }

        private void StfdbMoveDown_Click(object sender, RoutedEventArgs e)
        {
            CommitStfdbGridEdit();
            int index = StfdbDataGrid.SelectedIndex;
            if (index >= 0 && index < StfdbEntries.Count - 1)
            {
                var item = StfdbEntries[index];
                StfdbEntries.RemoveAt(index);
                StfdbEntries.Insert(index + 1, item);
                StfdbDataGrid.SelectedIndex = index + 1;
                StfdbDataGrid.ScrollIntoView(item);
                StfdbService.RefreshRowNumbers(StfdbEntries);
            }
        }

        private void StfdbValidate_Click(object sender, RoutedEventArgs e)
        {
            CommitStfdbGridEdit();
            if (StfdbEntries.Count == 0) return;
            StfdbService.ValidateStfdbEntries(StfdbEntries);
            StfdbDataGrid.Items.Refresh();
            StfdbStatusText.Text = "存在チェックを再実行しました";
        }

        private void StfdbDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StfdbService.ValidateStfdbEntries(StfdbEntries);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void StfdbSave_Click(object sender, RoutedEventArgs e)
        {
            CommitStfdbGridEdit();
            if (string.IsNullOrEmpty(_currentStfdbPath) || !File.Exists(_currentStfdbPath))
            {
                MessageBox.Show("保存対象のSTFDBファイルがありません。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                StfdbService.SaveStfdbFile(_currentStfdbPath, StfdbEntries, _currentStfdbEncoding, _currentStfdbNewline);
                
                string bakName = Path.GetFileName(_currentStfdbPath) + ".bak";
                UpdateStatus($"STFDBを保存しました: {StfdbEntries.Count}件 (バックアップ: {bakName})", "#2EA043");
                StfdbStatusText.Text = $"保存完了: {StfdbEntries.Count}件";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"STFDBの保存に失敗しました: \n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}