KeyBackend — minimal local API for storing OpenWeatherMap API key

Overview

- Minimal ASP.NET Core minimal API that stores the API key in a per-user folder (outside the repo), so the key is not committed to Git.

Endpoints

- GET /api/key — returns { "apiKey": "..." } or 404 if not set
- POST /api/key — accepts JSON { "apiKey": "..." } to store the key
- DELETE /api/key — removes the stored key

Storage location

- Keys are written to the platform-appropriate application data folder, e.g. on macOS:
  ~/Library/Application Support/RealWeatherApp/openweather.key

Run

- From the KeyBackend folder:

```bash
dotnet run --project KeyBackend.csproj
```

Example usage

- Set key:

```bash
curl -X POST http://localhost:5000/api/key -H "Content-Type: application/json" -d '{"apiKey":"YOUR_KEY_HERE"}'
```

- Read key:

```bash
curl http://localhost:5000/api/key
```

Security notes

- This is intended for local development. You may want to secure the endpoint (auth) or use environment variables for production.
