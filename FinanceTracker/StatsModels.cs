using Avalonia.Media;

namespace FinanceTracker.Models
{
    public class StatsCategoryItem
    {
        public string Category { get; set; } = string.Empty;
        public string AmountText { get; set; } = string.Empty;
        public string PercentText { get; set; } = string.Empty;
        public IBrush Brush { get; set; } = Brushes.Gray;
    }

    public class StatsTopItem
    {
        public int Rank { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string AmountText { get; set; } = string.Empty;
        public IBrush RankBrush { get; set; } = Brushes.Gray;
    }
}