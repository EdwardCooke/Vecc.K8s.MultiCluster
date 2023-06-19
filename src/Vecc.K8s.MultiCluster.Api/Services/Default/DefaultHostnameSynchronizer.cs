﻿using k8s.Models;
using Microsoft.Extensions.Options;
using System.Net;
using Vecc.K8s.MultiCluster.Api.Models.Core;

namespace Vecc.K8s.MultiCluster.Api.Services.Default
{
    public class DefaultHostnameSynchronizer : IHostnameSynchronizer
    {
        private readonly ILogger<DefaultHostnameSynchronizer> _logger;
        private readonly IIngressManager _ingressManager;
        private readonly IServiceManager _serviceManager;
        private readonly ICache _cache;
        private readonly IOptions<MultiClusterOptions> _multiClusterOptions;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly LeaderStatus _leaderStatus;
        private readonly IDateTimeProvider _dateTimeProvider;
        private readonly CancellationTokenSource _shutdownCancellationTokenSource;
        private readonly CancellationToken _shutdownCancellationToken;
        private readonly ManualResetEvent _shutdownEvent;

        public DefaultHostnameSynchronizer(
            ILogger<DefaultHostnameSynchronizer> logger,
            IIngressManager ingressManager,
            IServiceManager serviceManager,
            ICache cache,
            IOptions<MultiClusterOptions> multiClusterOptions,
            IHostApplicationLifetime lifetime,
            LeaderStatus leaderStatus,
            IDateTimeProvider dateTimeProvider)
        {
            _logger = logger;
            _ingressManager = ingressManager;
            _serviceManager = serviceManager;
            _cache = cache;
            _multiClusterOptions = multiClusterOptions;
            _lifetime = lifetime;
            _leaderStatus = leaderStatus;
            _dateTimeProvider = dateTimeProvider;
            _lifetime.ApplicationStopping.Register(OnApplicationStopping);
            _shutdownCancellationTokenSource = new CancellationTokenSource();
            _shutdownEvent = new ManualResetEvent(false);
            _shutdownCancellationToken = _shutdownCancellationTokenSource.Token;
        }

        public async Task SynchronizeLocalClusterAsync()
        {
            IList<V1Ingress>? ingresses = null;
            IList<V1Service>? services = null;
            IList<V1Endpoints>? endpoints = null;
            IList<V1Service>? loadBalancerServices = null;
            Dictionary<string, IList<V1Ingress>> ingressHosts = null;

            await Task.WhenAll(
                Task.Run(async () => ingresses = await _ingressManager.GetAllIngressesAsync(null)),
                Task.Run(async () => services = await _serviceManager.GetServicesAsync(null)),
                Task.Run(async () => endpoints = await _serviceManager.GetEndpointsAsync(null)));

            _logger.LogInformation("Counts: ingress-{@ingress} services-{@services} endpoints-{@endpoints}", ingresses!.Count, services!.Count, endpoints!.Count);

            await Task.WhenAll(
                Task.Run(async () => ingressHosts = await _ingressManager.GetAvailableHostnamesAsync(ingresses, services, endpoints)),
                Task.Run(async () => loadBalancerServices = await _serviceManager.GetLoadBalancerEndpointsAsync(services)));

            var serviceHosts = await _serviceManager.GetAvailableHostnamesAsync(services, endpoints);

            _logger.LogInformation("Counts validingresses-{@ingress} load balancer services-{@lbservices} valid services-{@services}",
                ingressHosts!.Count, loadBalancerServices!.Count, serviceHosts.Count);

            var validServiceHosts = new Dictionary<string, V1Service>();
            var validIngressHosts = new Dictionary<string, V1Ingress>();
            var invalidHostnames = new List<string>();

            _logger.LogInformation("Setting service tracking");
            _logger.LogInformation("Purging current tracked list");
            await _cache.UntrackAllServicesAsync();
            _logger.LogInformation("Tracking ingress related services");
            foreach (var ingress in ingresses)
            {
                await _cache.SetResourceVersionAsync(ingress.Metadata.Uid, ingress.Metadata.ResourceVersion);
                var serviceNames = await _ingressManager.GetRelatedServiceNamesAsync(ingress);
                foreach (var name in serviceNames)
                {
                    await _cache.TrackServiceAsync(ingress.Metadata.NamespaceProperty, name);
                }
            }
            _logger.LogInformation("Tracking load balancer services");
            foreach (var service in loadBalancerServices)
            {
                await _cache.TrackServiceAsync(service.Metadata.NamespaceProperty, service.Metadata.Name);
            }
            _logger.LogInformation("Tracking services");
            foreach (var service in services)
            {
                await _cache.SetResourceVersionAsync(service.Metadata.Uid, service.Metadata.ResourceVersion);
            }
            _logger.LogInformation("Tracking endpoints");
            foreach (var endpoint in endpoints)
            {
                await _cache.SetResourceVersionAsync(endpoint.Metadata.Uid, endpoint.Metadata.ResourceVersion);
            }
            _logger.LogInformation("Done tracking services");

            foreach (var service in serviceHosts)
            {
                if (service.Value.Count > 1)
                {
                    _logger.LogWarning("Too many service hosts for {@hostname}", service.Key);
                    invalidHostnames.Add(service.Key);
                    continue;
                }
                if (ingressHosts.ContainsKey(service.Key))
                {
                    _logger.LogWarning("Host {@hostname} exists in both an ingress and a service", service.Key);
                    invalidHostnames.Add(service.Key);
                    continue;
                }

                validServiceHosts[service.Key] = service.Value[0];
            }

            foreach (var ingressHost in ingressHosts)
            {
                if (serviceHosts.ContainsKey(ingressHost.Key))
                {
                    _logger.LogWarning("Host {@hostname} exists in both an ingress and a service", ingressHost.Key);
                    continue;
                }

                foreach (var ingress in ingressHost.Value)
                {
                    if (validIngressHosts.TryGetValue(ingressHost.Key, out var foundIngress))
                    {
                        // check to make sure the endpoint IP's match, otherwise, mark as invalid and ignore this hostname.
                        var balancerEndpoints = foundIngress.Status.LoadBalancer.Ingress;
                        var ingressEndpoints = ingress.Status.LoadBalancer.Ingress;
                        var same = ingressEndpoints.All(lb => balancerEndpoints.Any(blb => blb.Ip == lb.Ip));
                        same = same && balancerEndpoints.All(blb => ingressEndpoints.Any(lb => lb.Ip == blb.Ip));
                        if (!same)
                        {
                            _logger.LogWarning("Exposed IP mismatch for {@hostname}", ingressHost.Key);
                            invalidHostnames.Add(ingressHost.Key);
                        }
                        continue;
                    }

                    validIngressHosts[ingressHost.Key] = ingress;
                }
            }

            var ipAddresses = new Dictionary<string, List<HostIP>>();
            var localClusterIdentifier = _multiClusterOptions.Value.ClusterIdentifier;

            foreach (var service in validServiceHosts)
            {
                var addresses = service.Value.Status.LoadBalancer.Ingress.Select(ingress => IPAddress.Parse(ingress.Ip)).ToArray();
                var priorityAnnotation = service.Value.GetAnnotation("multicluster.veccsolutions.com/priority");
                if (!int.TryParse(priorityAnnotation, out var priority) &&
                    priorityAnnotation != null)
                {
                    _logger.LogError("Priority annotation was not parsable as an int, defaulting to 0.");
                }

                var weightAnnotation = service.Value.GetAnnotation("multicluster.veccsolutions.com/weight");
                if (!int.TryParse(weightAnnotation, out var weight))
                {
                    weight = 50;
                    if (weightAnnotation != null)
                    {
                        _logger.LogError("Weight annotation was not parsable as an int, defaulting to 50");
                    }
                }

                if (!ipAddresses.ContainsKey(service.Key))
                {
                    ipAddresses[service.Key] = new List<HostIP>();
                }

                ipAddresses[service.Key].AddRange(addresses.Select(address => new HostIP
                {
                    ClusterIdentifier = localClusterIdentifier,
                    IPAddress = address.ToString(),
                    Priority = priority,
                    Weight = weight
                }));
            }

            foreach (var ingress in validIngressHosts)
            {
                var addresses = ingress.Value.Status.LoadBalancer.Ingress.Select(ingress => IPAddress.Parse(ingress.Ip)).ToArray();
                var priorityAnnotation = ingress.Value.GetAnnotation("multicluster.veccsolutions.com/priority");
                if (!int.TryParse(priorityAnnotation, out var priority) &&
                    priorityAnnotation != null)
                {
                    _logger.LogError("Priority annotation was not parsable as an int, defaulting to 0.");
                }

                var weightAnnotation = ingress.Value.GetAnnotation("multicluster.veccsolutions.com/weight");
                if (!int.TryParse(weightAnnotation, out var weight))
                {
                    weight = 50;
                    if (weightAnnotation != null)
                    {
                        _logger.LogError("Weight annotation was not parsable as an int, defaulting to 50");
                    }
                }

                if (!ipAddresses.ContainsKey(ingress.Key))
                {
                    ipAddresses[ingress.Key] = new List<HostIP>();
                }

                ipAddresses[ingress.Key].AddRange(addresses.Select(address => new HostIP
                {
                    ClusterIdentifier = localClusterIdentifier,
                    IPAddress = address.ToString(),
                    Priority = priority,
                    Weight = weight
                }));
            }

            foreach (var host in ipAddresses)
            {
                await _cache.SetHostIPsAsync(host.Key, host.Value.ToArray());
            }
        }

        public async Task<bool> SynchronizeLocalIngressAsync(V1Ingress ingress)
        {
            //TODO: we need to cache the service/ingress and state of the service object related to a hostname
            //      regardless if they are valid or not. We can then reference those cached objects to
            //      decrease the load on the kubernetes api server, instead of querying the entire cluster state
            //      we would only query the necessary services/endpoints and ingresses
            _logger.LogInformation("Synchronizing local cluster ingress {@namespace}/{@ingress}", ingress.Namespace(), ingress.Name());
            await SynchronizeLocalClusterAsync();
            _logger.LogInformation("Done");

            return true;
        }

        public async Task<bool> SynchronizeLocalServiceAsync(V1Service service)
        {
            //TODO: see SynchronizeLocalIngressAsync
            _logger.LogInformation("Synchronizing local cluster ingress {@namespace}/{@ingress}", service.Namespace(), service.Name());
            await SynchronizeLocalClusterAsync();
            _logger.LogInformation("Done");

            return true;
        }

        public async Task<bool> SynchronizeLocalEndpointsAsync(V1Endpoints endpoints)
        {
            _logger.LogInformation("Synchronizing local cluster ingress {@namespace}/{@ingress}", endpoints.Namespace(), endpoints.Name());
            await SynchronizeLocalClusterAsync();
            _logger.LogInformation("Done");

            return true;
        }

        public async Task WatchClusterHeartbeatsAsync()
        {
            while (_shutdownCancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_multiClusterOptions.Value.HeartbeatCheckInterval * 1000, _shutdownCancellationToken);
                }
                catch
                {
                    _logger.LogInformation("Cluster heartbeat monitor shutdown.");
                    return;
                }

                if (!_leaderStatus.IsLeader)
                {
                    _logger.LogTrace("Not the leader, not checking cluster heartbeats");
                    continue;
                }

                _logger.LogInformation("Checking cluster heartbeats");

                try
                {
                    var clusterIdentifiers = await _cache.GetClusterIdentifiersAsync();
                    var timeout = _dateTimeProvider.UtcNow.AddSeconds(-_multiClusterOptions.Value.HeartbeatTimeout);

                    foreach (var clusterIdentifier in clusterIdentifiers)
                    {
                        var clusterHeartbeat = await _cache.GetClusterHeartbeatTimeAsync(clusterIdentifier);
                        if (clusterHeartbeat < timeout)
                        {
                            var clusterHosts = await _cache.GetHostnamesAsync(clusterIdentifier);
                            _logger.LogWarning("Cluster {@clusterIdentifier} timeout is expired, last heartbeat was at {@heartbeat}", clusterIdentifier, clusterHeartbeat);

                            foreach (var hostname in clusterHosts)
                            {
                                _logger.LogWarning("Removing cluster hostname {@hostname} due to expired timeout", hostname);
                                await _cache.RemoveClusterHostnameAsync(clusterIdentifier, hostname);
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error checking cluster heartbeats");
                }
            }

            _shutdownEvent.Set();
        }

        public async Task ClusterHeartbeatAsync()
        {
            while (!_shutdownCancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_multiClusterOptions.Value.HeartbeatSetInterval * 1000, _shutdownCancellationToken);
                }
                catch
                {
                    _logger.LogInformation("Shutting down heartbeat due to application shutdown.");
                }

                if (!_leaderStatus.IsLeader)
                {
                    _logger.LogTrace("Not the leader, skipping cluster heartbeat");
                    continue;
                }

                _logger.LogInformation("Sending heartbeat");

                //set our own heartbeat
                var localClusterIdentifier = _multiClusterOptions.Value.ClusterIdentifier;
                var now = _dateTimeProvider.UtcNow;
                await _cache.SetClusterHeartbeatAsync(localClusterIdentifier, now);
                var heartbeatTasks = _multiClusterOptions.Value.Peers.Select(peer =>
                {
                    try
                    {
                        return Task.Run(async () =>
                        {
                            try
                            {
                                var httpClient = new HttpClient();
                                httpClient.BaseAddress = new Uri(peer.Url);
                                await httpClient.PostAsync($"/api/heartbeat/{localClusterIdentifier}", JsonContent.Create(new
                                {
                                    HeartBeat = now
                                }));
                            }
                            catch (Exception exception)
                            {
                                _logger.LogError("Unable to post heartbeat to {@peer}", peer);
                            }
                        }, _shutdownCancellationToken);
                    }
                    catch (TaskCanceledException)
                    {
                        _logger.LogWarning("Shutdown requested while posting heartbeat to {@peer}", peer);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Unexpexted exception posting heartbeat to {@peer}", peer);
                    }
                    return Task.CompletedTask;
                });

            await Task.WhenAll(heartbeatTasks);
        }
    }

    private void OnApplicationStopping()
    {
        _shutdownCancellationTokenSource.Cancel();
        _shutdownEvent.WaitOne(TimeSpan.FromSeconds(5));
    }
}
}