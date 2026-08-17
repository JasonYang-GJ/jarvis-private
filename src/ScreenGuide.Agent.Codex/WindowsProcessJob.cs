using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScreenGuide.Agent.Codex;

internal sealed class WindowsProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public WindowsProcessJob()
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 Windows Job Object。");
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!NativeMethods.SetInformationJobObject(
                    _handle,
                    JobObjectInfoType.ExtendedLimitInformation,
                    pointer,
                    (uint)size))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法设置 Windows Job Object 的进程树终止策略。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public void Assign(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(process);
        if (!NativeMethods.AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将 Codex 加入 Windows Job Object。");
        }
    }

    public void Terminate(uint exitCode = 1223)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NativeMethods.TerminateJobObject(_handle, exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法终止 Codex 进程树。");
        }
    }

    public bool HasActiveProcesses
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var size = Marshal.SizeOf<JobObjectBasicAccountingInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                if (!NativeMethods.QueryInformationJobObject(
                        _handle,
                        JobObjectInfoType.BasicAccountingInformation,
                        pointer,
                        (uint)size,
                        out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法核验 Codex Job Object 的活动进程。");
                }

                return Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(pointer)
                    .ActiveProcesses > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    private enum JobObjectInfoType
    {
        BasicAccountingInformation = 1,
        ExtendedLimitInformation = 9
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeFileHandle job,
            JobObjectInfoType infoType,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(
            SafeFileHandle job,
            JobObjectInfoType infoType,
            IntPtr information,
            uint informationLength,
            out uint returnLength);
    }
}
