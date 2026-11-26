using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnugWin.Core.PhoneScreen;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceKind
{
    [JsonPropertyName("phone")]
    Phone = 0,
    [JsonPropertyName("tablet")]
    Tablet = 1,
    [JsonPropertyName("computer")]
    Computer = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenOrientation
{
    [JsonPropertyName("portrait")]
    Portrait = 0,
    [JsonPropertyName("landscape")]
    Landscape = 1
}

public static class DeviceKindExtensions
{
    public static bool IsTouch(this DeviceKind kind) => kind is DeviceKind.Phone or DeviceKind.Tablet;

    public static ScreenOrientation UsualOrientation(this DeviceKind kind) => kind switch
    {
        DeviceKind.Phone => ScreenOrientation.Portrait,
        _ => ScreenOrientation.Landscape
    };

    public static IReadOnlyList<ScreenOrientation> AllowedOrientations(this DeviceKind kind) => kind switch
    {
        DeviceKind.Computer => [ScreenOrientation.Landscape],
        _ => [ScreenOrientation.Portrait, ScreenOrientation.Landscape]
    };

    public static IReadOnlySet<DeviceKind> SwappableKinds(this DeviceKind kind) => kind.IsTouch()
        ? new HashSet<DeviceKind> { DeviceKind.Phone, DeviceKind.Tablet }
        : new HashSet<DeviceKind> { DeviceKind.Computer };

    public static string DisplayName(this DeviceKind kind) => kind switch
    {
        DeviceKind.Phone => "手机",
        DeviceKind.Tablet => "平板",
        DeviceKind.Computer => "电脑",
        _ => "设备"
    };

    public static string DisplayName(this ScreenOrientation orientation) => orientation switch
    {
        ScreenOrientation.Portrait => "竖屏",
        ScreenOrientation.Landscape => "横屏",
        _ => string.Empty
    };
}

public sealed record PhoneScreenSpecData(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("points")] int? Points = null,
    [property: JsonPropertyName("notch")] int? Notch = null)
{
    [JsonIgnore]
    public bool IsValid => Width > 0 && Height > 0 && Width <= Height && (Points ?? 1) > 0 && (Notch ?? 0) >= 0;

    /// <summary>
    /// Catalog and config store short-edge first; UI renders and edits width × height in screen orientation.
    /// </summary>
    public (int Width, int Height) Dimensions(ScreenOrientation orientation) =>
        orientation == ScreenOrientation.Portrait ? (Width, Height) : (Height, Width);

    public string SizeText(ScreenOrientation orientation = ScreenOrientation.Portrait)
    {
        var (w, h) = Dimensions(orientation);
        return $"{w} × {h}";
    }

    public static PhoneScreenSpecData? Custom(int? a, int? b)
    {
        if (a is null || b is null) return null;
        if (a < 1 || a > 20000 || b < 1 || b > 20000) return null;
        return new PhoneScreenSpecData(Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value));
    }
}

public sealed record PhoneModel(
    string Brand,
    string Name,
    PhoneScreenSpecData Screen,
    DeviceKind Kind = DeviceKind.Phone)
{
    public string Id => $"{Brand} {Name}";
}

public sealed record PhoneChoice(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("screen")] PhoneScreenSpecData Screen,
    [property: JsonPropertyName("kind")] DeviceKind Kind = DeviceKind.Phone)
{
    public static readonly PhoneChoice Standard = new(
        "Apple iPhone 17 Pro Max",
        new PhoneScreenSpecData(1320, 2868, 440),
        DeviceKind.Phone);

    public static readonly PhoneChoice StandardTablet = new(
        "Apple iPad Pro 11 英寸（M4）",
        new PhoneScreenSpecData(1668, 2420, 834),
        DeviceKind.Tablet);

    public static readonly PhoneChoice StandardComputer = new(
        "通用 1080p",
        new PhoneScreenSpecData(1080, 1920),
        DeviceKind.Computer);

    public static PhoneChoice FromModel(PhoneModel model) => new(model.Id, model.Screen, model.Kind);
}

public sealed class PhoneCatalogException : Exception
{
    public PhoneCatalogException(string message) : base($"机型库：{message}") { }
}

public sealed class PhoneCatalog
{
    public sealed record Brand(
        string Name,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<PhoneModel> Models,
        DeviceKind Kind = DeviceKind.Phone)
    {
        public string SectionId => $"{Kind}/{Name}";
    }

    public static readonly PhoneCatalog Empty = new(
        Array.Empty<Brand>(),
        new Dictionary<string, PhoneModel>(StringComparer.Ordinal));

    public IReadOnlyList<Brand> Brands { get; }
    private readonly IReadOnlyDictionary<string, PhoneModel> _modelsById;

    public IEnumerable<PhoneModel> Models => Brands.SelectMany(b => b.Models);

    private PhoneCatalog(IReadOnlyList<Brand> brands, IReadOnlyDictionary<string, PhoneModel> modelsById)
    {
        Brands = brands;
        _modelsById = modelsById;
    }

    public static PhoneCatalog FromJson(string json)
    {
        RawFile? file;
        try
        {
            file = JsonSerializer.Deserialize<RawFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            throw new PhoneCatalogException($"解不开：{ex.Message}");
        }

        if (file is null)
            throw new PhoneCatalogException("内容为空");

        var seenModels = new HashSet<string>(StringComparer.Ordinal);
        var modelsById = new Dictionary<string, PhoneModel>(StringComparer.Ordinal);
        var result = new List<Brand>();

        // Upstream v2.6 device-models.json schema: { "phone": [...], "tablet": [...], "computer": [...] }
        if (file.Phone is not null || file.Tablet is not null || file.Computer is not null)
        {
            ParseSection(file.Phone, DeviceKind.Phone, seenModels, modelsById, result);
            ParseSection(file.Tablet, DeviceKind.Tablet, seenModels, modelsById, result);
            ParseSection(file.Computer, DeviceKind.Computer, seenModels, modelsById, result);
            return new PhoneCatalog(result, modelsById);
        }

        // Legacy phone-models.json schema: { "brands": [...] }
        if (file.Brands is null)
            throw new PhoneCatalogException("缺少 phone / brands 字段");

        ParseSection(file.Brands, DeviceKind.Phone, seenModels, modelsById, result);
        return new PhoneCatalog(result, modelsById);
    }

    private static void ParseSection(
        List<RawBrand>? section,
        DeviceKind kind,
        HashSet<string> seenModels,
        Dictionary<string, PhoneModel> modelsById,
        List<Brand> output)
    {
        if (section is null) return;
        var seenBrands = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawBrand in section)
        {
            if (string.IsNullOrEmpty(rawBrand.Name) || rawBrand.Models is null || rawBrand.Models.Count == 0)
                throw new PhoneCatalogException($"品牌「{rawBrand.Name}」没有名字或没有机型");

            if (!seenBrands.Add(Normalize(rawBrand.Name)))
                throw new PhoneCatalogException($"{kind} 里品牌「{rawBrand.Name}」重复了");

            var models = new List<PhoneModel>();
            foreach (var entry in rawBrand.Models)
            {
                if (entry.Screen is null || entry.Screen.Count != 2)
                    throw new PhoneCatalogException($"{rawBrand.Name} {entry.Name}：screen 要写成 [短边, 长边]");

                var screen = new PhoneScreenSpecData(entry.Screen[0], entry.Screen[1], entry.Points, entry.Notch);
                var model = new PhoneModel(rawBrand.Name, entry.Name ?? string.Empty, screen, kind);
                if (!screen.IsValid)
                    throw new PhoneCatalogException($"{model.Id}：分辨率要是正数、短边在前");

                var allNames = entry.LegacyNames is { Count: > 0 }
                    ? new[] { entry.Name ?? string.Empty }.Concat(entry.LegacyNames)
                    : [entry.Name ?? string.Empty];
                foreach (string rawName in allNames)
                {
                    if (string.IsNullOrEmpty(Normalize(rawName)))
                        throw new PhoneCatalogException($"{model.Id}：型号名不能为空");
                    string fullId = $"{rawBrand.Name} {rawName}";
                    if (!seenModels.Add(Normalize($"{kind}/{fullId}")))
                        throw new PhoneCatalogException($"{fullId}：重复了");
                    modelsById.TryAdd(fullId, model);
                }

                models.Add(model);
            }

            output.Add(new Brand(rawBrand.Name, rawBrand.Aliases ?? new List<string>(), models, kind));
        }
    }

    public static PhoneCatalog LoadFromFile(string path) => FromJson(System.IO.File.ReadAllText(path));

    public IReadOnlyList<Brand> BrandsIn(params DeviceKind[] kinds)
    {
        if (kinds.Length == 0) return Brands;
        var set = new HashSet<DeviceKind>(kinds);
        return Brands.Where(b => set.Contains(b.Kind)).ToList();
    }

    public PhoneModel? FindModel(string id, DeviceKind? kind = null)
    {
        if (_modelsById.TryGetValue(id, out var indexed) && (kind is null || indexed.Kind == kind.Value))
            return indexed;
        return Models.FirstOrDefault(m => m.Id == id && (kind is null || m.Kind == kind.Value));
    }

    public PhoneChoice Resolve(PhoneChoice choice)
    {
        if (choice.Model is not null && FindModel(choice.Model) is { } model)
            return PhoneChoice.FromModel(model);
        return choice;
    }

    public static readonly IReadOnlyList<PhoneChoice> GenericPresets =
    [
        new("通用全面屏手机 (19.5:9)", new PhoneScreenSpecData(1170, 2532, 390), DeviceKind.Phone),
        new("通用宽长手机 (20:9)", new PhoneScreenSpecData(1080, 2400), DeviceKind.Phone),
        new("通用平板 (4:3)", new PhoneScreenSpecData(1620, 2160, 810), DeviceKind.Tablet),
        new("宽屏平板 (16:10)", new PhoneScreenSpecData(1600, 2560, 800), DeviceKind.Tablet),
        new("通用电脑 1080p (16:9)", new PhoneScreenSpecData(1080, 1920), DeviceKind.Computer),
        new("通用电脑 2K (16:9)", new PhoneScreenSpecData(1440, 2560), DeviceKind.Computer),
        new("通用笔记本 (16:10)", new PhoneScreenSpecData(1600, 2560), DeviceKind.Computer)
    ];

    public IReadOnlyList<Brand> Search(string query, IReadOnlySet<DeviceKind>? kinds = null)
    {
        var source = kinds is null ? Brands : Brands.Where(b => kinds.Contains(b.Kind)).ToList();
        var q = Normalize(query);
        if (string.IsNullOrEmpty(q)) return source;

        var matched = new List<Brand>();
        foreach (var brand in source)
        {
            var brandNames = new[] { brand.Name }.Concat(brand.Aliases).Select(Normalize).ToList();
            var models = brand.Models.Where(model =>
            {
                var modelNorm = Normalize(model.Name);
                return brandNames.Any(bn => (bn + modelNorm).Contains(q, StringComparison.Ordinal));
            }).ToList();

            if (models.Count > 0)
                matched.Add(new Brand(brand.Name, brand.Aliases, models, brand.Kind));
        }
        return matched;
    }

    /// <summary>
    /// Attempts to match a remote controller name (e.g. from UU Remote's controller list like
    /// "明明的 iPhone 16 Pro Max" or "Xiaomi 15 Ultra") against phone/tablet models in the catalog.
    /// Prefers the longest/most-specific match so "iPhone 16 Pro Max" beats "iPhone 16 Pro" and "iPhone 16".
    /// </summary>
    public PhoneModel? MatchControllerName(string? controllerName)
    {
        string raw = NormalizeControllerText(controllerName);
        if (raw.Length < 4) return null;

        PhoneModel? bestModel = null;
        int bestScore = 0;

        foreach (var brand in Brands)
        {
            if (!brand.Kind.IsTouch()) continue;
            var brandTokens = new[] { brand.Name }.Concat(brand.Aliases)
                .Select(NormalizeControllerText)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var model in brand.Models)
            {
                string modelNorm = NormalizeControllerText(model.Name);
                string baseNorm = NormalizeModelBase(model.Name);
                if (modelNorm.Length < 4 && baseNorm.Length < 4) continue;

                // Candidate 1: Brand + Model (e.g. "xiaomi15ultra", "苹果iphone16pro", "huawei mate60pro")
                foreach (var bt in brandTokens)
                {
                    if (modelNorm.Length >= 4)
                    {
                        string combined = bt + modelNorm;
                        if (combined.Length >= 5 && raw.Contains(combined, StringComparison.Ordinal))
                        {
                            int score = combined.Length * 2 + 120;
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestModel = model;
                            }
                        }
                    }

                    if (baseNorm.Length >= 4 && baseNorm != modelNorm)
                    {
                        string combinedBase = bt + baseNorm;
                        if (combinedBase.Length >= 5 && raw.Contains(combinedBase, StringComparison.Ordinal))
                        {
                            int score = combinedBase.Length * 2 + 90;
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestModel = model;
                            }
                        }
                    }
                }

                // Candidate 2: Self-identifying product lines (e.g. "iphone16pro", "ipadpro11", "galaxys24ultra")
                foreach (var (candidate, bonus) in new[] { (modelNorm, 40), (baseNorm, 20) })
                {
                    if (candidate.Length < 4) continue;
                    bool selfIdentifying = candidate.StartsWith("iphone", StringComparison.Ordinal)
                        || candidate.StartsWith("ipad", StringComparison.Ordinal)
                        || candidate.StartsWith("galaxy", StringComparison.Ordinal)
                        || candidate.StartsWith("pixel", StringComparison.Ordinal)
                        || candidate.StartsWith("redmi", StringComparison.Ordinal)
                        || candidate.StartsWith("oneplus", StringComparison.Ordinal)
                        || candidate.StartsWith("mate", StringComparison.Ordinal)
                        || candidate.StartsWith("pura", StringComparison.Ordinal)
                        || candidate.StartsWith("findx", StringComparison.Ordinal)
                        || candidate.StartsWith("iqoo", StringComparison.Ordinal)
                        || candidate.StartsWith("mix", StringComparison.Ordinal);

                    if (selfIdentifying && raw.Contains(candidate, StringComparison.Ordinal))
                    {
                        int score = candidate.Length * 2 + bonus;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestModel = model;
                        }
                    }
                }
            }
        }

        return bestModel;
    }

    private static string NormalizeModelBase(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return string.Empty;
        string s = modelName;
        int idxCn = s.IndexOf('（');
        if (idxCn > 0) s = s[..idxCn];
        int idxEn = s.IndexOf('(');
        if (idxEn > 0) s = s[..idxEn];
        s = s.Replace("英寸", "", StringComparison.Ordinal)
             .Replace("-inch", "", StringComparison.OrdinalIgnoreCase)
             .Replace("inch", "", StringComparison.OrdinalIgnoreCase);
        return NormalizeControllerText(s);
    }

    internal static string NormalizeControllerText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var chars = text.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }

    private sealed class RawFile
    {
        public List<RawBrand>? Brands { get; set; }
        public List<RawBrand>? Phone { get; set; }
        public List<RawBrand>? Tablet { get; set; }
        public List<RawBrand>? Computer { get; set; }
    }

    private sealed class RawBrand
    {
        public string? Name { get; set; }
        public List<string>? Aliases { get; set; }
        public List<RawModel>? Models { get; set; }
    }

    private sealed class RawModel
    {
        public string? Name { get; set; }
        public List<string>? LegacyNames { get; set; }
        public List<int>? Screen { get; set; }
        public int? Points { get; set; }
        public int? Notch { get; set; }
    }
}
