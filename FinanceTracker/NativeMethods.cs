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
        }

        private static SqliteConnection? _connection;

        // Только живые записи. Удалённых здесь нет вообще — они вычищаются из БД,
        // а для синхронизации удаления остаётся минимальная метка в _tombstones.
        private static readonly List<ExpenseRecord> _expenses = new();

        // Метки удаления: guid -> версия. Данных покупки здесь нет вообще,
        // только идентификатор и метка времени, чтобы удаление разошлось по сети.
        private static readonly Dictionary<Guid, HybridTimestamp> _tombstones = new();

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
                _tombstones.Clear();
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
                    // Флаг удаления для баз, созданных до разделения на expenses/deleted_expenses
                    cmd.CommandText = "ALTER TABLE expenses ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0;";
                    cmd.ExecuteNonQuery();
                }

                // Метки удаления отдельно от данных покупок: после удаления в expenses
                // не остаётся ничего, кроме guid и метки времени.
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS deleted_expenses (" +
                    "guid TEXT PRIMARY KEY," +
                    "updated_at TEXT NOT NULL);";
                cmd.ExecuteNonQuery();
            }

            // Разовый перенос tombstone'ов прошлых версий: данные удалённых записей
            // стираются из expenses, в deleted_expenses остаётся только guid и метка.
            using (var cmd = _connection.CreateCommand())
            {
                var legacy = new List<(long Id, Guid Guid, HybridTimestamp UpdatedAt)>();
                cmd.CommandText = "SELECT id, guid, updated_at FROM expenses WHERE deleted <> 0;";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        Guid guid = Guid.TryParse(r.GetString(1), out var g) ? g : Guid.NewGuid();
                        if (!HybridTimestamp.TryParse(r.GetString(2), out var ts))
                            ts = new HybridTimestamp(0, 0);
                        legacy.Add((r.GetInt64(0), guid, ts));
                    }
                }

                foreach (var (id, guid, ts) in legacy)
                {
                    cmd.Parameters.Clear();
                    cmd.CommandText =
                        "INSERT OR REPLACE INTO deleted_expenses (guid, updated_at) VALUES ($g, $u);";
                    cmd.Parameters.AddWithValue("$g", guid.ToString());
                    cmd.Parameters.AddWithValue("$u", ts.ToString());
                    cmd.ExecuteNonQuery();

                    cmd.Parameters.Clear();
                    cmd.CommandText = "DELETE FROM expenses WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$id", id);
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
            _tombstones.Clear();

            using (var select = _connection!.CreateCommand())
            {
                select.CommandText = "SELECT guid, updated_at FROM deleted_expenses;";
                using var readerDel = select.ExecuteReader();
                while (readerDel.Read())
                {
                    if (!Guid.TryParse(readerDel.GetString(0), out var guid)) continue;
                    if (!HybridTimestamp.TryParse(readerDel.GetString(1), out var ts))
                        ts = new HybridTimestamp(0, 0);

                    ObserveTimestamp(ts);
                    _tombstones[guid] = ts;
                }
            }

            using var selectExp = _connection.CreateCommand();
            selectExp.CommandText = "SELECT id, guid, name, amount, date, category, updated_at FROM expenses ORDER BY id;";
            using var readerExp = selectExp.ExecuteReader();
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
                    UpdatedAt = updatedAt
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

        // Метка удаления: guid + версия, без данных покупки. Заносится только если
        // такой метки ещё не было или пришла более новая (LWW).
        private static void UpsertTombstoneLocked(Guid guid, HybridTimestamp updatedAt)
        {
            if (_tombstones.TryGetValue(guid, out var current) && current >= updatedAt) return;
            _tombstones[guid] = updatedAt;
            ExecuteNonQuery(
                "INSERT OR REPLACE INTO deleted_expenses (guid, updated_at) VALUES ($g, $u);",
                ("$g", guid.ToString()), ("$u", updatedAt.ToString()));
        }

        private static void RemoveTombstoneLocked(Guid guid)
        {
            if (!_tombstones.Remove(guid)) return;
            ExecuteNonQuery("DELETE FROM deleted_expenses WHERE guid = $g;", ("$g", guid.ToString()));
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
                    Category = category
                });
                return _expenses.Count - 1;
            }
        }

        public static bool UpdateExpense(int index, string name, double amount, string date, string category)
        {
            lock (_syncLock)
            {
                if (_connection is null) return false;
                if (index < 0 || index >= _expenses.Count) return false;

                var rec = _expenses[index];
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
                _expenses[index] = rec;
                return true;
            }
        }

        // Удаление стирает запись из БД полностью. Чтобы удаление разошлось на другие
        // устройства, остаётся только метка в deleted_expenses (guid + версия) — без
        // названия, суммы, даты и категории.
        public static bool DeleteExpense(int index)
        {
            lock (_syncLock)
            {
                if (_connection is null) return false;
                if (index < 0 || index >= _expenses.Count) return false;

                var rec = _expenses[index];
                var updatedAt = NextTimestamp();

                UpsertTombstoneLocked(rec.Guid, updatedAt);

                if (!ExecuteNonQuery("DELETE FROM expenses WHERE id = $id;", ("$id", rec.Id)))
                {
                    // Строка осталась жить — метку удаления откатываем, иначе она
                    // удалит запись на соседних устройствах, оставив её здесь.
                    RemoveTombstoneLocked(rec.Guid);
                    return false;
                }

                _expenses.RemoveAt(index);
                return true;
            }
        }

        /// <summary>
        /// Пакетное удаление всех записей, дата которых попадает в [from; to] включительно.
        /// Всё в одной транзакции: неделя/месяц/год — это сотни строк, а частичное
        /// удаление после сбоя оставило бы БД в неконсистентном состоянии.
        /// Возвращает количество удалённых записей.
        /// </summary>
        public static int DeleteExpensesInRange(DateTime from, DateTime to)
        {
            lock (_syncLock)
            {
                if (_connection is null) return 0;

                // Даты хранятся как "yyyy-MM-dd", поэтому границы сравниваем по датам,
                // а не по строкам: так не зависим от формата импортированных записей.
                var doomed = new List<int>();
                for (int i = 0; i < _expenses.Count; i++)
                {
                    if (TryGetExpenseDate(_expenses[i].Date, out var date) && date >= from && date <= to)
                        doomed.Add(i);
                }
                if (doomed.Count == 0) return 0;

                // Метки удаления собираем отдельно: к _tombstones прикасаемся только
                // после успешного коммита, иначе откат транзакции оставил бы
                // «мёртвые» метки, которые стёрли бы запись при следующей синхронизации.
                var newTombstones = new List<(Guid Guid, HybridTimestamp UpdatedAt)>(doomed.Count);

                using (var tx = _connection.BeginTransaction())
                {
                    using var deleteCmd = _connection.CreateCommand();
                    deleteCmd.Transaction = tx;
                    deleteCmd.CommandText = "DELETE FROM expenses WHERE id = $id;";
                    var deleteId = deleteCmd.Parameters.Add("$id", SqliteType.Integer);

                    using var tombCmd = _connection.CreateCommand();
                    tombCmd.Transaction = tx;
                    tombCmd.CommandText =
                        "INSERT OR REPLACE INTO deleted_expenses (guid, updated_at) VALUES ($g, $u);";
                    var tombGuid = tombCmd.Parameters.Add("$g", SqliteType.Text);
                    var tombUpdatedAt = tombCmd.Parameters.Add("$u", SqliteType.Text);

                    foreach (int index in doomed)
                    {
                        var rec = _expenses[index];
                        var updatedAt = NextTimestamp();

                        deleteId.Value = rec.Id;
                        deleteCmd.ExecuteNonQuery();

                        tombGuid.Value = rec.Guid.ToString();
                        tombUpdatedAt.Value = updatedAt.ToString();
                        tombCmd.ExecuteNonQuery();

                        newTombstones.Add((rec.Guid, updatedAt));
                    }

                    tx.Commit();
                }

                foreach (var (guid, updatedAt) in newTombstones)
                    UpsertTombstoneLocked(guid, updatedAt);

                // С конца к началу, чтобы индексы doomed оставались валидными.
                for (int i = doomed.Count - 1; i >= 0; i--)
                    _expenses.RemoveAt(doomed[i]);

                return doomed.Count;
            }
        }

        // Дата покупки в БД всегда "yyyy-MM-dd"; TryParse — страховка для старых записей.
        private static bool TryGetExpenseDate(string? value, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date))
            {
                return true;
            }
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        public static int GetExpenseCount()
        {
            lock (_syncLock)
            {
                return _expenses.Count;
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
                if (index < 0 || index >= _expenses.Count) return false;

                var rec = _expenses[index];
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
                cmd.CommandText = "SELECT COUNT(*) FROM expenses WHERE category = $n;";
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

        private static ExpenseItem ToSyncItem(ExpenseRecord rec) => new ExpenseItem
        {
            Id = rec.Guid,
            UpdatedAtUtc = rec.UpdatedAt,   // см. ExpenseItem
            Name = rec.Name,
            Amount = rec.Amount,
            Date = rec.Date,
            Category = rec.Category
        };

        /// <summary>Только живые записи — для статистики и экспорта. Меток удаления здесь нет.</summary>
        public static List<ExpenseItem> GetLiveExpenses()
        {
            lock (_syncLock)
            {
                var result = new List<ExpenseItem>(_expenses.Count);
                foreach (var rec in _expenses)
                    result.Add(ToSyncItem(rec));
                return result;
            }
        }

        /// <summary>Полный набор для синхронизации: живые записи плюс метки удаления (Deleted = true).</summary>
        public static List<ExpenseItem> GetForSync()
        {
            lock (_syncLock)
            {
                var result = new List<ExpenseItem>(_expenses.Count + _tombstones.Count);
                foreach (var rec in _expenses)
                    result.Add(ToSyncItem(rec));
                foreach (var (guid, updatedAt) in _tombstones)
                    result.Add(new ExpenseItem { Id = guid, UpdatedAtUtc = updatedAt, Deleted = true });
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
                    result.Add(ToSyncItem(rec));
            }
            foreach (var (guid, updatedAt) in _tombstones)
            {
                if (updatedAt >= lastSync)
                    result.Add(new ExpenseItem { Id = guid, UpdatedAtUtc = updatedAt, Deleted = true });
            }
            return result;
        }

        /// Merge по Id + LWW: побеждает запись с большей HybridTimestamp.
        /// Метка удаления переносится так же, как обычная запись, поэтому удаление
        /// распространяется на другие устройства и не «воскресает». При этом данные
        /// удалённой покупки стираются из БД полностью — остаётся только guid и метка.
        private static void MergeExpensesLocked(IEnumerable<ExpenseItem> incoming)
        {
            if (_connection is null) return;

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

                    if (item.Deleted)
                    {
                        // Удаление с другого устройства: строка уходит из БД целиком.
                        if (!ExecuteNonQuery("DELETE FROM expenses WHERE id = $id;", ("$id", existing.Id)))
                            continue;

                        _expenses.RemoveAt(existingIndex);
                        UpsertTombstoneLocked(guid, incomingUpdatedAt);
                        continue;
                    }

                    if (ExecuteNonQuery(
                        "UPDATE expenses SET name = $n, amount = $a, date = $d, category = $c, updated_at = $u WHERE id = $id;",
                        ("$n", item.Name), ("$a", item.Amount), ("$d", item.Date),
                        ("$c", category), ("$u", incomingUpdatedAt.ToString()),
                        ("$id", existing.Id)))
                    {
                        existing.Name = item.Name;
                        existing.Amount = item.Amount;
                        existing.Date = item.Date;
                        existing.Category = category;
                        existing.UpdatedAt = incomingUpdatedAt;
                        _expenses[existingIndex] = existing;
                    }
                }
                else if (item.Deleted)
                {
                    // Записи у нас нет — сохраняем только метку, чтобы она не вернулась
                    // вместе с данными с какого-нибудь устройства.
                    UpsertTombstoneLocked(guid, incomingUpdatedAt);
                }
                else
                {
                    // Локальная метка удаления новее входящей записи — запись остаётся удалённой.
                    if (_tombstones.TryGetValue(guid, out var tombstoneAt) && tombstoneAt >= incomingUpdatedAt)
                        continue;

                    if (ExecuteNonQuery(
                        "INSERT INTO expenses (guid, name, amount, date, category, updated_at, deleted) VALUES ($g, $n, $a, $d, $c, $u, 0);",
                        ("$g", guid.ToString()), ("$n", item.Name), ("$a", item.Amount),
                        ("$d", item.Date), ("$c", category), ("$u", incomingUpdatedAt.ToString())))
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
                            Category = category
                        });

                        // Метка удаления устарела — убираем её, иначе следующая же
                        // синхронизация с ней снова сотрёт запись.
                        if (_tombstones.ContainsKey(guid))
                            RemoveTombstoneLocked(guid);

                        // Категория пришла с другого устройства — держим таблицу categories в согласии.
                        if (category != "Другое")
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