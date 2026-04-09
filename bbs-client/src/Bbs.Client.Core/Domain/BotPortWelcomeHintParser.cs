using System.Text.Json;

namespace Bbs.Client.Core.Domain;

public static class BotPortWelcomeHintParser
{
    public static bool TryExtractDashboardPort(string? payload, out int dashboardPort)
    {
        dashboardPort = 0;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "welcome", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payloadElement) ||
                payloadElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!payloadElement.TryGetProperty("dashboard_port", out var dashboardPortElement))
            {
                return false;
            }

            if (dashboardPortElement.ValueKind == JsonValueKind.Number &&
                dashboardPortElement.TryGetInt32(out var numericPort) &&
                numericPort is >= 1 and <= 65535)
            {
                dashboardPort = numericPort;
                return true;
            }

            if (dashboardPortElement.ValueKind == JsonValueKind.String &&
                int.TryParse(dashboardPortElement.GetString(), out var textPort) &&
                textPort is >= 1 and <= 65535)
            {
                dashboardPort = textPort;
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}