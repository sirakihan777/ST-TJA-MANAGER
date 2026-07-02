using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ST_Fumen_Manager_WPF.Models
{
    public class StmdEntryItem : INotifyPropertyChanged
    {
        private int _rowNumber;
        private string _tjaPath = "";
        private string _title = "";
        private string _subtitle = "";
        private string _genre = "";
        private string _stage = "";
        private int _order;
        private string _message = "";

        public string ResolvedTjaPath { get; set; } = "";
        public bool Exists { get; set; }

        public int RowNumber
        {
            get => _rowNumber;
            set { _rowNumber = value; OnPropertyChanged(); }
        }

        public string TjaPath
        {
            get => _tjaPath;
            set { _tjaPath = value; OnPropertyChanged(); }
        }

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

        public int Order
        {
            get => _order;
            set { _order = value; OnPropertyChanged(); }
        }

        public string Message
        {
            get => _message;
            set { _message = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
