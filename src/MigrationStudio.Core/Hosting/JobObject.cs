using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MigrationStudio.Core.Hosting
{
    /// <summary>호스트 종료 시 에이전트를 함께 끝내기 위한 Job Object(Windows).</summary>
    public static class JobObject
    {
        private static IntPtr _hostJobHandle = IntPtr.Zero;
        private static readonly object Gate = new object();

        private const uint JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, uint infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static bool TryAssignKillOnClose(Process process, Action<string> warn)
        {
            if (process == null)
            {
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            lock (Gate)
            {
                try
                {
                    if (_hostJobHandle == IntPtr.Zero)
                    {
                        _hostJobHandle = CreateJobObject(IntPtr.Zero, null);
                        if (_hostJobHandle == IntPtr.Zero)
                        {
                            if (warn != null) warn("Job Object 생성 실패: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                            return false;
                        }

                        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                        {
                            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JobObjectLimitKillOnJobClose }
                        };
                        var size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                        var ptr = Marshal.AllocHGlobal(size);
                        try
                        {
                            Marshal.StructureToPtr(info, ptr, false);
                            if (!SetInformationJobObject(_hostJobHandle, JobObjectExtendedLimitInformation, ptr, (uint)size))
                            {
                                if (warn != null) warn("Job Object 설정 실패: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                                CloseHandle(_hostJobHandle);
                                _hostJobHandle = IntPtr.Zero;
                                return false;
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(ptr);
                        }
                    }

                    if (!AssignProcessToJobObject(_hostJobHandle, process.Handle))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (warn != null) warn("Job Object 할당 실패(무시): " + new Win32Exception(error).Message);
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    if (warn != null) warn("Job Object 오류(무시): " + ex.Message);
                    return false;
                }
            }
        }

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
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
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
    }
}
