using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FinanceTracker.Models;
using Microsoft.Data.Sqlite;

namespace FinanceTracker.Interop
{
    internal static class NativeMethods
    {
        private const string LastSyncKey = "last_sync";

        private struct ExpenseRecord
        {
            public long Id;                     // локальный rowid SQLite
            public Guid Guid;                   // глобальный Id для синхронизации
            public HybridTimestamp UpdatedAt;   // было DateTime
            public string Name;
            public double Amount;
            public string Date;
            public string Category;
            public bool IsDeleted;              // tombstone: запись удалена, но ещё переносится по сети
        }

        private static SqliteConnection? _connection;
        private static readonly List<ExpenseRecord> _expenses = new();

        // Единая сериализация доступа. UI-поток (Add/Update/Delete/Rename) и фоновые
        // sync-задачи меняют _expenses, _lastTimestamp и БД одновременно, поэтому
        // все операции — под одним Monitor (он реентерабелен, вложенные вызовы безопасны).
        private static readonly object _syncLock = new();

        // "Логические часы" процесса: гарантируют монотонность при равном physicalMs.
        private static HybridTimestamp _lastTimestamp = new HybridTimestamp(0, 0);

        private static HybridTimestamp NextTimestamp()
        {
            var now = HybridTimestamp.Now();
            if (now.PhysicalMs > _lastTimestamp.PhysicalMs)
                _lastTimestamp = now;
            else
                _lastTimestamp = new HybridTimestamp(_lastTimestamp.PhysicalMs, _lastTimestamp.Logical + 1);
            return _lastTimestamp;
        }

        private static void ObserveTimestamp(HybridTimestamp ts)
        {
            if (ts > _lastTimestamp) _lastTimestamp = ts;
        }

        private static void CloseConnection()
        {
            lock (_syncLock)
            {
                if (_connection is not null)
                {
                    try { _connection.Dispose(); } catch { /* ignore */ }
                    _connection = null;
                }
                _expenses.Clear();
                _lastTimestamp = new HybridTimestamp(0, 0);
            }
        }

        private static bool OpenAndCreate(string dataFilePath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dataFilePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            _connection = new SqliteConnection(connectionString);
            _connection.Open();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS expenses (" +
                    "id INTEGER PRIMARY KEY AUTOINCREMENT," +
                    "guid TEXT NOT NULL DEFAULT ''," +
                    "name TEXT NOT NULL," +
                    "amount REAL NOT NULL," +
                    "date TEXT NOT NULL," +
                    "category TEXT NOT NULL DEFAULT 'Другое'," +
                    "updated_at TEXT NOT NULL DEFAULT '0:0'," +
                    "deleted INTEGER NOT NULL DEFAULT 0);";
                cmd.ExecuteNonQuery();

                // Миграция существующих баз
                bool hasCategory = false, hasGuid = false, hasUpdatedAt = false, hasDeleted = false;
                using (var prgCmd = _connection.CreateCommand())
                {
                    prgCmd.CommandText = "PRAGMA table_info(expenses);";
                    using var reader = prgCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var col = reader.GetString(1);
                        if (col == "category") hasCategory = true;
                        else if (col == "guid") hasGuid = true;
                        else if (col == "updated_at") hasUpdatedAt = true;
                        else if (col == "deleted") hasDeleted = true;
                    }
                }

                if (!hasCategory)
                {
                    cmd.CommandText = "ALTER TABLE expenses ADD COLUMN category TEXT NOT NULL DEFAULT 'Другое';";
                    cmd.ExecuteNonQuery();
                }

                if (!hasGuid)
                {
                    cmd.CommandText = "ALTER TABLE expenses ADD COLUMN guid TEXT NOT NULL DEFAULT '';";
                    cmd.ExecuteNonQuery();

                    var ids = new List<long>();
                    cmd.CommandText = "SELECT id FROM expenses WHERE guid = '';";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) ids.Add(r.GetInt64(0));

                    foreach (var rowId in ids)
                    {
                        cmd.CommandText = "UPDATE expenses SET guid = $g WHERE id = $id;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("$g", Guid.NewGuid().ToString());
                        cmd.Parameters.AddWithValue("$id", rowId);
                        cmd.ExecuteNonQuery();
                    }
                }

                if (!hasUpdatedAt)
                {
                    // Эпоха — чтобы первая синхронизация подтянула все старые записи
                    cmd.CommandText = "ALTER TABLE expenses ADD COLUMN updated_at TEXT NOT NULL DEFAULT '0:0';";
                    cmd.ExecuteNonQuery();
                }

                if (!hasDeleted)
                {
                    // Тombstone-флаг: удаления теперь синхронизируются
                    cmd.CommandText = "ALTER TABLE expenses ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0;";
                    cmd.ExecuteNonQuery();
                }
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS categories (" +
                    "id INTEGER PRIMARY KEY AUTOINCREMENT," +
                    "name TEXT NOT NULL UNIQUE);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "INSERT OR IGNORE INTO categories (name) VALUES ('Продукты'), ('Здоровье'), ('Другое');";
                cmd.ExecuteNonQuery();
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS sync_meta (" +
                    "key TEXT PRIMARY KEY," +
                    "value TEXT NOT NULL);";
                cmd.ExecuteNonQuery();
            }

            _expenses.Clear();
            using var select = _connection!.CreateCommand();
            select.CommandText = "SELECT id, guid, name, amount, date, category, updated_at, deleted FROM expenses ORDER BY id;";
            using var readerExp = select.ExecuteReader();
            while (readerExp.Read())
            {
                Guid guid = Guid.TryParse(readerExp.GetString(1), out var g) ? g : Guid.NewGuid();

                // Читаем как HybridTimestamp (поддерживает и старый ISO-формат).
                if (!HybridTimestamp.TryParse(readerExp.GetString(6), out var updatedAt))
                    updatedAt = new HybridTimestamp(0, 0);

                ObserveTimestamp(updatedAt);

                _expenses.Add(new ExpenseRecord
                {
                    Id = readerExp.GetInt64(0),
                    Guid = guid,
                    Name = readerExp.GetString(2),
                    Amount = readerExp.GetDouble(3),
                    Date = readerExp.GetString(4),
                    Category = readerExp.GetString(5),
                    UpdatedAt = updatedAt,
                    IsDeleted = readerExp.GetInt64(7) != 0
                });
            }

            return true;
        }

        public static bool InitializeStorage(string dataFilePath)
        {
            CloseConnection();
            lock (_syncLock)
            {
                try
                {
                    if (!OpenAndCreate(dataFilePath)) { CloseConnection(); return false; }
                    return true;
                }
                catch
                {
                    CloseConnection();
                    return false;
                }
            }
        }

        private static bool ExecuteNonQuery(string sql, params (string Name, object Value)[] parameters)
        {
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (name, value) in parameters)
                    cmd.Parameters.AddWithValue(name, value);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch { return false; }
        }

        // Преобразует индекс в "видимом" списке (без tombstone) в индекс в _expenses.
        // Пока запись в tombstone, UI её не видит, но индексы остаются стабильными.
        private static int VisibleToFullIndex(int visibleIndex)
        {
            if (visibleIndex < 0) return -1;
            int seen = 0;
            for (int i = 0; i < _expenses.Count; i++)
            {
                if (_expenses[i].IsDeleted) continue;
                if (seen == visibleIndex) return i;
                seen++;
            }
            return -1;
        }

        public static int AddExpense(string name, double amount, string date, string category = "Другое")
        {
            lock (_syncLock)
            {
                if (_connection is null) return -1;

                var guid = Guid.NewGuid();
                var updatedAt = NextTimestamp();

                if (!ExecuteNonQuery(
                    "INSERT INTO expenses (guid, name, amount, date, category, updated_at, deleted) VALUES ($g, $n, $a, $d, $c, $u, 0);",
                    ("$g", guid.ToString()), ("$n", name), ("$a", amount), ("$d", date),
                    ("$c", category), ("$u", updatedAt.ToString())))
                {
                    return -1;
                }

                long id;
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT last_insert_rowid();";
                    id = Convert.ToInt64(cmd.ExecuteScalar());
                }

                _expenses.Add(new ExpenseRecord
                {
                    Id = id,
                    Guid = guid,
                    UpdatedAt = updatedAt,
                    Name = name,
                    Amount = amount,
                    Date = date,
                    Category = category,
                    IsDeleted = false
                });
                return _expenses.Count - 1;
            }
        }

        public static bool UpdateExpense(int index, string name, double amount, string date, string category)
        {
            lock (_syncLock)
            {
                if (_connection is null) return false;
                int full = VisibleToFullIndex(index);
                if (full < 0) return false;

                var rec = _expenses[full];
                var updatedAt = NextTimestamp();

                if (!ExecuteNonQuery(
                    "UPDATE expenses SET name = $n, amount = $a, date = $d, category = $c, updated_at = $u WHERE id = $id;",
                    ("$n", name), ("$a", amount), ("$d", date), ("$c", category),
                    ("$u", updatedAt.ToString()), ("$id", rec.Id)))
                {
                    return false;
                }

                rec.Name = name;
                rec.Amount = amount;
                rec.Date = date;
                rec.Category = category;
                rec.UpdatedAt = updatedAt;
                _expenses[full] = rec;
                return true;
            }
        }

        // Удаление превращается в tombstone: запись остаётся в БД (для переноса удаления
        // на другие устройства), но скрывается из всех UI-методов.
        public static bool DeleteExpense(int index)
        {
            lock (_syncLock)
            {
                if (_connection is null) return false;
                int full = VisibleToFullIndex(index);
                if (full < 0) return false;

                var rec = _expenses[full];
                if (rec.IsDeleted) return false;

                var updatedAt = NextTimestamp();
                if (!ExecuteNonQuery(
                    "UPDATE expenses SET deleted = 1, updated_at = $u WHERE id = $id;",
                    ("$u", updatedAt.ToString()), ("$id", rec.Id)))
                    return false;

                rec.IsDeleted = true;
                rec.UpdatedAt = updatedAt;
                _expenses[full] = rec;
                return true;
            }
        }

        public static int GetExpenseCount()
        {
            lock (_syncLock)
            {
                int count = 0;
                foreach (var rec in _expenses)
                    if (!rec.IsDeleted) count++;
                return count;
            }
        }

        public static bool GetExpenseByIndex(
            int index, StringBuilder nameBuffer, int nameBufferSize,
            out double amount, StringBuilder dateBuffer, int dateBufferSize,
            StringBuilder categoryBuffer, int categoryBufferSize)
        {
            lock (_syncLock)
            {
                amount = 0.0;
                int full = VisibleToFullIndex(index);
                if (full < 0) return false;

                var rec = _expenses[full];
                nameBuffer?.Clear(); nameBuffer?.Append(rec.Name);
                amount = rec.Amount;
                dateBuffer?.Clear(); dateBuffer?.Append(rec.Date);
                categoryBuffer?.Clear(); categoryBuffer?.Append(rec.Category);
                return true;
            }
        }

        public static double GetMonthlyTotal(int month, int year)
        {
            lock (_syncLock)
            {
                double total = 0.0;
                string yy = year.ToString();
                string mm = month.ToString("00");
                foreach (var rec in _expenses)
                {
                    if (rec.IsDeleted) continue;
                    if (rec.Date is not null && rec.Date.Length >= 10
                        && rec.Date.Substring(0, 4) == yy
                        && rec.Date.Substring(5, 2) == mm)
                    {
                        total += rec.Amount;
                    }
                }
                return total;
            }
        }

        public static void CleanupStorage() => CloseConnection();

        // ====== Категории ======
        public static List<string> GetCategories()
        {
            lock (_syncLock)
            {
                var result = new List<string>();
                if (_connection is null) return result;
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT name FROM categories ORDER BY id;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add(reader.GetString(0));
                return result;
            }
        }

        private static bool CategoryExists(string name)
        {
            if (_connection is null) return false;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM categories WHERE name = $n;";
            cmd.Parameters.AddWithValue("$n", name);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        public static bool AddCategory(string name)
        {
            lock (_syncLock)
            {
                if (string.IsNullOrWhiteSpace(name)) return false;
                name = name.Trim();
                if (CategoryExists(name)) return false;
                return ExecuteNonQuery("INSERT INTO categories (name) VALUES ($n);", ("$n", name));
            }
        }

        public static int GetCategoryUsageCount(string name)
        {
            lock (_syncLock)
            {
                if (_connection is null) return 0;
                if (string.IsNullOrWhiteSpace(name)) return 0;
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM expenses WHERE category = $n AND deleted = 0;";
                cmd.Parameters.AddWithValue("$n", name.Trim());
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public static bool DeleteCategory(string name)
        {
            lock (_syncLock)
            {
                if (string.IsNullOrWhiteSpace(name)) return false;
                name = name.Trim();
                if (string.Equals(name, "Другое", StringComparison.OrdinalIgnoreCase)) return false;
                return ExecuteNonQuery("DELETE FROM categories WHERE name = $n;", ("$n", name));
            }
        }

        public static bool RenameCategory(string oldName, string newName)
        {
            lock (_syncLock)
            {
                if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
                oldName = oldName.Trim();
                newName = newName.Trim();
                if (oldName == newName) return true;
                if (CategoryExists(newName)) return false;

                bool ok = ExecuteNonQuery("UPDATE categories SET name = $new WHERE name = $old;",
                    ("$new", newName), ("$old", oldName));
                if (!ok) return false;

                var now = NextTimestamp();
                ok = ExecuteNonQuery(
                    "UPDATE expenses SET category = $new, updated_at = $u WHERE category = $old;",
                    ("$new", newName), ("$old", oldName),
                    ("$u", now.ToString()));
                if (!ok) return false;

                for (int i = 0; i < _expenses.Count; i++)
                {
                    var rec = _expenses[i];
                    if (rec.Category == oldName)
                    {
                        rec.Category = newName;
                        rec.UpdatedAt = now;
                        _expenses[i] = rec;
                    }
                }
                return true;
            }
        }

        // ====== Синхронизация ======
        public static List<ExpenseItem> GetAllExpenses()
        {
            lock (_syncLock)
            {
                var result = new List<ExpenseItem>(_expenses.Count);
                foreach (var rec in _expenses)
                {
                    result.Add(new ExpenseItem
                    {
                        Id = rec.Guid,
                        UpdatedAtUtc = rec.UpdatedAt,   // см. ExpenseItem
                        Name = rec.Name,
                        Amount = rec.Amount,
                        Date = rec.Date,
                        Category = rec.Category,
                        Deleted = rec.IsDeleted
                    });
                }
                return result;
            }
        }

        /// Атомарно: применяет входящие записи и отдаёт дельту с момента lastSync.
        /// Нужно на сервере, чтобы параллельные клиенты не перемежали merge и чтение.
        public static List<ExpenseItem> MergeAndGetSince(HybridTimestamp lastSync, IEnumerable<ExpenseItem> incoming)
        {
            lock (_syncLock)
            {
                MergeExpensesLocked(incoming);
                return GetExpensesSinceLocked(lastSync);
            }
        }

        /// Применяет входящие записи (клиентская сторона).
        public static void MergeExpenses(IEnumerable<ExpenseItem> incoming)
        {
            lock (_syncLock) { MergeExpensesLocked(incoming); }
        }

        // Возвращает записи, изменённые начиная с метки. >= (а не >), чтобы запись,
        // созданная на сервере с меткой ровно lastSync, не потерялась навсегда.
        private static List<ExpenseItem> GetExpensesSinceLocked(HybridTimestamp lastSync)
        {
            var result = new List<ExpenseItem>();
            foreach (var rec in _expenses)
            {
                if (rec.UpdatedAt >= lastSync)
                {
                    result.Add(new ExpenseItem
                    {
                        Id = rec.Guid,
                        UpdatedAtUtc = rec.UpdatedAt,
                        Name = rec.Name,
                        Amount = rec.Amount,
                        Date = rec.Date,
                        Category = rec.Category,
                        Deleted = rec.IsDeleted
                    });
                }
            }
            return result;
        }

        /// Merge по Id + LWW: побеждает запись с большей HybridTimestamp.
        /// Удалённые записи (tombstone) переносятся так же, как остальные, поэтому
        /// удаление распространяется на другие устройства и не «воскресает».
        private static void MergeExpensesLocked(IEnumerable<ExpenseItem> incoming)
        {
            foreach (var item in incoming)
            {
                string category = string.IsNullOrWhiteSpace(item.Category) ? "Другое" : item.Category;
                var guid = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;

                // Если отправитель не задал метку — выдаём локальную.
                var incomingUpdatedAt = item.UpdatedAtUtc == default
                    ? NextTimestamp()
                    : item.UpdatedAtUtc;

                // Учитываем входящие часы, чтобы наши будущие метки были строго больше.
                ObserveTimestamp(incomingUpdatedAt);

                int existingIndex = _expenses.FindIndex(rec => rec.Guid == guid);

                if (existingIndex >= 0)
                {
                    var existing = _expenses[existingIndex];

                    // LWW: пропускаем входящую запись, если локальная новее
                    if (existing.UpdatedAt >= incomingUpdatedAt)
                        continue;

                    if (ExecuteNonQuery(
                        "UPDATE expenses SET name = $n, amount = $a, date = $d, category = $c, updated_at = $u, deleted = $x WHERE id = $id;",
                        ("$n", item.Name), ("$a", item.Amount), ("$d", item.Date),
                        ("$c", category), ("$u", incomingUpdatedAt.ToString()),
                        ("$x", item.Deleted ? 1 : 0), ("$id", existing.Id)))
                    {
                        existing.Name = item.Name;
                        existing.Amount = item.Amount;
                        existing.Date = item.Date;
                        existing.Category = category;
                        existing.UpdatedAt = incomingUpdatedAt;
                        existing.IsDeleted = item.Deleted;
                        _expenses[existingIndex] = existing;
                    }
                }
                else
                {
                    if (ExecuteNonQuery(
                        "INSERT INTO expenses (guid, name, amount, date, category, updated_at, deleted) VALUES ($g, $n, $a, $d, $c, $u, $x);",
                        ("$g", guid.ToString()), ("$n", item.Name), ("$a", item.Amount),
                        ("$d", item.Date), ("$c", category),
                        ("$u", incomingUpdatedAt.ToString()), ("$x", item.Deleted ? 1 : 0)))
                    {
                        long id;
                        using (var cmd = _connection!.CreateCommand())
                        {
                            cmd.CommandText = "SELECT last_insert_rowid();";
                            id = Convert.ToInt64(cmd.ExecuteScalar());
                        }
                        _expenses.Add(new ExpenseRecord
                        {
                            Id = id,
                            Guid = guid,
                            UpdatedAt = incomingUpdatedAt,
                            Name = item.Name,
                            Amount = item.Amount,
                            Date = item.Date,
                            Category = category,
                            IsDeleted = item.Deleted
                        });

                        // Категория пришла с другого устройства — держим таблицу categories в согласии.
                        if (!item.Deleted && category != "Другое")
                            EnsureCategory(category);
                    }
                }
            }
        }

        private static void EnsureCategory(string name)
        {
            ExecuteNonQuery("INSERT OR IGNORE INTO categories (name) VALUES ($n);", ("$n", name));
        }

        // ====== Водяной знак последней синхронизации (для инкрементальной выдачи) ======
        public static HybridTimestamp? GetLastSyncTime()
        {
            lock (_syncLock)
            {
                if (_connection is null) return null;
                try
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "SELECT value FROM sync_meta WHERE key = $k;";
                    cmd.Parameters.AddWithValue("$k", LastSyncKey);
                    var value = cmd.ExecuteScalar() as string;
                    if (value is not null && HybridTimestamp.TryParse(value, out var ts))
                        return ts;
                    return null;
                }
                catch { return null; }
            }
        }

        public static void AdvanceLastSync(IEnumerable<ExpenseItem> seen)
        {
            lock (_syncLock)
            {
                if (_connection is null) return;
                try
                {
                    HybridTimestamp max = GetLastSyncTime() ?? default;
                    foreach (var item in seen)
                    {
                        if (item.UpdatedAtUtc > max)
                            max = item.UpdatedAtUtc;
                    }
                    ExecuteNonQuery(
                        "INSERT INTO sync_meta (key, value) VALUES ($k, $v) " +
                        "ON CONFLICT(key) DO UPDATE SET value = $v;",
                        ("$k", LastSyncKey), ("$v", max.ToString()));
                }
                catch { /* ignore */ }
            }
        }

        // ====== Настройки (пары ключ-значение в таблице sync_meta) ======
        public static bool GetBoolSetting(string key, bool defaultValue)
        {
            lock (_syncLock)
            {
                if (_connection is null) return defaultValue;
                try
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "SELECT value FROM sync_meta WHERE key = $k;";
                    cmd.Parameters.AddWithValue("$k", key);
                    var value = cmd.ExecuteScalar() as string;
                    return value is not null && bool.TryParse(value, out var result) ? result : defaultValue;
                }
                catch { return defaultValue; }
            }
        }

        public static void SetBoolSetting(string key, bool value)
        {
            lock (_syncLock)
            {
                if (_connection is null) return;
                ExecuteNonQuery(
                    "INSERT INTO sync_meta (key, value) VALUES ($k, $v) " +
                    "ON CONFLICT(key) DO UPDATE SET value = $v;",
                    ("$k", key), ("$v", value.ToString()));
            }
        }

        public static string GetStringSetting(string key, string defaultValue)
        {
            lock (_syncLock)
            {
                if (_connection is null) return defaultValue;
                try
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "SELECT value FROM sync_meta WHERE key = $k;";
                    cmd.Parameters.AddWithValue("$k", key);
                    return cmd.ExecuteScalar() as string ?? defaultValue;
                }
                catch { return defaultValue; }
            }
        }

        public static void SetStringSetting(string key, string value)
        {
            lock (_syncLock)
            {
                if (_connection is null) return;
                ExecuteNonQuery(
                    "INSERT INTO sync_meta (key, value) VALUES ($k, $v) " +
                    "ON CONFLICT(key) DO UPDATE SET value = $v;",
                    ("$k", key), ("$v", value));
            }
        }
    }
}