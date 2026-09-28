using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DotCode.Engine.Sandbox;

/// <summary>Windows Job Object for sandboxed shell commands: the whole process tree is killed when the job closes
/// (no orphaned background processes), children cannot break away, and optional memory / process-count limits and
/// UI restrictions (clipboard, desktop switching, system settings, logoff) apply. Windows has no unprivileged
/// file-system isolation equivalent to bubblewrap/Seatbelt, so this is containment, not confinement.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class JobObject : IDisposable
{
    private nint _handle;

    private JobObject(nint handle) => _handle = handle;

    public static JobObject? Attach(Process process, SandboxPolicy policy)
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) return null;
        var info = new ExtendedLimitInformation
        {
            Basic = new BasicLimitInformation { LimitFlags = KillOnJobClose | DieOnUnhandledException },
        };
        if (policy.MemoryLimitBytes is { } mem)
        {
            info.Basic.LimitFlags |= JobMemory;
            info.JobMemoryLimit = (nuint)mem;
        }
        if (policy.MaxProcesses is { } max)
        {
            info.Basic.LimitFlags |= ActiveProcess;
            info.Basic.ActiveProcessLimit = (uint)max;
        }
        var ui = new BasicUiRestrictions
        {
            UIRestrictionsClass = UiDesktop | UiDisplaySettings | UiExitWindows | UiGlobalAtoms | UiReadClipboard | UiWriteClipboard | UiSystemParameters,
        };
        var ok = SetExtendedLimits(job, ExtendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>())
                 && SetUiRestrictions(job, BasicUiRestrictionsClass, ref ui, (uint)Marshal.SizeOf<BasicUiRestrictions>());
        try
        {
            ok = ok && AssignProcessToJobObject(job, process.Handle);
        }
        catch (InvalidOperationException) { ok = false; }
        if (!ok)
        {
            CloseHandle(job);
            return null;
        }
        return new JobObject(job);
    }

    /// <summary>True when the process belongs to any job (used by tests).</summary>
    public static bool IsInJob(Process process) => IsProcessInJob(process.Handle, 0, out var result) && result;

    /// <summary>Closing the job kills every process still in it.</summary>
    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) CloseHandle(h);
    }

    private const uint KillOnJobClose = 0x2000, DieOnUnhandledException = 0x400, JobMemory = 0x200, ActiveProcess = 0x8;
    private const uint UiDesktop = 0x40, UiDisplaySettings = 0x10, UiExitWindows = 0x80, UiGlobalAtoms = 0x20,
        UiReadClipboard = 0x2, UiWriteClipboard = 0x4, UiSystemParameters = 0x8;
    private const int BasicUiRestrictionsClass = 4, ExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicUiRestrictions
    {
        public uint UIRestrictionsClass;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetExtendedLimits(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetUiRestrictions(nint job, int infoClass, ref BasicUiRestrictions info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
