using Avalonia.Controls;
using FinanceTracker.Localization;

namespace FinanceTracker.Views
{
    public partial class StatsWindow : Window
    {
        public StatsWindow(int month, int year)
        {
            InitializeComponent();
            Title = Localizer.Instance["Stats_Title"];

            StatsContent.BackRequested += (_, _) => Close();
            StatsContent.SetPeriod(month, year);

            // При возврате фокуса в окно перечитываем данные (в главном окне могли измениться)
            Activated += (_, _) => StatsContent.Refresh();
        }
    }
}