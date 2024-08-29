﻿using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Rbac;
using Vecc.K8s.MultiCluster.Api.Models.K8sEntities;
using Vecc.K8s.MultiCluster.Api.Services.Default;

namespace Vecc.K8s.MultiCluster.Api.Controllers
{
    [EntityRbac(typeof(V1HostnameCache), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(V1ClusterCache), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(V1ResourceCache), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(V1ServiceCache), Verbs = RbacVerb.All)]
    public class K8sHostnameCacheController : IEntityController<V1HostnameCache>
    {
        private readonly ILogger<K8sHostnameCacheController> _logger;
        private readonly KubernetesQueue _queue;

        public K8sHostnameCacheController(ILogger<K8sHostnameCacheController> logger, KubernetesQueue queue)
        {
            _logger = logger;
            _queue = queue;
        }
        public Task DeletedAsync(V1HostnameCache entity, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Hostname cache {@namespace}/{@hostname} deleted", entity.Namespace(), entity.Name());
            _queue.OnHostChangedAsync(entity.Name());
            _logger.LogInformation("Host changed triggered");
            return Task.CompletedTask;
        }

        public Task ReconcileAsync(V1HostnameCache entity, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Hostname cache {@namespace}/{@hostname} reconcile requested", entity.Namespace(), entity.Name());
            _queue.OnHostChangedAsync(entity.Name());
            _logger.LogInformation("Host changed triggered");
            return Task.CompletedTask;
        }
    }
}