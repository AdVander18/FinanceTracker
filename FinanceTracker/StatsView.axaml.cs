using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using FinanceTracker.Interop;
using FinanceTracker.Localization;
using FinanceTracker.Models;

namespace FinanceTracker.Views
{
    public partial class StatsView : UserControl
    {
        private static readonly IBrush[] Palette =
        {
            new SolidColorBrush(Color.Parse("#E67E22")),
            new SolidColorBrush(Color.Parse("#3498DB")),
            new SolidColorBrush(Color.Parse("#27AE60")),
            new SolidColorBrush(Color.Parse("#9B59B6")),
            new SolidColorBrush(Color.Parse("#E74C3C")),
            new SolidColorBrush(Color.Parse("#16A085")),
            new SolidColorBrush(Color.Parse("#F39C12")),
            new SolidColorBrush(Color.Parse("#2ECC71")),
            new SolidColorBrush(Color.Parse("#8E44AD")),
            new SolidColorBrush(Color.Parse("#1ABC9C")),
            new SolidColorBrush(Color.Parse("#D35400")),
            new SolidColorBrush(Color.Parse("#C0392B")),
        };

        private static readonly IBrush RankGold = new SolidColorBrush(Color.Parse("#F1C40F"));
        private static readonly IBrush RankSilver = new SolidColorBrush(Color.Parse("#95A5A6"));
        private static readonly IBrush RankBronze = new SolidColorBrush(Color.Parse("#D35400"));

        private readonly List<ExpenseItem> _all = new();
        private readonly ObservableCollection<StatsCategoryItem> _legendItems = new();
        private readonly ObservableCollection<StatsTopItem> _topItems = new();

        public event EventHandler? BackRequested;

        public StatsView()
        {
            InitializeComponent();

            foreach (var name in GetMonthNames())
                MonthComboBox.Items.Add(name);
            for (int year = 2000; year <= 2100; year++)
                YearComboBox.Items.Add(year);

            MonthComboBox.SelectedIndex = DateTime.Now.Month - 1;
            YearComboBox.SelectedItem = DateTime.Now.Year;

            LegendItems.ItemsSource = _legendItems;
            TopItems.ItemsSource = _topItems;

            Refresh();
        }

        /// <summary>Задаёт период (без сброса текущей вкладки) и обновляет данные.</summary>
        public void SetPeriod(int month, int year)
        {
            if (month < 1) month = 1;
            if (month > 12) month = 12;
            MonthComboBox.SelectedIndex = month - 1;
            YearComboBox.SelectedItem = year;
            Refresh();
        }

        /// <summary>Перечитывает данные из хранилища и перестраивает все секции.</summary>
        public void Refresh()
        {
            _all.Clear();
            _all.AddRange(NativeMethods.GetAllExpenses());
            Rebuild();
        }

        private void OnBackClick(object? sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        private void OnPeriodChanged(object? sender, SelectionChangedEventArgs e)
            => Rebuild();

        private void Rebuild()
        {
            int month = MonthComboBox.SelectedIndex >= 0 ? MonthComboBox.SelectedIndex + 1 : DateTime.Now.Month;
            int year = YearComboBox.SelectedItem is int y ? y : _selectedYearFallback();

            RebuildCategories(month, year);
            RebuildYearSummary(year);
            RebuildTopExpenses(month, year);
        }

        private static int _selectedYearFallback() => DateTime.Now.Year;

        // ---------------- Категории (круговая диаграмма) ----------------

        private void RebuildCategories(int month, int year)
        {
            _legendItems.Clear();
            PieCanvas.Children.Clear();

            var totals = new Dictionary<string, double>();
            foreach (var exp in _all)
            {
                if (!MatchesPeriod(exp, month, year)) continue;
                totals.TryGetValue(exp.Category, out double current);
                totals[exp.Category] = current + exp.Amount;
            }

            double total = totals.Values.Sum();
            bool hasData = total > 0 && totals.Count > 0;

            CategoriesEmpty.IsVisible = !hasData;
            PieHost.IsVisible = hasData;

            if (!hasData) return;

            var culture = Localizer.Instance.Culture;
            foreach (var kv in totals.OrderByDescending(x => x.Value))
            {
                _legendItems.Add(new StatsCategoryItem
                {
                    Category = kv.Key,
                    AmountText = kv.Value.ToString("N2", culture) + " ₽",
                    PercentText = (kv.Value / total * 100.0).ToString("0.0", culture) + "%",
                    Brush = Palette[_legendItems.Count % Palette.Length]
                });
            }

            const double cx = 130, cy = 130, r = 126;
            var ordered = totals.OrderByDescending(x => x.Value).ToList();

            if (ordered.Count == 1)
            {
                var full = new Ellipse { Width = r * 2, Height = r * 2, Fill = Palette[0] };
                Canvas.SetLeft(full, cx - r);
                Canvas.SetTop(full, cy - r);
                PieCanvas.Children.Add(full);
            }
            else
            {
                double angle = -90.0;
                int colorIndex = 0;
                var separator = GetBrush("CardBackgroundBrush") ?? Brushes.Transparent;
                foreach (var kv in ordered)
                {
                    double sweep = kv.Value / total * 360.0;
                    PieCanvas.Children.Add(BuildWedge(cx, cy, r, angle, angle + sweep, Palette[colorIndex % Palette.Length], separator));
                    colorIndex++;
                    angle += sweep;
                }
            }

            // Отверстие пончика
            var hole = new Ellipse
            {
                Width = 150,
                Height = 150,
                Fill = GetBrush("CardBackgroundBrush") ?? Brushes.White
            };
            Canvas.SetLeft(hole, cx - 75);
            Canvas.SetTop(hole, cy - 75);
            PieCanvas.Children.Add(hole);

            // Итого в центре
            var totalText = new TextBlock
            {
                Text = Localizer.Instance["Stats_Total"] + "\n" + total.ToString("N2", culture) + " ₽",
                Foreground = GetBrush("ItemAmountForegroundBrush") ?? Brushes.Gray,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Width = 140
            };
            Canvas.SetLeft(totalText, cx - 70);
            Canvas.SetTop(totalText, cy - 32);
            PieCanvas.Children.Add(totalText);
        }

        private static Path BuildWedge(double cx, double cy, double r, double startAngle, double endAngle, IBrush fill, IBrush separator)
        {
            var startPt = PointOnCircle(cx, cy, r, startAngle);
            var endPt = PointOnCircle(cx, cy, r, endAngle);
            bool large = (endAngle - startAngle) > 180.0;

            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new Point(cx, cy), true);
                ctx.LineTo(startPt);
                ctx.ArcTo(endPt, new Size(r, r), 0.0, large, SweepDirection.Clockwise);
                ctx.EndFigure(true);
            }

            return new Path
            {
                Data = geom,
                Fill = fill,
                Stroke = separator,
                StrokeThickness = 2
            };
        }

        private static Point PointOnCircle(double cx, double cy, double r, double angleDeg)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }

        // ---------------- Сводка за год ----------------

        private void RebuildYearSummary(int year)
        {
            double[] monthly = new double[12];
            int count = 0;

            foreach (var exp in _all)
            {
                if (!DateTime.TryParse(exp.Date, out var date)) continue;
                if (date.Year != year) continue;
                monthly[date.Month - 1] += exp.Amount;
                count++;
            }

            double yearTotal = monthly.Sum();
            double avg = yearTotal / 12.0;
            var culture = Localizer.Instance.Culture;

            YearTotalValue.Text = yearTotal.ToString("N2", culture) + " ₽";
            YearAvgValue.Text = avg.ToString("N2", culture) + " ₽";
            YearRecordsValue.Text = count.ToString(culture);

            int maxIdx = -1, minIdx = -1;
            for (int i = 0; i < 12; i++)
            {
                if (monthly[i] <= 0) continue;
                if (maxIdx < 0 || monthly[i] > monthly[maxIdx]) maxIdx = i;
                if (minIdx < 0 || monthly[i] < monthly[minIdx]) minIdx = i;
            }

            if (maxIdx >= 0)
            {
                YearMaxValue.Text = monthly[maxIdx].ToString("N2", culture) + " ₽";
                YearMaxLabel.Text = GetMonthNames()[maxIdx];
            }
            else
            {
                YearMaxValue.Text = "—";
                YearMaxLabel.Text = string.Empty;
            }

            if (minIdx >= 0)
            {
                YearMinValue.Text = monthly[minIdx].ToString("N2", culture) + " ₽";
                YearMinLabel.Text = GetMonthNames()[minIdx];
            }
            else
            {
                YearMinValue.Text = "—";
                YearMinLabel.Text = string.Empty;
            }

            YearEmpty.IsVisible = count == 0;

            // Помесячные столбики
            YearRowsPanel.Children.Clear();
            double maxMonth = monthly.Max();
            var names = GetMonthNames();

            for (int i = 0; i < 12; i++)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                var nameTb = new TextBlock
                {
                    Text = names[i],
                    MinWidth = 84,
                    FontSize = 13,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = GetBrush("ItemNameForegroundBrush")
                };
                Grid.SetColumn(nameTb, 0);
                row.Children.Add(nameTb);

                double ratio = maxMonth > 0 ? monthly[i] / maxMonth : 0.0;
                double barPart = Math.Max(1, ratio * 1000);
                double restPart = Math.Max(1, 1000 - barPart);

                var barHost = new Grid
                {
                    Height = 14,
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                barHost.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(barPart, GridUnitType.Star)));
                barHost.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(restPart, GridUnitType.Star)));

                var bar = new Border
                {
                    Background = Palette[i % Palette.Length],
                    CornerRadius = new CornerRadius(7),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                };
                if (monthly[i] <= 0) bar.IsVisible = false;
                Grid.SetColumn(bar, 0);
                barHost.Children.Add(bar);

                Grid.SetColumn(barHost, 1);
                row.Children.Add(barHost);

                var valueTb = new TextBlock
                {
                    Text = monthly[i] > 0 ? monthly[i].ToString("N2", culture) + " ₽" : "0",
                    MinWidth = 92,
                    FontSize = 13,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = GetBrush("ItemAmountForegroundBrush")
                };
                Grid.SetColumn(valueTb, 2);
                row.Children.Add(valueTb);

                YearRowsPanel.Children.Add(row);
            }
        }

        // ---------------- Топ расходов ----------------

        private void RebuildTopExpenses(int month, int year)
        {
            _topItems.Clear();

            var filtered = _all
                .Where(x => MatchesPeriod(x, month, year))
                .OrderByDescending(x => x.Amount)
                .Take(10)
                .ToList();

            TopEmpty.IsVisible = filtered.Count == 0;
            TopScroll.IsVisible = filtered.Count > 0;

            if (filtered.Count == 0) return;

            var culture = Localizer.Instance.Culture;
            for (int i = 0; i < filtered.Count; i++)
            {
                var e = filtered[i];
                _topItems.Add(new StatsTopItem
                {
                    Rank = i + 1,
                    Name = e.Name,
                    Category = e.Category,
                    AmountText = e.Amount.ToString("N2", culture) + " ₽",
                    RankBrush = i == 0 ? RankGold : i == 1 ? RankSilver : i == 2 ? RankBronze : (GetBrush("SubtitleForegroundBrush") ?? Brushes.Gray)
                });
            }
        }

        // ---------------- Вспомогательное ----------------

        private static bool MatchesPeriod(ExpenseItem e, int month, int year)
        {
            if (!DateTime.TryParse(e.Date, out var date)) return false;
            return date.Month == month && date.Year == year;
        }

        private static string[] GetMonthNames()
        {
            var dtf = Localizer.Instance.Culture.DateTimeFormat;
            return Enumerable.Range(1, 12).Select(i =>
            {
                string name = dtf.GetMonthName(i);
                return string.IsNullOrEmpty(name) ? name : char.ToUpper(name[0]) + name.Substring(1);
            }).ToArray();
        }

        private IBrush? GetBrush(string resourceKey)
        {
            if (this.TryFindResource(resourceKey, out var value) && value is IBrush b)
                return b;
            return null;
        }
    }
}