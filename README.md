EN
## What It Is
Finance Tracker is a cross-platform desktop and mobile application for tracking personal expenses. It maintains a list of expenses (purchases) with amounts, dates, and categories, displays statistics, can export data to Excel, and can sync data between devices on a local network.

## Platforms
- **Windows** (desktop, .NET 8.0, executable file)
- **Android** (APK, .NET 10 Android, arm64/x64), minSdk 24

## Key Features
- **Expense tracking**: add, edit, delete entries (name, amount in ₽, date, category)
- **Filtering** by month and year with the total amount for the period
- **Sorting** by name, amount, date
- **Categories**: create, rename, delete; displayed on entry cards
- **Statistics** for the selected month/year:
  - breakdown of expenses by category (with percentage shares)
  - “top” largest purchases with ranking
  - year total, average monthly spending, record/lowest month, average and number of entries
- **Export and import XLSX** (Excel) — columns: name, amount, date, category
- **Sync** between devices over a local network (P2P):
  - automatic device discovery (UDP broadcast + multicast + subnet probing)
  - TCP data transfer with incremental sync (since the last update)
  - conflict resolution using last-write-wins (hybrid timestamps); deletions are propagated to other devices
- **Settings**: toggle light/dark theme, change language (Russian/English), hide “spent” amounts
- **Convenience** across different screens: swipe gestures for quick editing/deleting entries; on Android — multi-select and bulk deletion; list virtualization for smooth scrolling
- **Easter eggs**: buttons with sounds (burger, chickenburger, patrickstar) and a flashy “WASTED” overlay

## Technologies
- **UI**: Avalonia 11.3 (Fluent theme, Inter font), MVVM-like structure
- **Storage**: SQLite (Microsoft.Data.Sqlite), data in the `expenses.dat` file next to the application
- **Excel export**: custom lightweight XLSX module on BCL (works on both Windows and Android)
- **Sync**: pure TCP/UDP sockets without external servers — all over Wi-Fi/LAN, no internet needed

## System Requirements
- Windows 10/11 (x64) or Android 7.0+ (API 24)
- For sync: devices on the same local network

## Special Features
- Single running instance on Windows: when launched again, the old process automatically closes, and the data file is released
- All data is stored locally; servers and clouds are not used
  
RU
# Finance Tracker — описание для релиза

## Что это
**Finance Tracker** — кросс-платформенное десктопное и мобильное приложение для учёта личных расходов. Ведёт список трат (покупок) с суммами, датами и категориями, показывает статистику, умеет экспортировать данные в Excel и синхронизировать их между устройствами в локальной сети.

## Платформы
- **Windows** (desktop, .NET 8.0, исполняемый файл)
- **Android** (APK, .NET 10 Android, `arm64`/`x64`), `minSdk 24`

## Основные возможности
- **Учёт расходов**: добавление, редактирование, удаление записей (название, сумма в ₽, дата, категория)
- **Фильтрация** по месяцу и году с итоговой суммой за период
- **Сортировка** по названию, сумме, дате
- **Категории**: создание, переименование, удаление; отображаются на карточках записей
- **Статистика** за выбранный месяц/год:
  - разбивка расходов по категориям (с долями в %)
  - «топ» самых крупных покупок с рейтингом
  - итог за год, средний расход в месяц, рекордный/минимальный месяц, среднее и число записей
- **Экспорт и импорт XLSX** (Excel) — колонки: название, сумма, дата, категория
- **Синхронизация между устройствами по локальной сети (P2P)**:
  - автообнаружение устройств (UDP broadcast + multicast + зондирование подсети)
  - передача данных по TCP с инкрементальной синхронизацией (с момента последнего обновления)
  - разрешение конфликтов по принципу «последняя запись побеждает» (гибридные метки времени), удаления переносятся на другие устройства
- **Настройки**: переключение светлой/тёмной темы, смена языка (русский/английский), скрытие «потраченных» сумм
- **Удобство на разных экранах**: свайп-жесты для быстрого редактирования/удаления записей, на Android — мультивыбор и массовое удаление, виртуализация списка для плавной прокрутки
- **Пасхалки**: кнопки со звуками (бургер, куриный бургер, Патрик) и эффектный оверлей «ПОТРАЧЕНО»

## Технологии
- **UI**: Avalonia 11.3 (Fluent-тема, шрифт Inter), MVVM-подобная структура
- **Хранение**: SQLite (Microsoft.Data.Sqlite), данные в файле `expenses.dat` рядом с приложением
- **Экспорт Excel**: собственный лёгкий XLSX-модуль на BCL (работает и на Windows, и на Android)
- **Синхронизация**: чистые TCP/UDP сокеты без внешних серверов — всё по Wi-Fi/LAN, интернет не нужен

## Системные требования
- Windows 10/11 (x64) или Android 7.0+ (API 24)
- Для синхронизации: устройства в одной локальной сети

## Особенности
- Один запущенный экземпляр на Windows: при повторном запуске старый процесс автоматически закрывается, файл данных освобождается
- Все данные хранятся локально; серверы и облака не используются
