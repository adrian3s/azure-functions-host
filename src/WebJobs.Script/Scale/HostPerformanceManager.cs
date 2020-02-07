// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.ProcessManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Script.Scale
{
    public class HostPerformanceManager : IDisposable
    {
        private readonly IEnvironment _environment;
        private readonly IOptions<HostHealthMonitorOptions> _healthMonitorOptions;
        private readonly ProcessMonitor _processMonitor;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;
        private bool _disposed = false;

        public HostPerformanceManager(IEnvironment environment, IOptions<HostHealthMonitorOptions> healthMonitorOptions, ILogger<HostPerformanceManager> logger, IServiceProvider serviceProvider)
        {
            if (environment == null)
            {
                throw new ArgumentNullException(nameof(environment));
            }
            if (healthMonitorOptions == null)
            {
                throw new ArgumentNullException(nameof(healthMonitorOptions));
            }

            _environment = environment;
            _healthMonitorOptions = healthMonitorOptions;
            _logger = logger;
            _serviceProvider = serviceProvider;
            _processMonitor = new ProcessMonitor(Process.GetCurrentProcess().Id, environment);

            _processMonitor.Start();
        }

        public virtual async Task<bool> IsUnderHighLoad(Collection<string> exceededCounters = null, ILogger logger = null)
        {
            // first check sandbox enforced performance counters
            var counters = GetPerformanceCounters(logger);
            bool isUnderHighLoad = false;
            if (counters != null)
            {
                isUnderHighLoad = PerformanceCounterThresholdsExceeded(counters, exceededCounters, _healthMonitorOptions.Value.CounterThreshold);
            }

            // next check process thresholds (CPU, Memory, etc.)
            isUnderHighLoad |= await ProcessThresholdsExceeded(exceededCounters, logger);

            return isUnderHighLoad;
        }

        private async Task<bool> ProcessThresholdsExceeded(Collection<string> exceededCounters = null, ILogger logger = null)
        {
            var stats = _processMonitor.GetStats();
            string formattedLoadHistory = string.Join(",", stats.CpuLoadHistory);
            logger?.LogDebug($"[HostMonitor] Host stats: EffectiveCores={_environment.GetEffectiveCoresCount()}, CpuLoadHistory=({formattedLoadHistory}), AvgLoad: {Math.Round(stats.CpuLoadHistory.Average())}, MaxLoad: {Math.Round(stats.CpuLoadHistory.Max())}");

            double workerCpuAverage = 0;
            var dispatcher = GetDispatcherAsync();
            if (dispatcher != null)
            {
                // if OOP, check worker stats
                int numSamples = 5;
                var workerStats = await dispatcher.GetWorkerStatsAsync();
                if (stats.CpuLoadHistory.Count() > numSamples && workerStats.Stats.All(p => p.Value.CpuLoadHistory.Count() > numSamples))
                {
                    // first compute the average CPU load for each worker
                    var averageCpuStats = new List<double>();
                    foreach (var currStats in workerStats.Stats)
                    {
                        int count = currStats.Value.CpuLoadHistory.Count();
                        if (count >= numSamples)
                        {
                            // take the last 5 samples
                            var averageCpu = stats.CpuLoadHistory.Skip(count - numSamples).Take(numSamples).Average();
                            averageCpuStats.Add(averageCpu);
                        }
                    }

                    // compute the final average across all workers
                    workerCpuAverage = averageCpuStats.Average();
                }
            }

            // calculate the aggregate load of host + workers (if OOP)
            var aggregateAverage = Math.Round(stats.CpuLoadHistory.Average() + workerCpuAverage);
            logger?.LogDebug($"[HostMonitor] Host aggregate load {aggregateAverage}");
            if (aggregateAverage >= HostHealthMonitorOptions.DefaultMaxCpuThreshold)
            {
                logger?.LogDebug($"[HostMonitor] Host overloaded ({aggregateAverage} >= {HostHealthMonitorOptions.DefaultMaxCpuThreshold})");
                if (exceededCounters != null)
                {
                    exceededCounters.Add("CPU");
                }
                return true;
            }

            return false;
        }

        public async Task PingWorkerAsync()
        {
            if (_environment.IsOutOfProc())
            {
                var dispatcher = GetDispatcherAsync();
                if (dispatcher != null)
                {
                    await dispatcher.PingAsync();
                }
            }
        }

        internal static bool PerformanceCounterThresholdsExceeded(ApplicationPerformanceCounters counters, Collection<string> exceededCounters = null, float threshold = HostHealthMonitorOptions.DefaultCounterThreshold)
        {
            bool exceeded = false;

            // determine all counters whose limits have been exceeded
            exceeded |= ThresholdExceeded("ActiveConnections", counters.ActiveConnections, counters.ActiveConnectionLimit, threshold, exceededCounters);
            exceeded |= ThresholdExceeded("Connections", counters.Connections, counters.ConnectionLimit, threshold, exceededCounters);
            exceeded |= ThresholdExceeded("Threads", counters.Threads, counters.ThreadLimit, threshold, exceededCounters);
            exceeded |= ThresholdExceeded("Processes", counters.Processes, counters.ProcessLimit, threshold, exceededCounters);
            exceeded |= ThresholdExceeded("NamedPipes", counters.NamedPipes, counters.NamedPipeLimit, threshold, exceededCounters);
            exceeded |= ThresholdExceeded("Sections", counters.Sections, counters.SectionLimit, threshold, exceededCounters);
            exceeded |= ThresholdExceeded("RemoteDirMonitors", counters.RemoteDirMonitors, counters.RemoteDirMonitorLimit, threshold, exceededCounters);

            return exceeded;
        }

        internal static bool ThresholdExceeded(string name, long currentValue, long limit, float threshold, Collection<string> exceededCounters = null)
        {
            if (limit <= 0)
            {
                // no limit to apply
                return false;
            }

            float currentUsage = (float)currentValue / limit;
            bool exceeded = currentUsage > threshold;
            if (exceeded && exceededCounters != null)
            {
                exceededCounters.Add(name);
            }
            return exceeded;
        }

        internal ApplicationPerformanceCounters GetPerformanceCounters(ILogger logger = null)
        {
            string json = _environment.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteAppCountersName);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    // TEMP: need to parse this specially to work around bug where
                    // sometimes an extra garbage character occurs after the terminal
                    // brace
                    int idx = json.LastIndexOf('}');
                    if (idx > 0)
                    {
                        json = json.Substring(0, idx + 1);
                    }

                    return JsonConvert.DeserializeObject<ApplicationPerformanceCounters>(json);
                }
                catch (JsonReaderException ex)
                {
                    logger.LogError($"Failed to deserialize application performance counters. JSON Content: \"{json}\"", ex);
                }
            }

            return null;
        }

        private IFunctionInvocationDispatcher GetDispatcherAsync()
        {
            var hostManager = _serviceProvider.GetService<IScriptHostManager>();
            var dispatcherFactory = (hostManager as IServiceProvider)?.GetService<IFunctionInvocationDispatcherFactory>();
            if (dispatcherFactory != null)
            {
                var dispatcher = dispatcherFactory.GetFunctionDispatcher();
                if (dispatcher.State == FunctionInvocationDispatcherState.Initialized)
                {
                    return dispatcher;
                }
            }
            return null;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _processMonitor?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
