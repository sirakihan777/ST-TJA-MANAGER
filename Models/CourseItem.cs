using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ST_Fumen_Manager_WPF.Models
{
    public class CourseItem : INotifyPropertyChanged
    {
        private string _title = "";
        private string _subtitle = "";
        private string _genre = "";
        private string _level = "";
        private string _maker = "";

        public string FilePath { get; set; } = "";
        public string RawCourseVal { get; set; } = "";
        public string CourseName { get; set; } = "";
        public int Combo { get; set; }
        public bool HasGimmick { get; set; }
        public string GimmickText => HasGimmick ? "ギミックあり！" : "";

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string Subtitle
        {
            get => _subtitle;
            set { _subtitle = value; OnPropertyChanged(); }
        }

        public string Genre
        {
            get => _genre;
            set { _genre = value; OnPropertyChanged(); }
        }

        public string Level
        {
            get => _level;
            set { _level = value; OnPropertyChanged(); }
        }

        public string Maker
        {
            get => _maker;
            set { _maker = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
