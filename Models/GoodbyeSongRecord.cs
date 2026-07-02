namespace ST_Fumen_Manager_WPF.Models
{
    /// <summary>
    /// サヨナラ曲（新筐体で配信終了となった曲）の情報。
    /// </summary>
    public class GoodbyeSongRecord
    {
        /// <summary>ジャンル</summary>
        public string Genre { get; set; } = "";

        /// <summary>曲名</summary>
        public string Title { get; set; } = "";

        /// <summary>サブタイトル</summary>
        public string Subtitle { get; set; } = "";

        /// <summary>正規化済み曲名</summary>
        public string NormalizedTitle { get; set; } = "";

        /// <summary>正規化済みサブタイトル</summary>
        public string NormalizedSubtitle { get; set; } = "";

        /// <summary>サヨナラ日（例: 20/4/22）</summary>
        public string GoodbyeDate { get; set; } = "";
    }
}
