using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Notch.Platform.Interop;

namespace Notch.Platform.Terminal;

/// <summary>
/// A process running inside a Windows pseudo console (ConPTY). Output arrives as raw UTF-8
/// terminal bytes; input is written the same way. Disposing kills the whole process tree.
/// </summary>
public sealed unsafe class PseudoConsoleSession : IDisposable
{
    private readonly Lock _gate = new();
    private readonly FileStream _input;
    private readonly FileStream _output;
    private nint _pseudoConsole;
    private nint _process;
    private nint _job;
    private bool _disposed;

    private PseudoConsoleSession(nint pseudoConsole, nint process, nint job, int processId, SafeFileHandle input, SafeFileHandle output)
    {
        _pseudoConsole = pseudoConsole;
        _process = process;
        _job = job;
        ProcessId = processId;
        _input = new FileStream(input, FileAccess.Write, bufferSize: 0);
        _output = new FileStream(output, FileAccess.Read, bufferSize: 0);
    }

    public int ProcessId { get; }

    /// <summary>Raised on a background thread. The buffer is only valid during the call.</summary>
    public event Action<ReadOnlyMemory<byte>>? Output;

    /// <summary>Raised once, on a background thread, with the process exit code.</summary>
    public event Action<int>? Exited;

    /// <param name="commandLine">Full command line, quoted as CreateProcess expects.</param>
    /// <param name="environment">Variables added to (or overriding) this process's environment.</param>
    /// <exception cref="Win32Exception">The process could not be started.</exception>
    public static PseudoConsoleSession Start(
        string commandLine,
        string workingDirectory,
        int columns,
        int rows,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        nint pseudoConsole = 0, attributeList = 0, job = 0;
        PROCESS_INFORMATION process = default;

        try
        {
            if (!ConsoleNative.CreatePipe(out inputRead, out inputWrite, 0, 0)
                || !ConsoleNative.CreatePipe(out outputRead, out outputWrite, 0, 0))
            {
                throw new Win32Exception();
            }

            int hr = ConsoleNative.CreatePseudoConsole(ToCoord(columns, rows), inputRead, outputWrite, 0, out pseudoConsole);
            if (hr != 0)
            {
                throw new Win32Exception(hr);
            }

            nuint listSize = 0;
            ConsoleNative.InitializeProcThreadAttributeList(0, 1, 0, ref listSize);
            attributeList = Marshal.AllocHGlobal((nint)listSize);
            if (!ConsoleNative.InitializeProcThreadAttributeList(attributeList, 1, 0, ref listSize))
            {
                throw new Win32Exception();
            }

            if (!ConsoleNative.UpdateProcThreadAttribute(attributeList, 0, ConsoleNative.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, pseudoConsole, (nuint)nint.Size, 0, 0))
            {
                throw new Win32Exception();
            }

            var startup = new STARTUPINFOEXW { lpAttributeList = attributeList };
            startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);

            // Explicitly empty standard handles, so the child uses the pseudo console even
            // when this process was itself started with redirected output.
            startup.StartupInfo.dwFlags = ConsoleNative.STARTF_USESTDHANDLES;

            // CreateProcessW may modify the command line buffer, so it needs a writable copy.
            char[] commandBuffer = [.. commandLine, '\0'];
            string environmentBlock = BuildEnvironmentBlock(environment);

            fixed (char* command = commandBuffer)
            fixed (char* env = environmentBlock)
            fixed (char* directory = workingDirectory)
            {
                if (!ConsoleNative.CreateProcess(
                    null,
                    command,
                    0,
                    0,
                    inheritHandles: false,
                    ConsoleNative.EXTENDED_STARTUPINFO_PRESENT | ConsoleNative.CREATE_UNICODE_ENVIRONMENT,
                    env,
                    directory,
                    &startup,
                    out process))
                {
                    throw new Win32Exception();
                }
            }

            job = CreateKillOnCloseJob();
            if (job != 0)
            {
                ConsoleNative.AssignProcessToJobObject(job, process.hProcess);
            }

            ConsoleNative.CloseHandle(process.hThread);

            var session = new PseudoConsoleSession(pseudoConsole, process.hProcess, job, (int)process.dwProcessId, inputWrite, outputRead);
            pseudoConsole = 0;
            process = default;
            job = 0;
            inputWrite = null;
            outputRead = null;

            session.StartThreads();
            return session;
        }
        finally
        {
            // The pseudo console holds its own copies of these two ends.
            inputRead?.Dispose();
            outputWrite?.Dispose();

            // Everything below is only still set if startup failed part-way.
            inputWrite?.Dispose();
            outputRead?.Dispose();
            if (process.hProcess != 0)
            {
                ConsoleNative.CloseHandle(process.hProcess);
            }

            if (job != 0)
            {
                ConsoleNative.CloseHandle(job);
            }

            if (pseudoConsole != 0)
            {
                ConsoleNative.ClosePseudoConsole(pseudoConsole);
            }

            if (attributeList != 0)
            {
                ConsoleNative.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _input.Write(data);
                _input.Flush();
            }
            catch (IOException)
            {
                // The process has already gone.
            }
        }
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (!_disposed && _pseudoConsole != 0)
            {
                ConsoleNative.ResizePseudoConsole(_pseudoConsole, ToCoord(columns, rows));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Closing the job kills the process and everything it started.
            if (_job != 0)
            {
                ConsoleNative.CloseHandle(_job);
                _job = 0;
            }

            _input.Dispose();
        }

        ClosePseudoConsole();
    }

    private static COORD ToCoord(int columns, int rows) => new()
    {
        X = (short)Math.Clamp(columns, 2, short.MaxValue),
        Y = (short)Math.Clamp(rows, 2, short.MaxValue),
    };

    private static nint CreateKillOnCloseJob()
    {
        nint job = ConsoleNative.CreateJobObject(0, 0);
        if (job == 0)
        {
            return 0;
        }

        var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        info.BasicLimitInformation.LimitFlags = ConsoleNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        ConsoleNative.SetInformationJobObject(job, ConsoleNative.JobObjectExtendedLimitInformation, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        return job;
    }

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? extra)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string?)entry.Value ?? "";
        }

        if (extra is not null)
        {
            foreach ((string key, string value) in extra)
            {
                variables[key] = value;
            }
        }

        var block = new StringBuilder();
        foreach ((string key, string value) in variables)
        {
            block.Append(key).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    private void StartThreads()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY output" }.Start();
        new Thread(WaitForExit) { IsBackground = true, Name = "ConPTY exit" }.Start();
    }

    private void ReadLoop()
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = _output.Read(buffer, 0, buffer.Length)) > 0)
            {
                Output?.Invoke(buffer.AsMemory(0, read));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The pseudo console was closed.
        }
        finally
        {
            _output.Dispose();
        }
    }

    private void WaitForExit()
    {
        nint process = _process;
        ConsoleNative.WaitForSingleObject(process, ConsoleNative.INFINITE);
        ConsoleNative.GetExitCodeProcess(process, out uint exitCode);
        ConsoleNative.CloseHandle(process);
        _process = 0;

        // The output pipe only reports end-of-stream once the pseudo console is closed.
        ClosePseudoConsole();
        Exited?.Invoke(unchecked((int)exitCode));
    }

    private void ClosePseudoConsole()
    {
        // Under the lock so a concurrent Resize never touches a closed handle.
        lock (_gate)
        {
            if (_pseudoConsole != 0)
            {
                ConsoleNative.ClosePseudoConsole(_pseudoConsole);
                _pseudoConsole = 0;
            }
        }
    }
}
