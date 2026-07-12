using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GardenRankingsCore.Utils;

public static class DiscordWebhook
{
    private static readonly HttpClient HttpClient = new();

    public static async Task SendAsync(string url, string content)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var payload = new { content = content };
            var json = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await HttpClient.PostAsync(url, httpContent);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error($"Failed to send Discord webhook: {response.StatusCode} - {error}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error sending Discord webhook: {ex.Message}");
        }
    }
}
