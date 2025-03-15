using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

var bot = new TelegramBotClient("7742644654:AAEdkUAdK6KWrEWglrtMn-u3qpOq4A4hSm4");

using CancellationTokenSource cts = new();

var receiverOptions = new ReceiverOptions()
{
    AllowedUpdates = Array.Empty<UpdateType>()
};

bot.StartReceiving(
    UpdateHandlerAsync,
    PollingErrorHandlerAsync,
    receiverOptions,
    cts.Token
);

Console.WriteLine("Bot started. Press Enter to exit");
Console.ReadLine();
cts.Cancel();

// Вызывается при приеме сообщения Telegram-ботом
async Task UpdateHandlerAsync(ITelegramBotClient botClient, Update update, CancellationToken token)
{
    try
    {
        if (update.Message is not { Text: { } messageText } message ||
            string.IsNullOrWhiteSpace(messageText))
            return;

        if (messageText.StartsWith("/"))
        {
            await HandleCommand(messageText, message.Chat.Id, token);
            return;
        }

        Console.WriteLine($"Received: {messageText}");

        var weatherService = new WeatherService();
        var weatherReport = await weatherService.GetTemperature(messageText, token);
        await SendMessage(weatherReport, message.Chat.Id, token, ParseMode.MarkdownV2);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex}");
        await SendMessage($"Ошибка: {ex.Message}", update.Message!.Chat.Id, token);
    }
}

async Task SendMessage(
    string text,
    long chatId,
    CancellationToken token,
    ParseMode parseMode = ParseMode.MarkdownV2) // Поддержка форматирования
{
    await bot.SendTextMessageAsync(
        chatId: chatId,
        text: text,
        parseMode: parseMode,
        cancellationToken: token);
}

async Task HandleCommand(string command, long chatId, CancellationToken token)
{
    var response = command.ToLower() switch
    {
        "/start" => "Добро пожаловать! Отправьте мне название города 🌤",
        "/help" => "Помощь:\nПросто отправьте название города на любом языке\nПример: _Москва_ или _London_",
        _ => "Неизвестная команда 🤔"
    };

    await SendMessage(response, chatId, token, ParseMode.MarkdownV2);
}

Task PollingErrorHandlerAsync(ITelegramBotClient botClient, Exception exception, CancellationToken token)
{
    Console.WriteLine(exception.Message);

    return Task.CompletedTask;
}


public class WeatherService
{
    const string GeocodingApiUrl = "https://geocoding-api.open-meteo.com/v1/search";
    const string WeatherApiUrl = "https://api.open-meteo.com/v1/forecast";
    const string TemperatureFormat = "0.0";

    private static readonly HttpClient _httpClient = new HttpClient();

    public async Task<string> GetTemperature(string cityName, CancellationToken token)
    {
        try
        {
            var normalizeName = NormalizeCityName(cityName);
            var location = await GetGeoChords(normalizeName, token);
            var weather = await GetWeatherData(location, token);

            // Проверка наличия всех необходимых данных
            if (weather.Current?.Temperature_2m == null
                || weather.Current.Wind_speed_10m == null
                || weather.Current.Relative_humidity_2m == null
                || weather.Current.ApparentTemperature == null)
            {
                throw new InvalidOperationException("Неполные данные о погоде");
            }

            return FormatTemperature(cityName, weather);
        }
        catch (InvalidOperationException ex)
        {
            return $"⚠️ Error: {EscapeMarkdownV2(ex.Message)}";
        }
    }

    private string FormatTemperature(string cityName, WeatherResponse weather)
    {
        var temp = weather.Current.Temperature_2m.ToString(TemperatureFormat, CultureInfo.InvariantCulture);
        var wind = weather.Current.Wind_speed_10m.ToString(TemperatureFormat, CultureInfo.InvariantCulture);
        var humidity = weather.Current.Relative_humidity_2m.ToString();
        var apparentTemperature = weather.Current.ApparentTemperature.ToString(TemperatureFormat, CultureInfo.InvariantCulture);

        return $"""
            🌆 *{EscapeMarkdownV2(cityName)}*
            🌡 Текущая температура: {EscapeMarkdownV2(temp)}°C
            🌞 Ощущается: {EscapeMarkdownV2(apparentTemperature)}°C
            💨 Скорость ветра: {EscapeMarkdownV2(wind)} m/s
            💧 Влажность: {EscapeMarkdownV2(humidity)}%            
            """;
    }

    async Task<GeoLocation> GetGeoChords(string cityName, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(cityName))
            throw new ArgumentException("Неверное название города", nameof(cityName));

        var encodedCityName = Uri.EscapeDataString(cityName);
        var url = $"{GeocodingApiUrl}?name={encodedCityName}" +
            "&count=10" +
            "&language=ru" +
            "&format=json";
        var response = await GetApiResponse<GeoResponse>(url, token);

        var foundCity = response.Results?.FirstOrDefault();

        return foundCity ?? throw new InvalidOperationException($"Город '{cityName}' не найден");
    }

    async Task<WeatherResponse> GetWeatherData(GeoLocation location, CancellationToken token)
    {
        var lat = location.Latitude.ToString(CultureInfo.InvariantCulture);
        var lon = location.Longitude.ToString(CultureInfo.InvariantCulture);

        var url = $"{WeatherApiUrl}?latitude={lat}&longitude={lon}" +
            "&current=temperature_2m,relative_humidity_2m,wind_speed_10m,apparent_temperature&wind_speed_unit=ms";

        return await GetApiResponse<WeatherResponse>(url, token);
    }

    async Task<T> GetApiResponse<T>(string url, CancellationToken token)
    {
        try
        {
            var response = await _httpClient.GetAsync(url, token);
            var content = await response.Content.ReadAsStringAsync(token);
            response.EnsureSuccessStatusCode();

            return JsonSerializer.Deserialize<T>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Invalid API response");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"HTTP Error: {ex.Message}");
            throw;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"JSON Parsing Error: {ex.Message}");
            throw;
        }
    }

    private string NormalizeCityName(string cityName)
    {
        return cityName.Trim()
            .Replace("-", " ")
            .Replace("ё", "е")
            .ToLowerInvariant();
    }

    string EscapeMarkdownV2(string text) =>
        Regex.Replace(text, @"([_*\[\]()~`>#+\-=|{}.!])", @"\$1");
}

public record GeoResponse(List<GeoLocation> Results);

public record GeoLocation(
    double Latitude,
    double Longitude,
    string Name);

public record WeatherResponse(CurrentWeather Current);

public record CurrentWeather(
    [property: JsonPropertyName("temperature_2m")] double Temperature_2m,
    [property: JsonPropertyName("relative_humidity_2m")] int Relative_humidity_2m,
    [property: JsonPropertyName("wind_speed_10m")] double Wind_speed_10m,
    [property: JsonPropertyName("apparent_temperature")] double ApparentTemperature
);