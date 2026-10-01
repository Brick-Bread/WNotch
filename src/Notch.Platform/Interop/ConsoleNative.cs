using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Notch.Platform.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct COORD
{
    public short X;
    public short Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STARTUPINFOW
{
    public uint cb;
    public nint lpReserved;
    public nint lpDesktop;
    public nint lpTitle;
    public uint dwX;
    public uint dwY;
    public uint dwXSize;
    public uint dwYSize;
    public uint dwXCountChars;
    public uint dwYCountChars;
    public uint dwFillAttribute;
    public uint dwFlags;
    public ushort wShowWindow;
    public ushort cbReserved2;
    public nint lpReserved2;
    public nint hStdInput;
    public nint hStdOutput;
    public nint hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STARTUPINFOEXW
{
    public STARTUPINFOW StartupInfo;
    public nint lpAttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public nint hProcess;
    public nint hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
internal struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

internal static unsafe partial class ConsoleNative
{
    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const nuint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const uint INFINITE = 0xFFFFFFFF;
    internal const uint STARTF_USESTDHANDLES = 0x00000100;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, nint attributes, uint size);

    [LibraryImport("kernel32.dll")]
    internal static partial int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint pseudoConsole);

    [LibraryImport("kernel32.dll")]
    internal static partial int ResizePseudoConsole(nint pseudoConsole, COORD size);

    [LibraryImport("kernel32.dll")]
    internal static partial void ClosePseudoConsole(nint pseudoConsole);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, uint flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(nint attributeList, uint flags, nuint attribute, nint value, nuint size, nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll")]
    internal static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcess(
        char* applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        char* environment,
        char* currentDirectory,
        STARTUPINFOEXW* startupInfo,
        out PROCESS_INFORMATION processInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    internal static partial nint CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
