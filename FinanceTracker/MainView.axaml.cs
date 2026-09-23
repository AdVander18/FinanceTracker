using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Selection;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FinanceTracker.Interop;
using FinanceTracker.Localization;
using FinanceTracker.Models;
using FinanceTracker.Services;

namespace FinanceTracker.Views
{
    public partial class MainView : UserControl
    {
        private TaskCompletionSource<bool>? _messageTcs;
        // РЎРёРЅС…СЂРѕРЅРёР·Р°С†РёСЏ
        private bool _syncPanelVisible = false;
        private readonly ObservableCollection<DiscoveredDevice> _discoveredDevices = new();
        private CancellationTokenSource? _serverCts;
        private CancellationTokenSource? _discoveryCts;
        private TranslateTransform? _settingsPanelTransform;
        private readonly ObservableCollection<ExpenseItem> _expenses = new();
        private readonly List<ExpenseItem> _allExpenses = new();
        private readonly string _dataFilePath;
        private int _editingIndex = -1;
        private ExpenseItem? _pendingDeleteItem;
        private bool _selectionActive;
        private bool _settingsPanelVisible = false;
        private int _selectedMonth;
        private int _selectedYear;
        private string[] _monthNames = GetLocalizedMonthNames();

        // Р’ РєРѕРЅСЃС‚СЂСѓРєС‚РѕСЂРµ, РІРјРµСЃС‚Рѕ СЃС‚Р°СЂРѕР№ РёРЅРёС†РёР°Р»РёР·Р°С†РёРё _monthNames:

        // РќРѕРІС‹Рµ РїРѕР»СЏ РґР»СЏ РєР°С‚РµРіРѕСЂРёР№
        private ObservableCollection<string> _categories = new();
        private string? _renamingCategory = null; // РµСЃР»Рё РЅРµ null, С‚Рѕ РёРґС‘С‚ РїРµСЂРµРёРјРµРЅРѕРІР°РЅРёРµ
        private CancellationTokenSource? _wastedCts;
        private const string HideWastedSettingKey = "hide_wasted_text";
        private const string ThemeSettingKey = "theme";
        private const string LanguageSettingKey = "language";
        private enum SortMode { None, Name, Amount, Date }
        private SortMode _sortMode = SortMode.None;
        private bool _sortAscending = true;
#if ANDROID
        private StatsView? _statsView;
#endif

        public MainView()
        {
            InitializeComponent();

#if ANDROID
            // РќР° Android С‚РµРЅСЊ РєР°Р¶РґРѕР№ РєР°СЂС‚РѕС‡РєРё РґРѕСЂРѕРіРѕ РѕС‚СЂРёСЃРѕРІС‹РІР°РµС‚СЃСЏ РїСЂРё РїСЂРѕРєСЂСѓС‚РєРµ вЂ”
            // РґРѕР±Р°РІР»СЏРµРј СЃС‚РёР»СЊ РїРѕР·Р¶Рµ XAML-СЃС‚РёР»РµР№, С‡С‚РѕР±С‹ РѕРЅ РїРµСЂРµРєСЂС‹Р» BoxShadow.
            Styles.Add(new Style(x => x.OfType<Border>().Class("card"))
            {
                Setters =
                {
                    new Setter(Border.BoxShadowProperty, new BoxShadows(new BoxShadow()))
                }
            });
#endif

            DiscoveredDevicesList.ItemsSource = _discoveredDevices;
            _settingsPanelTransform = SettingsPanel.RenderTransform as TranslateTransform;
            _dataFilePath = GetDataFilePath();

            // РџС‹С‚Р°РµРјСЃСЏ РѕС‚РєСЂС‹С‚СЊ С„Р°Р№Р» РґР°РЅРЅС‹С… РЅРµСЃРєРѕР»СЊРєРѕ СЂР°Р· (РґСЂСѓРіРѕР№ РїСЂРѕС†РµСЃСЃ РјРѕРі
            // С‚РѕР»СЊРєРѕ С‡С‚Рѕ Р·Р°РєСЂС‹С‚СЊ С„Р°Р№Р», Р° РЅР°С€Р° РїРѕРїС‹С‚РєР° РѕС‚РєСЂС‹С‚РёСЏ вЂ” СЃРѕРІРїР°СЃС‚СЊ СЃ РЅРёРј).
            bool storageInitialized = NativeMethods.InitializeStorage(_dataFilePath);
            for (int attempt = 0; !storageInitialized && attempt < 5; attempt++)
            {
                Thread.Sleep(300);
                storageInitialized = NativeMethods.InitializeStorage(_dataFilePath);
            }

            if (!storageInitialized)
            {
                try
                {
                    // РЈРґР°Р»СЏРµРј РїРѕРІСЂРµР¶РґС‘РЅРЅС‹Р№ С„Р°Р№Р», РµСЃР»Рё РѕРЅ СЃСѓС‰РµСЃС‚РІСѓРµС‚ Рё РЅРµ Р·Р°Р±Р»РѕРєРёСЂРѕРІР°РЅ
                    if (File.Exists(_dataFilePath))
                        File.Delete(_dataFilePath);
                    // РџСЂРѕР±СѓРµРј РёРЅРёС†РёР°Р»РёР·РёСЂРѕРІР°С‚СЊ Р·Р°РЅРѕРІРѕ
                    storageInitialized = NativeMethods.InitializeStorage(_dataFilePath);
                }
                catch (IOException ex)
                {
                    // Р¤Р°Р№Р» Р·Р°РЅСЏС‚ РґСЂСѓРіРёРј РїСЂРѕС†РµСЃСЃРѕРј (РЅР°РїСЂРёРјРµСЂ, РїСЂРёР»РѕР¶РµРЅРёРµ СѓР¶Рµ Р·Р°РїСѓС‰РµРЅРѕ)
                    throw new IOException(Localizer.Instance["Error_DataFileAccess"], ex);
                }
if (!storageInitialized)
                    throw new InvalidOperationException(Localizer.Instance["Error_StorageInit"]);
            }

            // Восстанавливаем сохранённые настройки: язык и тему.
            // Язык задаём до заполнения списка месяцев и до RefreshLocalizedStaticTexts().
            string savedLang = NativeMethods.GetStringSetting(LanguageSettingKey, "en");
            if (savedLang == "ru" || savedLang == "en")
                Localizer.Instance.Language = savedLang;
            _monthNames = GetLocalizedMonthNames();

            if (Application.Current is { } app)
            {
                string savedTheme = NativeMethods.GetStringSetting(ThemeSettingKey, "");
                if (savedTheme == "Light" || savedTheme == "Dark")
                    app.RequestedThemeVariant = savedTheme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            }

            // Заполняем выпадающие списки месяцев и годов
            foreach (var monthName in _monthNames)
                MonthComboBox.Items.Add(monthName);
            for (int year = 2000; year <= 2100; year++)
                YearComboBox.Items.Add(year);

            _selectedMonth = DateTime.Now.Month;
            _selectedYear = DateTime.Now.Year;
            MonthComboBox.SelectedIndex = _selectedMonth - 1;
            YearComboBox.SelectedItem = _selectedYear;

            ExpensesList.ItemsSource = _expenses;

#if ANDROID
            InstallAndroidItemTemplate();
#endif

            // Р—Р°РіСЂСѓР¶Р°РµРј РєР°С‚РµРіРѕСЂРёРё
            _categories = new ObservableCollection<string>(NativeMethods.GetCategories());
            CategoryComboBox.ItemsSource = _categories;
            CategoriesList.ItemsSource = _categories;

            // Р—Р°РіСЂСѓР·РєР° РґР°РЅРЅС‹С…
            LoadExpensesFromNative();
            FilterExpensesBySelectedMonth();
            UpdateTotal();

            NewDatePicker.SelectedDate = DateTime.Today;

#if ANDROID
            foreach (var child in WastedTextHost.Children)
            {
                if (child is TextBlock tb)
                    tb.FontSize = 52;
            }
#endif

            MonthComboBox.SelectionChanged += OnMonthSelectionChanged;
            YearComboBox.SelectionChanged += OnMonthSelectionChanged;
            RefreshLocalizedStaticTexts();

            // Загружаем сохранённое значение переключателя "Убрать текст при покупке"
            HideWastedSwitch.IsChecked = NativeMethods.GetBoolSetting(HideWastedSettingKey, false);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
#if ANDROID
            ConfigureSystemBars();
#endif
        }

#if ANDROID
        private void ConfigureSystemBars()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var insetsManager = topLevel.InsetsManager;
            if (insetsManager is null) return;

            insetsManager.DisplayEdgeToEdge = true;
            ApplySafeAreaPadding(insetsManager.SafeAreaPadding);

            topLevel.InsetsManager!.SafeAreaChanged += OnSafeAreaChanged;
        }

        private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
        {
            ApplySafeAreaPadding(e.SafeAreaPadding);
        }

        private void ApplySafeAreaPadding(Thickness safeArea)
        {
            RootGrid.Margin = new Thickness(
                Math.Max(0, safeArea.Left),
                Math.Max(0, safeArea.Top),
                Math.Max(0, safeArea.Right),
                Math.Max(0, safeArea.Bottom));
        }
#endif

        // РќР° Android РІРјРµСЃС‚Рѕ РєРѕРЅС‚РµРєСЃС‚РЅРѕРіРѕ РјРµРЅСЋ РїРѕ РґРѕР»РіРѕРјСѓ РЅР°Р¶Р°С‚РёСЋ РёСЃРїРѕР»СЊР·СѓРµС‚СЃСЏ СЃРІР°Р№Рї
        // РєР°СЂС‚РѕС‡РєРё. РџРѕРґРјРµРЅСЏРµРј С€Р°Р±Р»РѕРЅ СЌР»РµРјРµРЅС‚Р° СЃРїРёСЃРєР° РЅР° SwipeExpenseRow.
#if ANDROID
        private void InstallAndroidItemTemplate()
        {
            // ScrollGestureRecognizer СЃРїРёСЃРєР° РїРѕРґРїРёСЃР°РЅ РЅР° РґРІРёР¶РµРЅРёСЏ СЃ handledEventsToo:true вЂ”
            // e.Handled РµРіРѕ РЅРµ РѕСЃС‚Р°РЅР°РІР»РёРІР°РµС‚. РћРЅ РїРµСЂРµС…РІР°С‚С‹РІР°РµС‚ РїР°Р»РµС†, РєР°Рє С‚РѕР»СЊРєРѕ Р›Р®Р‘РђРЇ
            // РѕСЃСЊ СѓС…РѕРґРёС‚ РґР°Р»СЊС€Рµ ScrollStartDistance (~5px), Р»РѕРјР°СЏ РіРѕСЂРёР·РѕРЅС‚Р°Р»СЊРЅС‹Р№ СЃРІР°Р№Рї
            // РєР°СЂС‚РѕС‡РєРё. РћС‚РєР»СЋС‡Р°РµРј РіРѕСЂРёР·РѕРЅС‚Р°Р»СЊРЅС‹Р№ РїР°РЅ Сѓ ScrollViewer: СЂР°СЃРїРѕР·РЅР°РІР°С‚РµР»СЊ С‚РѕРіРґР°
            // РёРіРЅРѕСЂРёСЂСѓРµС‚ РѕСЃСЊ X РїРѕР»РЅРѕСЃС‚СЊСЋ Рё РІРµСЂС‚РёРєР°Р»СЊРЅР°СЏ РїСЂРѕРєСЂСѓС‚РєР° РЅРµ СЃС‚СЂР°РґР°РµС‚.
            ExpensesScrollViewer.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;

            ExpensesList.ItemTemplate = new FuncDataTemplate(
                typeof(ExpenseItem),
                (data, scope) =>
                {
                    var row = new SwipeExpenseRow();
                    row.EditRequested += OnSwipeEditRequested;
                    row.DeleteRequested += OnSwipeDeleteRequested;
                    row.SwipeBegan += OnSwipeBegan;
                    row.SelectionToggled += OnSwipeSelectionToggled;
                    row.SelectionModeProvider = () => _selectionActive;
                    return row;
                },
                supportsRecycling: true);
        }

        private void OnSwipeEditRequested(object? sender, ExpenseItem expense)
        {
            CloseAllSwipeRows();

            int index = _expenses.IndexOf(expense);
            if (index < 0) return;

            _editingIndex = index;
            NewNameBox.Text = expense.Name;
            NewAmountBox.Text = expense.Amount.ToString();
            NewDatePicker.SelectedDate = DateTime.TryParse(expense.Date, out var dt) ? dt : DateTime.Today;
            CategoryComboBox.SelectedItem = expense.Category;
            AddEditPanelTitle.Text = Localizer.Instance["Title_EditPurchase"];
            AddPanel.IsVisible = true;
        }

        private void OnSwipeDeleteRequested(object? sender, ExpenseItem expense)
        {
            _pendingDeleteItem = expense;
            DeleteConfirmTitle.Text = Localizer.Instance["Dialog_DeleteConfirm_Title"];
            DeleteConfirmText.Text = Localizer.Instance["Dialog_DeleteConfirm_Text"];
            DeleteConfirmOverlay.IsVisible = true;
        }

        private void OnSwipeBegan(object? sender, EventArgs e)
        {
            CloseOtherSwipeRows(sender as SwipeExpenseRow);
        }

        private void OnSwipeSelectionToggled(object? sender, ExpenseItem expense)
        {
            CloseAllSwipeRows();
            UpdateSelectionUI();
        }
#endif

        // Р—Р°РєСЂС‹РІР°РµС‚ РѕС‚РєСЂС‹С‚С‹Рµ (РІС‹РґРІРёРЅСѓС‚С‹Рµ) РєР°СЂС‚РѕС‡РєРё. РќР° Windows СЃРїРёСЃРєР° РЅРµС‚ вЂ” РјРµС‚РѕРґ РЅРёС‡РµРіРѕ РЅРµ РґРµР»Р°РµС‚.
        private void CloseOtherSwipeRows(SwipeExpenseRow? except)
        {
            foreach (var row in GetSwipeRows())
            {
                if (row != except)
                    row.CloseRow();
            }
        }

        private void CloseAllSwipeRows() => CloseOtherSwipeRows(null);

        private IEnumerable<SwipeExpenseRow> GetSwipeRows()
        {
            foreach (var child in ExpensesList.GetVisualDescendants())
            {
                if (child is SwipeExpenseRow row)
                    yield return row;
            }
        }

        private void UpdateSelectionUI()
        {
            int count = _allExpenses.Count(x => x.IsSelected);
            _selectionActive = count > 0;
            SelectionBar.IsVisible = count > 0;
            if (count <= 0) return;

            SelectionCountText.Text = Localizer.Instance.Format("Label_SelectedCount", count);
            DeleteSelectedButton.Content = Localizer.Instance.Format("Button_DeleteSelected", count);
        }

        private void OnClearSelectionClick(object sender, RoutedEventArgs e)
        {
            foreach (var item in _allExpenses)
                item.IsSelected = false;
            UpdateSelectionUI();
        }

        private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
        {
            CloseAllSwipeRows();

            while (true)
            {
                int idx = _allExpenses.FindIndex(x => x.IsSelected);
                if (idx < 0) break;

                var item = _allExpenses[idx];
                if (!NativeMethods.DeleteExpense(idx)) break;
                _allExpenses.RemoveAt(idx);
            }

            foreach (var item in _allExpenses)
                item.IsSelected = false;

            FilterExpensesBySelectedMonth();
            UpdateTotal();
            UpdateSelectionUI();
        }

        private void OnDeleteConfirmOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (e.Source == DeleteConfirmOverlay)
                OnDeleteConfirmCancelClick(sender, new RoutedEventArgs());
        }

        private void OnDeleteConfirmSelectClick(object sender, RoutedEventArgs e)
        {
            var item = _pendingDeleteItem;
            _pendingDeleteItem = null;
            DeleteConfirmOverlay.IsVisible = false;

            if (item is null) return;
            item.IsSelected = true;
            UpdateSelectionUI();
        }

        private void OnDeleteConfirmDeleteClick(object sender, RoutedEventArgs e)
        {
            var item = _pendingDeleteItem;
            _pendingDeleteItem = null;
            DeleteConfirmOverlay.IsVisible = false;

            if (item is null) return;
            DeleteExpenseItem(item);
            CloseAllSwipeRows();
            UpdateSelectionUI();
        }

        private void OnDeleteConfirmCancelClick(object sender, RoutedEventArgs e)
        {
            _pendingDeleteItem = null;
            DeleteConfirmOverlay.IsVisible = false;
        }

        private static string GetDataFilePath()
        {
#if ANDROID
            // РљР°С‚Р°Р»РѕРі РґР°РЅРЅС‹С… РїСЂРёР»РѕР¶РµРЅРёСЏ РЅР° Android
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(dir, "expenses.db");
#else
            return Path.Combine(AppContext.BaseDirectory, "expenses.dat");
#endif
        }

        private void PlaySound(string soundFileName)
        {
            try
            {
#if ANDROID
                // РќР° Android Р·РІСѓРєРё РІСЃС‚СЂРѕРµРЅС‹ РІ СЃР±РѕСЂРєСѓ РєР°Рє РІСЃС‚СЂРѕРµРЅРЅС‹Рµ СЂРµСЃСѓСЂСЃС‹ .NET
                // (AndroidAsset РЅРµ РёСЃРїРѕР»СЊР·СѓРµС‚СЃСЏ РёР·-Р·Р° РЅРµ-ASCII РїСѓС‚Рё РїСЂРѕРµРєС‚Р°), РёР·РІР»РµРєР°РµРј РІ РєСЌС€.
                string cachePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Sounds", soundFileName);

                if (!File.Exists(cachePath))
                {
                    var assembly = typeof(MainView).Assembly;
                    string resourceName = "Sounds." + soundFileName;
                    using var asset = assembly.GetManifestResourceStream(resourceName);
                    if (asset is null) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    using var outStream = File.Create(cachePath);
                    asset.CopyTo(outStream);
                }

                var player = new global::Android.Media.MediaPlayer();
                player.SetDataSource(cachePath);
                player.Prepared += (s, e) => player.Start();
                player.Completion += (s, e) => player.Release();
                player.Error += (s, e) => player.Release();
                player.PrepareAsync();
#else
                string soundPath = Path.Combine(AppContext.BaseDirectory, "Sounds", soundFileName);
                if (File.Exists(soundPath) && OperatingSystem.IsWindows())
                {
                    using (var player = new System.Media.SoundPlayer(soundPath))
                    {
                        player.Play();
                    }
                }
#endif
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка воспроизведения звука: {ex.Message}");
            }
        }

        private void OnBurgerClick(object sender, RoutedEventArgs e)
        {
            PlaySound("burger.wav");
        }

        private void OnChickenClick(object sender, RoutedEventArgs e)
        {
            PlaySound("chicken.wav");
        }

        private void OnStarClick(object sender, RoutedEventArgs e)
        {
            PlaySound("star.wav");
        }

        private void LoadExpensesFromNative()
        {
            _allExpenses.Clear();
            int count = NativeMethods.GetExpenseCount();

            for (int i = 0; i < count; i++)
            {
                var nameBuffer = new StringBuilder(256);
                var dateBuffer = new StringBuilder(32);
                var categoryBuffer = new StringBuilder(64);
                double amount;

                bool ok = NativeMethods.GetExpenseByIndex(
                    i,
                    nameBuffer,
                    nameBuffer.Capacity,
                    out amount,
                    dateBuffer,
                    dateBuffer.Capacity,
                    categoryBuffer,
                    categoryBuffer.Capacity
                );

                if (ok)
                {
                    _allExpenses.Add(new ExpenseItem
                    {
                        Name = nameBuffer.ToString(),
                        Amount = amount,
                        Date = dateBuffer.ToString(),
                        Category = categoryBuffer.ToString()
                    });
                }
            }

            // РќРѕРІС‹Рµ СЌРєР·РµРјРїР»СЏСЂС‹ вЂ” РІС‹РґРµР»РµРЅРёРµ СЃР±СЂР°СЃС‹РІР°РµС‚СЃСЏ
            UpdateSelectionUI();
        }

        private void FilterExpensesBySelectedMonth()
        {
            _expenses.Clear();
            foreach (var expense in _allExpenses)
            {
                if (DateTime.TryParse(expense.Date, out DateTime date) &&
                    date.Month == _selectedMonth && date.Year == _selectedYear)
                {
                    _expenses.Add(expense);
                }
            }
            SortCurrentExpenses();
        }

        private void UpdateTotal()
        {
            double total = NativeMethods.GetMonthlyTotal(_selectedMonth, _selectedYear);
            TotalText.Text = total.ToString("N2") + " ₽";
            MonthTotalLabel.Text = Localizer.Instance.Format(
                "Total_Month_Format",
                _monthNames[_selectedMonth - 1],
                _selectedYear);
        }

        private void OnMonthSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MonthComboBox.SelectedIndex >= 0)
                _selectedMonth = MonthComboBox.SelectedIndex + 1;
            if (YearComboBox.SelectedItem is int year)
                _selectedYear = year;

            FilterExpensesBySelectedMonth();
            UpdateTotal();
            CloseAllSwipeRows();

            AddPanel.IsVisible = false;
            _editingIndex = -1;
        }

        private void OnAddPurchaseClick(object sender, RoutedEventArgs e)
        {
            _editingIndex = -1;
            AddEditPanelTitle.Text = Localizer.Instance["Title_NewPurchase"];
            NewNameBox.Text = string.Empty;
            NewAmountBox.Text = string.Empty;
            NewDatePicker.SelectedDate = DateTime.Today;
            if (CategoryComboBox.Items.Count > 0)
                CategoryComboBox.SelectedIndex = 0;
            AddPanel.IsVisible = true;
        }

        private void OnEditClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is ExpenseItem expense)
            {
                int index = _expenses.IndexOf(expense);
                if (index < 0) return;

                _editingIndex = index;
                NewNameBox.Text = expense.Name;
                NewAmountBox.Text = expense.Amount.ToString();
                NewDatePicker.SelectedDate = DateTime.TryParse(expense.Date, out var dt) ? dt : DateTime.Today;
                CategoryComboBox.SelectedItem = expense.Category;
                AddEditPanelTitle.Text = Localizer.Instance["Title_EditPurchase"];
                AddPanel.IsVisible = true;
            }
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is ExpenseItem expense)
                DeleteExpenseItem(expense);
        }

        private void DeleteExpenseItem(ExpenseItem expense)
        {
            int filteredIndex = _expenses.IndexOf(expense);
            int fullIndex = _allExpenses.IndexOf(expense);
            if (fullIndex < 0) return;

            if (_editingIndex == filteredIndex)
            {
                AddPanel.IsVisible = false;
                _editingIndex = -1;
            }

            bool deleted = NativeMethods.DeleteExpense(fullIndex);
            if (!deleted) return;

            _allExpenses.RemoveAt(fullIndex);
            if (filteredIndex >= 0)
                _expenses.RemoveAt(filteredIndex);
            UpdateTotal();
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            string name = NewNameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            if (!double.TryParse(NewAmountBox.Text, out double amount)) return;

            DateTime? selectedDate = NewDatePicker.SelectedDate?.Date;
            string dateStr = selectedDate?.ToString("yyyy-MM-dd") ?? DateTime.Today.ToString("yyyy-MM-dd");

            string category = CategoryComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(category))
                category = Localizer.Instance["Category_Default"];

            bool saved = false;

            if (_editingIndex >= 0)
            {
                ExpenseItem expense = _expenses[_editingIndex];
                int fullIndex = _allExpenses.IndexOf(expense);
                if (fullIndex < 0) return;

                bool updated = NativeMethods.UpdateExpense(fullIndex, name, amount, dateStr, category);
                if (updated)
                {
                    expense.Name = name;
                    expense.Amount = amount;
                    expense.Date = dateStr;
                    expense.Category = category;

                    FilterExpensesBySelectedMonth();
                    AddPanel.IsVisible = false;
                    _editingIndex = -1;
                    UpdateTotal();
                    saved = true;
                }
            }
            else
            {
                int newIndex = NativeMethods.AddExpense(name, amount, dateStr, category);
                if (newIndex >= 0)
                {
                    var newExpense = new ExpenseItem
                    {
                        Name = name,
                        Amount = amount,
                        Date = dateStr,
                        Category = category
                    };
                    _allExpenses.Add(newExpense);

                    if (DateTime.TryParse(dateStr, out DateTime date) &&
                        date.Month == _selectedMonth && date.Year == _selectedYear)
                    {
                        _expenses.Add(newExpense);
                    }

                    NewNameBox.Text = string.Empty;
                    NewAmountBox.Text = string.Empty;
                    AddPanel.IsVisible = false;
                    UpdateTotal();
                    saved = true;
                }
                SortCurrentExpenses();
            }
            if (saved)
            {
                CloseAllSwipeRows();
                ShowWasted();
            }
        }

        private void ShowWasted()
        {
            if (HideWastedSwitch.IsChecked == true)
                return;
            _wastedCts?.Cancel();
            _wastedCts = new CancellationTokenSource();
            _ = RunWastedAsync(_wastedCts.Token);
        }

        private async Task RunWastedAsync(CancellationToken ct)
        {
            var overlay = WastedOverlay;
            var scale = (ScaleTransform)overlay.RenderTransform!;

            try
            {
                overlay.IsVisible = true;
                overlay.Opacity = 0;
                scale.ScaleX = 1.6;
                scale.ScaleY = 1.6;

                // Р–РґС‘Рј РѕРґРёРЅ РєР°РґСЂ, С‡С‚РѕР±С‹ РїРµСЂРµС…РѕРґ РЅР° ScaleTransform В«СѓРІРёРґРµР»В»
                // СЃС‚Р°СЂС‚РѕРІРѕРµ Р·РЅР°С‡РµРЅРёРµ Рё СѓСЃРїРµР» РїР»Р°РІРЅРѕ Р°РЅРёРјРёСЂРѕРІР°С‚СЊ СѓРјРµРЅСЊС€РµРЅРёРµ.
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct);

                overlay.Opacity = 1;
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;

                await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);

                overlay.Opacity = 0;
            }
            catch (OperationCanceledException)
            {
                // РџРѕРєР°Р· РїСЂРµСЂРІР°РЅ РЅРѕРІС‹Рј РІС‹Р·РѕРІРѕРј ShowWasted вЂ” РѕРІРµСЂР»РµРµРј Р·Р°Р№РјС‘С‚СЃСЏ РЅРѕРІС‹Р№ Р·Р°РїСѓСЃРє.
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    overlay.Opacity = 0;
                    overlay.IsVisible = false;
                    scale.ScaleX = 1.0;
                    scale.ScaleY = 1.0;
                }
            }
        }


        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            AddPanel.IsVisible = false;
            _editingIndex = -1;
            CloseAllSwipeRows();
        }

        private void OnAddCategoryClick(object sender, RoutedEventArgs e)
        {
            string newCategory = NewCategoryBox.Text?.Trim();
            if (string.IsNullOrEmpty(newCategory))
                return;

            if (_renamingCategory != null)
            {
                string oldCategory = _renamingCategory;
                if (oldCategory == newCategory)
                {
                    ResetCategoryEditMode();
                    return;
                }

                bool ok = NativeMethods.RenameCategory(oldCategory, newCategory);
                if (ok)
                {
                    int index = _categories.IndexOf(oldCategory);
                    if (index >= 0)
                        _categories[index] = newCategory;

                    LoadExpensesFromNative();
                    FilterExpensesBySelectedMonth();
                    UpdateTotal();

                    if (CategoryComboBox.SelectedItem as string == oldCategory)
                        CategoryComboBox.SelectedItem = newCategory;
                }
                ResetCategoryEditMode();
            }
            else
            {
                bool ok = NativeMethods.AddCategory(newCategory);
                if (ok)
                {
                    _categories.Add(newCategory);
                    if (AddPanel.IsVisible)
                        CategoryComboBox.SelectedItem = newCategory;
                }
                NewCategoryBox.Text = string.Empty;
            }
        }

        private async void OnDeleteCategoryClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string category)
                return;

            // Р РµР·РµСЂРІРЅСѓСЋ РєР°С‚РµРіРѕСЂРёСЋ СѓРґР°Р»СЏС‚СЊ РЅРµР»СЊР·СЏ
            string defaultCategory = Localizer.Instance["Category_Default"];
            if (string.Equals(category, defaultCategory, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "Другое", StringComparison.OrdinalIgnoreCase))
            {
                await ShowMessage(Localizer.Instance["Dialog_DeleteCategory_Title"],
                    Localizer.Instance["Dialog_DeleteCategory_Reserved"]);
                return;
            }

            // РџСЂРѕРІРµСЂСЏРµРј, РёСЃРїРѕР»СЊР·СѓРµС‚СЃСЏ Р»Рё РєР°С‚РµРіРѕСЂРёСЏ
            int usageCount = NativeMethods.GetCategoryUsageCount(category);
            if (usageCount > 0)
            {
                await ShowMessage(Localizer.Instance["Dialog_DeleteCategory_Title"],
                    Localizer.Instance.Format("Dialog_DeleteCategory_InUse", category, usageCount));
                return;
            }

            if (NativeMethods.DeleteCategory(category))
            {
                _categories.Remove(category);

                // Р•СЃР»Рё СЃРµР№С‡Р°СЃ СЂРµРґР°РєС‚РёСЂСѓРµС‚СЃСЏ РёРјРµРЅРЅРѕ СЌС‚Р° РєР°С‚РµРіРѕСЂРёСЏ вЂ” СЃР±СЂР°СЃС‹РІР°РµРј СЂРµР¶РёРј
                if (_renamingCategory == category)
                    ResetCategoryEditMode();
            }
            else
            {
                await ShowMessage(Localizer.Instance["Dialog_DeleteCategory_Title"],
                    Localizer.Instance.Format("Dialog_DeleteCategory_Failed", category));
            }
        }

        private void OnRenameCategoryClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string category)
            {
                _renamingCategory = category;
                NewCategoryBox.Text = category;
                AddCategoryButton.Content = Localizer.Instance["Button_SaveShort"];
            }
        }

        private void ResetCategoryEditMode()
        {
            _renamingCategory = null;
            NewCategoryBox.Text = string.Empty;
            AddCategoryButton.Content = Localizer.Instance["Button_AddShort"];
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _serverCts?.Cancel();
            _discoveryCts?.Cancel();

            NativeMethods.CleanupStorage();

#if ANDROID
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.InsetsManager != null)
                topLevel.InsetsManager.SafeAreaChanged -= OnSafeAreaChanged;
#endif

            base.OnDetachedFromVisualTree(e);
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is null) return;

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(
    new Avalonia.Platform.Storage.FilePickerSaveOptions
    {
        Title = Localizer.Instance["FilePicker_Export_Title"],
        SuggestedFileName = $"expenses_{DateTime.Now:yyyy-MM-dd}.xlsx",
        DefaultExtension = "xlsx",
        FileTypeChoices = new[]
        {
            new Avalonia.Platform.Storage.FilePickerFileType(
                Localizer.Instance["FileType_Excel"])
            { Patterns = new[] { "*.xlsx" } }
        }
    });

                if (file is null) return;

                var expenses = NativeMethods.GetAllExpenses()
                    .OrderBy(x => x.Date)
                    .ThenBy(x => x.Name)
                    .ToList();

                using (var stream = await file.OpenWriteAsync())
                {
                    XlsxService.Export(stream, expenses);
                }

                await ShowMessage(
                    Localizer.Instance["Dialog_Export_Title"],
                    Localizer.Instance.Format("Dialog_Export_Message", expenses.Count));
            }
            catch (Exception ex) { await ShowMessage(Localizer.Instance["Dialog_Export_Error"], ex.Message); }
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is null) return;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = Localizer.Instance["FilePicker_Import_Title"],
                        FileTypeFilter = new[]{
                            new FilePickerFileType(Localizer.Instance["FileType_Excel"])   { Patterns = new[] { "*.xlsx" } },
                            new FilePickerFileType(Localizer.Instance["FileType_AllFiles"]){ Patterns = new[] { "*" } }
                        }
                    });

                if (files.Count == 0) return;

                List<ExpenseItem> imported;
                using (var stream = await files[0].OpenReadAsync())
                {
                    imported = XlsxService.Import(stream);
                }

                if (imported.Count == 0)
                {
                    await ShowMessage(Localizer.Instance["Dialog_Import_Title"],
                  Localizer.Instance["Dialog_Import_Empty"]);
                    return;
                }

                NativeMethods.MergeExpenses(imported);

                var categories = NativeMethods.GetCategories();
                foreach (var category in imported.Select(x => x.Category))
                {
                    if (!categories.Contains(category))
                        NativeMethods.AddCategory(category);
                }

                _categories = new ObservableCollection<string>(NativeMethods.GetCategories());
                CategoryComboBox.ItemsSource = _categories;
                CategoriesList.ItemsSource = _categories;

                LoadExpensesFromNative();
                FilterExpensesBySelectedMonth();
                UpdateTotal();

                await ShowMessage(Localizer.Instance["Dialog_Import_Complete"],
                  Localizer.Instance.Format("Dialog_Import_Message", imported.Count));
            }
            catch (Exception ex) { await ShowMessage(Localizer.Instance["Dialog_Import_Error"], ex.Message); }
        }

        private async Task ShowMessage(string title, string message)
        {
            // РЈР±РµР¶РґР°РµРјСЃСЏ, С‡С‚Рѕ РјС‹ РІ UI-РїРѕС‚РѕРєРµ
            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(() => ShowMessage(title, message));
                return;
            }

            MessageTitle.Text = title;
            MessageText.Text = message;
            MessageOverlay.IsVisible = true;

            _messageTcs = new TaskCompletionSource<bool>();
            await _messageTcs.Task;
        }
        private void OnMessageOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            // Р—Р°РєСЂС‹РІР°РµРј С‚РѕР»СЊРєРѕ РµСЃР»Рё РєР»РёРє РїРѕ СЃР°РјРѕРјСѓ С„РѕРЅСѓ, Р° РЅРµ РїРѕ РєР°СЂС‚РѕС‡РєРµ
            if (e.Source == MessageOverlay)
                OnMessageOkClick(sender, new RoutedEventArgs());
        }

        private void OnMessageOkClick(object sender, RoutedEventArgs e)
        {
            MessageOverlay.IsVisible = false;
            _messageTcs?.TrySetResult(true);
            _messageTcs = null;
        }

        public int SelectedMonth => _selectedMonth;
        public int SelectedYear => _selectedYear;

        private void OnStatsClick(object sender, RoutedEventArgs e)
        {
#if ANDROID
            if (_statsView is null)
            {
                _statsView = new StatsView();
                _statsView.BackRequested += OnStatsBackRequested;
                StatsOverlay.Child = _statsView;
            }
            _statsView.SetPeriod(_selectedMonth, _selectedYear);
            StatsOverlay.IsVisible = true;
#else
            var window = new StatsWindow(_selectedMonth, _selectedYear);
            window.Show();
#endif
        }

#if ANDROID
        private void OnStatsBackRequested(object? sender, EventArgs e)
        {
            _statsView?.Refresh();
            StatsOverlay.IsVisible = false;
        }
#endif

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            if (_settingsPanelVisible)
                CloseSettingsPanel();
            else
                OpenSettingsPanel();
        }

        private void OpenSettingsPanel()
        {
            _settingsPanelVisible = true;
            var panel = SettingsPanel;
            if (panel == null) return;

            panel.IsVisible = true;
            if (_settingsPanelTransform != null)
                _settingsPanelTransform.X = panel.Width;

            Dispatcher.UIThread.Post(() =>
            {
                if (_settingsPanelTransform != null)
                    _settingsPanelTransform.X = 0;
            }, DispatcherPriority.Render);
        }

        private void CloseSettingsPanel()
        {
            if (!_settingsPanelVisible) return;
            _settingsPanelVisible = false;
            var panel = SettingsPanel;
            if (panel == null) return;

            if (_settingsPanelTransform != null)
                _settingsPanelTransform.X = panel.Width;

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            timer.Tick += (s, args) =>
            {
                panel.IsVisible = false;
                timer.Stop();
            };
            timer.Start();
        }

        private void OnCloseSettingsClick(object sender, RoutedEventArgs e)
        {
            CloseSettingsPanel();
        }

        private void OnRootGridPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (MessageOverlay.IsVisible)
                return;

            if (!_settingsPanelVisible) return;

            if (SettingsPanel.IsVisualAncestorOf(e.Source as Visual) ||
                SettingsButton.IsVisualAncestorOf(e.Source as Visual))
            {
                return;
            }

            e.Handled = true;
            CloseSettingsPanel();
        }

        // Левая кнопка мыши, зажатая прямо на покупке, выделяет/снимает её.
        private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(sender as Control);
            if (!point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
                return;

            if (sender is Control control && control.DataContext is ExpenseItem item)
            {
                item.IsSelected = !item.IsSelected;
                UpdateSelectionUI();
            }
        }

        private void OnToggleThemeClick(object sender, RoutedEventArgs e)
        {
            var app = Application.Current;
            if (app is null) return;

            app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark
                ? ThemeVariant.Light
                : ThemeVariant.Dark;

            NativeMethods.SetStringSetting(ThemeSettingKey,
                app.RequestedThemeVariant == ThemeVariant.Dark ? "Dark" : "Light");
        }

        private void OnHideWastedToggleChanged(object sender, RoutedEventArgs e)
        {
            NativeMethods.SetBoolSetting(HideWastedSettingKey, HideWastedSwitch.IsChecked == true);
        }

        private async void OnStartServerClick(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(SyncPortBox.Text?.Trim(), out int port) || port < 1 || port > 65535)
            {
                SyncLog.Text = Localizer.Instance["Sync_InvalidPort"];
                return;
            }

            _serverCts?.Cancel();
            _discoveryCts?.Cancel();
            _serverCts = new CancellationTokenSource();
            _discoveryCts = new CancellationTokenSource();

            _discoveredDevices.Clear();

            SyncLog.Text = Localizer.Instance["Sync_Starting"];

            var serverTask = Task.Run(() => SyncService.StartServerAsync(
                port,
                msg => Dispatcher.UIThread.Post(() => SyncLog.Text += msg + Environment.NewLine),
                null,
                // РљРѕР»Р±СЌРє, РєРѕС‚РѕСЂС‹Р№ РІС‹Р·РѕРІРµС‚СЃСЏ СЃСЂР°Р·Сѓ РїРѕСЃР»Рµ MergeExpenses
                () => Dispatcher.UIThread.Post(() =>
                {
                    LoadExpensesFromNative();
                    FilterExpensesBySelectedMonth();
                    UpdateTotal();
                    SyncLog.Text += Localizer.Instance["Sync_UiUpdated"] + Environment.NewLine;
                }),
                _serverCts.Token
            ));

            var listenerTask = Task.Run(() => SyncService.StartDiscoveryListenerAsync(
                (ip, discoveredPort) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_discoveredDevices.Any(d => d.Ip == ip && d.Port == discoveredPort))
                        {
                            _discoveredDevices.Add(new DiscoveredDevice { Ip = ip, Port = discoveredPort });
                        }
                    });
                },
                _discoveryCts.Token,
                AppendSyncLog
            ));

            var broadcasterTask = Task.Run(() => SyncService.StartDiscoveryBroadcasterAsync(
                port,
                AppendSyncLog,
                _discoveryCts.Token
            ));

            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
                // СЃРµСЂРІРµСЂ РѕС‚РјРµРЅС‘РЅ вЂ” РѕР±РЅРѕРІР»РµРЅРёРµ СѓР¶Рµ РЅРµ С‚СЂРµР±СѓРµС‚СЃСЏ
            }
        }

        private void OnSyncClick(object sender, RoutedEventArgs e)
        {
            _syncPanelVisible = !_syncPanelVisible;
            SyncPanel.IsVisible = _syncPanelVisible;

            if (_syncPanelVisible)
            {
                _discoveredDevices.Clear();
                StartDeviceDiscovery();
            }
            else
            {
                _serverCts?.Cancel();
                _discoveryCts?.Cancel();
                SyncLog.Text = string.Empty;
                _discoveredDevices.Clear();
            }
        }

        private async void OnStartClientClick(object sender, RoutedEventArgs e)
        {
            string ip = ServerIpBox.Text?.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                SyncLog.Text = Localizer.Instance["Sync_EnterServerIp"];
                return;
            }

            if (!int.TryParse(SyncPortBox.Text?.Trim(), out int port) || port < 1 || port > 65535)
            {
                SyncLog.Text = Localizer.Instance["Sync_InvalidPort"];
                return;
            }

            SyncLog.Text += Localizer.Instance["Sync_SearchStarted"] + Environment.NewLine;
            var lastSync = NativeMethods.GetLastSyncTime() ?? default;
            await Task.Run(() => SyncService.StartClientAsync(ip, port, msg =>
                Dispatcher.UIThread.Post(() => SyncLog.Text += msg + Environment.NewLine),
                lastSync));

            Dispatcher.UIThread.Post(() =>
            {
                LoadExpensesFromNative();
                FilterExpensesBySelectedMonth();
                UpdateTotal();
            });
        }

        private void OnDiscoveredDeviceClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is DiscoveredDevice device)
            {
                ServerIpBox.Text = device.Ip;
                SyncPortBox.Text = device.Port.ToString();
            }
        }

        private void AppendSyncLog(string msg)
            => Dispatcher.UIThread.Post(() => SyncLog.Text += msg + Environment.NewLine);

        private void StartDeviceDiscovery()
        {
            _discoveryCts?.Cancel();
            _discoveryCts = new CancellationTokenSource();

            _ = Task.Run(() => SyncService.StartDiscoveryListenerAsync(
                (ip, discoveredPort) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_discoveredDevices.Any(d => d.Ip == ip && d.Port == discoveredPort))
                        {
                            _discoveredDevices.Add(new DiscoveredDevice { Ip = ip, Port = discoveredPort });
                        }
                    });
                },
                _discoveryCts.Token,
                AppendSyncLog
            ));

            if (int.TryParse(SyncPortBox.Text?.Trim(), out int port) && port >= 1 && port <= 65535)
            {
                _ = Task.Run(() => SyncService.StartDiscoveryBroadcasterAsync(
                    port,
                    AppendSyncLog,
                    _discoveryCts.Token
                ));
            }

            SyncLog.Text += Localizer.Instance["Sync_SearchStarted"] + Environment.NewLine;
        }
        private static string[] GetLocalizedMonthNames()
        {
            var dtf = Localizer.Instance.Culture.DateTimeFormat;
            return Enumerable.Range(1, 12)
                .Select(i =>
                {
                    string name = dtf.GetMonthName(i);
                    return string.IsNullOrEmpty(name) ? name : char.ToUpper(name[0]) + name.Substring(1);
                })
                .ToArray();
        }

        private void RefreshLocalizedStaticTexts()
        {
            // РњРµСЃСЏС†С‹
            int prevMonth = MonthComboBox.SelectedIndex;
            MonthComboBox.Items.Clear();
            _monthNames = GetLocalizedMonthNames();
            foreach (var m in _monthNames) MonthComboBox.Items.Add(m);
            MonthComboBox.SelectedIndex = prevMonth;

            // Р—Р°РіРѕР»РѕРІРѕРє "РџРћРўР РђР§Р•РќРћ" (РІРєР»СЋС‡Р°СЏ РїРѕРґР»РѕР¶РєРё-С‚РµРЅРё)
            string wasted = Localizer.Instance["Wasted_Text"];
            foreach (var child in WastedTextHost.Children)
                if (child is TextBlock tb) tb.Text = wasted;

            // Р—Р°РіРѕР»РѕРІРѕРє РїР°РЅРµР»Рё РґРѕР±Р°РІР»РµРЅРёСЏ (РµСЃР»Рё РѕС‚РєСЂС‹С‚Р° вЂ” РїРѕ СЂРµР¶РёРјСѓ СЂРµРґР°РєС‚РёСЂРѕРІР°РЅРёСЏ)
            AddEditPanelTitle.Text = Localizer.Instance[
                _editingIndex >= 0 ? "Title_EditPurchase" : "Title_NewPurchase"];

            // РљРЅРѕРїРєР° "Р”РѕР±Р°РІРёС‚СЊ/РЎРѕС…СЂР°РЅРёС‚СЊ" РІ СЂР°Р·РґРµР»Рµ РєР°С‚РµРіРѕСЂРёР№
            AddCategoryButton.Content = Localizer.Instance[
                _renamingCategory != null ? "Button_SaveShort" : "Button_AddShort"];

            UpdateTotal();
            UpdateSelectionUI();
        }

        private void OnSortByNameClick(object sender, RoutedEventArgs e) => ApplySort(SortMode.Name);
        private void OnSortByAmountClick(object sender, RoutedEventArgs e) => ApplySort(SortMode.Amount);
        private void OnSortByDateClick(object sender, RoutedEventArgs e) => ApplySort(SortMode.Date);

        private void ApplySort(SortMode mode)
        {
            if (_sortMode == mode)
                _sortAscending = !_sortAscending; // РїРѕРІС‚РѕСЂРЅС‹Р№ РєР»РёРє вЂ” РјРµРЅСЏРµРј РЅР°РїСЂР°РІР»РµРЅРёРµ
            else
            {
                _sortMode = mode;
                _sortAscending = true;
            }

            SortCurrentExpenses();
        }

        private void SortCurrentExpenses()
        {
            if (_sortMode == SortMode.None) return;

            IEnumerable<ExpenseItem> sorted = _sortMode switch
            {
                SortMode.Name => _sortAscending
                    ? _expenses.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                    : _expenses.OrderByDescending(x => x.Name, StringComparer.CurrentCultureIgnoreCase),

                SortMode.Amount => _sortAscending
                    ? _expenses.OrderBy(x => x.Amount)
                    : _expenses.OrderByDescending(x => x.Amount),

                SortMode.Date => _sortAscending
                    ? _expenses.OrderBy(x => DateTime.TryParse(x.Date, out var d) ? d : DateTime.MinValue)
                    : _expenses.OrderByDescending(x => DateTime.TryParse(x.Date, out var d) ? d : DateTime.MinValue),

                _ => _expenses.AsEnumerable()
            };

            var snapshot = sorted.ToList();
            _expenses.Clear();
            foreach (var item in snapshot)
                _expenses.Add(item);
        }

        private void OnToggleLanguageClick(object sender, RoutedEventArgs e)
        {
            var loc = Localizer.Instance;
            loc.Language = loc.Language == "ru" ? "en" : "ru";
            NativeMethods.SetStringSetting(LanguageSettingKey, loc.Language);
            RefreshLocalizedStaticTexts();
        }
    }

    public class DiscoveredDevice
    {
        public string Ip { get; set; } = "";
        public int Port { get; set; }
        public string DisplayText => $"{Ip}:{Port}";
    }
}
