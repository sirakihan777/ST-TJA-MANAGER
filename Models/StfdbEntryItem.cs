using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ST_Fumen_Manager_WPF.Models
{
    public class StfdbEntryItem : INotifyPropertyChanged
    {
        private int _rowNumber;
        private string _severity = "OK";
        private string _message = "";
        private string _title = "";
        private string _genre = "";
        private string _course = "";
        private string _level = "";
        private string _editedPath = "";

        public string StfdbFilePath { get; set; } = "";
        public int LineIndex { get; set; }
        public string RawPath { get; set; } = "";
        public string ResolvedTjaPath { get; set; } = "";
        public bool Exists { get; set; }

        public int RowNumber
        {
            get => _rowNumber;
            set { _rowNumber = value; OnPropertyChanged(); }
        }

        public string Severity
        {
            get => _severity;
            set { _severity = value; OnPropertyChanged(); }
        }

        public string Message
        {
            get => _message;
            set { _message = value; OnPropertyChanged(); }
        }

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string Genre
        {
            get => _genre;
            set { _genre = value; OnPropertyChanged(); }
        }

        public string Course
        {
            get => _course;
            set { _course = value; OnPropertyChanged(); }
        }

        public string Level
        {
            get => _level;
            set { _level = value; OnPropertyChanged(); }
        }

        public string EditedPath
        {
            get => _editedPath;
            set { _editedPath = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
