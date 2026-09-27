using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Weather;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace RaceAiPlugin;

/// <summary>
/// Real weather at the track from Open-Meteo (free, no API key). Sets the CSP WeatherFX type
/// (Sol/Pure show clouds, fog, rain), temperatures, pressure, humidity and wind.
/// Requires <c>EnableWeatherFx: true</c> in extra_cfg.yml, and <c>EnableRealTime: true</c> for the real time of day.
/// </summary>
public sealed class RealWeatherService : BackgroundService
{
    private const double NordschleifeLat = 50.3356, NordschleifeLon = 6.9475;

    private readonly RaceAiConfiguration _config;
    private readonly WeatherManager _weatherManager;
    private readonly IWeatherTypeProvider _weatherTypeProvider;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public RealWeatherService(RaceAiConfiguration config, WeatherManager weatherManager, IWeatherTypeProvider weatherTypeProvider)
    {
        _config = config;
        _weatherManager = weatherManager;
        _weatherTypeProvider = weatherTypeProvider;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AssettoServer-RaceAiPlugin");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.RealWeather) return;
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        double lat = _weatherManager.TrackParams?.Latitude ?? NordschleifeLat;
        double lon = _weatherManager.TrackParams?.Longitude ?? NordschleifeLon;
        if (lat == 0 && lon == 0) (lat, lon) = (NordschleifeLat, NordschleifeLon);
        Log.Information("Race AI: real weather from Open-Meteo for {Lat:F4}, {Lon:F4}, every {Min} min", lat, lon, _config.RealWeatherUpdateMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _config.RealWeatherUpdateMinutes)));
        do
        {
            try { await UpdateAsync(lat, lon, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Warning("Race AI: real weather update failed: {Message}", ex.Message); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task UpdateAsync(double lat, double lon, CancellationToken ct)
    {
        string url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}&current=temperature_2m,relative_humidity_2m,precipitation,weather_code,cloud_cover,pressure_msl,wind_speed_10m,wind_direction_10m&wind_speed_unit=ms");
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        var cur = doc.RootElement.GetProperty("current");
        float Get(string name) => cur.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;

        int code = (int)Get("weather_code");
        float temp = Get("temperature_2m"), hum = Get("relative_humidity_2m"), pressure = Get("pressure_msl");
        float windSpeed = Get("wind_speed_10m"), windDir = Get("wind_direction_10m"), cloud = Get("cloud_cover"), precip = Get("precipitation");

        var fx = MapWmo(code, cloud, precip, windSpeed);
        var type = _weatherTypeProvider.GetWeatherType(fx);
        var last = _weatherManager.CurrentWeather;

        _weatherManager.SetWeather(new WeatherData(last.Type, type)
        {
            TransitionDuration = _config.RealWeatherTransitionSeconds * 1000.0,
            TemperatureAmbient = temp,
            TemperatureRoad = (float)WeatherUtils.GetRoadTemperature(_weatherManager.CurrentDateTime.TimeOfDay.TickOfDay / 10_000_000.0, temp, type.TemperatureCoefficient),
            Pressure = pressure > 800 ? (int)MathF.Round(pressure) : 1013,
            Humidity = Math.Clamp(hum / 100f, 0, 1),
            WindSpeed = windSpeed * 3.6f,
            WindDirection = (int)MathF.Round(windDir),
            RainIntensity = last.RainIntensity,
            RainWetness = last.RainWetness,
            RainWater = last.RainWater,
            TrackGrip = last.TrackGrip
        });
        Log.Information("Race AI: real weather {Type} (WMO {Code}), {Temp:F1} °C, {Hum:F0} %, wind {Wind:F1} m/s, rain {Rain:F1} mm/h",
            fx, code, temp, hum, windSpeed, precip);
    }

    /// <summary>WMO weather interpretation code (Open-Meteo) to CSP WeatherFX type.</summary>
    public static WeatherFxType MapWmo(int code, float cloudCover, float precipitation, float windSpeed) => code switch
    {
        0 => windSpeed > 12 ? WeatherFxType.Windy : WeatherFxType.Clear,
        1 => WeatherFxType.FewClouds,
        2 => cloudCover > 60 ? WeatherFxType.BrokenClouds : WeatherFxType.ScatteredClouds,
        3 => WeatherFxType.OvercastClouds,
        45 or 48 => WeatherFxType.Fog,
        51 => WeatherFxType.LightDrizzle,
        53 => WeatherFxType.Drizzle,
        55 => WeatherFxType.HeavyDrizzle,
        56 or 66 => WeatherFxType.LightSleet,
        57 or 67 => WeatherFxType.Sleet,
        61 or 80 => WeatherFxType.LightRain,
        63 or 81 => precipitation > 6 ? WeatherFxType.HeavyRain : WeatherFxType.Rain,
        65 or 82 => WeatherFxType.HeavyRain,
        71 or 77 or 85 => WeatherFxType.LightSnow,
        73 => WeatherFxType.Snow,
        75 or 86 => WeatherFxType.HeavySnow,
        95 => WeatherFxType.Thunderstorm,
        96 or 99 => WeatherFxType.HeavyThunderstorm,
        _ => cloudCover > 85 ? WeatherFxType.OvercastClouds : WeatherFxType.ScatteredClouds
    };
}
