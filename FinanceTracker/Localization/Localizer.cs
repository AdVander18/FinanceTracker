using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace FinanceTracker.Localization
{
    // Синглтон-локализатор. Поддерживает EN и RU, уведомляет UI об изменениях
    // через INotifyPropertyChanged (индексер [Key] в биндингах обновляется автоматически).
    public sealed class Localizer : INotifyPropertyChanged
    {
        public static Localizer Instance { get; } = new();

        private string _language = "en";

        // Культуру выставляем сразу при создании: сохранённая настройка может быть
        // пустой или повреждённой, тогда до смены языка CurrentCulture остался бы
        // системным, и штатные DatePicker показывали бы месяцы на языке ОС.
        static Localizer() => ApplyCulture("en");

        public string Language
        {
            get => _language;
            set
            {
                // Культуру применяем ДО проверки на изменение: при первом запуске
                // сохранённый язык может совпадать с языком по умолчанию ("en"),
                // и иначе культура не применится вовсе.
                ApplyCulture(value);
                if (_language == value) return;
                _language = value;
                // Avalonia обновляет indexer-биндинги Path="[Key]" только по
                // PropertyChanged("Item") — ни null, ни "Item[]" движок не слушает.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
            }
        }

        public CultureInfo Culture => CreateCulture(_language);

        private static CultureInfo CreateCulture(string language) =>
            new CultureInfo(language == "ru" ? "ru-RU" : "en-US");

        // Штатные контролы Avalonia (DatePicker, Calendar, ComboBox с датами) берут
        // названия месяцев и дней недели из CultureInfo.CurrentCulture, а не из Localizer.
        // Без этого языка интерфейса они остаются на языке системы.
        private static void ApplyCulture(string language)
        {
            var culture = CreateCulture(language);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
        {
            ["en"] = new Dictionary<string, string>
            {
                // Header
                ["AppTitle"] = "Finance Tracker",
                ["Tooltip_ExportExcel"] = "Export to Excel",
                ["Tooltip_ImportExcel"] = "Import from Excel",
                ["Tooltip_Settings"] = "Settings",
                ["Tooltip_Delete"] = "Delete",
                ["Tooltip_Rename"] = "Rename",
                ["Tooltip_Burger"] = "Burger",
                ["Tooltip_Chicken"] = "ChickenBurger",
                ["Tooltip_Star"] = "Star",
                ["Tooltip_DelPurch"] = "Deleting purchases",

                // Month panel
                ["Label_Month"] = "Month:",
                ["Label_Year"] = "Year:",

                // Bottom panel
                ["Total_Month_Format"] = "Total for {0} {1}:",
                ["Button_Add"] = "Add",

                // Context menu
                ["Menu_Edit"] = "Edit",
                ["Menu_Delete"] = "Delete",

                // Delete confirm (Android swipe)
                ["Dialog_DeleteConfirm_Title"] = "Delete purchase",
                ["Dialog_DeleteConfirm_Text"] =
                    "Do you want to delete or select purchases for deletion?",
                ["Dialog_DeleteConfirm_Delete"] = "Delete",
                ["Dialog_DeleteConfirm_Select"] = "Select",

                // Delete purchases by period
                ["Delete_Title"] = "Delete purchases",
                ["Delete_Title_For"] = "Delete purchases for",
                ["Delete_Title_Confirm"] = "Confirm deletion",
                ["Delete_MenuHint"] = "Choose the period to delete purchases for",
                ["Delete_Action_For"] = "Delete purchases for",
                ["Delete_Action_Week"] = "Delete for a week",
                ["Delete_Action_Month"] = "Delete for a month",
                ["Delete_Action_Year"] = "Delete for a year",
                ["Delete_Action_AllTime"] = "Delete for all time",
                ["Delete_Action_Custom"] = "Delete for a period",
                ["Delete_RangeHint"] = "Or specify the period manually",
                ["Delete_From"] = "From",
                ["Delete_To"] = "To",
                ["Delete_Period_Week"] = "a week",
                ["Delete_Period_Month"] = "a month",
                ["Delete_Period_Year"] = "a year",
                ["Delete_Period_AllTime"] = "all time",
                ["Delete_Period_Custom"] = "the period from {0} to {1}",
                ["Delete_Confirm_Question"] =
                    "Are you sure you want to delete purchases for {0}?",
                ["Delete_Confirm_Details"] =
                    "Records: {0} ({1} ₽), {2}. This cannot be undone.",
                ["Delete_Confirm_Empty"] = "There are no records for this period.",
                ["Delete_RangePreview"] = "In the period: {0} records, {1} ₽",
                ["Delete_RangePreview_Empty"] = "There are no records in the period.",
                ["Delete_Range_AllFrom"] = "from {0}",
                ["Delete_Result_Title"] = "Deletion complete",
                ["Delete_Result_Message"] = "Deleted records: {0} ({1} ₽).",
                ["Delete_Result_Failed"] = "Failed to delete the records. Try again.",
                ["Button_Back"] = "Back",
                ["Button_Yes"] = "Yes",
                ["Button_No"] = "No",

                // Selection (Android)
                ["Button_DeleteSelected"] = "Delete selected ({0})",
                ["Button_ClearSelection"] = "Clear selection",
                ["Label_SelectedCount"] = "Selected: {0}",

                // Add/edit panel
                ["Title_NewPurchase"] = "New purchase",
                ["Title_EditPurchase"] = "Edit purchase",
                ["Watermark_PurchaseName"] = "Purchase name",
                ["Watermark_Amount"] = "Amount, ₽",
                ["Button_Save"] = "Save",
                ["Button_Cancel"] = "Cancel",

                // Settings
                ["Tooltip_Language"] = "English",
                ["Title_Settings"] = "Settings",
                ["Button_ToggleTheme"] = "Toggle theme",
                ["Button_ToggleLanguage"] = "Switch language",
                ["Switch_HideWasted"] = "Hide text on purchase",
                ["Title_Categories"] = "Categories",
                ["Watermark_NewCategory"] = "New category",
                ["Button_AddShort"] = "Add",
                ["Button_SaveShort"] = "Save",
                ["Button_SyncPurchases"] = "Sync purchases",
                ["Title_SyncMode"] = "Sync mode",
                ["Button_ReceiveData"] = "Receive data (server)",
                ["Watermark_ServerIp"] = "Server IP",
                ["Button_SendData"] = "Send data (client)",
                ["Watermark_Port"] = "Port",

                // Wasted
                ["Wasted_Text"] = "WASTED",

                // Message box
                ["Button_Ok"] = "OK",

                // Default categories
                ["Category_Default"] = "Other",

                // Excel export
                ["Excel_SheetName"] = "Expenses",
                ["Excel_Column_Name"] = "Name",
                ["Excel_Column_Amount"] = "Amount",
                ["Excel_Column_Date"] = "Date",
                ["Excel_Column_Category"] = "Category",

                // Errors
                ["Error_DataFileAccess"] =
                    "Failed to access the data file. The application may already be running.",
                ["Error_StorageInit"] =
                    "Failed to initialize data storage.",

                // Export
                ["FilePicker_Export_Title"] = "Export to Excel",
                ["FilePicker_Import_Title"] = "Import from Excel",
                ["FileType_Excel"] = "Excel (.xlsx)",
                ["FileType_AllFiles"] = "All files",
                ["Dialog_Export_Title"] = "Export complete",
                ["Dialog_Export_Message"] = "Records exported: {0}.",
                ["Dialog_Export_Error"] = "Export error",

                // Import
                ["Dialog_Import_Title"] = "Import",
                ["Dialog_Import_Empty"] = "No expenses found in the file.",
                ["Dialog_Import_Complete"] = "Import complete",
                ["Dialog_Import_Message"] = "Records imported: {0}.",
                ["Dialog_Import_Error"] = "Import error",

                // Category dialogs
                ["Dialog_DeleteCategory_Title"] = "Delete category",
                ["Dialog_DeleteCategory_Reserved"] =
                    "The \"Other\" category cannot be deleted — it is used as a fallback.",
                ["Dialog_DeleteCategory_InUse"] =
                    "Category \"{0}\" is used in {1} records. " +
                    "Change the category of those records first, then delete it.",
                ["Dialog_DeleteCategory_Failed"] =
                    "Failed to delete category \"{0}\".",

                // Sync log
                ["Sync_InvalidPort"] = "Enter a valid port number (1-65535).",
                ["Sync_Starting"] = "Starting server and searching for devices...",
                ["Sync_UiUpdated"] = "UI updated.",
                ["Sync_EnterServerIp"] = "Enter the server IP address.",
                ["Sync_Connecting"] = "Connecting to the server...",
                ["Sync_SearchStarted"] = "Device search started...",
                ["Sync_MulticastLockCaptured"] = "MulticastLock acquired (Wi-Fi accepts broadcast).",
                ["Sync_MulticastLockFailed"] = "Failed to acquire MulticastLock: {0}",
                ["Sync_ListeningBroadcast"] = "Listening for broadcast on port {0}...",
                ["Sync_JoinedMulticast"] = "Joined multicast group {0}...",
                ["Sync_MulticastJoinFailed"] = "Failed to join multicast group {0}...",
                ["Sync_UdpListenError"] = "UDP listening error: {0}",
                ["Sync_DiscoveryBroadcasterError"] = "Discovery broadcaster error: {0}",
                ["Sync_SoBroadcastUnavailable"] = "SO_BROADCAST unavailable ({0}), directed broadcast disabled.",
                ["Sync_GetLocalIpFailed"] = "GetLocalIPAddress failed: {0}",
                ["Sync_BroadcastTargetsFailed"] = "Failed to compute broadcast targets: {0}",
                ["Sync_DiscoveryAnnounce"] =
                    "Discovery: announcing {0}:{1}, broadcast targets: {2}, probe hosts: {3}.",
                ["Sync_BroadcastSendError"] = "Broadcast send error: {0}",
                ["Sync_ServerStarted"] = "Server started on port {0}, waiting for connections...",
                ["Sync_ClientConnected"] = "Client connected.",
                ["Sync_ReceivedRecords"] = "Records received from client: {0}",
                ["Sync_LocalDataUpdated"] = "Local data updated.",
                ["Sync_ServerStopped"] = "Server stopped.",
                ["Sync_ServerError"] = "Server error: {0}",
                ["Sync_ConnectingTo"] = "Connecting to {0}:{1}...",
                ["Sync_SentRecords"] = "Records sent: {0}",
                ["Sync_ClientError"] = "Client error: {0}",
                ["Sync_ConnectionClosed"] = "Connection closed prematurely.",

                // Sort
                ["Tooltip_Sort"] = "Sort",
                ["Sort_ByName"] = "By name",
                ["Sort_ByAmount"] = "By amount",
                ["Sort_ByDate"] = "By date",

                // Statistics
                ["Tooltip_Statistics"] = "Statistics",
                ["Stats_Title"] = "Statistics",
                ["Stats_PeriodLabel"] = "Period:",
                ["Stats_Tab_Categories"] = "By categories",
                ["Stats_Tab_Year"] = "Year",
                ["Stats_Tab_Top"] = "Top expenses",
                ["Stats_NoData"] = "No expenses for the selected period.",
                ["Stats_Total"] = "Total",
                ["Stats_Share"] = "Share",
                ["Stats_Category"] = "Category",
                ["Stats_Amount"] = "Amount",
                ["Stats_YearTotal"] = "Total for the year",
                ["Stats_AvgMonth"] = "Average per month",
                ["Stats_MaxMonth"] = "Max month",
                ["Stats_MinMonth"] = "Min month",
                ["Stats_Records"] = "Records",
                ["Stats_ByMonth"] = "By month",
                ["Stats_TopHint"] = "Top 10 most expensive purchases",
                ["Stats_TopEmpty"] = "No expenses for the selected period.",
                ["Stats_YearEmpty"] = "No expenses for the year.",
                ["Stats_Back"] = "Back",
            },

            ["ru"] = new Dictionary<string, string>
            {
                // Заголовок
                ["AppTitle"] = "Finance Tracker",
                ["Tooltip_ExportExcel"] = "Экспорт в Excel",
                ["Tooltip_ImportExcel"] = "Импорт из Excel",
                ["Tooltip_Settings"] = "Настройки",
                ["Tooltip_Delete"] = "Удалить",
                ["Tooltip_Rename"] = "Переименовать",
                ["Tooltip_Burger"] = "Бургер",
                ["Tooltip_Chicken"] = "БургерСкурочкой",
                ["Tooltip_Star"] = "Звезда",
                ["Tooltip_DelPurch"] = "Удаление покупок",

                // Панель месяца
                ["Label_Month"] = "Месяц:",
                ["Label_Year"] = "Год:",

                // Нижняя панель
                ["Total_Month_Format"] = "Итого за {0} {1}:",
                ["Button_Add"] = "Добавить",

                // Контекстное меню
                ["Menu_Edit"] = "Изменить",
                ["Menu_Delete"] = "Удалить",

                // Подтверждение удаления (свайп на Android)
                ["Dialog_DeleteConfirm_Title"] = "Удаление покупки",
                ["Dialog_DeleteConfirm_Text"] =
                    "Вы хотите удалить или выделить покупки для удаления?",
                ["Dialog_DeleteConfirm_Delete"] = "Удалить",
                ["Dialog_DeleteConfirm_Select"] = "Выделить",

                // Удаление покупок за период
                ["Delete_Title"] = "Удаление покупок",
                ["Delete_Title_For"] = "Удалить покупки за",
                ["Delete_Title_Confirm"] = "Подтверждение удаления",
                ["Delete_MenuHint"] = "Выберите период, за который нужно удалить покупки",
                ["Delete_Action_For"] = "Удалить покупки за",
                ["Delete_Action_Week"] = "Удалить за неделю",
                ["Delete_Action_Month"] = "Удалить за месяц",
                ["Delete_Action_Year"] = "Удалить за год",
                ["Delete_Action_AllTime"] = "Удалить за всё время",
                ["Delete_Action_Custom"] = "Удалить за период",
                ["Delete_RangeHint"] = "Или укажите период вручную",
                ["Delete_From"] = "С",
                ["Delete_To"] = "По",
                ["Delete_Period_Week"] = "неделю",
                ["Delete_Period_Month"] = "месяц",
                ["Delete_Period_Year"] = "год",
                ["Delete_Period_AllTime"] = "всё время",
                ["Delete_Period_Custom"] = "период с {0} по {1}",
                ["Delete_Confirm_Question"] =
                    "Вы уверены, что хотите удалить покупки за {0}?",
                ["Delete_Confirm_Details"] = "Записей: {0} ({1} ₽), {2}. Отменить будет нельзя.",
                ["Delete_Confirm_Empty"] = "За этот период записей нет.",
                ["Delete_RangePreview"] = "В периоде: {0} записей, {1} ₽",
                ["Delete_RangePreview_Empty"] = "В этом периоде записей нет.",
                ["Delete_Range_AllFrom"] = "с {0}",
                ["Delete_Result_Title"] = "Удаление выполнено",
                ["Delete_Result_Message"] = "Удалено записей: {0} ({1} ₽).",
                ["Delete_Result_Failed"] = "Не удалось удалить записи. Попробуйте ещё раз.",
                ["Button_Back"] = "Назад",
                ["Button_Yes"] = "Да",
                ["Button_No"] = "Нет",

                // Выделение (Android)
                ["Button_DeleteSelected"] = "Удалить выбранные ({0})",
                ["Button_ClearSelection"] = "Снять выделение",
                ["Label_SelectedCount"] = "Выделено: {0}",

                // Панель добавления
                ["Title_NewPurchase"] = "Новая покупка",
                ["Title_EditPurchase"] = "Изменение покупки",
                ["Watermark_PurchaseName"] = "Название покупки",
                ["Watermark_Amount"] = "Сумма, ₽",
                ["Button_Save"] = "Сохранить",
                ["Button_Cancel"] = "Отмена",

                // Настройки
                ["Tooltip_Language"] ="Русский",
                ["Title_Settings"] = "Настройки",
                ["Button_ToggleTheme"] = "Переключить тему",
                ["Button_ToggleLanguage"] = "Сменить язык",
                ["Switch_HideWasted"] = "Убрать текст при покупке",
                ["Title_Categories"] = "Категории",
                ["Watermark_NewCategory"] = "Новая категория",
                ["Button_AddShort"] = "Добавить",
                ["Button_SaveShort"] = "Сохранить",
                ["Button_SyncPurchases"] = "Синхронизировать покупки",
                ["Title_SyncMode"] = "Режим синхронизации",
                ["Button_ReceiveData"] = "Принимать данные (сервер)",
                ["Watermark_ServerIp"] = "IP сервера",
                ["Button_SendData"] = "Отправить данные (клиент)",
                ["Watermark_Port"] = "Порт",

                // Потрачено
                ["Wasted_Text"] = "ПОТРАЧЕНО",

                // Окно сообщения
                ["Button_Ok"] = "OK",

                // Категории
                ["Category_Default"] = "Другое",

                // Экспорт в Excel
                ["Excel_SheetName"] = "Расходы",
                ["Excel_Column_Name"] = "Название",
                ["Excel_Column_Amount"] = "Сумма",
                ["Excel_Column_Date"] = "Дата",
                ["Excel_Column_Category"] = "Категория",

                // Ошибки
                ["Error_DataFileAccess"] =
                    "Не удалось получить доступ к файлу данных. Возможно, приложение уже запущено.",
                ["Error_StorageInit"] =
                    "Не удалось инициализировать хранилище данных.",

                // Экспорт
                ["FilePicker_Export_Title"] = "Экспорт в Excel",
                ["FilePicker_Import_Title"] = "Импорт из Excel",
                ["FileType_Excel"] = "Excel (.xlsx)",
                ["FileType_AllFiles"] = "Все файлы",
                ["Dialog_Export_Title"] = "Экспорт завершён",
                ["Dialog_Export_Message"] = "Экспортировано записей: {0}.",
                ["Dialog_Export_Error"] = "Ошибка экспорта",

                // Импорт
                ["Dialog_Import_Title"] = "Импорт",
                ["Dialog_Import_Empty"] = "В файле не найдено расходов.",
                ["Dialog_Import_Complete"] = "Импорт завершён",
                ["Dialog_Import_Message"] = "Импортировано записей: {0}.",
                ["Dialog_Import_Error"] = "Ошибка импорта",

                // Категории — диалоги
                ["Dialog_DeleteCategory_Title"] = "Удаление категории",
                ["Dialog_DeleteCategory_Reserved"] =
                    "Категорию \"Другое\" удалить нельзя — она используется как резервная.",
                ["Dialog_DeleteCategory_InUse"] =
                    "Категория \"{0}\" используется в {1} записях. " +
                    "Сначала измените категорию этих записей, затем удалите её.",
                ["Dialog_DeleteCategory_Failed"] =
                    "Не удалось удалить категорию \"{0}\".",

                // Журнал синхронизации
                ["Sync_InvalidPort"] = "Введите корректный номер порта (1-65535).",
                ["Sync_Starting"] = "Запуск сервера и поиска устройств...",
                ["Sync_UiUpdated"] = "Интерфейс обновлён.",
                ["Sync_EnterServerIp"] = "Введите IP-адрес сервера.",
                ["Sync_Connecting"] = "Подключение к серверу...",
                ["Sync_SearchStarted"] = "Поиск устройств запущен...",
                ["Sync_MulticastLockCaptured"] = "MulticastLock захвачен (Wi-Fi принимает broadcast).",
                ["Sync_MulticastLockFailed"] = "Не удалось захватить MulticastLock: {0}",
                ["Sync_ListeningBroadcast"] = "Слушаем broadcast на порту {0}...",
                ["Sync_JoinedMulticast"] = "Присоединились к multicast-группе {0}...",
                ["Sync_MulticastJoinFailed"] = "Не удалось присоединиться к multicast-группе {0}...",
                ["Sync_UdpListenError"] = "Ошибка UDP-прослушивания: {0}",
                ["Sync_DiscoveryBroadcasterError"] = "Ошибка discovery-рассыльщика: {0}",
                ["Sync_SoBroadcastUnavailable"] = "SO_BROADCAST недоступен ({0}), направленный broadcast отключён.",
                ["Sync_GetLocalIpFailed"] = "GetLocalIPAddress упал: {0}",
                ["Sync_BroadcastTargetsFailed"] = "Не удалось вычислить broadcast-цели: {0}",
                ["Sync_DiscoveryAnnounce"] =
                    "Discovery: анонс {0}:{1}, broadcast-целей: {2}, хостов зондирования: {3}.",
                ["Sync_BroadcastSendError"] = "Ошибка отправки broadcast: {0}",
                ["Sync_ServerStarted"] = "Сервер запущен на порту {0}, ожидание подключения...",
                ["Sync_ClientConnected"] = "Клиент подключён.",
                ["Sync_ReceivedRecords"] = "Получено записей от клиента: {0}",
                ["Sync_LocalDataUpdated"] = "Локальные данные обновлены.",
                ["Sync_ServerStopped"] = "Сервер остановлен.",
                ["Sync_ServerError"] = "Ошибка сервера: {0}",
                ["Sync_ConnectingTo"] = "Подключение к {0}:{1}...",
                ["Sync_SentRecords"] = "Отправлено своих записей: {0}",
                ["Sync_ClientError"] = "Ошибка клиента: {0}",
                ["Sync_ConnectionClosed"] = "Соединение закрыто преждевременно.",

                // Сортировка покупок
                ["Tooltip_Sort"] = "Сортировка",
                ["Sort_ByName"] = "По названию",
                ["Sort_ByAmount"] = "По стоимости",
                ["Sort_ByDate"] = "По дате",

                // Статистика
                ["Tooltip_Statistics"] = "Статистика",
                ["Stats_Title"] = "Статистика",
                ["Stats_PeriodLabel"] = "Период:",
                ["Stats_Tab_Categories"] = "По категориям",
                ["Stats_Tab_Year"] = "За год",
                ["Stats_Tab_Top"] = "Топ расходов",
                ["Stats_NoData"] = "Нет расходов за выбранный период.",
                ["Stats_Total"] = "Итого",
                ["Stats_Share"] = "Доля",
                ["Stats_Category"] = "Категория",
                ["Stats_Amount"] = "Сумма",
                ["Stats_YearTotal"] = "Итого за год",
                ["Stats_AvgMonth"] = "Среднее за месяц",
                ["Stats_MaxMonth"] = "Максимальный месяц",
                ["Stats_MinMonth"] = "Минимальный месяц",
                ["Stats_Records"] = "Записей",
                ["Stats_ByMonth"] = "По месяцам",
                ["Stats_TopHint"] = "Топ самых дорогих покупок",
                ["Stats_TopEmpty"] = "Нет расходов за выбранный период.",
                ["Stats_YearEmpty"] = "Нет расходов за год.",
                ["Stats_Back"] = "Назад",
            }
        };

        // Индексатор для XAML-биндинга: {loc:Loc SomeKey}
        public string this[string key] =>
            Strings.TryGetValue(_language, out var dict) && dict.TryGetValue(key, out var value)
                ? value
                : key;

        // Форматированный доступ (для строк с {0})
        public string Format(string key, params object?[] args) =>
            string.Format(Culture, this[key], args);

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
