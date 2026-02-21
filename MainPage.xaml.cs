using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.ApplicationModel;

namespace RealWeatherApp;

public partial class MainPage : ContentPage
{
    // sunrise and sunset local times for background calculation
    DateTime? sunriseTime;
    DateTime? sunsetTime;

    public MainPage()
    {
        InitializeComponent();
        // Update current time every second and refresh background
        this.Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            if (CurrentTimeLabel != null) CurrentTimeLabel.Text = $"Now: {DateTime.Now.ToString("h:mm:ss tt")}";
            UpdateBackground();
            return true; // repeat
        });
        // do not automatically request location on startup; weather will be fetched when user taps the button
        // optionally we could show IP-based weather here by calling GetWeatherByIpAsync() if desired
    }

    private async void OnGetWeatherClicked(object sender, EventArgs e)
    {
        try
        {
            using var client = new HttpClient();

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
            var sunriseLocal = DateTimeOffset.FromUnixTimeSeconds(sunrise).ToLocalTime().ToString("h:mm tt");
            var sunsetLocal = DateTimeOffset.FromUnixTimeSeconds(sunset).ToLocalTime().ToString("h:mm tt");

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
        // check current status first
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (status == PermissionStatus.Denied || status == PermissionStatus.Disabled)
        {
            var result = await DisplayAlert("Location Disabled",
                "Location permission has been denied. The app needs it to provide accurate weather. Open Settings?",
                "Open Settings", "Cancel");
            if (result)
            {
                AppInfo.ShowSettingsUI();
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

            // Use ipwho.is (HTTPS) for IP-based fallback
            using var client = new HttpClient();
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
            using var client = new HttpClient();
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
            var sunriseLocal = DateTimeOffset.FromUnixTimeSeconds(sunrise).ToLocalTime().ToString("h:mm tt");
            var sunsetLocal = DateTimeOffset.FromUnixTimeSeconds(sunset).ToLocalTime().ToString("h:mm tt");

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
            // record sunrise/sunset and refresh background
            if (data.RootElement.TryGetProperty("sys", out var sys))
            {
                var sr = sys.GetProperty("sunrise").GetInt64();
                var ss = sys.GetProperty("sunset").GetInt64();
                sunriseTime = DateTimeOffset.FromUnixTimeSeconds(sr).ToLocalTime().DateTime;
                sunsetTime = DateTimeOffset.FromUnixTimeSeconds(ss).ToLocalTime().DateTime;
                UpdateBackground();
            }

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

    // Try to get API key from local backend (http://localhost:5000) first,
    // then fall back to configuration (appsettings) if backend not available.
    async Task<string?> GetApiKeyAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync("http://localhost:5000/api/key");
            if (resp.IsSuccessStatusCode)
            {
                var j = await resp.Content.ReadAsStringAsync();
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

    void UpdateBackground()
    {
        // compute colors: before sunrise dark, between sunrise and sunset blend to day, after sunset dark
        if (sunriseTime == null || sunsetTime == null) return;
        var now = DateTime.Now;
        Brush brush;
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
            Color Lerp(Color a, Color b, double f)
            {
                return new Color(
                    (float)(a.Red + (b.Red - a.Red) * f),
                    (float)(a.Green + (b.Green - a.Green) * f),
                    (float)(a.Blue + (b.Blue - a.Blue) * f));
            }

            Color start = t < 0.5 ? Lerp(morning, midday, t * 2) : Lerp(midday, evening, (t - 0.5) * 2);
            // darken slightly for the gradient end
            Color end = new Color((float)(start.Red * 0.8), (float)(start.Green * 0.8), (float)(start.Blue * 0.8));
            brush = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(start, 0.0f),
                new GradientStop(end, 1.0f)
            }, new Point(0,0), new Point(0,1));
        }
        this.Background = brush;
    }
}