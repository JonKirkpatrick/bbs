using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Bbs.Client.App.ViewModels;

internal static class DeploymentTransportHelpers
{
    internal static string BuildAgentControlSocketPath(string botId)
    {
        var safe = new StringBuilder(botId.Length);
        foreach (var ch in botId)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')
            {
                safe.Append(ch);
            }
            else
            {
                safe.Append('_');
            }
        }

        if (safe.Length == 0)
        {
            safe.Append("bot");
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"bbs-agent-{safe}.sock");
        return socketPath + ".control";
    }

    internal static AgentControlResponse SendAgentControlRequest(string controlSocketPath, string messageType, IReadOnlyDictionary<string, object> payload, int timeoutMs)
    {
        const int maxAttempts = 12;
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(controlSocketPath))
                {
                    lastFailure = new IOException($"Control socket not found: {controlSocketPath}");
                    Thread.Sleep(100 * attempt);
                    continue;
                }

                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.ReceiveTimeout = timeoutMs;
                socket.SendTimeout = timeoutMs;
                socket.Connect(new UnixDomainSocketEndPoint(controlSocketPath));

                using var stream = new NetworkStream(socket, ownsSocket: true);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                _ = TryReadControlEnvelope(reader);

                var requestId = Guid.NewGuid().ToString("N");
                var request = new Dictionary<string, object?>
                {
                    ["v"] = "0.2",
                    ["id"] = requestId,
                    ["type"] = messageType,
                    ["payload"] = payload
                };

                writer.WriteLine(JsonSerializer.Serialize(request));

                while (true)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        throw new InvalidOperationException($"Agent control socket returned empty response for {messageType}.");
                    }

                    var envelope = ParseControlEnvelope(line);
                    if (!string.Equals(envelope.Id, requestId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return envelope;
                }
            }
            catch (SocketException ex)
            {
                lastFailure = ex;
                if (attempt == maxAttempts)
                {
                    throw;
                }

                Thread.Sleep(100 * attempt);
            }
            catch (IOException ex) when (FindSocketException(ex) is not null)
            {
                lastFailure = ex;
                if (attempt == maxAttempts)
                {
                    throw;
                }

                Thread.Sleep(100 * attempt);
            }
        }

        throw new InvalidOperationException($"Failed {messageType} control request after retries.", lastFailure);
    }

    internal static bool TryResolveSocketErrorCode(Exception ex, out string socketErrorCode)
    {
        var directSocket = FindSocketException(ex);
        if (directSocket is not null)
        {
            socketErrorCode = $"socket_{directSocket.SocketErrorCode}".ToLowerInvariant();
            return true;
        }

        var inspected = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<Exception>();
        pending.Enqueue(ex);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!inspected.Add(current))
            {
                continue;
            }

            var type = current.GetType();
            if (type.Name.Contains("SocketException", StringComparison.OrdinalIgnoreCase))
            {
                var socketCodeProperty = type.GetProperty("SocketErrorCode");
                if (socketCodeProperty?.GetValue(current) is { } codeValue)
                {
                    socketErrorCode = $"socket_{codeValue}".ToLowerInvariant();
                    return true;
                }

                socketErrorCode = "socket_error";
                return true;
            }

            if (current is IOException)
            {
                socketErrorCode = "socket_io_error";
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Enqueue(current.InnerException);
            }
        }

        socketErrorCode = string.Empty;
        return false;
    }

    internal static IReadOnlyList<string> BuildHostCandidates(string rawHost)
    {
        var candidates = new List<string>();
        var trimmed = rawHost.Trim();
        if (trimmed.Length == 0)
        {
            return candidates;
        }

        candidates.Add(trimmed);

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && !string.IsNullOrWhiteSpace(absolute.Host))
        {
            candidates.Add(absolute.Host);
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate($"tcp://{trimmed}", UriKind.Absolute, out var implicitUri) &&
            !string.IsNullOrWhiteSpace(implicitUri.Host))
        {
            candidates.Add(implicitUri.Host);
        }

        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            candidates.Add(trimmed[1..^1]);
        }

        if (IPAddress.TryParse(trimmed, out var parsedIp))
        {
            candidates.Add(parsedIp.ToString());
        }

        if (TryNormalizeThreePartLoopback(trimmed, out var normalizedLoopback))
        {
            candidates.Add(normalizedLoopback);
        }

        if (IsLikelyLoopback(trimmed))
        {
            candidates.Add("127.0.0.1");
            candidates.Add("localhost");
        }

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AgentControlResponse? TryReadControlEnvelope(StreamReader reader)
    {
        try
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            return ParseControlEnvelope(line);
        }
        catch
        {
            return null;
        }
    }

    internal static AgentControlResponse ParseControlEnvelope(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(root, "type");
        var id = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(root, "id");

        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return new AgentControlResponse(type, id, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var server = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "server");
        var message = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "message");
        var sessionId = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "session_id");
        var botId = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "bot_id");
        var controlToken = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "control_token");
        var ownerToken = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "owner_token");
        var dashboardEndpoint = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "dashboard_endpoint");
        var dashboardHost = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "dashboard_host");
        var dashboardPort = MainWindowViewModelHelpers.GetStringPropertyOrEmpty(payload, "dashboard_port");

        return new AgentControlResponse(type, id, message, server, sessionId, botId, controlToken, ownerToken, dashboardEndpoint, dashboardHost, dashboardPort);
    }

    private static bool TryNormalizeThreePartLoopback(string value, out string normalized)
    {
        normalized = string.Empty;
        var parts = value.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!string.Equals(parts[0], "127", StringComparison.Ordinal) || !string.Equals(parts[1], "0", StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[2], out var finalOctet) || finalOctet is < 0 or > 255)
        {
            return false;
        }

        normalized = $"127.0.0.{finalOctet}";
        return true;
    }

    private static bool IsLikelyLoopback(string host)
    {
        var value = host.Trim();
        return value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("127.", StringComparison.Ordinal);
    }

    private static SocketException? FindSocketException(Exception ex)
    {
        if (ex is SocketException directSocket)
        {
            return directSocket;
        }

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                var nested = FindSocketException(inner);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        if (ex.InnerException is not null)
        {
            return FindSocketException(ex.InnerException);
        }

        return null;
    }
}