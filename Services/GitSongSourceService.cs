using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ST_Fumen_Manager_WPF.Services
{
    /// <summary>
    /// ESE曲データのGitクローン・更新を管理するサービス。
    /// 操作対象は必ず専用キャッシュフォルダのみ。
    /// ユーザーの本番Songsフォルダには一切触れない。
    /// </summary>
    public static class GitSongSourceService
    {
        // --- パス定義 ---

        /// <summary>
        /// キャッシュルート: %APPDATA%\ST-TJA-MANAGER\Cache\ESE
        ///
        /// 実際のパス例: C:\Users\morit\AppData\Roaming\ST-TJA-MANAGER\Cache\ESE
        /// </summary>
        public static string CacheRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ST-TJA-MANAGER", "Cache", "ESE");

        /// <summary>
        /// Songs フォルダ: %APPDATA%\ST-TJA-MANAGER\Cache\ESE\Songs
        ///
        /// git clone の出力先。ユーザーの本番 Songs とは別の専用キャッシュ。
        /// </summary>
        public static string SongsPath => Path.Combine(CacheRoot, "Songs");

        private const string RemoteUrl = "https://ese.tjadataba.se/ESE/ESE.git";

        // --- Songsフォルダの状態 ---

        public enum SongsState
        {
            /// <summary>Songs フォルダが存在しない → clone が必要</summary>
            NotExist,
            /// <summary>Songs フォルダが存在し、git rev-parse で有効と確認できた → fetch+reset が可能</summary>
            CloneExists,
            /// <summary>Songs フォルダは存在するが .git がない、または git repo として壊れている → 要確認</summary>
            PartialOrBroken
        }

        /// <summary>
        /// 現在の Songs フォルダの状態を返す。
        /// .git フォルダの存在だけでなく、git rev-parse --git-dir で実際に検証する。
        /// </summary>
        public static SongsState CheckSongsState()
        {
            if (!Directory.Exists(SongsPath)) return SongsState.NotExist;
            if (!Directory.Exists(Path.Combine(SongsPath, ".git"))) return SongsState.PartialOrBroken;

            // .git は存在するが中身が壊れていないか実際に git で確認
            if (!IsValidGitRepo(SongsPath)) return SongsState.PartialOrBroken;

            return SongsState.CloneExists;
        }

        /// <summary>
        /// 指定パスが有効な git リポジトリかどうかを git rev-parse で確認する。
        /// </summary>
        private static bool IsValidGitRepo(string path)
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "rev-parse --git-dir",
                        WorkingDirectory = path,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gitがインストールされているか確認する。
        /// </summary>
        public static bool IsGitInstalled()
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Songs フォルダ (%APPDATA%... 配下のみ) を削除する。
        /// 安全チェックを行い、キャッシュ外であれば絶対に削除しない。
        /// </summary>
        public static void DeletePartialSongsFolder(Action<string> log)
        {
            string fullSongs = Path.GetFullPath(SongsPath);
            string fullCache = Path.GetFullPath(CacheRoot);
            if (!fullSongs.StartsWith(fullCache, StringComparison.OrdinalIgnoreCase))
            {
                log("[ERROR] 安全チェック失敗: 削除対象がキャッシュ外のため中止しました。");
                return;
            }

            try
            {
                Directory.Delete(SongsPath, true);
                log($"[OK] Songs フォルダを削除しました: {SongsPath}");
            }
            catch (Exception ex)
            {
                log($"[ERROR] Songs フォルダの削除に失敗しました: {ex.Message}");
            }
        }

        /// <summary>
        /// ESEをクローンまたは更新する。
        /// ログはコールバックで逐次通知する。
        /// 進捗 (\r 区切りも含む) は onProgress コールバックで最新1行を通知する。
        /// 操作は必ず SongsPath (専用キャッシュ) のみに限定する。
        /// </summary>
        /// <param name="log">確定ログ行の通知コールバック</param>
        /// <param name="onProgress">最新進捗の通知コールバック (ステータス欄用)</param>
        /// <param name="ct">キャンセルトークン</param>
        /// <returns>成功なら true</returns>
        public static async Task<bool> FetchOrUpdateAsync(
            Action<string> log,
            Action<string>? onProgress = null,
            CancellationToken ct = default)
        {
            if (!IsGitInstalled())
            {
                log("[ERROR] Gitがインストールされていません。Git for Windowsをインストールしてください。");
                return false;
            }

            log($"[INFO] キャッシュ先: {SongsPath}");

            Directory.CreateDirectory(CacheRoot);

            var state = CheckSongsState();
            log($"[INFO] 状態チェック: {state}");

            if (state == SongsState.PartialOrBroken)
            {
                log("[ERROR] Songs フォルダが中途半端または破損した状態です。先に削除してから再実行してください。");
                return false;
            }

            if (state == SongsState.NotExist)
            {
                return await RunCloneAsync(log, onProgress, ct);
            }
            else // CloneExists
            {
                return await RunFetchResetAsync(log, onProgress, ct);
            }
        }

        // --- clone / fetch+reset ---

        private static async Task<bool> RunCloneAsync(
            Action<string> log,
            Action<string>? onProgress,
            CancellationToken ct)
        {
            log("[INFO] ESEリポジトリが未取得です。shallow clone を開始します...");
            log("[INFO] 初回cloneはリポジトリのサイズによって数分〜10分以上かかる場合があります。");
            log($"[INFO] コマンド: git clone --depth=1 --no-tags --single-branch {RemoteUrl} Songs");
            log($"[INFO] 実行場所: {CacheRoot}");

            bool ok = await RunGitWithProgressAsync(
                CacheRoot,
                $"clone --depth=1 --no-tags --single-branch {RemoteUrl} Songs",
                log, onProgress, ct);

            if (ct.IsCancellationRequested)
            {
                log("[INFO] キャンセルされました。不完全な Songs フォルダが残っている場合があります。");
                log($"[INFO] 次回実行時に {SongsPath} の削除確認ダイアログが表示されます。");
                return false;
            }

            if (!ok)
            {
                log("[ERROR] clone に失敗しました。ネットワーク接続を確認してください。");
                return false;
            }

            log("[OK] clone 完了。");
            return true;
        }

        private static async Task<bool> RunFetchResetAsync(
            Action<string> log,
            Action<string>? onProgress,
            CancellationToken ct)
        {
            log("[INFO] ESEリポジトリが存在します。fetch + reset で更新します...");
            log($"[INFO] 実行場所: {SongsPath}");

            // fetch: origin/HEAD ではなく FETCH_HEAD を使うため origin を明示する
            log("[INFO] コマンド: git fetch origin --depth=1");
            bool fetchOk = await RunGitWithProgressAsync(
                SongsPath,
                "fetch origin --depth=1",
                log, onProgress, ct);

            if (ct.IsCancellationRequested)
            {
                log("[INFO] キャンセルされました。");
                return false;
            }

            if (!fetchOk)
            {
                log("[ERROR] fetch に失敗しました。ネットワーク接続を確認してください。");
                return false;
            }

            // reset: FETCH_HEAD を使う (origin/HEAD が未設定でも動く)
            log("[INFO] コマンド: git reset --hard FETCH_HEAD");
            bool resetOk = await RunGitWithProgressAsync(
                SongsPath,
                "reset --hard FETCH_HEAD",
                log, onProgress, ct);

            if (ct.IsCancellationRequested)
            {
                log("[INFO] キャンセルされました。");
                return false;
            }

            if (!resetOk)
            {
                log("[ERROR] reset に失敗しました。");
                return false;
            }

            log("[OK] 更新完了。");
            return true;
        }

        // --- 内部実装 ---

        /// <summary>
        /// Gitコマンドを非同期実行し、stdout/stderr を char[] バッファで読み取る。
        /// \r 区切りの Git 進捗行も拾って onProgress に通知する。
        /// \n または \r\n で終わる行はログに出す。
        /// workingDir は必ず CacheRoot 以下であることを検証する。
        /// </summary>
        private static async Task<bool> RunGitWithProgressAsync(
            string workingDir,
            string arguments,
            Action<string> log,
            Action<string>? onProgress,
            CancellationToken ct)
        {
            // 安全性確認: 操作先が必ず CacheRoot 配下であること
            string fullWorking = Path.GetFullPath(workingDir);
            string fullCache = Path.GetFullPath(CacheRoot);
            if (!fullWorking.StartsWith(fullCache, StringComparison.OrdinalIgnoreCase))
            {
                log($"[ERROR] 安全チェック失敗: '{workingDir}' は専用キャッシュ外のパスです。操作を中止しました。");
                return false;
            }

            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                // GIT_TERMINAL_PROMPT=0 → 認証プロンプトで固まらないようにする
                psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

                process = new Process { StartInfo = psi };
                process.Start();

                var proc = process; // ラムダキャプチャ用

                // stdout と stderr を並行して非同期読み取り
                var stdoutTask = ReadStreamWithProgressAsync(proc.StandardOutput, log, onProgress, ct);
                var stderrTask = ReadStreamWithProgressAsync(proc.StandardError, log, onProgress, ct);

                // プロセス終了を非同期待機 (最大10分)
                var exitTask = Task.Run(() => proc.WaitForExit(600_000), CancellationToken.None);

                // キャンセル監視タスク
                var cancelWatchTask = Task.Run(async () =>
                {
                    while (true)
                    {
                        bool cancelled = ct.IsCancellationRequested;
                        bool exited = false;
                        try { exited = proc.HasExited; } catch { exited = true; }

                        if (cancelled && !exited)
                        {
                            // 2段階Kill: entireProcessTree → 単体Kill
                            try
                            {
                                proc.Kill(entireProcessTree: true);
                            }
                            catch (System.ComponentModel.Win32Exception)
                            {
                                try { proc.Kill(); } catch { }
                            }
                            catch (InvalidOperationException)
                            {
                                // Kill前にプロセスが終了したレース → 無視
                            }
                            catch { }
                            break;
                        }

                        if (exited) break;

                        await Task.Delay(200, CancellationToken.None);
                    }
                }, CancellationToken.None);

                await Task.WhenAll(stdoutTask, stderrTask, exitTask, cancelWatchTask);

                if (ct.IsCancellationRequested)
                    return false;

                try
                {
                    return process.ExitCode == 0;
                }
                catch
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                log($"[ERROR] Git実行中に例外が発生しました: {ex.Message}");
                return false;
            }
            finally
            {
                try { process?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// StreamReader を char[] バッファで読み取り、\r / \n / \r\n で区切って通知する。
        /// \r で終わる行 (Git進捗の上書き出力) は onProgress に通知する。
        /// \n で終わる行はログに出す。
        /// </summary>
        private static async Task ReadStreamWithProgressAsync(
            StreamReader reader,
            Action<string> log,
            Action<string>? onProgress,
            CancellationToken ct)
        {
            var buf = new char[4096];
            var lineBuffer = new StringBuilder();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                    if (read == 0) break; // EOF

                    for (int i = 0; i < read; i++)
                    {
                        char c = buf[i];

                        if (c == '\r')
                        {
                            bool isCrLf = (i + 1 < read && buf[i + 1] == '\n');
                            if (isCrLf)
                            {
                                i++; // \n をスキップ
                                string line = lineBuffer.ToString().TrimEnd();
                                lineBuffer.Clear();
                                if (!string.IsNullOrWhiteSpace(line))
                                    log($"  {line}");
                            }
                            else
                            {
                                // \r のみ → Git進捗の上書き行
                                string progress = lineBuffer.ToString().TrimEnd();
                                lineBuffer.Clear();
                                if (!string.IsNullOrWhiteSpace(progress))
                                    onProgress?.Invoke(progress);
                            }
                        }
                        else if (c == '\n')
                        {
                            string line = lineBuffer.ToString().TrimEnd();
                            lineBuffer.Clear();
                            if (!string.IsNullOrWhiteSpace(line))
                                log($"  {line}");
                        }
                        else
                        {
                            lineBuffer.Append(c);
                        }
                    }

                    // バッファ末尾に残った途中行を進捗として表示
                    if (lineBuffer.Length > 0)
                    {
                        string partial = lineBuffer.ToString().TrimEnd();
                        if (!string.IsNullOrWhiteSpace(partial))
                            onProgress?.Invoke(partial);
                    }
                }

                // 残り
                if (lineBuffer.Length > 0)
                {
                    string remaining = lineBuffer.ToString().TrimEnd();
                    if (!string.IsNullOrWhiteSpace(remaining))
                        log($"  {remaining}");
                }
            }
            catch
            {
                // ストリームが閉じられた場合は正常終了扱い
            }
        }
    }
}
