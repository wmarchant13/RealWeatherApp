using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace RealWeatherApp;

public partial class MainPage : ContentPage
{
    // sunrise and sunset local times for background calculation
    DateTime? sunriseTime;
    DateTime? sunsetTime;
    // location timezone offset from UTC (seconds)
    TimeSpan? locationOffset;
    // last-known location coordinates
    double? locationLatitude;
    double? locationLongitude;
    // shared HttpClient for reuse
    static readonly System.Net.Http.HttpClient _http = new System.Net.Http.HttpClient();

    public MainPage()
    {
        InitializeComponent();
        // Update current time every second and refresh background
        this.Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            var now = GetLocationNow();
            if (CurrentTimeLabel != null) CurrentTimeLabel.Text = $"Now: {now.ToString("h:mm:ss tt")}";
            UpdateBackground();
            return true; // repeat
        });
        // do not automatically request location on startup; weather will be fetched when user taps the button
        // optionally we could show IP-based weather here by calling GetWeatherByIpAsync() if desired
        // layout changes will trigger background updates via timer
    }

    private async void OnGetWeatherClicked(object sender, EventArgs e)
    {
        try
        {
            var client = _http;

            // try to obtain key from local backend first, then fall back to configuration
            var apiKey = await GetApiKeyAsync();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (ConditionLabel != null)
                    ConditionLabel.Text = "API key not configured";
                return;
            }
            var cityText = CityEntry?.Text;
            string url;
            if (!string.IsNullOrWhiteSpace(cityText))
            {
                url = $"https://api.openweathermap.org/data/2.5/weather?q={Uri.EscapeDataString(cityText)}&appid={apiKey}&units=imperial";
            }
            else
            {
                // fallback to Buffalo
                var city = "Buffalo,US";
                url = $"https://api.openweathermap.org/data/2.5/weather?q={city}&appid={apiKey}&units=imperial";
            }

            var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    if (ConditionLabel != null) ConditionLabel.Text = $"Error fetching weather ({response.StatusCode})";
                    return;
                }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonDocument.Parse(json);

            var temp = data.RootElement.GetProperty("main").GetProperty("temp").GetDouble();
            var humidity = data.RootElement.GetProperty("main").GetProperty("humidity").GetDouble();
            var desc = data.RootElement.GetProperty("weather")[0].GetProperty("description").GetString();

            // capture coordinates if present
            if (data.RootElement.TryGetProperty("coord", out var coord))
            {
                if (coord.TryGetProperty("lat", out var latEl)) locationLatitude = latEl.GetDouble();
                if (coord.TryGetProperty("lon", out var lonEl)) locationLongitude = lonEl.GetDouble();
            }


            // Dew point (approx) using Magnus formula (convert to C, compute, convert back)
            var dewPointF = CalculateDewPointF(temp, humidity);

            // Wind
            double windSpeed = 0;
            int windDeg = 0;
            if (data.RootElement.TryGetProperty("wind", out var windEl))
            {
                if (windEl.TryGetProperty("speed", out var sp)) windSpeed = sp.GetDouble();
                if (windEl.TryGetProperty("deg", out var dg)) windDeg = dg.GetInt32();
            }
            var windDir = WindCompass(windDeg);

            // Sunrise / sunset
            var sunrise = data.RootElement.GetProperty("sys").GetProperty("sunrise").GetInt64();
            var sunset = data.RootElement.GetProperty("sys").GetProperty("sunset").GetInt64();
            // OpenWeather provides timezone offset in seconds
            var tzSeconds = 0;
            if (data.RootElement.TryGetProperty("timezone", out var tzEl)) tzSeconds = tzEl.GetInt32();
            locationOffset = TimeSpan.FromSeconds(tzSeconds);
            var sunriseLocal = DateTimeOffset.FromUnixTimeSeconds(sunrise).ToOffset(locationOffset.Value).ToString("h:mm tt");
            var sunsetLocal = DateTimeOffset.FromUnixTimeSeconds(sunset).ToOffset(locationOffset.Value).ToString("h:mm tt");
            // record sunrise/sunset as DateTime in the location timezone
            sunriseTime = DateTimeOffset.FromUnixTimeSeconds(sunrise).ToOffset(locationOffset.Value).DateTime;
            sunsetTime = DateTimeOffset.FromUnixTimeSeconds(sunset).ToOffset(locationOffset.Value).DateTime;

            // Update UI labels
            if (TempLabel != null) TempLabel.Text = $"Temperature: {temp:F1}°F";
            if (DewPointLabel != null) DewPointLabel.Text = $"Dew Point: {dewPointF:F1}°F";
            if (WindLabel != null) WindLabel.Text = $"Wind: {windDir} {windSpeed:F1} mph";
            // rotate icon to show wind direction (arrow points up for N)
            if (WindIconLabel != null) WindIconLabel.Rotation = windDeg;
            if (ConditionLabel != null) ConditionLabel.Text = $"Condition: {desc}";
            if (SunriseLabel != null) SunriseLabel.Text = $"Sunrise: {sunriseLocal}";
            if (SunsetLabel != null) SunsetLabel.Text = $"Sunset: {sunsetLocal}";
            // show resolved location name if available
            if (data.RootElement.TryGetProperty("name", out var nameEl))
            {
                var resolved = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    if (LocationLabel != null) LocationLabel.Text = resolved;
                    if (CityEntry != null) CityEntry.Text = resolved;
                }
            }
            UpdateDebugInfo();
        }
        catch (Exception ex)
        {
            if (ConditionLabel != null) ConditionLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async void OnUseLocationClicked(object sender, EventArgs e)
    {
        await InitializeLocationAndWeatherAsync();
    }


    // Attempt to get device GPS location (requests permission), fall back to IP lookup
    async Task InitializeLocationAndWeatherAsync()
    {
        // Use CoreLocation on macOS (via MAUI Geolocation). Accuracy depends on
        // Wi‑Fi positioning but it may pick up the correct town rather than the
        // IP location. Request user permission first; macOS shows a prompt.
        // On MacCatalyst, directly attempt to get location without explicit permission
        // checks (the platform will surface its own prompt if needed). For other
        // platforms, use the normal Permissions flow.
        if (OperatingSystem.IsMacCatalyst())
        {
            try
            {
                var request = new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(20));
                var loc = await Geolocation.Default.GetLocationAsync(request);
                if (loc != null && loc.Accuracy.HasValue && loc.Accuracy.Value > 0 && loc.Accuracy.Value <= 200)
                {
                    await GetWeatherByCoordsAsync(loc.Latitude, loc.Longitude);
                    return;
                }
            }
            catch
            {
                // fall back to IP-based lookup below
            }
        }
        else
        {
            // check current status first
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status == PermissionStatus.Denied || status == PermissionStatus.Disabled)
            {
                var result = await DisplayAlert("Location Disabled",
                    "Location permission has been denied. The app needs it to provide accurate weather. Open Settings?",
                    "Open Settings", "Cancel");
                if (result)
                {
                    // fall back to App settings UI; Launcher may not be available on other platforms
                    try
                    {
                        AppInfo.ShowSettingsUI();
                    }
                    catch
                    {
                        // ignore
                    }
                }
                // continue to IP fallback regardless
            }
            else
            {
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
                if (status == PermissionStatus.Granted)
                {
                    try
                    {
                        var request = new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(20));
                        var loc = await Geolocation.Default.GetLocationAsync(request);
                        if (loc != null && loc.Accuracy.HasValue && loc.Accuracy.Value > 0 && loc.Accuracy.Value <= 200)
                        {
                            await GetWeatherByCoordsAsync(loc.Latitude, loc.Longitude);
                            return;
                        }
                    }
                    catch
                    {
                        // ignore location failure and fall back to IP
                    }
                }
            }
        }

            // Use ipwho.is (HTTPS) for IP-based fallback
            var client = _http;
            var ipUrl = "https://ipwho.is/";
            var resp = await client.GetAsync(ipUrl);
            if (!resp.IsSuccessStatusCode)
            {
                if (CityEntry != null) CityEntry.Text = "Buffalo,US";
                await Task.Delay(10);
                OnGetWeatherClicked(this, EventArgs.Empty);
                return;
            }

            var ipJson = await resp.Content.ReadAsStringAsync();
            using var ipDoc = JsonDocument.Parse(ipJson);
            double lat = 0, lon = 0;
            if (ipDoc.RootElement.TryGetProperty("latitude", out var latEl)) lat = latEl.GetDouble();
            if (ipDoc.RootElement.TryGetProperty("longitude", out var lonEl)) lon = lonEl.GetDouble();
            if (ipDoc.RootElement.TryGetProperty("city", out var cityEl)) if (CityEntry != null) CityEntry.Text = cityEl.GetString();

            if (lat != 0 || lon != 0)
            {
                await GetWeatherByCoordsAsync(lat, lon);
            }
            else
            {
                if (CityEntry != null) CityEntry.Text = "Buffalo,US";
                OnGetWeatherClicked(this, EventArgs.Empty);
            }
        }

    async Task GetWeatherByCoordsAsync(double lat, double lon)
    {
        try
        {
            // record requested coordinates
            locationLatitude = lat;
            locationLongitude = lon;
            var client = _http;
            var apiKey = await GetApiKeyAsync();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ConditionLabel.Text = "API key not configured";
                return;
            }
            var url = $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&appid={apiKey}&units=imperial";
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                ConditionLabel.Text = $"Error fetching weather ({response.StatusCode})";
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonDocument.Parse(json);
            // reuse parsing logic by copying code from OnGetWeatherClicked
            var temp = data.RootElement.GetProperty("main").GetProperty("temp").GetDouble();
            var humidity = data.RootElement.GetProperty("main").GetProperty("humidity").GetDouble();
            var desc = data.RootElement.GetProperty("weather")[0].GetProperty("description").GetString();

            var dewPointF = CalculateDewPointF(temp, humidity);

            double windSpeed = 0;
            int windDeg = 0;
            if (data.RootElement.TryGetProperty("wind", out var windEl))
            {
                if (windEl.TryGetProperty("speed", out var sp)) windSpeed = sp.GetDouble();
                if (windEl.TryGetProperty("deg", out var dg)) windDeg = dg.GetInt32();
            }
            var windDir = WindCompass(windDeg);

            var sunrise = data.RootElement.GetProperty("sys").GetProperty("sunrise").GetInt64();
            var sunset = data.RootElement.GetProperty("sys").GetProperty("sunset").GetInt64();
            // try to read timezone from response
            var tzSeconds = 0;
            if (data.RootElement.TryGetProperty("timezone", out var tzEl)) tzSeconds = tzEl.GetInt32();
            locationOffset = TimeSpan.FromSeconds(tzSeconds);
            var sunriseLocal = DateTimeOffset.FromUnixTimeSeconds(sunrise).ToOffset(locationOffset.Value).ToString("h:mm tt");
            var sunsetLocal = DateTimeOffset.FromUnixTimeSeconds(sunset).ToOffset(locationOffset.Value).ToString("h:mm tt");

            TempLabel.Text = $"Temperature: {temp:F1}°F";
            DewPointLabel.Text = $"Dew Point: {dewPointF:F1}°F";
            WindLabel.Text = $"Wind: {windDir} {windSpeed:F1} mph";
            WindIconLabel.Rotation = windDeg;
            ConditionLabel.Text = $"Condition: {desc}";
            SunriseLabel.Text = $"Sunrise: {sunriseLocal}";
            SunsetLabel.Text = $"Sunset: {sunsetLocal}";
            // clear any accuracy warnings after successful GPS-based fetch

            if (data.RootElement.TryGetProperty("name", out var nameEl))
            {
                var resolved = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(resolved)) LocationLabel.Text = resolved;
                // update the search box with the resolved location name so it matches the weather result
                if (!string.IsNullOrWhiteSpace(resolved)) CityEntry.Text = resolved;
            }
            // record sunrise/sunset in location timezone and refresh background
            if (data.RootElement.TryGetProperty("sys", out var sys))
            {
                var sr = sys.GetProperty("sunrise").GetInt64();
                var ss = sys.GetProperty("sunset").GetInt64();
                if (locationOffset.HasValue)
                {
                    sunriseTime = DateTimeOffset.FromUnixTimeSeconds(sr).ToOffset(locationOffset.Value).DateTime;
                    sunsetTime = DateTimeOffset.FromUnixTimeSeconds(ss).ToOffset(locationOffset.Value).DateTime;
                }
                else
                {
                    sunriseTime = DateTimeOffset.FromUnixTimeSeconds(sr).ToLocalTime().DateTime;
                    sunsetTime = DateTimeOffset.FromUnixTimeSeconds(ss).ToLocalTime().DateTime;
                }
                UpdateBackground();
            }

            UpdateDebugInfo();

            // (Reverse geocoding removed; using API-provided name)
        }
        catch (Exception ex)
        {
            ConditionLabel.Text = $"Error: {ex.Message}";
        }
    }

    // Calculate dew point in Fahrenheit using Magnus-Tetens approximation
    double CalculateDewPointF(double tempF, double humidityPercent)
    {
        // convert F to C
        var tC = (tempF - 32.0) * 5.0 / 9.0;
        var a = 17.27;
        var b = 237.7;
        var gamma = (a * tC) / (b + tC) + Math.Log(humidityPercent / 100.0);
        var dewC = (b * gamma) / (a - gamma);
        var dewF = dewC * 9.0 / 5.0 + 32.0;
        return dewF;
    }

    string WindCompass(int deg)
    {
        string[] dirs = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
        var idx = (int)((deg / 22.5) + 0.5) % 16;
        return dirs[idx];
    }

    static Color LerpColor(Color a, Color b, double f)
    {
        return new Color(
            (float)(a.Red + (b.Red - a.Red) * f),
            (float)(a.Green + (b.Green - a.Green) * f),
            (float)(a.Blue + (b.Blue - a.Blue) * f));
    }

    // Try to get API key from local backend (http://localhost:5000) first,
    // then fall back to configuration (appsettings) if backend not available.
    async Task<string?> GetApiKeyAsync()
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
            var resp = await _http.GetAsync("http://localhost:5000/api/key", cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                // try fallback port 5001 if 5000 is unavailable
                resp = await _http.GetAsync("http://localhost:5001/api/key", cts.Token);
            }
            if (resp.IsSuccessStatusCode)
            {
                var j = await resp.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(j);
                if (doc.RootElement.TryGetProperty("apiKey", out var el)) return el.GetString();
            }
        }
        catch
        {
            // ignore and fallback to configuration
        }

        return MauiProgram.AppConfiguration?["OpenWeather:ApiKey"];
    }



    void UpdateDebugInfo()
    {
        try
        {
            if (ConditionLabel == null) return;
            var tz = locationOffset.HasValue ? (locationOffset.Value.TotalHours.ToString("0.##") + "h") : "n/a";
            var lat = locationLatitude.HasValue ? locationLatitude.Value.ToString("F4") : "n/a";
            var lon = locationLongitude.HasValue ? locationLongitude.Value.ToString("F4") : "n/a";
            double elev = double.NaN;
            if (locationLatitude.HasValue && locationLongitude.HasValue)
            {
                (elev, _) = SolarPosition(locationLatitude.Value, locationLongitude.Value, DateTime.UtcNow);
            }
            var elevs = double.IsNaN(elev) ? "n/a" : elev.ToString("F1");
            var nowlocal = GetLocationNow();
            ConditionLabel.Text = $"Local: {nowlocal:h:mm:ss tt} TZ:{tz} Lat:{lat} Lon:{lon} Elev:{elevs}";
        }
        catch { }
    }

    DateTime GetLocationNow()
    {
        if (locationOffset.HasValue) return DateTime.UtcNow + locationOffset.Value;
        return DateTime.Now;
    }

    // Compute approximate solar elevation and azimuth for given lat/lon and UTC time.
    // Returns elevation (degrees) and azimuth (degrees, 0 = North, increasing eastward)
    (double elevation, double azimuth) SolarPosition(double latDeg, double lonDeg, DateTime utc)
    {
        double ToRad(double d) => d * Math.PI / 180.0;
        double ToDeg(double r) => r * 180.0 / Math.PI;

        int year = utc.Year;
        int month = utc.Month;
        double day = utc.Day + utc.Hour / 24.0 + utc.Minute / 1440.0 + utc.Second / 86400.0;
        if (month <= 2)
        {
            year -= 1; month += 12;
        }
        int A = year / 100;
        int B = 2 - A + (A / 4);
        double JD = Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + B - 1524.5;
        double T = (JD - 2451545.0) / 36525.0;

        double L0 = (280.46646 + 36000.76983 * T + 0.0003032 * T * T) % 360.0;
        if (L0 < 0) L0 += 360.0;
        double M = 357.52911 + 35999.05029 * T - 0.0001537 * T * T;
        double Mr = ToRad(M);
        double C = (1.914602 - 0.004817 * T - 0.000014 * T * T) * Math.Sin(Mr)
                   + (0.019993 - 0.000101 * T) * Math.Sin(2 * Mr)
                   + 0.000289 * Math.Sin(3 * Mr);
        double trueLong = L0 + C;

        double epsilon = 23.439291 - 0.0130042 * T; // obliquity
        double lambda = ToRad(trueLong);
        double epsilonR = ToRad(epsilon);

        double sinDec = Math.Sin(lambda) * Math.Sin(epsilonR);
        double dec = Math.Asin(sinDec);

        // Compute Greenwich Mean Sidereal Time (degrees)
        double JD0 = Math.Floor(JD + 0.5) - 0.5;
        double T0 = (JD0 - 2451545.0) / 36525.0;
        double H = (utc - DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc)).TotalHours; // fractional day hours
        double GMST = 280.46061837 + 360.98564736629 * (JD - 2451545.0) + 0.000387933 * T0 * T0 - T0 * T0 * T0 / 38710000.0;
        GMST = GMST % 360.0; if (GMST < 0) GMST += 360.0;

        // Sun's right ascension (degrees)
        double alpha = Math.Atan2(Math.Cos(epsilonR) * Math.Sin(lambda), Math.Cos(lambda));
        double alphaDeg = ToDeg(alpha);
        if (alphaDeg < 0) alphaDeg += 360.0;

        // Local sidereal time
        double lst = GMST + lonDeg;
        lst = lst % 360.0; if (lst < 0) lst += 360.0;

        double hourAngleDeg = lst - alphaDeg;
        if (hourAngleDeg < -180) hourAngleDeg += 360;
        if (hourAngleDeg > 180) hourAngleDeg -= 360;
        double Hrad = ToRad(hourAngleDeg);

        double latR = ToRad(latDeg);

        // elevation
        double cosZenith = Math.Sin(latR) * Math.Sin(dec) + Math.Cos(latR) * Math.Cos(dec) * Math.Cos(Hrad);
        cosZenith = Math.Clamp(cosZenith, -1.0, 1.0);
        double zenith = Math.Acos(cosZenith);
        double elevation = 90.0 - ToDeg(zenith);

        // azimuth (approx)
        double az = Math.Atan2(Math.Sin(Hrad), Math.Cos(Hrad) * Math.Sin(latR) - Math.Tan(dec) * Math.Cos(latR));
        double azDeg = ToDeg(az);
        azDeg = (azDeg + 180.0) % 360.0; // convert to 0..360

        return (elevation, azDeg);
    }

    void UpdateBackground()
    {
        // compute colors: prefer solar elevation when location is known
        if (sunriseTime == null || sunsetTime == null) return;
        var now = GetLocationNow();
        Brush brush;
        // if we have coordinates, compute solar elevation and map to a subtle multi-phase sky
        if (locationLatitude.HasValue && locationLongitude.HasValue)
        {
            var (elev, az) = SolarPosition(locationLatitude.Value, locationLongitude.Value, DateTime.UtcNow);
            // define phase colors (subtle)
            Color night = Color.FromRgb(8, 16, 38);
            Color sunrise = Color.FromRgb(255, 140, 66);
            Color morning = Color.FromRgb(255, 213, 150);
            Color midday = Color.FromRgb(168, 215, 245);
            Color earlyEvening = Color.FromRgb(240, 210, 180);
            Color sunset = Color.FromRgb(255, 122, 80);

            // helper lerp
            // use shared Lerp helper

            // daytime fraction between sunrise/sunset if available (0..1)
            double dayFrac = 0.5;
            if (sunriseTime.HasValue && sunsetTime.HasValue && sunsetTime > sunriseTime)
            {
                dayFrac = Math.Clamp((now - sunriseTime.Value).TotalSeconds / (sunsetTime.Value - sunriseTime.Value).TotalSeconds, 0.0, 1.0);
            }

            Color top, bottom;
            if (elev <= -6)
            {
                // night
                top = night;
                bottom = LerpColor(night, new Color(night.Red * 0.6f, night.Green * 0.6f, night.Blue * 0.7f), 0.5);
            }
            else if (elev < 0)
            {
                // dawn/dusk blend from night->sunrise based on elevation
                double f = (elev + 6.0) / 6.0; // 0..1
                top = LerpColor(night, sunrise, f * 0.8);
                bottom = LerpColor(night, morning, f * 0.6);
            }
            else
            {
                // daytime: use dayFrac to choose morning->midday->earlyEvening->sunset
                if (dayFrac < 0.25)
                {
                    double f = dayFrac / 0.25; // morning
                    top = LerpColor(sunrise, morning, f);
                    bottom = LerpColor(morning, midday, Math.Min(1.0, f + 0.1));
                }
                else if (dayFrac < 0.55)
                {
                    double f = (dayFrac - 0.25) / (0.55 - 0.25); // toward midday
                    top = LerpColor(morning, midday, f);
                    bottom = LerpColor(morning, midday, Math.Min(1.0, f + 0.2));
                }
                else if (dayFrac < 0.8)
                {
                    double f = (dayFrac - 0.55) / (0.8 - 0.55); // early evening
                    top = LerpColor(midday, earlyEvening, f);
                    bottom = LerpColor(midday, earlyEvening, Math.Min(1.0, f + 0.2));
                }
                else
                {
                    double f = (dayFrac - 0.8) / 0.2; // sunset
                    top = LerpColor(earlyEvening, sunset, f);
                    bottom = LerpColor(midday, sunset, Math.Min(1.0, f + 0.15));
                }
            }

            // subtle darkening for bottom
            bottom = LerpColor(bottom, new Color(0, 0, 0), 0.05);
            brush = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(top, 0.0f),
                new GradientStop(bottom, 1.0f)
            }, new Point(0,0), new Point(0,1));
            this.Background = brush;
            return;
        }
        if (now < sunriseTime)
        {
            brush = new SolidColorBrush(Color.FromArgb("FF000020"));
        }
        else if (now > sunsetTime)
        {
            brush = new SolidColorBrush(Color.FromArgb("FF000020"));
        }
        else
        {
            // day between sunrise and sunset; simple gradient based on position
            double total = (sunsetTime.Value - sunriseTime.Value).TotalMinutes;
            double elapsed = (now - sunriseTime.Value).TotalMinutes;
            double t = Math.Clamp(elapsed / total, 0, 1);
            // morning orange to midday blue to evening orange
            Color morning = Color.FromRgb(255, 153, 51);
            Color midday = Color.FromRgb(135, 206, 235);
            Color evening = Color.FromRgb(255, 153, 51);
            // simple linear interpolation between two colors
            // use shared Lerp helper

            Color start = t < 0.5 ? LerpColor(morning, midday, t * 2) : LerpColor(midday, evening, (t - 0.5) * 2);
            // darken slightly for the gradient end
            Color end = new Color((float)(start.Red * 0.8), (float)(start.Green * 0.8), (float)(start.Blue * 0.8));
            brush = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(start, 0.0f),
                new GradientStop(end, 1.0f)
            }, new Point(0,0), new Point(0,1));
        }
        this.Background = brush;

        // sun/moon overlay removed
    }
}