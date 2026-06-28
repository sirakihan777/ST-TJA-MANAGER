using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using ST_Fumen_Manager_WPF.Models;

namespace ST_Fumen_Manager_WPF
{
    public class CandidateItem
    {
        public bool IsAlreadyAdded { get; set; }
        public string StatusText => IsAlreadyAdded ? "[追加済み]" : "[未追加]";
        public string Title { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Course { get; set; } = "";
        public string Level { get; set; } = "";
        public string FilePath { get; set; } = "";
    }

    public partial class StfdbAddTjaDialog : Window
    {
        public List<CandidateItem> SelectedCandidates { get; private set; } = new();

        public StfdbAddTjaDialog(IEnumerable<CourseItem> loadedCourses, IEnumerable<string> existingPaths)
        {
            InitializeComponent();

            var existingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in existingPaths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                {
                    try { existingSet.Add(Path.GetFullPath(p)); } catch { }
                }
            }

            // 同じファイルパスが複数コースで重複してリスト表示されないように、ファイル単位でグルーピング
            var candidates = new List<CandidateItem>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in loadedCourses)
            {
                if (string.IsNullOrEmpty(c.FilePath))
                    continue;

                string normPath;
                try { normPath = Path.GetFullPath(c.FilePath); } catch { normPath = c.FilePath; }

                if (seenFiles.Contains(normPath))
                    continue;

                seenFiles.Add(normPath);

                bool added = existingSet.Contains(normPath);
                candidates.Add(new CandidateItem
                {
                    IsAlreadyAdded = added,
                    Title = c.Title,
                    Genre = c.Genre,
                    Course = c.CourseName,
                    Level = c.Level,
                    FilePath = c.FilePath
                });
            }

            // 未追加のものを上に、追加済みを下にソート
            candidates = candidates.OrderBy(x => x.IsAlreadyAdded).ThenBy(x => x.Title).ToList();
            CandidatesDataGrid.ItemsSource = candidates;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            SelectedCandidates = CandidatesDataGrid.SelectedItems.Cast<CandidateItem>().ToList();
            if (SelectedCandidates.Count == 0)
            {
                MessageBox.Show("項目が選択されていません。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
