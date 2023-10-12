﻿using Newtonsoft.Json;
using StackExchange.Redis;
using Vecc.K8s.MultiCluster.Api.Models.Core;

namespace Vecc.K8s.MultiCluster.Api.Services.Default
{
    public class RedisCache : ICache
    {
        private readonly ILogger<RedisCache> _logger;
        private readonly IDatabase _database;
        private readonly IQueue _queue;

        public RedisCache(ILogger<RedisCache> logger, IDatabase database, IQueue queue)
        {
            _logger = logger;
            _database = database;
            _queue = queue;
        }

        public async Task<Models.Core.Host?> GetHostInformationAsync(string hostname)
        {
            var key = $"hostnames.ips.{hostname}";
            var hostData = await _database.StringGetAsync(key);

            if (!hostData.HasValue)
            {
                _logger.LogWarning("{@hostname} not found in cache", hostname);
                return null;
            }

            var host = JsonConvert.DeserializeObject<HostModel>((string)hostData!);
            if (host == null)
            {
                _logger.LogError("{@hostname} found in cache but did not fit in the model. {@hostdata}", hostname, (string)hostData!);
                return null;
            }

            var result = new Models.Core.Host
            {
                Hostname = host.Hostname,
                HostIPs = host.HostIPs
            };

            return result;
        }

        public async Task<string[]> GetHostnamesAsync(string clusterIdentifier)
        {
            var keys = Array.Empty<string>();

            if (string.IsNullOrWhiteSpace(clusterIdentifier))
            {
                _logger.LogDebug("Getting all hostnames");

                keys = await GetKeysAsync("hostnames.ips.*");
                keys = keys.Select(key => key.Split('.', 3)[2]).ToArray();
            }
            else
            {
                keys = await GetKeysAsync($"cluster.{clusterIdentifier}.hosts.*");
                keys = keys.Select(key => key.Split('.', 4)[3]).ToArray();
            }

            return keys.Distinct().ToArray();
        }

        public async Task<Models.Core.Host[]?> GetHostsAsync(string clusterIdentifier)
        {
            var clusterIdentifiers = await GetClusterIdentifiersAsync();
            if (!clusterIdentifiers.Contains(clusterIdentifier))
            {
                _logger.LogWarning("Cluster identifier not found while getting hosts for {@clusterIdentifier}", clusterIdentifier);
                return null;
            }

            var keyPrefix = $"cluster.{clusterIdentifier}.hosts.";
            var keys = await GetKeysAsync($"{keyPrefix}*");
            var result = new List<Models.Core.Host>();

            foreach (var key in keys)
            {
                var slugs = key.Split('.', 4);
                if (slugs.Length != 4)
                {
                    _logger.LogError("Invalid key {@key} while fetching cluster hosts", key);
                    continue;
                }
                var cachedHost = await _database.StringGetAsync($"{keyPrefix}{slugs[3]}");
                var host = JsonConvert.DeserializeObject<HostModel>(cachedHost);
                result.Add(new Models.Core.Host
                {
                    HostIPs = host.HostIPs,
                    Hostname = host.Hostname
                });
            }

            return result.ToArray();
        }

        public async Task<string[]> GetKeysAsync(string prefix)
        {
            var allKeys = await _database.ExecuteAsync("KEYS", prefix);

            if (allKeys.Type != ResultType.MultiBulk)
            {
                _logger.LogError("KEYS returned incorrect type {@type}", allKeys.Type);
                return Array.Empty<string>();
            }

            var result = (string[])allKeys!;
            return result;
        }

        /// <summary>
        /// Sets the hostname in the credis cache. Returns whether the host was updated or not.
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="hostIPs"></param>
        /// <returns></returns>
        public async Task<bool> SetHostIPsAsync(string hostname, string clusterIdentifier, HostIP[] hostIPs)
        {
            var result = false;
            await VerifyClusterExistsAsync(clusterIdentifier);

            var key = $"cluster.{clusterIdentifier}.hosts.{hostname}";
            var hostModel = new HostModel
            {
                HostIPs = hostIPs,
                Hostname = hostname,
                ClusterIdentifier = clusterIdentifier
            };
            var ips = JsonConvert.SerializeObject(hostModel);
            var oldConfig = await _database.StringGetAsync(key);

            if (!oldConfig.HasValue || oldConfig != ips)
            {
                result = true;
                var status = await _database.StringSetAsync(key, ips);

                if (!status)
                {
                    //TODO: Implement retry logic for redis cache
                    _logger.LogError("Unable to update ips for host {@hostname}", hostname);
                }
            }

            //Only refresh the hostname ip's if they actually changed
            if (result)
            {
                await RefreshHostnameIps(hostname);
            }
            return result;
        }

        public async Task<string[]> GetClusterIdentifiersAsync()
        {
            var identifierResult = await _database.StringGetAsync("clusteridentifiers");

            if (!identifierResult.HasValue)
            {
                _logger.LogWarning("No cluster identifiers found, expected on first run.");
                return Array.Empty<string>();
            }

            var identifiers = (string)identifierResult!;
            var result = identifiers.Split('\t');

            return result;
        }

        public async Task<DateTime> GetClusterHeartbeatTimeAsync(string clusterIdentifier)
        {
            var heartbeat = await _database.StringGetAsync($"cluster.{clusterIdentifier}.heartbeat");
            if (DateTime.TryParseExact(heartbeat, "O", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var result))
            {
                return result;
            }
            else
            {
                _logger.LogError("Unable to parse heartbeat {@heartbeat} for cluster {@clusteridentifier}", (string?)heartbeat, clusterIdentifier);
                return default;
            }
        }

        public async Task<string> GetLastResourceVersionAsync(string uniqueIdentifier)
        {
            var result = await _database.StringGetAsync($"resourceversion.{uniqueIdentifier}");

            if (!result.HasValue)
            {
                return string.Empty;
            }

            return result!;
        }

        public async Task RemoveClusterHostnameAsync(string clusterIdentifier, string hostname)
        {
            var key = $"cluster.{clusterIdentifier}.hosts.{hostname}";

            await _database.KeyDeleteAsync(key);
            await RefreshHostnameIps(hostname);

            return;
        }

        public async Task<bool> RemoveClusterIdentifierAsync(string clusterIdentifier)
        {
            var clusterIdentifiers = await GetClusterIdentifiersAsync();
            if (!clusterIdentifiers.Contains(clusterIdentifier))
            {
                var identifiers = string.Join('\t', clusterIdentifiers.Where(i => i.ToLowerInvariant() != clusterIdentifier.ToLowerInvariant()));
                await _database.StringSetAsync("clusteridentifiers", identifiers);
                return true;
            }
            return false;
        }

        public async Task SetClusterHeartbeatAsync(string clusterIdentifier, DateTime heartbeat)
        {
            var key = $"cluster.{clusterIdentifier}.heartbeat";
            await _database.StringSetAsync(clusterIdentifier, heartbeat.ToString("O"));
        }

        public async Task SetResourceVersionAsync(string uniqueIdentifier, string version)
        {
            await _database.StringSetAsync($"resourceversion.{uniqueIdentifier}", version);
        }

        public async Task SynchronizeCachesAsync()
        {
            var clusterIdentifiers = await GetClusterIdentifiersAsync();
            var clusterKeys = await GetKeysAsync("cluster.*");

            foreach (var key in clusterKeys)
            {
                var slugs = key.Split('.', 4);
                var clusterIdentifier = slugs[1];
                if (!clusterIdentifiers.Contains(clusterIdentifier))
                {
                    await RemoveClusterIdentifierAsync(clusterIdentifier);
                    if (slugs.Length == 4 && slugs[2] == "hosts")
                    {
                        await RemoveClusterHostnameAsync(clusterIdentifier, slugs[3]);
                    }
                    else
                    {
                        await _database.KeyDeleteAsync(key);
                    }
                }
            }
        }

        private async Task RefreshHostnameIps(string hostname)
        {
            string key;
            var ipList = new List<HostIP>();

            var clusterIdentifiers = await GetClusterIdentifiersAsync();
            foreach (var identifier in clusterIdentifiers)
            {
                key = $"cluster.{identifier}.hosts.{hostname}";
                var clusterIps = await _database.StringGetAsync(key);
                var value = (string?)clusterIps;
                if (value != null)
                {
                    var hostModel = JsonConvert.DeserializeObject<HostModel>(value);
                    if (hostModel == null)
                    {
                        _logger.LogError("Serialized host data does not fit the hostmodel type. {@serialized}", value);
                    }
                    else
                    {
                        ipList.AddRange(hostModel.HostIPs);
                    }
                }
                else
                {
                    _logger.LogDebug("Key value {@key} is null", key);
                }
            }

            key = $"hostnames.ips.{hostname}";
            var host = new HostModel
            {
                Hostname = hostname,
                HostIPs = ipList.ToArray()
            };

            var ips = JsonConvert.SerializeObject(host);
            var status = await _database.StringSetAsync(key, ips);
            if (!status)
            {
                //TODO: Implement retry logic for redis cache
                _logger.LogError("Unable to update ips for host {@hostname}", hostname);
            }

            await _queue.PublishHostChangedAsync(hostname);
        }

        private async Task VerifyClusterExistsAsync(string clusterIdentifier)
        {
            var clusterIdentifiers = await GetClusterIdentifiersAsync();
            if (!clusterIdentifiers.Contains(clusterIdentifier))
            {
                var identifiers = string.Join('\t', clusterIdentifiers.Union(new[] { clusterIdentifier }));
                await _database.StringSetAsync("clusteridentifiers", identifiers);
            }
        }

        public async Task<bool> IsServiceMonitoredAsync(string ns, string name)
        {
            var cached = await _database.StringGetAsync($"trackedservices.{ns}.{name}");
            var result = cached.HasValue;

            return result;
        }

        public async Task TrackServiceAsync(string ns, string name)
        {
            await _database.StringSetAsync($"trackedservices.{ns}.{name}", "yes");
        }

        public async Task UntrackAllServicesAsync()
        {
            var keys = await GetKeysAsync("trackedservices.*");
            foreach (var key in keys)
            {
                await _database.KeyDeleteAsync(key);
            }
        }

        private class HostModel
        {
            public string ClusterIdentifier { get; set; } = string.Empty;
            public string Hostname { get; set; } = string.Empty;
            public HostIP[] HostIPs { get; set; } = Array.Empty<HostIP>();
        }
    }
}
