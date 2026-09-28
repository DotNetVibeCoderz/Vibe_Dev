using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DotCode.TermCapture;

/// <summary>Minimal Windows ConPTY host: creates a pseudo console of a fixed size, starts a process attached to it
/// and exposes the input/output pipes.</summary>
internal sealed unsafe class PseudoConsole : IDisposable
{
    private nint _hpc;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private nint _processHandle;
    private nint _threadHandle;
    public FileStream Input { get; }
    public FileStream Output { get; }
    public int ProcessId { get; private set; }

    public PseudoConsole(short cols, short rows)
    {
        if (!CreatePipe(out var inputRead, out _inputWrite, 0, 0)) throw new Win32Exception();
        if (!CreatePipe(out _outputRead, out var outputWrite, 0, 0)) throw new Win32Exception();
        var hr = CreatePseudoConsole(new Coord(cols, rows), inputRead, outputWrite, 0, out _hpc);
        if (hr != 0) throw new Win32Exception(hr);
        inputRead.Dispose();
        outputWrite.Dispose();
        Input = new FileStream(_inputWrite, FileAccess.Write);
        Output = new FileStream(_outputRead, FileAccess.Read);
    }

    public void Start(string commandLine, string cwd, IDictionary<string, string?> env)
    {
        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new Win32Exception();
        const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc, (nint)sizeof(nint), 0, 0)) throw new Win32Exception();

        var si = new StartupInfoEx();
        si.StartupInfo.cb = sizeof(StartupInfoEx);
        // Null std handles force the child onto the pseudo console even when this process has redirected stdio.
        si.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES
        si.lpAttributeList = attrList;

        // Environment block: current env + overrides, UTF-16, double-null terminated.
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var (k, v) in env) { if (v is null) vars.Remove(k); else vars[k] = v; }
        var block = new StringBuilder();
        foreach (var (k, v) in vars) block.Append(k).Append('=').Append(v).Append('\0');
        block.Append('\0');
        var envPtr = Marshal.StringToHGlobalUni(block.ToString());

        const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        var cmd = new StringBuilder(commandLine);
        if (!CreateProcessW(null, cmd, 0, 0, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, envPtr, cwd, ref si, out var pi))
            throw new Win32Exception();
        _processHandle = pi.hProcess;
        _threadHandle = pi.hThread;
        ProcessId = pi.dwProcessId;
        Marshal.FreeHGlobal(envPtr);
    }

    public bool HasExited => WaitForSingleObject(_processHandle, 0) == 0;

    public void Dispose()
    {
        try { Input.Dispose(); } catch { }
        if (_hpc != 0) { ClosePseudoConsole(_hpc); _hpc = 0; }
        if (_processHandle != 0)
        {
            if (!HasExited) TerminateProcess(_processHandle, 1);
            CloseHandle(_processHandle);
            CloseHandle(_threadHandle);
            _processHandle = 0;
        }
        try { Output.Dispose(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord(short x, short y) { public short X = x; public short Y = y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public nint lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, nint lpPipeAttributes, int nSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out nint phPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(nint hPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint lpAttributeList, int dwAttributeCount, int dwFlags, ref nint lpSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint lpAttributeList, uint dwFlags, nint attribute, nint lpValue, nint cbSize, nint lpPreviousValue, nint lpReturnSize);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, nint lpProcessAttributes, nint lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, nint lpEnvironment, string? lpCurrentDirectory, ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint hProcess, uint uExitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
