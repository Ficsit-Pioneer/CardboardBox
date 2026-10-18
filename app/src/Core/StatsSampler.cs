using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CardboardBox.Core
{
    /// <summary>Takes a CPU / RAM reading once per second and keeps the last few minutes for the charts.</summary>
    public class StatsSampler
    {
        public const int HistoryLength = 120;

        public double ServerCpu { get; private set; }
        public long ServerRamBytes { get; private set; }
        public double SystemCpu { get; private set; }
        public long SystemRamUsedBytes { get; private set; }
        public long SystemRamTotalBytes { get; private set; }

        public readonly List<double> ServerCpuHistory = new List<double>();
        public readonly List<double> SystemCpuHistory = new List<double>();
        public readonly List<double> ServerRamHistory = new List<double>(); // in MB

        private TimeSpan _lastCpuTime;
        private DateTime _lastSample;
        private int _lastPid;

        public void Sample(Process server)
        {
            SystemCpu = Native.SampleSystemCpu();
            Native.GetMemory(out var total, out var avail);
            SystemRamTotalBytes = total;
            SystemRamUsedBytes = total - avail;

            double cpu = 0;
            long ram = 0;
            try
            {
                if (server != null && !server.HasExited)
                {
                    server.Refresh();
                    var now = DateTime.UtcNow;
                    var cpuTime = server.TotalProcessorTime;
                    if (_lastPid == server.Id && _lastSample != default(DateTime))
                    {
                        var wall = (now - _lastSample).TotalMilliseconds;
                        if (wall > 0)
                            cpu = (cpuTime - _lastCpuTime).TotalMilliseconds / (wall * Environment.ProcessorCount) * 100.0;
                    }
                    _lastPid = server.Id;
                    _lastCpuTime = cpuTime;
                    _lastSample = now;
                    ram = server.WorkingSet64;
                }
                else _lastPid = 0;
            }
            catch { _lastPid = 0; }

            ServerCpu = Math.Max(0, Math.Min(100, cpu));
            ServerRamBytes = ram;
            Push(ServerCpuHistory, ServerCpu);
            Push(SystemCpuHistory, SystemCpu);
            Push(ServerRamHistory, ram / (1024.0 * 1024.0));
        }

        private static void Push(List<double> list, double v)
        {
            list.Add(v);
            if (list.Count > HistoryLength) list.RemoveAt(0);
        }
    }
}
