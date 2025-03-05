﻿using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using Microsoft.Extensions.Options;
using NewRelic.Api.Agent;
using System.Collections.Concurrent;
using System.Net;
using System.Xml.Linq;
using Vecc.K8s.MultiCluster.Api.Models.Core;

namespace Vecc.K8s.MultiCluster.Api.Services.Default
{
    public class DefaultDnsResolver
    {
        private readonly ILogger<DefaultDnsResolver> _logger;
        private readonly IOptions<MultiClusterOptions> _options;
        private readonly IRandom _random;
        private readonly ICache _cache;
        private readonly ConcurrentDictionary<string, WeightedHostIp[]> _hosts;

        public DefaultDnsResolver(ILogger<DefaultDnsResolver> logger, IOptions<MultiClusterOptions> options, IRandom random, ICache cache)
        {
            _logger = logger;
            _options = options;
            _random = random;
            _cache = cache;
            _hosts = new ConcurrentDictionary<string, WeightedHostIp[]>();
        }

        public OnHostChangedAsyncDelegate OnHostChangedAsync => new OnHostChangedAsyncDelegate(RefreshHostInformationAsync);

        [Transaction]
        public Task<Response> ResolveAsync(Request incoming)
        {
            var result = Response.FromRequest(incoming);

            foreach (var question in incoming.Questions)
            {
                var q = question.Name.ToString();
                switch (question.Type)
                {
                    case RecordType.A:
                        SetARecords(question.Name, result);
                        break;
                    case RecordType.NS:
                        SetNSRecords(question.Name, result);
                        break;
                }
            }

            return Task.FromResult(result);
        }

        [Transaction]
        public async Task InitializeAsync()
        {
            var hostnames = await _cache.GetHostnamesAsync();

            if (hostnames != null)
            {
                foreach (var hostname in hostnames)
                {
                    await RefreshHostInformationAsync(hostname);
                }

                foreach (var hostname in _hosts.Keys.ToArray())
                {
                    if (!hostnames.Contains(hostname))
                    {
                        _hosts.Remove(hostname, out var _);
                    }
                }
            }
        }

        [Transaction]
        private async Task RefreshHostInformationAsync(string? hostname)
        {
            _logger.LogInformation("{@hostname} updated, refreshing state.", hostname);
            if (hostname == null)
            {
                _logger.LogError("Hostname is null");
                return;
            }

            var hostInformation = await _cache.GetHostInformationAsync(hostname);
            if (hostInformation?.HostIPs == null || hostInformation.HostIPs.Length == 0)
            {
                //no host information
                _logger.LogWarning("Host information lost for {@hostname}", hostname);
                _hosts.Remove(hostname, out var _);
                return;
            }

            var weightStart = 1;
            var weightedIPs = new List<WeightedHostIp>();
            foreach (var ip in hostInformation.HostIPs)
            {
                WeightedHostIp weightedHostIp;
                if (!IPAddress.TryParse(ip.IPAddress, out var ipAddress))
                {
                    _logger.LogWarning("IPAddress is not parseable {@hostname} {@clusterIdentifier} {@ip}", hostname, ip.ClusterIdentifier, ip.IPAddress);
                }
                if (ip.Weight == 0)
                {
                    weightedHostIp = new WeightedHostIp
                    {
                        IP = ip,
                        Priority = ip.Priority,
                        WeightMin = 0,
                        WeightMax = 0
                    };
                }
                else
                {
                    var maxWeight = weightStart + ip.Weight;
                    weightedHostIp = new WeightedHostIp
                    {
                        IP = ip,
                        Priority = ip.Priority,
                        WeightMin = weightStart,
                        WeightMax = maxWeight
                    };
                    weightStart = maxWeight + 1;
                }
                weightedIPs.Add(weightedHostIp);
            }

            _hosts[hostname] = weightedIPs.ToArray();
        }

        [Trace]
        private void SetARecords(Domain hostname, Response packet)
        {
            if (_hosts.TryGetValue(hostname.ToString(), out var host))
            {
                //figure out the host ips here
                var highestPriority = host.Min(h => h.Priority);
                var hostIPs = host.Where(h => h.Priority == highestPriority).ToArray();
                WeightedHostIp chosenHostIP;

                if (hostIPs.Length == 0)
                {
                    _logger.LogError("No host for {@hostname}", hostname);
                    return;
                }
                if (hostIPs.Length == 1)
                {
                    _logger.LogTrace("Only one host, no need to calculate weights");
                    chosenHostIP = hostIPs[0];
                }
                else
                {
                    var maxWeight = hostIPs.Max(h => h.WeightMax);
                    if (maxWeight == 0)
                    {
                        //no weighted hosts available, randomly choose one
                        var max = hostIPs.Length - 1;
                        var next = _random.Next(max);
                        chosenHostIP = hostIPs[next];
                    }
                    else
                    {
                        //don't choose the 0 weights, they are fail over endpoints
                        var next = _random.Next(1, maxWeight);
                        chosenHostIP = hostIPs.Single(h => h.WeightMin <= next && h.WeightMax >= next);
                    }
                }

                var record = GetIPResourceRecord(hostname, chosenHostIP.IP.IPAddress);
                if (record != null)
                {
                    packet.AnswerRecords.Add(record);
                }
            }
            else
            {
                _logger.LogInformation("Unknown host: {@hostname}", hostname);
            }
        }

        [Trace]
        private void SetNSRecords(Domain hostname, Response packet)
        {
            IEnumerable<IResourceRecord>? answers = default;
            IEnumerable<IResourceRecord>? additionalAnswers = default;

            if (_options.Value.NameserverNames.TryGetValue(hostname.ToString(), out var names))
            {
                var moreAnswers = new List<IResourceRecord>();

                answers = names.Select(name => new NameServerResourceRecord(hostname, new Domain(name), TimeSpan.FromSeconds(_options.Value.DefaultRecordTTL)));

                foreach (var name in names)
                {
                    if (_hosts.TryGetValue(name, out var hosts))
                    {
                        var ips = new List<string>();
                        foreach (var host in hosts)
                        {
                            if (!ips.Contains(host.IP.IPAddress))
                            {
                                var record = GetIPResourceRecord(new Domain(name), host.IP.IPAddress);
                                if (record != null)
                                {
                                    moreAnswers.Add(record);
                                }
                                ips.Add(host.IP.IPAddress);
                            }
                        }
                    }
                }

                additionalAnswers = moreAnswers;
            }
            else
            {
                var answer = new StartOfAuthorityResourceRecord(
                    hostname,
                    new Domain(_options.Value.DNSHostname),
                    new Domain(_options.Value.DNSServerResponsibleEmailAddress),
                    ttl: TimeSpan.FromSeconds(_options.Value.DefaultRecordTTL));
                answers = [answer];
            }

            if (answers != null)
            {
                foreach (var answer in answers)
                {
                    packet.AnswerRecords.Add(answer);
                }
            }

            if (additionalAnswers != null)
            {
                foreach (var additionalAnswer in additionalAnswers)
                {
                    packet.AdditionalRecords.Add(additionalAnswer);
                }
            }
        }

        [Trace]
        private BaseResourceRecord? GetIPResourceRecord(Domain hostname, string ip)
        {
            var ipAddress = IPAddress.Parse(ip);

            var record = new IPAddressResourceRecord(
                hostname,
                ipAddress,
                TimeSpan.FromSeconds(_options.Value.DefaultRecordTTL));

            return record;
        }

        private struct WeightedHostIp
        {
            public int Priority { get; set; }
            public int WeightMin { get; set; }
            public int WeightMax { get; set; }
            public HostIP IP { get; set; }
        }
    }
}
