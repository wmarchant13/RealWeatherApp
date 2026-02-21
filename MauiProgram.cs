using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace RealWeatherApp;

public static class MauiProgram
{
	// centralized configuration access (nullable until CreateMauiApp runs)
	public static IConfiguration? AppConfiguration { get; private set; }

	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// pull in JSON configuration files (base + development override) from Properties
		builder.Configuration
			.AddJsonFile("Properties/appsettings.json", optional: true, reloadOnChange: true)
			.AddJsonFile("Properties/appsettings.development.json", optional: true, reloadOnChange: true);

#if DEBUG
		builder.Logging.AddDebug();
#endif

		// make configuration available for pages
		AppConfiguration = builder.Configuration;

		return builder.Build();
	}
}
