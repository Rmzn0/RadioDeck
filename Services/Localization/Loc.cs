using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RadioDeck.Services.Localization;

/// <summary>A language the user interface is available in.</summary>
public sealed record UiLanguage(string Code, string NativeName, bool RightToLeft = false);

/// <summary>
/// User interface texts. Every language is one JSON file (key → text) embedded in the
/// program; English is the reference and the fallback for a missing entry.
/// </summary>
public static class Loc
{
    public const string English = "en";

    /// <summary>In the order shown in the settings.</summary>
    public static IReadOnlyList<UiLanguage> Languages { get; } = new UiLanguage[]
    {
        new("en", "English"),
        new("tr", "Türkçe"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new("pt", "Português (Brasil)"),
        new("it", "Italiano"),
        new("nl", "Nederlands"),
        new("pl", "Polski"),
        new("cs", "Čeština"),
        new("sk", "Slovenčina"),
        new("hu", "Magyar"),
        new("ro", "Română"),
        new("bg", "Български"),
        new("sr", "Srpski"),
        new("hr", "Hrvatski"),
        new("el", "Ελληνικά"),
        new("uk", "Українська"),
        new("ru", "Русский"),
        new("sv", "Svenska"),
        new("nb", "Norsk bokmål"),
        new("da", "Dansk"),
        new("fi", "Suomi"),
        new("ar", "العربية", RightToLeft: true),
        new("fa", "فارسی", RightToLeft: true),
        new("ur", "اردو", RightToLeft: true),
        new("kk", "Қазақша"),
        new("uz", "Oʻzbekcha"),
        new("ky", "Кыргызча"),
        new("tk", "Türkmençe"),
        new("tg", "Тоҷикӣ"),
        new("az", "Azərbaycanca"),
        new("ja", "日本語"),
        new("zh", "简体中文")
    };

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Tables = new(StringComparer.OrdinalIgnoreCase);
    private static volatile IReadOnlyDictionary<string, string> _current = Load(English);
    private static volatile UiLanguage _language = Languages[0];

    /// <summary>Raised after the language changed; may come from any thread.</summary>
    public static event Action? Changed;

    public static UiLanguage Language => _language;
    public static bool IsRightToLeft => _language.RightToLeft;

    /// <summary>The language for a stored setting; empty or unknown = the language of Windows, else English.</summary>
    public static UiLanguage Resolve(string? code)
    {
        if (Find(code) is { } chosen) return chosen;
        for (var culture = CultureInfo.CurrentUICulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            if (Find(culture.Name) is { } exact) return exact;
            if (Find(culture.TwoLetterISOLanguageName) is { } byLanguage) return byLanguage;
        }
        // Norwegian reports "nn" or "no" as well.
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "nn" or "no" ? Find("nb")! : Languages[0];
    }

    /// <summary>Switches the user interface language. Empty = follow Windows.</summary>
    public static void Use(string? code)
    {
        var language = Resolve(code);
        if (language.Code == _language.Code) return;
        _current = Load(language.Code);
        _language = language;
        Changed?.Invoke();
    }

    public static string T(string key)
    {
        if (_current.TryGetValue(key, out var text) && text.Length > 0) return text;
        return Load(English).TryGetValue(key, out var fallback) ? fallback : key;
    }

    public static string T(string key, params object?[] args)
    {
        var format = T(key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            // A broken translation must never take the program down.
            return string.Format(CultureInfo.InvariantCulture, Load(English).TryGetValue(key, out var fallback) ? fallback : key, args);
        }
    }

    /// <summary>All texts of one language as stored (no fallback); used by the checks.</summary>
    public static IReadOnlyDictionary<string, string> Table(string code) => Load(code);

    private static UiLanguage? Find(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : Languages.FirstOrDefault(l => string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, string> Load(string code) => Tables.GetOrAdd(code, static c =>
    {
        try
        {
            var assembly = typeof(Loc).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($".Localization.{c}.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return new Dictionary<string, string>();
            using var stream = assembly.GetManifestResourceStream(name)!;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return new Dictionary<string, string>();
        }
    });
}

/// <summary>Binding source for XAML: <c>{loc:T Key}</c> follows the language while the program runs.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private LocSource()
    {
        Loc.Changed += () =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) Raise();
            else dispatcher.InvokeAsync(Raise);
        };
    }

    public string this[string key] => Loc.T(key);

    public System.Windows.FlowDirection FlowDirection =>
        Loc.IsRightToLeft ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
    }
}

/// <summary><c>Text="{loc:T Some_Key}"</c></summary>
[System.Windows.Markup.MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : System.Windows.Markup.MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    [System.Windows.Markup.ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocSource.Instance,
            Mode = System.Windows.Data.BindingMode.OneWay
        }.ProvideValue(serviceProvider);
}
