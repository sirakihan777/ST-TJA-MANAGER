namespace ST_Fumen_Manager_WPF.Models
{
    /// <summary>
    /// 家庭用（CS）版作品に収録されている曲の情報。
    /// </summary>
    public class ConsumerSongRecord
    {
        /// <summary>作品名（例: PS4 1, NS2）</summary>
        public string WorkTitle { get; set; } = "";

        /// <summary>ジャンル（例: ポップス, アニメ）</summary>
        public string Genre { get; set; } = "";

        /// <summary>曲名</summary>
        public string Title { get; set; } = "";

        /// <summary>サブタイトル</summary>
        public string Subtitle { get; set; } = "";

        /// <summary>正規化済み曲名</summary>
        public string NormalizedTitle { get; set; } = "";

        /// <summary>正規化済みサブタイトル</summary>
        public string NormalizedSubtitle { get; set; } = "";

        /// <summary>隠し曲の場合 true</summary>
        public bool IsHidden { get; set; }

        /// <summary>DLC 曲の場合 true</summary>
        public bool IsDlc { get; set; }

        /// <summary>wikiwiki 上の元URL</summary>
        public string SourceUrl { get; set; } = "";
    }
}
