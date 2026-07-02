using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ST_Fumen_Manager_WPF.Models
{
    public class CourseData : INotifyPropertyChanged
    {
        private string _level = "1";
        
        public string CourseName { get; set; } = "";
        public string RawCourseVal { get; set; } = "";
        public int Combo { get; set; }
        public bool HasGimmick { get; set; }
        public string GimmickText => HasGimmick ? "ギミックあり！" : "";

        public string Level
        {
            get => _level;
            set { _level = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class CourseItem : INotifyPropertyChanged
    {
        private string _title = "";
        private string _subtitle = "";
        private string _genre = "";
        private string _stage = "";
        private string _maker = "";
        private CourseData? _selectedCourse;

        public string FilePath { get; set; } = "";

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

        public string Stage
        {
            get => _stage;
            set { _stage = value; OnPropertyChanged(); }
        }

        public string Maker
        {
            get => _maker;
            set { _maker = value; OnPropertyChanged(); }
        }

        public ObservableCollection<CourseData> Courses { get; } = new();

        public CourseData? SelectedCourse
        {
            get => _selectedCourse;
            set
            {
                if (_selectedCourse != value)
                {
                    _selectedCourse = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CourseName));
                    OnPropertyChanged(nameof(Level));
                    OnPropertyChanged(nameof(Combo));
                    OnPropertyChanged(nameof(GimmickText));
                }
            }
        }

        // 後方互換・他の機能（ダイアログ等）へのブリッジプロパティ
        public string CourseName => SelectedCourse?.CourseName ?? Courses.FirstOrDefault()?.CourseName ?? "";
        public string RawCourseVal => SelectedCourse?.RawCourseVal ?? Courses.FirstOrDefault()?.RawCourseVal ?? "";
        public string Level => SelectedCourse?.Level ?? Courses.FirstOrDefault()?.Level ?? "";
        public int Combo => SelectedCourse?.Combo ?? Courses.FirstOrDefault()?.Combo ?? 0;
        public string GimmickText => SelectedCourse?.GimmickText ?? Courses.FirstOrDefault()?.GimmickText ?? "";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
