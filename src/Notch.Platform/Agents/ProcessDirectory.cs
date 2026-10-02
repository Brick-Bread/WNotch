using System.Runtime.InteropServices;

namespace Notch.Platform.Agents;

/// <summary>
/// Reads the folder another program was started in. opencode's plugin reports the folder a
/// session works in, and this is how Notch tells which of several opencode windows that is.
/// Only programs of the same user can be read, and only 64-bit ones; anything else gives null.
/// </summary>
public static unsafe class ProcessDirectory
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    // Offsets into the 64-bit PEB and RTL_USER_PROCESS_PARAMETERS, which have not moved since Windows 7.
    private const int PebProcessParameters = 0x20;
    private const int ParametersCurrentDirectory = 0x38;

    /// <summary>The working folder of the program with this id, or null when it cannot be read.</summary>
    public static string? TryRead(int pid)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || pid <= 0)
        {
            return null;
        }

        nint process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            if (IsWow64Process(process, out bool wow64) && wow64)
            {
                return null;
            }

            var basic = new ProcessBasicInformation();
            if (NtQueryInformationProcess(process, 0, ref basic, sizeof(ProcessBasicInformation), out _) != 0 || basic.PebBaseAddress == 0)
            {
                return null;
            }

            nint parameters = 0;
            if (!ReadProcessMemory(process, basic.PebBaseAddress + PebProcessParameters, &parameters, (nuint)sizeof(nint), out _) || parameters == 0)
            {
                return null;
            }

            UnicodeString path;
            if (!ReadProcessMemory(process, parameters + ParametersCurrentDirectory, &path, (nuint)sizeof(UnicodeString), out _)
                || path.Buffer == 0 || path.Length is 0 or > 32_000)
            {
                return null;
            }

            var buffer = new char[path.Length / 2];
            fixed (char* chars = buffer)
            {
                if (!ReadProcessMemory(process, path.Buffer, chars, path.Length, out _))
                {
                    return null;
                }
            }

            string folder = new(buffer);
            return folder.Length > 3 ? folder.TrimEnd('\\') : folder;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(nint process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint read);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass, ref ProcessBasicInformation information, int length, out int returned);
}
