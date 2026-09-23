using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using FinanceTracker.Models; // HybridTimestamp лежит в этом же namespace — можно убрать

namespace FinanceTracker.Models
{
    public class ExpenseItem : INotifyPropertyChanged
    {
        private Guid _id;
        private HybridTimestamp _updatedAtUtc;
        private string _name = string.Empty;
        private double _amount;
        private string _date = string.Empty;
        private string _category = string.Empty;
        private bool _isSelected;
        private bool _deleted;

        /// <summary>Глобально уникальный идентификатор записи (для merge по Id).</summary>
        public Guid Id
        {
            get => _id;
            set { if (_id != value) { _id = value; OnPropertyChanged(); } }
        }

        /// <summary>Версия записи (гибридные часы). Побеждает большая (LWW).</summary>
        public HybridTimestamp UpdatedAtUtc
        {
            get => _updatedAtUtc;
            set
            {
                if (!_updatedAtUtc.Equals(value))
                {
                    _updatedAtUtc = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        public double Amount
        {
            get => _amount;
            set { if (_amount != value) { _amount = value; OnPropertyChanged(); } }
        }

        public string Date
        {
            get => _date;
            set { if (_date != value) { _date = value; OnPropertyChanged(); } }
        }

        public string Category
        {
            get => _category;
            set { if (_category != value) { _category = value; OnPropertyChanged(); } }
        }

        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        /// Tombstone-флаг: сериализуется и синхронизируется (удаления переносятся на другие устройства).
        public bool Deleted
        {
            get => _deleted;
            set { if (_deleted != value) { _deleted = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}