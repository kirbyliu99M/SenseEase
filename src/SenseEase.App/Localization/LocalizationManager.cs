using System;
using System.ComponentModel;

namespace SenseEase.App.Localization;

/// <summary>The two UI languages SenseEase ships. Traditional Chinese is the app's
/// primary language (zh-Hant); English (US) is the toggle alternative.</summary>
public enum UILanguage
{
    ChineseTraditional,
    English,
}

/// <summary>
/// Process-wide UI-language state for the live EN / 繁體中文 toggle. A single
/// INotifyPropertyChanged singleton that every localized surface binds to — directly,
/// or via the <c>{loc:T}</c> markup extension and the language-aware tier converters —
/// so flipping the language updates the whole UI without a restart.
///
/// <para>Deliberately self-contained: it does NOT read
/// <see cref="Configuration.UserPreferences"/> at construction, so it is safe to touch
/// from the bare-Application smoke tests (it simply starts at the
/// <see cref="UILanguage.ChineseTraditional"/> default). App startup sets
/// <see cref="Language"/> from persisted prefs; the Settings toggle writes both this
/// singleton and the prefs file.</para>
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    private static readonly LocalizationManager _current = new();

    /// <summary>The process-wide instance every localized binding points at.</summary>
    public static LocalizationManager Current => _current;

    private UILanguage _language = UILanguage.ChineseTraditional;

    /// <summary>The active UI language. Setting it raises PropertyChanged for both
    /// <see cref="Language"/> and <see cref="IsEnglish"/> so bound surfaces re-pick
    /// their text immediately.</summary>
    public UILanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, _languageArgs);
            PropertyChanged?.Invoke(this, _isEnglishArgs);
        }
    }

    /// <summary>Convenience flag for triggers/bindings that just need a bool.</summary>
    public bool IsEnglish => _language == UILanguage.English;

    /// <summary>Pick the half matching the current language. The single helper every
    /// code-behind / converter uses, so the language branch lives in one place.</summary>
    public string Pick(string zh, string en) => _language == UILanguage.English ? en : zh;

    /// <summary>Tolerant parse of a persisted <c>language_mode</c> string. Unknown /
    /// malformed values fall back to the Traditional-Chinese default rather than
    /// throwing — mirrors the UserPreferences theme-mode posture.</summary>
    public static UILanguage Parse(string? raw)
        => Enum.TryParse<UILanguage>(raw, ignoreCase: true, out var m) ? m : UILanguage.ChineseTraditional;

    public event PropertyChangedEventHandler? PropertyChanged;
    private static readonly PropertyChangedEventArgs _languageArgs = new(nameof(Language));
    private static readonly PropertyChangedEventArgs _isEnglishArgs = new(nameof(IsEnglish));
}
