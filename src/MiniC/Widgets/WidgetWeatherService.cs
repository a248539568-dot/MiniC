using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MiniC.Widgets;

public enum WeatherSymbol { Clear, PartlyCloudy, Cloudy, Rain, Snow, Fog, Thunder }
public sealed record WeatherForecastDay(DateOnly Date, string Condition, double High, double Low, WeatherSymbol Symbol);
public sealed record WeatherHourlyForecast(TimeOnly Time, double Temperature, string Condition, WeatherSymbol Symbol);
public sealed record WeatherSnapshot(string Place, double Temperature, string Condition, double High, double Low,
    DateTimeOffset FetchedAt, WeatherSymbol Symbol = WeatherSymbol.Cloudy, string Source = "Open-Meteo",
    IReadOnlyList<WeatherForecastDay>? Forecast = null, IReadOnlyList<WeatherHourlyForecast>? Hourly = null);
public sealed record WeatherLocation(string City, double Latitude, double Longitude);

/// <summary>中国气象局主用；失败时依次回退 wttr.in 与 Open-Meteo。IP 只用于初次城市建议。</summary>
public sealed class WidgetWeatherService
{
    private static readonly HttpClient SharedClient = new(CreateDirectHandler()) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _client;
    private readonly Dictionary<string, (WeatherSnapshot Snapshot, DateTimeOffset StoredAt)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private WeatherLocation? _lastLocation;
    private DateTimeOffset _locationTime;

    public WidgetWeatherService(HttpClient? client = null) : this(client, cleanupLegacyCredentials: true) { }
    internal WidgetWeatherService(HttpClient? client, bool cleanupLegacyCredentials)
    {
        _client = client ?? SharedClient;
        if (cleanupLegacyCredentials) WeatherLegacyCleanup.RemoveObsoleteProviderCredentials();
    }

    // IP location must use the local connection, not a proxy's exit address.
    internal static HttpClientHandler CreateDirectHandler() => new() { UseProxy = false, Proxy = null };

    public void ResetLocation() { _lastLocation = null; _locationTime = default; }

    public async Task<WeatherLocation?> LocateAsync(CancellationToken token = default)
    {
        if (_lastLocation is not null && DateTimeOffset.UtcNow - _locationTime < TimeSpan.FromHours(12))
            return _lastLocation;
        try
        {
            using var ip = await GetJsonAsync("https://ipwho.is/", token);
            var root = ip.RootElement;
            if (!root.GetProperty("success").GetBoolean()) return null;
            var city = root.GetProperty("city").GetString();
            if (string.IsNullOrWhiteSpace(city)) return null;
            var latitude = root.GetProperty("latitude").GetDouble();
            var longitude = root.GetProperty("longitude").GetDouble();
            var name = city;
            try
            {
                var localized = await GeocodeAsync(city, token);
                if (localized is not null && Math.Abs(localized.Latitude - latitude) < 1
                    && Math.Abs(localized.Longitude - longitude) < 1)
                    name = localized.City;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { }
            _lastLocation = new WeatherLocation(name, latitude, longitude);
            _locationTime = DateTimeOffset.UtcNow;
            return _lastLocation;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    public async Task<WeatherSnapshot> GetAsync(string city, WeatherLocation? location = null,
        CancellationToken token = default)
    {
        city = city.Trim();
        if (city.Length is < 2 or > 80) throw new ArgumentException("请输入城市名称。", nameof(city));
        var hasCached = _cache.TryGetValue(city, out var cached);
        if (hasCached && DateTimeOffset.UtcNow - cached.StoredAt < TimeSpan.FromMinutes(30))
            return cached.Snapshot;

        try
        {
            try
            {
                var cma = await GetCmaAsync(city, token);
                Store(city, cma);
                return cma;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ProviderFailure(ex))
            {
                var point = ValidCoordinates(location) ? location : await GeocodeAsync(city, token);
                if (point is null) throw new InvalidOperationException("未找到该城市，请尝试输入更完整的名称。");
                try
                {
                    var wttr = await GetWttrAsync(point, token);
                    Store(city, wttr);
                    return wttr;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception fallbackError) when (ProviderFailure(fallbackError))
                {
                    var openMeteo = await GetOpenMeteoAsync(point, token);
                    Store(city, openMeteo);
                    return openMeteo;
                }
            }
        }
        catch (Exception) when (!token.IsCancellationRequested && hasCached)
        {
            return cached.Snapshot;
        }
    }

    private void Store(string city, WeatherSnapshot snapshot) =>
        _cache[city] = (snapshot, DateTimeOffset.UtcNow);

    private static bool ProviderFailure(Exception exception) => exception is HttpRequestException or JsonException
        or InvalidOperationException or FormatException or KeyNotFoundException or IndexOutOfRangeException
        or ArgumentOutOfRangeException or OperationCanceledException;

    private async Task<WeatherSnapshot> GetCmaAsync(string city, CancellationToken token)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var search = await GetJsonAsync(
            $"https://weather.cma.cn/api/autocomplete?q={Uri.EscapeDataString(city)}&limit=10&timestamp={timestamp}", token);
        var searchRoot = search.RootElement;
        if (searchRoot.GetProperty("code").GetInt32() != 0 ||
            !searchRoot.TryGetProperty("data", out var candidates) || candidates.GetArrayLength() == 0)
            throw new InvalidOperationException("中国气象局未找到该城市。");

        var normalized = NormalizeCmaCity(city);
        var stations = candidates.EnumerateArray().Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Split('|')).Where(parts => parts.Length >= 2).ToArray();
        var station = stations.FirstOrDefault(parts => NormalizeCmaCity(parts[1]) == normalized
            || parts.Skip(2).Any(value => string.Equals(value, city, StringComparison.OrdinalIgnoreCase)))
            ?? stations.FirstOrDefault() ?? throw new InvalidOperationException("中国气象局未返回有效站点。");

        using var view = await GetJsonAsync(
            $"https://weather.cma.cn/api/weather/view?stationid={Uri.EscapeDataString(station[0])}", token);
        using var hourlyDocument = await GetJsonAsync(
            $"https://weather.cma.cn/api/hourly/{Uri.EscapeDataString(station[0])}", token);
        var root = view.RootElement;
        if (root.GetProperty("code").GetInt32() != 0 || !root.TryGetProperty("data", out var data))
            throw new JsonException("中国气象局没有返回天气数据。");

        var location = data.GetProperty("location");
        var dailyItems = data.GetProperty("daily");
        var days = dailyItems.EnumerateArray().Take(4).Select(ParseCmaDay).ToArray();
        if (days.Length == 0) throw new JsonException("中国气象局没有返回每日预报。");
        var today = days[0];
        var hourly = ParseCmaHourly(hourlyDocument.RootElement, today.Date);
        var condition = hourly.FirstOrDefault()?.Condition ?? today.Condition;
        var fetchedAt = ParseCmaUpdate(data);
        return new WeatherSnapshot(location.GetProperty("name").GetString() ?? station[1],
            data.GetProperty("now").GetProperty("temperature").GetDouble(), condition,
            today.High, today.Low, fetchedAt, Classify(condition), "中国气象局",
            days.Skip(1).Take(3).ToArray(), hourly);
    }

    private static string NormalizeCmaCity(string value)
    {
        value = value.Trim();
        return value.EndsWith('市') ? value[..^1] : value;
    }

    private static WeatherForecastDay ParseCmaDay(JsonElement day)
    {
        var date = DateOnly.ParseExact(day.GetProperty("date").GetString()!, "yyyy/MM/dd",
            CultureInfo.InvariantCulture);
        var condition = day.GetProperty("dayText").GetString() ?? "天气变化";
        return new WeatherForecastDay(date, condition, day.GetProperty("high").GetDouble(),
            day.GetProperty("low").GetDouble(), Classify(condition));
    }

    private static WeatherHourlyForecast[] ParseCmaHourly(JsonElement root, DateOnly today)
    {
        if (root.GetProperty("code").GetInt32() != 0 || !root.TryGetProperty("data", out var groups)
            || groups.ValueKind != JsonValueKind.Array) return [];
        var result = new List<WeatherHourlyForecast>(8);
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("list", out var periods) || periods.ValueKind != JsonValueKind.Array) continue;
            foreach (var period in periods.EnumerateArray())
            {
                var forecast = DateTime.ParseExact(period.GetProperty("forecastTime").GetString()!,
                    "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None);
                if (DateOnly.FromDateTime(forecast) != today || result.Count >= 8) continue;
                var condition = period.GetProperty("text").GetString() ?? "天气变化";
                result.Add(new WeatherHourlyForecast(TimeOnly.FromDateTime(forecast),
                    period.GetProperty("temperature").GetDouble(), condition, Classify(condition)));
            }
            if (result.Count >= 8) break;
        }
        return result.GroupBy(period => period.Time).Select(group => group.First()).OrderBy(period => period.Time)
            .Take(8).ToArray();
    }

    private static DateTimeOffset ParseCmaUpdate(JsonElement data)
    {
        if (data.TryGetProperty("lastUpdate", out var value) && DateTime.TryParseExact(value.GetString(),
            "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return new DateTimeOffset(local, TimeSpan.FromHours(8));
        return DateTimeOffset.UtcNow;
    }

    private async Task<WeatherSnapshot> GetOpenMeteoAsync(WeatherLocation point, CancellationToken token)
    {
        using var forecast = await GetJsonAsync(FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={point.Latitude}&longitude={point.Longitude}&current=temperature_2m,weather_code&hourly=temperature_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=4"), token);
        var root = forecast.RootElement;
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        var code = current.GetProperty("weather_code").GetInt32();
        var dates = daily.GetProperty("time");
        var codes = daily.GetProperty("weather_code");
        var highs = daily.GetProperty("temperature_2m_max");
        var lows = daily.GetProperty("temperature_2m_min");
        var count = Math.Min(4, Math.Min(dates.GetArrayLength(), codes.GetArrayLength()));
        if (count == 0) throw new JsonException("Open-Meteo 没有返回每日预报。");
        var today = DateOnly.ParseExact(dates[0].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = Enumerable.Range(1, Math.Max(0, count - 1)).Take(3).Select(index =>
        {
            var dayCode = codes[index].GetInt32();
            return new WeatherForecastDay(DateOnly.ParseExact(dates[index].GetString()!, "yyyy-MM-dd",
                CultureInfo.InvariantCulture), Condition(dayCode), highs[index].GetDouble(), lows[index].GetDouble(),
                Symbol(dayCode));
        }).ToArray();
        var hourly = ParseOpenMeteoHourly(root, today);
        return new WeatherSnapshot(point.City, current.GetProperty("temperature_2m").GetDouble(),
            Condition(code), highs[0].GetDouble(), lows[0].GetDouble(), DateTimeOffset.UtcNow,
            Symbol(code), "Open-Meteo", days, hourly);
    }

    private static WeatherHourlyForecast[] ParseOpenMeteoHourly(JsonElement root, DateOnly today)
    {
        if (!root.TryGetProperty("hourly", out var hourly)) return [];
        var times = hourly.GetProperty("time");
        var temperatures = hourly.GetProperty("temperature_2m");
        var codes = hourly.GetProperty("weather_code");
        var count = Math.Min(times.GetArrayLength(), Math.Min(temperatures.GetArrayLength(), codes.GetArrayLength()));
        var result = new List<WeatherHourlyForecast>(8);
        for (var index = 0; index < count && result.Count < 8; index++)
        {
            var local = DateTime.ParseExact(times[index].GetString()!, "yyyy-MM-dd'T'HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None);
            if (DateOnly.FromDateTime(local) != today || local.Hour % 3 != 0) continue;
            var weatherCode = codes[index].GetInt32();
            result.Add(new WeatherHourlyForecast(TimeOnly.FromDateTime(local), temperatures[index].GetDouble(),
                Condition(weatherCode), Symbol(weatherCode)));
        }
        return result.ToArray();
    }

    internal Task<WeatherSnapshot> GetWttrForDiagnosticsAsync(WeatherLocation point, CancellationToken token = default) =>
        GetWttrAsync(point, token);

    private async Task<WeatherSnapshot> GetWttrAsync(WeatherLocation point, CancellationToken token)
    {
        var latitude = point.Latitude.ToString("0.0000", CultureInfo.InvariantCulture);
        var longitude = point.Longitude.ToString("0.0000", CultureInfo.InvariantCulture);
        using var doc = await GetWttrJsonAsync(
            $"https://wttr.in/{latitude},{longitude}?format=j1&lang=zh&m", token);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var nested)) root = nested;
        var currentItems = root.GetProperty("current_condition");
        var weatherItems = root.GetProperty("weather");
        if (currentItems.GetArrayLength() == 0 || weatherItems.GetArrayLength() == 0)
            throw new JsonException("wttr.in 没有返回天气数据。");

        var current = currentItems[0];
        var condition = WttrDescription(current);
        var allDays = weatherItems.EnumerateArray().Take(4).Select(ParseWttrDay).ToArray();
        var today = allDays[0];
        var hourly = ParseWttrHourly(weatherItems[0]);
        return new WeatherSnapshot(point.City, WttrNumber(current, "temp_C"), condition,
            today.High, today.Low, DateTimeOffset.UtcNow, Classify(condition), "wttr.in",
            allDays.Skip(1).Take(3).ToArray(), hourly);
    }

    private static WeatherForecastDay ParseWttrDay(JsonElement day)
    {
        var date = DateOnly.ParseExact(day.GetProperty("date").GetString()!, "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        var condition = "天气变化";
        if (day.TryGetProperty("hourly", out var hourly) && hourly.ValueKind == JsonValueKind.Array
            && hourly.GetArrayLength() > 0)
        {
            var bestDistance = int.MaxValue;
            foreach (var item in hourly.EnumerateArray())
            {
                var hour = WttrHour(item);
                var distance = Math.Abs(hour - 1200);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                condition = WttrDescription(item);
            }
        }
        return new WeatherForecastDay(date, condition, WttrNumber(day, "maxtempC"),
            WttrNumber(day, "mintempC"), Classify(condition));
    }

    private static WeatherHourlyForecast[] ParseWttrHourly(JsonElement day)
    {
        if (!day.TryGetProperty("hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Array) return [];
        return hourly.EnumerateArray().Take(8).Select(item =>
        {
            var value = WttrHour(item);
            var hour = Math.Clamp(value / 100, 0, 23);
            var minute = Math.Clamp(value % 100, 0, 59);
            var condition = WttrDescription(item);
            return new WeatherHourlyForecast(new TimeOnly(hour, minute), WttrNumber(item, "tempC"),
                condition, Classify(condition));
        }).ToArray();
    }

    private static int WttrHour(JsonElement item)
    {
        if (item.TryGetProperty("time", out var time)
            && int.TryParse(time.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour))
            return hour;
        return 1200;
    }

    private static string WttrDescription(JsonElement source)
    {
        foreach (var name in new[] { "lang_zh", "lang_zh-cn" })
        {
            if (!source.TryGetProperty(name, out var descriptions)) continue;
            if (descriptions.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(descriptions.GetString()))
                return descriptions.GetString()!;
            if (descriptions.ValueKind == JsonValueKind.Array && descriptions.GetArrayLength() > 0
                && descriptions[0].TryGetProperty("value", out var value)
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!;
        }
        if (source.TryGetProperty("weatherCode", out var codeElement)
            && int.TryParse(codeElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            return WttrCondition(code);
        if (source.TryGetProperty("weatherDesc", out var fallback) && fallback.ValueKind == JsonValueKind.Array
            && fallback.GetArrayLength() > 0 && fallback[0].TryGetProperty("value", out var text))
            return TranslateWttrEnglish(text.GetString());
        return "天气变化";
    }

    private static string WttrCondition(int code) => code switch
    {
        113 => "晴朗",
        116 => "多云",
        119 or 122 => "阴天",
        143 or 248 or 260 => "有雾",
        200 or 386 or 389 => "雷雨",
        392 or 395 => "雷雪",
        179 or 182 or 227 or 230 or 317 or 320 or 323 or 326 or 329 or 332 or 335 or 338
            or 350 or 362 or 365 or 368 or 371 => "下雪",
        176 or 185 or 263 or 266 or 281 or 284 or 293 or 296 or 299 or 302 or 305 or 308
            or 311 or 314 or 353 or 356 or 359 => "下雨",
        _ => "天气变化"
    };

    private static string TranslateWttrEnglish(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "天气变化";
        var value = description.ToLowerInvariant();
        if (value.Contains("thunder")) return value.Contains("snow") ? "雷雪" : "雷雨";
        if (value.Contains("snow") || value.Contains("sleet") || value.Contains("ice") || value.Contains("blizzard"))
            return "下雪";
        if (value.Contains("rain") || value.Contains("drizzle") || value.Contains("shower")) return "下雨";
        if (value.Contains("fog") || value.Contains("mist")) return "有雾";
        if (value.Contains("overcast")) return "阴天";
        if (value.Contains("cloud")) return "多云";
        if (value.Contains("sun") || value.Contains("clear")) return "晴朗";
        return "天气变化";
    }

    private static double WttrNumber(JsonElement source, string name)
    {
        var value = source.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    }

    private async Task<WeatherLocation?> GeocodeAsync(string city, CancellationToken token)
    {
        using var doc = await GetJsonAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=zh&format=json", token);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var first = results[0];
        return new WeatherLocation(first.GetProperty("name").GetString() ?? city,
            first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
    }

    private async Task<JsonDocument> GetWttrJsonAsync(string uri, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("MiniC-Desktop/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(content, cancellationToken: token);
    }

    private async Task<JsonDocument> GetJsonAsync(string uri, CancellationToken token)
    {
        using var response = await _client.GetAsync(uri, token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(content, cancellationToken: token);
    }

    private static bool ValidCoordinates(WeatherLocation? location) =>
        location is { Latitude: >= -90 and <= 90, Longitude: >= -180 and <= 180 }
        && (location.Latitude != 0 || location.Longitude != 0);

    public static WeatherSymbol Classify(string condition)
    {
        if (condition.Contains('雷')) return WeatherSymbol.Thunder;
        if (condition.Contains('雪') || condition.Contains('冰')) return WeatherSymbol.Snow;
        if (condition.Contains('雨')) return WeatherSymbol.Rain;
        if (condition.Contains('雾') || condition.Contains('霾') || condition.Contains('沙')) return WeatherSymbol.Fog;
        if (condition.Contains('阴')) return WeatherSymbol.Cloudy;
        if (condition.Contains('云')) return WeatherSymbol.PartlyCloudy;
        return WeatherSymbol.Clear;
    }

    public static WeatherSymbol Symbol(int code) => code switch
    {
        0 => WeatherSymbol.Clear,
        1 or 2 => WeatherSymbol.PartlyCloudy,
        3 => WeatherSymbol.Cloudy,
        45 or 48 => WeatherSymbol.Fog,
        51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => WeatherSymbol.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherSymbol.Snow,
        95 or 96 or 99 => WeatherSymbol.Thunder,
        _ => WeatherSymbol.Cloudy
    };

    private static string Condition(int code) => code switch
    {
        0 => "晴朗",
        1 or 2 => "少云",
        3 => "阴天",
        45 or 48 => "有雾",
        51 or 53 or 55 or 56 or 57 => "毛毛雨",
        61 or 63 or 65 or 66 or 67 => "下雨",
        71 or 73 or 75 or 77 => "下雪",
        80 or 81 or 82 => "阵雨",
        85 or 86 => "阵雪",
        95 or 96 or 99 => "雷雨",
        _ => "天气变化"
    };
}
