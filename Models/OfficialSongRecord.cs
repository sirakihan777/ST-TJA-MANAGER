using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ST_Fumen_Manager_WPF.Models
{
    /// <summary>
    /// ナムコ公式 songlist から取得した1曲分のデータ
    /// </summary>
    public class OfficialSongRecord
    {
        /// <summary>公式ジャンル名 (例: "ポップス")</summary>
        public string Genre { get; set; } = "";

        /// <summary>ジャンル内の公式順番 (0始まり)</summary>
        public int Order { get; set; }

        /// <summary>公式TITLE</summary>
        public string Title { get; set; } = "";

        /// <summary>公式SUBTITLE</summary>
        public string Subtitle { get; set; } = "";

        /// <summary>正規化済みTITLE (照合用)</summary>
        public string NormalizedTitle { get; set; } = "";

        /// <summary>正規化済みSUBTITLE (照合用)</summary>
        public string NormalizedSubtitle { get; set; } = "";
    }

    /// <summary>
    /// 照合結果の状態
    /// </summary>
    public enum MatchStatus
    {
        OK,
        Warning,
        Unmatched,
        Duplicate,
        Error
    }

    /// <summary>
    /// 照合方式
    /// </summary>
    public enum MatchMethod
    {
        ExactTitleExactSubtitle,
        ExactTitleNormalizedSubtitle,
        NormalizedTitleNormalizedSubtitle,
        NormalizedTitleEmptySubtitle,
        LooseTitle,
        SubtitleMismatch,
        Unmatched,
        Duplicate,
        Error
    }

    /// <summary>
    /// 照合プレビュー用の1行データ (INotifyPropertyChanged 実装でDataGridにバインド)
    /// </summary>
    public class OfficialMatchResult : INotifyPropertyChanged
    {
        private MatchStatus _status;
        private string _statusText = "";

        // --- 公式Database側 ---
        public string OfficialGenre { get; set; } = "";
        public int OfficialOrder { get; set; }
        public string OfficialTitle { get; set; } = "";
        public string OfficialSubtitle { get; set; } = "";

        // --- TJA側 ---
        public string TjaTitle { get; set; } = "";
        public string TjaSubtitle { get; set; } = "";
        public string TjaPath { get; set; } = "";

        // --- 結果 ---
        public string DestinationStfdb { get; set; } = "";
        public string MatchMethodText { get; set; } = "";
        public string Message { get; set; } = "";

        // --- 分類結果 ---
        public string Classification { get; set; } = "";
        public string ClassificationReason { get; set; } = "";
        public string ClassificationSource { get; set; } = "";
        public string Attributes { get; set; } = "";

        public MatchStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                _statusText = value.ToString();
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public string StatusText
        {
            get => _statusText;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
