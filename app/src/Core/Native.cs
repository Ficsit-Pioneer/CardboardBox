using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CardboardBox.Core
{
    /// <summary>Small Windows helpers: CPU / RAM numbers, keep-awake, and a "job" so Java dies with the app.</summary>
    public static class Native
    {
        // ---------------- System RAM ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        public static void GetMemory(out long totalBytes, out long availableBytes)
        {
            var m = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(m)) { totalBytes = (long)m.ullTotalPhys; availableBytes = (long)m.ullAvailPhys; }
            else { totalBytes = 0; availableBytes = 0; }
        }

        public static long TotalMemoryMB
        {
            get { GetMemory(out var t, out _); return t / (1024 * 1024); }
        }

        // ---------------- System CPU ----------------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        private static long _lastIdle, _lastTotal;

        /// <summary>Whole-PC CPU usage in percent since the previous call.</summary>
        public static double SampleSystemCpu()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
            long total = kernel + user; // kernel time includes idle time
            long dIdle = idle - _lastIdle, dTotal = total - _lastTotal;
            _lastIdle = idle; _lastTotal = total;
            if (dTotal <= 0) return 0;
            return Math.Max(0, Math.Min(100, 100.0 * (dTotal - dIdle) / dTotal));
        }

        // ---------------- Keep awake ----------------

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);
        private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

        /// <summary>Stops Windows from going to sleep while the server runs (the screen can still turn off).</summary>
        public static void KeepAwake(bool on)
        {
            try { SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS); } catch { }
        }

        // ---------------- Job object ----------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attrs, string name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private static IntPtr _job = IntPtr.Zero;

        /// <summary>
        /// Ties a child process to CardboardBox: if CardboardBox is closed or crashes, Windows kills
        /// the Java server too, so it never keeps running invisibly and locking the world.
        /// </summary>
        public static void TieToThisProcess(Process child)
        {
            try
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    var ptr = Marshal.AllocHGlobal(len);
                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);
                        SetInformationJobObject(_job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)len);
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                AssignProcessToJobObject(_job, child.Handle);
            }
            catch (Exception ex)
            {
                Log.WriteFileOnly("WARN could not attach server to job object: " + ex.Message);
            }
        }
    }
}
