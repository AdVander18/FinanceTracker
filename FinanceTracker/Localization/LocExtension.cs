using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace FinanceTracker.Localization
{
    // XAML-разметка: {loc:Loc SomeKey} — превращается в однонаправленный
    // биндинг на индексатор Localizer.Instance[Key], обновляется автоматически
    // при смене языка.
    public class LocExtension : MarkupExtension
    {
        public string Key { get; set; } = string.Empty;

        public LocExtension() { }
        public LocExtension(string key) { Key = key; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return new Binding
            {
                Source = Localizer.Instance,
                Path = $"[{Key}]",
                Mode = BindingMode.OneWay
            };
        }
    }
}