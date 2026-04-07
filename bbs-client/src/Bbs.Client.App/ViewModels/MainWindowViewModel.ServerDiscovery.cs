using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;

namespace Bbs.Client.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void StartStartupServerProbe()
    {
        _ = RunServerProbeCycleAsync(trigger: "startup", updateEditorStatus: false);
    }

    private async Task RunServerProbeCycleAsync(string trigger, bool updateEditorStatus)
    {
        if (!TryBeginProbeCycle())
        {
            if (updateEditorStatus)
            {
                _serverService.ServerEditorMessage = "Probe already in progress.";
            }

            return;
        }

        if (updateEditorStatus)
        {
            _serverService.ServerEditorMessage = "Probing known servers...";
        }

        try
        {
            var result = await ProbeKnownServersAsync(CancellationToken.None);
            if (updateEditorStatus)
            {
                _serverService.ServerEditorMessage = $"Probe complete: {result.ReachableCount} reachable, {result.UnreachableCount} unreachable.";
            }

            _logger.Log(LogLevel.Information, "server_probe_cycle_completed", "Known server probe cycle completed.",
                new Dictionary<string, string>
                {
                    ["trigger"] = trigger,
                    ["reachable"] = result.ReachableCount.ToString(),
                    ["unreachable"] = result.UnreachableCount.ToString()
                });
        }
        catch (Exception ex)
        {
            if (updateEditorStatus)
            {
                _serverService.ServerEditorMessage = "Probe failed. See logs for details.";
            }

            _logger.Log(LogLevel.Warning, "server_probe_cycle_failed", "Known server probe cycle failed.",
                new Dictionary<string, string>
                {
                    ["trigger"] = trigger,
                    ["error"] = ex.GetType().Name
                });
        }
        finally
        {
            EndProbeCycle();
        }
    }

    private async Task<(int ReachableCount, int UnreachableCount)> ProbeKnownServersAsync(CancellationToken cancellationToken)
    {
        var knownServers = await _storage.ListKnownServersAsync(cancellationToken);
        if (knownServers.Count == 0)
        {
            return (0, 0);
        }

        var reachableCount = 0;
        var unreachableCount = 0;

        foreach (var knownServer in knownServers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probeResult = await ProbeKnownServerWithRetryAsync(knownServer, cancellationToken);
            var metadata = new Dictionary<string, string>(knownServer.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                [ProbeStatusMetadataKey] = probeResult.IsReachable ? "reachable" : "unreachable",
                [ProbeLastCheckedMetadataKey] = DateTimeOffset.UtcNow.ToString("O")
            };

            if (probeResult.IsReachable)
            {
                metadata.Remove(ProbeLastErrorMetadataKey);
                reachableCount++;

                var pluginCatalogResult = await RefreshServerPluginCatalogCacheAsync(knownServer, cancellationToken);
                if (pluginCatalogResult.Updated)
                {
                    metadata["probe_plugin_count"] = pluginCatalogResult.PluginCount.ToString();
                    metadata["probe_plugin_sync_utc"] = DateTimeOffset.UtcNow.ToString("O");
                }
                else if (!string.IsNullOrWhiteSpace(pluginCatalogResult.ErrorCode))
                {
                    metadata["probe_plugin_error"] = pluginCatalogResult.ErrorCode;
                }

                var ownerTokenResult = await EnsureServerOwnerTokenAsync(knownServer, metadata, cancellationToken);
                if (!string.IsNullOrWhiteSpace(ownerTokenResult.OwnerToken))
                {
                    metadata[ClientOwnerTokenMetadataKey] = ownerTokenResult.OwnerToken;
                }
            }
            else
            {
                metadata[ProbeLastErrorMetadataKey] = probeResult.ErrorCode;
                unreachableCount++;
            }

            var updatedServer = KnownServer.Create(
                serverId: knownServer.ServerId,
                name: knownServer.Name,
                host: knownServer.Host,
                port: knownServer.Port,
                useTls: knownServer.UseTls,
                metadata: metadata,
                createdAtUtc: knownServer.CreatedAtUtc,
                updatedAtUtc: DateTimeOffset.UtcNow);

            await _storage.UpsertKnownServerAsync(updatedServer, cancellationToken);
            _logger.Log(LogLevel.Information, "startup_server_probe_result", "Startup probe completed for known server.",
                new Dictionary<string, string>
                {
                    ["server_id"] = knownServer.ServerId,
                    ["host"] = knownServer.Host,
                    ["port"] = knownServer.Port.ToString(),
                    ["reachable"] = probeResult.IsReachable.ToString(),
                    ["attempts"] = probeResult.Attempts.ToString(),
                    ["error"] = probeResult.ErrorCode
                });
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var selectedServerId = SelectedServer?.ServerId;
            LoadServersFromStorage();
            if (!string.IsNullOrWhiteSpace(selectedServerId))
            {
                SelectedServer = FindServerById(selectedServerId);
            }

            TriggerServerAccessRefresh();
        });

        return (reachableCount, unreachableCount);
    }

    private bool TryBeginProbeCycle()
    {
        lock (_serverProbeLock)
        {
            if (_isServerProbeInProgress)
            {
                return false;
            }

            IsServerProbeInProgress = true;
            return true;
        }
    }

    private void EndProbeCycle()
    {
        lock (_serverProbeLock)
        {
            IsServerProbeInProgress = false;
        }
    }

    private async Task<(bool IsReachable, int Attempts, string ErrorCode)> ProbeKnownServerWithRetryAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        string? lastError = null;
        for (var attempt = 1; attempt <= ServerProbeMaxAttempts; attempt++)
        {
            var singleResult = await ProbeKnownServerOnceAsync(knownServer, cancellationToken);
            if (singleResult.IsReachable)
            {
                return (true, attempt, string.Empty);
            }

            lastError = singleResult.ErrorCode;
            if (attempt < ServerProbeMaxAttempts)
            {
                await Task.Delay(ServerProbeRetryDelayMs, cancellationToken);
            }
        }

        return (false, ServerProbeMaxAttempts, lastError ?? "probe_failed");
    }

    private async Task<(bool IsReachable, int Attempts, string ErrorCode)> ProbeKnownServerOnceAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(ServerProbeTimeoutMs);

        var endpointCandidates = BuildServerBaseEndpointCandidates(knownServer);
        var observedErrors = new List<string>();

        foreach (var endpoint in endpointCandidates)
        {
            try
            {
                var statusUri = new Uri(endpoint + "/api/status");
                using var response = await _serverCatalogHttpClient.GetAsync(statusUri, probeCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    observedErrors.Add($"status_http_{(int)response.StatusCode}_{endpoint}");
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync(probeCts.Token);
                if (TryIsStatusAck(payload))
                {
                    return (true, 1, string.Empty);
                }

                observedErrors.Add($"status_payload_invalid_{endpoint}");
            }
            catch (TaskCanceledException)
            {
                observedErrors.Add($"status_timeout_{endpoint}");
            }
            catch (HttpRequestException)
            {
                observedErrors.Add($"status_http_error_{endpoint}");
            }
            catch (JsonException)
            {
                observedErrors.Add($"status_payload_json_error_{endpoint}");
            }
        }

        var errorCode = observedErrors.Count == 0
            ? "status_probe_failed"
            : $"status_probe_failed:{string.Join(',', observedErrors)}";

        return (false, 1, errorCode);
    }

    private static bool TryIsStatusAck(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(payload);
        return MainWindowViewModelHelpers.IsStatusOkObject(doc.RootElement);
    }

    private void TriggerSelectedServerCatalogRefresh(ServerSummaryItem server)
    {
        if (!ShouldRefreshServerCatalog(server.ServerId))
        {
            return;
        }

        _ = RefreshSelectedServerCatalogAsync(server.ServerId);
    }

    private bool ShouldRefreshServerCatalog(string serverId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_serverCatalogRefreshLock)
        {
            if (_serverCatalogRefreshInFlight.Contains(serverId))
            {
                return false;
            }

            if (_serverCatalogLastRefreshUtc.TryGetValue(serverId, out var lastRefresh) &&
                now - lastRefresh < TimeSpan.FromMilliseconds(ServerCatalogSelectionRefreshCooldownMs))
            {
                return false;
            }

            _serverCatalogRefreshInFlight.Add(serverId);
            return true;
        }
    }

    private void MarkServerCatalogRefreshComplete(string serverId)
    {
        lock (_serverCatalogRefreshLock)
        {
            _serverCatalogRefreshInFlight.Remove(serverId);
            _serverCatalogLastRefreshUtc[serverId] = DateTimeOffset.UtcNow;
        }
    }

    private async Task RefreshSelectedServerCatalogAsync(string serverId)
    {
        try
        {
            var knownServer = (await _storage.ListKnownServersAsync()).FirstOrDefault(s => s.ServerId == serverId);
            if (knownServer is null)
            {
                return;
            }

            var result = await RefreshServerPluginCatalogCacheAsync(knownServer, CancellationToken.None);
            if (!result.Updated)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var selectedServerId = SelectedServer?.ServerId;
                LoadServersFromStorage();
                if (!string.IsNullOrWhiteSpace(selectedServerId))
                {
                    SelectedServer = FindServerById(selectedServerId);
                }
            });
        }
        finally
        {
            MarkServerCatalogRefreshComplete(serverId);
        }
    }

    private async Task<(bool Updated, int PluginCount, string ErrorCode)> RefreshServerPluginCatalogCacheAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        var fetchResult = await FetchServerPluginCatalogAsync(knownServer, cancellationToken);
        if (!fetchResult.Succeeded)
        {
            _logger.Log(LogLevel.Warning, "server_plugin_catalog_fetch_failed", "Failed to fetch server plugin catalog.",
                new Dictionary<string, string>
                {
                    ["server_id"] = knownServer.ServerId,
                    ["reason"] = fetchResult.ErrorCode
                });

            return (false, 0, fetchResult.ErrorCode);
        }

        var cache = ServerPluginCache.Create(knownServer.ServerId, fetchResult.Plugins, DateTimeOffset.UtcNow);
        await _storage.UpsertServerPluginCacheAsync(cache, cancellationToken);

        _logger.Log(LogLevel.Information, "server_plugin_catalog_cached", "Server plugin catalog refreshed from probe.",
            new Dictionary<string, string>
            {
                ["server_id"] = knownServer.ServerId,
                ["plugin_count"] = cache.Plugins.Count.ToString(),
                ["source"] = fetchResult.Source
            });

        return (true, cache.Plugins.Count, string.Empty);
    }

    private async Task<(bool Succeeded, IReadOnlyList<PluginDescriptor> Plugins, string ErrorCode, string Source)> FetchServerPluginCatalogAsync(KnownServer knownServer, CancellationToken cancellationToken)
    {
        var endpointCandidates = BuildServerBaseEndpointCandidates(knownServer);
        var observedErrors = new List<string>();

        foreach (var endpoint in endpointCandidates)
        {
            try
            {
                var apiCatalogUri = new Uri(endpoint + "/api/game-catalog");
                using var apiResponse = await _serverCatalogHttpClient.GetAsync(apiCatalogUri, cancellationToken);
                if (apiResponse.IsSuccessStatusCode)
                {
                    var jsonPayload = await apiResponse.Content.ReadAsStringAsync(cancellationToken);
                    if (ServerPluginCatalogParser.TryParseFromJsonCatalog(jsonPayload, out var plugins, out _))
                    {
                        return (true, plugins, string.Empty, "api_game_catalog");
                    }

                    observedErrors.Add($"api_parse_failed_{endpoint}");
                }
                else
                {
                    observedErrors.Add($"api_http_{(int)apiResponse.StatusCode}_{endpoint}");
                }
            }
            catch (TaskCanceledException)
            {
                observedErrors.Add($"api_timeout_{endpoint}");
            }
            catch (HttpRequestException)
            {
                observedErrors.Add($"api_http_error_{endpoint}");
            }
        }

        var errorCode = observedErrors.Count == 0
            ? "plugin_catalog_fetch_failed"
            : $"plugin_catalog_fetch_failed:{string.Join(',', observedErrors)}";

        return (false, Array.Empty<PluginDescriptor>(), errorCode, "api_game_catalog");
    }

    private async Task<(bool Succeeded, string OwnerToken)> EnsureServerOwnerTokenAsync(KnownServer knownServer, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var existingToken = metadata.TryGetValue(ClientOwnerTokenMetadataKey, out var rawExisting) && !string.IsNullOrWhiteSpace(rawExisting)
            ? rawExisting.Trim()
            : string.Empty;

        var endpointCandidates = BuildServerBaseEndpointCandidates(knownServer);
        foreach (var endpoint in endpointCandidates)
        {
            try
            {
                var uri = string.IsNullOrWhiteSpace(existingToken)
                    ? new Uri(endpoint + "/api/owner-token")
                    : new Uri(endpoint + "/api/owner-token?owner_token=" + Uri.EscapeDataString(existingToken));

                using var response = await _serverCatalogHttpClient.GetAsync(uri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!TryParseOwnerTokenResponse(payload, out var token) || string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                return (true, token);
            }
            catch (TaskCanceledException)
            {
                continue;
            }
            catch (HttpRequestException)
            {
                continue;
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return (false, existingToken);
    }

    private static bool TryParseOwnerTokenResponse(string payload, out string ownerToken)
    {
        ownerToken = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (!MainWindowViewModelHelpers.IsStatusOkObject(root))
        {
            return false;
        }

        if (!root.TryGetProperty("owner_token", out var ownerTokenNode))
        {
            return false;
        }

        ownerToken = ownerTokenNode.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(ownerToken);
    }

    private static IReadOnlyList<string> BuildServerBaseEndpointCandidates(KnownServer knownServer)
    {
        var preferredScheme = knownServer.UseTls ? "https" : "http";
        var alternateScheme = knownServer.UseTls ? "http" : "https";
        var endpoints = new List<string>();

        var metadataDashboardEndpoint = MainWindowViewModelHelpers.FirstNonEmptyMetadataValue(knownServer.Metadata, DashboardEndpointMetadataKeys);
        if (Uri.TryCreate(metadataDashboardEndpoint, UriKind.Absolute, out var dashboardUri))
        {
            endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(dashboardUri.Scheme, dashboardUri.Host, dashboardUri.Port));
        }

        var metadataDashboardPort = MainWindowViewModelHelpers.ParsePositivePort(knownServer.Metadata, DashboardPortMetadataKeys);
        if (metadataDashboardPort is not null)
        {
            endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(preferredScheme, knownServer.Host, metadataDashboardPort.Value));
            endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(alternateScheme, knownServer.Host, metadataDashboardPort.Value));
        }

        endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(preferredScheme, knownServer.Host, knownServer.Port));
        endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(alternateScheme, knownServer.Host, knownServer.Port));

        if (knownServer.Port != DashboardPortFallback)
        {
            endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(preferredScheme, knownServer.Host, DashboardPortFallback));
            endpoints.Add(MainWindowViewModelHelpers.BuildBaseEndpoint(alternateScheme, knownServer.Host, DashboardPortFallback));
        }

        return endpoints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

}
