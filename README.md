# Finance Tracker

**Cross-platform desktop and mobile application for tracking personal expenses — on Windows and Android.**

Finance Tracker keeps a list of your expenses (purchases) with amounts, dates and categories, shows statistics, can export data to Excel, and can sync data between devices over a local network.

<p>
  <img src="https://img.shields.io/badge/language-C%23-2ea44f" alt="C#">
  <img src="https://img.shields.io/badge/UI-Avalonia%2011.3-68217A" alt="Avalonia 11.3">
  <img src="https://img.shields.io/badge/Platform-Windows%20|%20Android-3ddc84" alt="Windows | Android">
  <img src="https://img.shields.io/badge/Storage-SQLite-2ea44f" alt="SQLite">
  <img src="https://img.shields.io/badge/Sync-P2P%20LAN-00758f" alt="P2P LAN sync">
</p>

---

# EN

## What It Is

Finance Tracker is a cross-platform application for personal expense accounting. It maintains a list of expenses (purchases) with amounts, dates and categories, displays statistics, can export data to Excel, and can sync data between devices on a local network.

## Platforms

- **Windows** — desktop application (.NET 8.0, executable file)
- **Android** — APK (.NET 10 Android, `arm64` / `x64`), `minSdk 24` (Android 7.0+)

## Key Features

- **Expense tracking**: add, edit and delete entries (name, amount in ₽, date, category)
- **Filtering** by month and year with the total amount for the selected period
- **Sorting** by name, amount and date
- **Categories**: create, rename, delete; displayed on entry cards
- **Statistics** for the selected month/year:
  - breakdown of expenses by category (with percentage shares)
  - "top" largest purchases with ranking
  - year total, average monthly spending, record/lowest month, average and number of entries
- **Export and import XLSX** (Excel) — columns: name, amount, date, category
- **Sync between devices over a local network (P2P)**:
  - automatic device discovery (UDP broadcast + multicast + subnet probing)
  - TCP data transfer with incremental sync (since the last update)
  - conflict resolution using last-write-wins (hybrid timestamps); deletions propagate to other devices
- **Settings**: light/dark theme, language switch (Russian/English), hide "spent" amounts
- **Convenience across screens**: swipe gestures for quick editing/deleting entries; on Android — multi-select and bulk deletion; list virtualization for smooth scrolling
- **Easter eggs**: buttons with sounds (burger, chickenburger, patrickstar) and a flashy "WASTED" overlay

## How It Works

The application stores all data locally in an SQLite database. The UI is built with Avalonia and follows a MVVM-like structure, so the interface reacts instantly to data changes. On start-up only one instance is allowed on Windows: launching the app again automatically closes the previous process and releases the data file.

Sync runs without any external servers: devices discover each other in the local network via UDP (broadcast, multicast and subnet probing), then exchange changes over TCP. Each entry carries a hybrid timestamp, so conflicting edits are resolved by the "last write wins" rule. Deletions are also propagated, and only the changes accumulated since the last update are transferred (incremental sync).

## Architecture / Technologies

| Area | Technology |
| --- | --- |
| UI | Avalonia 11.3 (Fluent theme, Inter font), MVVM-like structure |
| Storage | SQLite (Microsoft.Data.Sqlite), data in the `expenses.dat` file next to the application |
| Excel export | Custom lightweight XLSX module built on BCL (works on both Windows and Android) |
| Sync | Pure TCP/UDP sockets without external servers — everything over Wi-Fi/LAN, no internet required |
| Frameworks | .NET 8.0 (Windows), .NET 10 Android (APK) |

## System Requirements

- Windows 10/11 (x64) or Android 7.0+ (API 24)
- For sync: devices on the same local network

## Installation / Build

1. Clone the repository:
   ```bash
   git clone https://github.com/AdVander18/FinanceTracker.git
   ```
2. Open the solution (`FinanceTracker.slnx`) in an IDE or build from the CLI with `dotnet build`.
3. Run the project on Windows or build/deploy the Android APK.

## Data and Privacy

- Single running instance on Windows: on a repeated launch the old process closes automatically and the data file is released.
- All data is stored **locally** on the device; servers and clouds are not used. Sync happens between devices in your own local network only.

---

# RU

# Finance Tracker

## Что это

**Finance Tracker** — кросс-платформенное десктопное и мобильное приложение для учёта личных расходов. Ведёт список трат (покупок) с суммами, датами и категориями, показывает статистику, умеет экспортировать данные в Excel и синхронизировать их между устройствами в локальной сети.

## Платформы

- **Windows** — десктопное приложение (.NET 8.0, исполняемый файл)
- **Android** — APK (.NET 10 Android, `arm64` / `x64`), `minSdk 24` (Android 7.0+)

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

## Как это работает

Все данные хранятся локально в базе данных SQLite. Интерфейс построен на Avalonia и имеет MVVM-подобную структуру, поэтому UI мгновенно реагирует на изменения данных. На Windows приложение запускается только в одном экземпляре: повторный запуск автоматически закрывает старый процесс и освобождает файл данных.

Синхронизация работает без внешних серверов: устройства находят друг друга в локальной сети по UDP (broadcast, multicast и зондирование подсети), затем обмениваются изменениями по TCP. Каждая запись несёт гибридную метку времени, поэтому конфликтные правки разрешаются по правилу «последняя запись побеждает». Удаления также переносятся на другие устройства, а передаются только изменения, накопленные с момента последнего обновления (инкрементальная синхронизация).

## Архитектура / Технологии

| Область | Технология |
| --- | --- |
| UI | Avalonia 11.3 (Fluent-тема, шрифт Inter), MVVM-подобная структура |
| Хранение | SQLite (Microsoft.Data.Sqlite), данные в файле `expenses.dat` рядом с приложением |
| Экспорт Excel | Собственный лёгкий XLSX-модуль на BCL (работает и на Windows, и на Android) |
| Синхронизация | Чистые TCP/UDP сокеты без внешних серверов — всё по Wi-Fi/LAN, интернет не нужен |
| Фреймворки | .NET 8.0 (Windows), .NET 10 Android (APK) |

## Системные требования

- Windows 10/11 (x64) или Android 7.0+ (API 24)
- Для синхронизации: устройства в одной локальной сети

## Установка / сборка

1. Склонируйте репозиторий:
   ```bash
   git clone https://github.com/AdVander18/FinanceTracker.git
   ```
2. Откройте решение (`FinanceTracker.slnx`) в IDE или соберите из командной строки через `dotnet build`.
3. Запустите проект на Windows или соберите/установите Android APK.

## Данные и приватность

- Один запущенный экземпляр на Windows: при повторном запуске старый процесс автоматически закрывается, файл данных освобождается.
- Все данные хранятся **локально** на устройстве; серверы и облака не используются. Синхронизация происходит только между устройствами в вашей собственной локальной сети.
