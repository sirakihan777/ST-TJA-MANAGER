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

        #region 公式順STFDB生成 Handlers

        // 照合結果を保持する
        private List<OfficialMatchResult> _officialMatchResults = new();

        // Git取得のキャンセルトークンソース
        private CancellationTokenSource? _gitCts;

        // ログ追記 (ログ欄が長くなり過ぎないよう最大500行に制限)
        private readonly System.Collections.Generic.Queue<string> _logLines = new();
        private const int MaxLogLines = 500;

        private void AppendOfficialLog(string line)
        {
            Dispatcher.Invoke(() =>
            {
                _logLines.Enqueue(line);
                while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
                TxtOfficialLog.Text = string.Join("\n", _logLines);
                OfficialLogScroll.ScrollToBottom();
            });
        }

        private void SetOfficialStatus(string text, string hexColor = "#A6A6A6")
        {
            Dispatcher.Invoke(() =>
            {
                TxtOfficialStatus.Text = text;
                try
                {
                    TxtOfficialStatus.Foreground =
                        (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hexColor)!;
                }
                catch { }
            });
        }

        /// <summary>Git進捗をステータスラベルに表示 (\ r 区切りの進捗行)</summary>
        private void SetGitProgress(string progress)
        {
            Dispatcher.Invoke(() =>
            {
                // 進捗はステータス欄に上書き表示するだけ (ログには追記しない)
                TxtOfficialStatus.Text = $"Git取得中... {progress}";
            });
        }

        /// <summary>Git曲データ取得/更新 ボタン</summary>
        private async void BtnGitFetch_Click(object sender, RoutedEventArgs e)
        {
            // 中途半端なフォルダ確認
            var state = GitSongSourceService.CheckSongsState();
            if (state == GitSongSourceService.SongsState.PartialOrBroken)
            {
                var dlgResult = MessageBox.Show(
                    $"Songs フォルダが存在しますが .git がありません (中途半端な状態)。\n\n" +
                    $"{GitSongSourceService.SongsPath}\n\n" +
                    "このフォルダを削除してから再cloneしますか？\n" +
                    "(キャッシュフォルダのみ削除します。本番Songsフォルダには影響しません)",
                    "中途半端なフォルダを検出",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (dlgResult != MessageBoxResult.Yes)
                {
                    AppendOfficialLog("[INFO] キャンセルしました。");
                    return;
                }

                AppendOfficialLog("[INFO] 中途半端な Songs フォルダを削除します...");
                await Task.Run(() =>
                    GitSongSourceService.DeletePartialSongsFolder(line => AppendOfficialLog(line)));
            }

            // CancellationTokenSource 初期化
            _gitCts?.Cancel();
            _gitCts?.Dispose();
            _gitCts = new CancellationTokenSource();
            var ct = _gitCts.Token;

            BtnGitFetch.IsEnabled = false;
            BtnGitCancel.IsEnabled = true;
            _logLines.Clear();
            TxtOfficialLog.Text = "";
            TxtGitElapsed.Text = "";
            SetOfficialStatus("Git取得中... 初回cloneは時間がかかる場合があります", "#FFFF00");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 経過時間タスク専用のキャンセルトークン (git の ct とは独立させる)
            using var elapsedCts = new CancellationTokenSource();
            var elapsedToken = elapsedCts.Token;

            // 経過時間を1秒ごとに更新するタスク
            var elapsedTask = Task.Run(async () =>
            {
                while (!elapsedToken.IsCancellationRequested)
                {
                    var elapsed = sw.Elapsed;
                    string elapsedStr = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2} 経過";
                    Dispatcher.Invoke(() => TxtGitElapsed.Text = elapsedStr);
                    try { await Task.Delay(1000, elapsedToken); }
                    catch (OperationCanceledException) { break; }
                    catch { break; }
                }
            }, CancellationToken.None);

            bool ok = false;
            try
            {
                ok = await GitSongSourceService.FetchOrUpdateAsync(
                    log: line => AppendOfficialLog(line),
                    onProgress: progress => SetGitProgress(progress),
                    ct: ct);

                sw.Stop();
                // elapsed タスクをここで止める (成功/失敗/キャンセル共通)
                elapsedCts.Cancel();
                await elapsedTask;

                if (ct.IsCancellationRequested)
                {
                    SetOfficialStatus("キャンセルされました", "#888888");
                    AppendOfficialLog("[INFO] キャンセルされました。");
                    AppendOfficialLog("[INFO] 不完全な Songs フォルダが残っている場合は、次回実行時に確認ダイアログが表示されます。");
                    Dispatcher.Invoke(() => TxtGitElapsed.Text = "");
                }
                else if (ok)
                {
                    string elapsed = $"{(int)sw.Elapsed.TotalMinutes:D2}:{sw.Elapsed.Seconds:D2}";
                    SetOfficialStatus($"Git取得完了 ({elapsed})", "#2EA043");
                    Dispatcher.Invoke(() =>
                    {
                        TxtSrcFolder.Text = GitSongSourceService.SongsPath;
                        TxtGitElapsed.Text = $"完了 {elapsed}";
                    });
                }
                else
                {
                    SetOfficialStatus("Git取得失敗。ログを確認してください。", "#FF5252");
                    Dispatcher.Invoke(() => TxtGitElapsed.Text = "");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                elapsedCts.Cancel();
                await elapsedTask;
                AppendOfficialLog($"[EXCEPTION] {ex.Message}");
                SetOfficialStatus("Git取得中に例外が発生しました。", "#FF5252");
                Dispatcher.Invoke(() => TxtGitElapsed.Text = "");
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    BtnGitFetch.IsEnabled = true;
                    BtnGitCancel.IsEnabled = false;
                });
                _gitCts?.Dispose();
                _gitCts = null;
            }
        }


        /// <summary>Git取得キャンセル ボタン</summary>
        private void BtnGitCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_gitCts != null && !_gitCts.IsCancellationRequested)
            {
                _gitCts.Cancel();
                AppendOfficialLog("[INFO] キャンセルを要求しました...");
                BtnGitCancel.IsEnabled = false;
            }
        }

        /// <summary>Database取得/更新 ボタン</summary>
        private async void BtnDbFetch_Click(object sender, RoutedEventArgs e)
        {
            BtnDbFetch.IsEnabled = false;
            TxtOfficialLog.Text = "";
            SetOfficialStatus("ナムコ公式songlist取得中...", "#FFFF00");

            try
            {
                var songs = await OfficialSongListService.FetchAndSaveAsync(
                    line => AppendOfficialLog(line));

                SetOfficialStatus($"Database取得完了: {songs.Count} 曲", "#2EA043");
                RefreshDbCacheInfo();
            }
            catch (OperationCanceledException)
            {
                SetOfficialStatus("キャンセルされました。", "#888888");
            }
            catch (Exception ex)
            {
                AppendOfficialLog($"[EXCEPTION] {ex.Message}");
                SetOfficialStatus("Database取得中に例外が発生しました。", "#FF5252");
            }
            finally
            {
                Dispatcher.Invoke(() => BtnDbFetch.IsEnabled = true);
            }
        }

        /// <summary>曲データフォルダ 参照...</summary>
        private void BtnSrcBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "曲データフォルダ（TJAが格納されているフォルダ）を選択してください",
                InitialDirectory = string.IsNullOrEmpty(TxtSrcFolder.Text)
                    ? GitSongSourceService.SongsPath
                    : TxtSrcFolder.Text
            };
            if (dialog.ShowDialog() == true)
                TxtSrcFolder.Text = dialog.FolderName;
        }

        /// <summary>出力Songsフォルダ 参照...</summary>
        private void BtnDestBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "official.stfdb の出力先 Songsフォルダを選択してください",
                InitialDirectory = string.IsNullOrEmpty(TxtDestFolder.Text)
                    ? ConfigService.LoadLastPath()
                    : TxtDestFolder.Text
            };
            if (dialog.ShowDialog() == true)
                TxtDestFolder.Text = dialog.FolderName;
        }

        /// <summary>照合プレビュー ボタン</summary>
        private async void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            string srcFolder = TxtSrcFolder.Text.Trim();
            string destFolder = TxtDestFolder.Text.Trim();

            if (string.IsNullOrEmpty(srcFolder) || !Directory.Exists(srcFolder))
            {
                MessageBox.Show("曲データフォルダを選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Database 読み込み
            var database = OfficialSongListService.LoadFromCache();
            if (database.Count == 0)
            {
                var result = MessageBox.Show(
                    "Databaseがキャッシュされていません。\n今すぐ取得しますか？",
                    "Database未取得",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    BtnDbFetch_Click(sender, e);
                    return;
                }
                return;
            }

            BtnPreview.IsEnabled = false;
            TxtOfficialLog.Text = "";
            OfficialPreviewGrid.ItemsSource = null;
            _officialMatchResults.Clear();
            SetOfficialStatus("照合中...", "#FFFF00");

            try
            {
                List<OfficialMatchResult> results = null!;

                await Task.Run(() =>
                {
                    AppendOfficialLog($"[INFO] TJA読み込み: {srcFolder}");
                    var tjaInfos = OfficialSongMatcher.ReadTjaInfos(srcFolder,
                        line => AppendOfficialLog(line));

                    AppendOfficialLog($"[INFO] 照合開始: Database={database.Count}曲 / TJA={tjaInfos.Count}件");
                    results = OfficialSongMatcher.Match(database, tjaInfos, destFolder,
                        line => AppendOfficialLog(line));
                });

                _officialMatchResults = results;
                OfficialPreviewGrid.ItemsSource = _officialMatchResults;

                int ok = results.Count(r => r.Status == MatchStatus.OK);
                int warn = results.Count(r => r.Status == MatchStatus.Warning);
                int unmatched = results.Count(r => r.Status == MatchStatus.Unmatched);
                int dup = results.Count(r => r.Status == MatchStatus.Duplicate);

                string summary = $"OK: {ok}  Warning: {warn}  Unmatched: {unmatched}  Duplicate: {dup}  合計: {results.Count}";
                TxtOfficialSummary.Text = summary;
                SetOfficialStatus("照合完了", "#2EA043");
            }
            catch (Exception ex)
            {
                AppendOfficialLog($"[EXCEPTION] {ex.Message}");
                SetOfficialStatus("照合中に例外が発生しました。", "#FF5252");
            }
            finally
            {
                Dispatcher.Invoke(() => BtnPreview.IsEnabled = true);
            }
        }

        /// <summary>official.stfdb生成 ボタン</summary>
        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (_officialMatchResults.Count == 0)
            {
                MessageBox.Show("先に「照合プレビュー」を実行してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string destFolder = TxtDestFolder.Text.Trim();
            if (string.IsNullOrEmpty(destFolder))
            {
                MessageBox.Show("出力Songsフォルダを選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool includeWarnings = ChkIncludeWarning.IsChecked == true;

            int okCount = _officialMatchResults.Count(r => r.Status == MatchStatus.OK);
            int warnCount = _officialMatchResults.Count(r => r.Status == MatchStatus.Warning);
            string warnMsg = includeWarnings ? $"\nWarning({warnCount}件)も含めます。" : $"\nWarning({warnCount}件)は除外します。";

            var confirm = MessageBox.Show(
                $"official.stfdb を生成します。\n" +
                $"出力先: {destFolder}\n" +
                $"OK: {okCount} 曲{warnMsg}\n\n" +
                $"既存の official.stfdb は .bak にバックアップされます。\n続行しますか？",
                "生成確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            BtnGenerate.IsEnabled = false;
            SetOfficialStatus("生成中...", "#FFFF00");

            try
            {
                int generated = 0;

                await Task.Run(() =>
                {
                    generated = OfficialStfdbGenerator.GenerateAll(
                        _officialMatchResults,
                        destFolder,
                        includeWarnings,
                        line => AppendOfficialLog(line));
                });

                SetOfficialStatus($"生成完了: {generated} ジャンル", "#2EA043");
                MessageBox.Show($"{generated} ジャンルの official.stfdb を生成しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppendOfficialLog($"[EXCEPTION] {ex.Message}");
                SetOfficialStatus("生成中に例外が発生しました。", "#FF5252");
                MessageBox.Show($"生成中にエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Dispatcher.Invoke(() => BtnGenerate.IsEnabled = true);
            }
        }

        /// <summary>Database キャッシュ情報を更新する</summary>
        private void RefreshDbCacheInfo()
        {
            Dispatcher.Invoke(() =>
            {
                string? info = OfficialSongListService.GetCacheInfo();
                TxtDbCacheInfo.Text = info != null
                    ? $"Database最終取得: {info}"
                    : "Database未取得";
            });
        }

        #endregion
    }
}