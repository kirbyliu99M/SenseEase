using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace SenseEase.App.Localization;

/// <summary>
/// XAML markup extension for a single localized string: <c>{loc:T Zh='中文' En='English'}</c>.
/// Returns a live one-way Binding to <see cref="LocalizationManager.Current"/> so the
/// target shows the half for the active language and re-picks the instant the user
/// toggles language — no restart, no per-element code-behind.
///
/// <para>Explicit <see cref="Zh"/>/<see cref="En"/> halves (rather than splitting a
/// "zh / en" literal at runtime) are used on purpose: many source strings contain
/// multiple "/" (colour lists like "灰 / 琥珀 / 紅", or "暈車 / 暈船 / Motion sickness"),
/// which a naive split would mangle. Because the binding source is the static singleton
/// (not an App resource), it also resolves correctly in the bare-Application smoke
/// tests, where there is no Application.Resources dictionary.</para>
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public string Zh { get; set; } = string.Empty;
    public string En { get; set; } = string.Empty;

    public TExtension() { }
    public TExtension(string zh, string en) { Zh = zh; En = en; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(LocalizationManager.Language))
        {
            Source = LocalizationManager.Current,
            Mode = BindingMode.OneWay,
            Converter = new PickConverter(Zh, En),
        };
        return binding.ProvideValue(serviceProvider);
    }

    /// <summary>Picks Zh/En for the bound <see cref="UILanguage"/> value.</summary>
    private sealed class PickConverter : IValueConverter
    {
        private readonly string _zh, _en;
        public PickConverter(string zh, string en) { _zh = zh; _en = en; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is UILanguage lang && lang == UILanguage.English ? _en : _zh;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
