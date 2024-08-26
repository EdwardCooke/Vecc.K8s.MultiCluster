﻿using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vecc.K8s.MultiCluster.Api.Services;

namespace Vecc.K8s.MultiCluster.Api.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("[Controller]")]
    public class HealthzController : Controller
    {
        private readonly IKubernetesClient _kubernetesClient;
        private readonly IDateTimeProvider _dateTimeProvider;
        private static DateTime _lastUp;
        private static bool _ready;

        public HealthzController(IKubernetesClient kubernetesClient, IDateTimeProvider dateTimeProvider)
        {
            _kubernetesClient = kubernetesClient;
            _dateTimeProvider = dateTimeProvider;
        }

        [HttpGet("Ready")]
        public async Task<IActionResult> ReadyAsync()
        {
            if (_ready)
            {
                return Ok("Already checked and ready to go");
            }
            V1Namespace? ns = null;
            try
            {
                ns = await _kubernetesClient.GetAsync<V1Namespace>(await _kubernetesClient.GetCurrentNamespaceAsync());
                if (ns == null)
                {
                    return StatusCode(500, "Namespace result was null");
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex);
            }

            _ready = true;
            return Ok(new { Status = "Kubernetes API returned something", Namespace = ns });
        }

        [HttpGet("Liveness")]
        public async Task<IActionResult> LivenessAsync()
        {
            if (_lastUp < _dateTimeProvider.UtcNow.AddSeconds(-10))
            {
                V1Namespace? ns = null;
                try
                {
                    ns = await _kubernetesClient.GetAsync<V1Namespace>(await _kubernetesClient.GetCurrentNamespaceAsync());
                    if (ns == null)
                    {
                        return StatusCode(500, "Namespace result was null");
                    }

                    _lastUp = _dateTimeProvider.UtcNow;
                    return Ok(new { Status = "Kubernetes API returned something", Namespace = ns });
                }
                catch (Exception ex)
                {
                    return StatusCode(500, ex);
                }
            }
            return Ok("Cached response is good.");
        }
    }
}
