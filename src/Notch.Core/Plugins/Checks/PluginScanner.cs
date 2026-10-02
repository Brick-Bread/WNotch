using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Notch.Core.Plugins.Checks;

public enum ScanSeverity
{
    /// <summary>Worth knowing; does not stop the plugin.</summary>
    Info,

    /// <summary>Could be misused; shown to the user before they switch the plugin on.</summary>
    Warning,

    /// <summary>Something no plugin has a reason to do; the plugin is not started.</summary>
    Block,
}

/// <param name="Category">A short word: keylogging, screen-capture, input-injection, process-injection, process, native-code, dynamic-code, network.</param>
public sealed record ScanFinding(ScanSeverity Severity, string Category, string Detail, string File);

/// <summary>What looking inside a plugin's files found.</summary>
public sealed record ScanReport(IReadOnlyList<ScanFinding> Findings)
{
    public static readonly ScanReport Empty = new([]);

    /// <summary>True when the plugin does something that no plugin should, so it must not run.</summary>
    public bool Blocked => Findings.Any(f => f.Severity == ScanSeverity.Block);

    public bool HasNativeCode => Findings.Any(f => f.Category == "native-code");

    /// <summary>One line naming what blocked the plugin, for Settings.</summary>
    public string? BlockReason
    {
        get
        {
            ScanFinding[] blockers = [.. Findings.Where(f => f.Severity == ScanSeverity.Block).DistinctBy(f => f.Category).Take(3)];
            return blockers.Length == 0
                ? null
                : "Blocked: it uses " + string.Join(", ", blockers.Select(f => f.Detail)) + ".";
        }
    }
}

/// <summary>
/// Looks inside a plugin's assemblies, without loading them, for calls that read the keyboard,
/// capture the screen, fake input or reach into other processes. This is a first filter: names
/// can be hidden, which is why plugins also run in a process that cannot do these things at all.
/// </summary>
public static class PluginScanner
{
    /// <summary>The scan is cut off after this many files so a folder of thousands cannot stall startup.</summary>
    private const int MaxFiles = 400;

    private static readonly Dictionary<string, (ScanSeverity Severity, string Category, string Description)> Imports =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SetWindowsHookEx"] = (ScanSeverity.Block, "keylogging", "keyboard or mouse hooks"),
            ["SetWindowsHookExA"] = (ScanSeverity.Block, "keylogging", "keyboard or mouse hooks"),
            ["SetWindowsHookExW"] = (ScanSeverity.Block, "keylogging", "keyboard or mouse hooks"),
            ["GetAsyncKeyState"] = (ScanSeverity.Block, "keylogging", "key state polling"),
            ["GetKeyboardState"] = (ScanSeverity.Block, "keylogging", "key state polling"),
            ["RegisterRawInputDevices"] = (ScanSeverity.Block, "keylogging", "raw input capture"),
            ["GetRawInputData"] = (ScanSeverity.Block, "keylogging", "raw input capture"),

            ["BitBlt"] = (ScanSeverity.Block, "screen-capture", "screen capture"),
            ["StretchBlt"] = (ScanSeverity.Block, "screen-capture", "screen capture"),
            ["PrintWindow"] = (ScanSeverity.Block, "screen-capture", "window capture"),
            ["D3D11CreateDevice"] = (ScanSeverity.Warning, "screen-capture", "Direct3D (can duplicate the desktop)"),
            ["CreateDXGIFactory"] = (ScanSeverity.Warning, "screen-capture", "DXGI (can duplicate the desktop)"),
            ["CreateDXGIFactory1"] = (ScanSeverity.Warning, "screen-capture", "DXGI (can duplicate the desktop)"),

            ["SendInput"] = (ScanSeverity.Block, "input-injection", "faked keyboard or mouse input"),
            ["keybd_event"] = (ScanSeverity.Block, "input-injection", "faked keyboard input"),
            ["mouse_event"] = (ScanSeverity.Block, "input-injection", "faked mouse input"),
            ["SetCursorPos"] = (ScanSeverity.Block, "input-injection", "mouse control"),
            ["AttachThreadInput"] = (ScanSeverity.Block, "input-injection", "other programs' input queues"),

            ["CreateRemoteThread"] = (ScanSeverity.Block, "process-injection", "code injection"),
            ["CreateRemoteThreadEx"] = (ScanSeverity.Block, "process-injection", "code injection"),
            ["WriteProcessMemory"] = (ScanSeverity.Block, "process-injection", "writing into other programs"),
            ["ReadProcessMemory"] = (ScanSeverity.Block, "process-injection", "reading other programs' memory"),
            ["VirtualAllocEx"] = (ScanSeverity.Block, "process-injection", "code injection"),
            ["NtCreateThreadEx"] = (ScanSeverity.Block, "process-injection", "code injection"),
            ["QueueUserAPC"] = (ScanSeverity.Block, "process-injection", "code injection"),
            ["SetThreadContext"] = (ScanSeverity.Block, "process-injection", "code injection"),
            ["NtWriteVirtualMemory"] = (ScanSeverity.Block, "process-injection", "writing into other programs"),

            ["OpenProcess"] = (ScanSeverity.Warning, "process", "opening other programs"),
            ["AdjustTokenPrivileges"] = (ScanSeverity.Warning, "process", "changing privileges"),
            ["ShellExecute"] = (ScanSeverity.Warning, "process", "starting programs"),
            ["ShellExecuteA"] = (ScanSeverity.Warning, "process", "starting programs"),
            ["ShellExecuteW"] = (ScanSeverity.Warning, "process", "starting programs"),
            ["ShellExecuteEx"] = (ScanSeverity.Warning, "process", "starting programs"),
            ["ShellExecuteExW"] = (ScanSeverity.Warning, "process", "starting programs"),
            ["CreateProcess"] = (ScanSeverity.Warning, "process", "starting programs"),
            ["CreateProcessA"] = (ScanSeverity.Warning, "process", "starting programs"),
            ["CreateProcessW"] = (ScanSeverity.Warning, "process", "starting programs"),
        };

    /// <summary>(namespace.type, member or null for any) pairs that are worth flagging when a plugin refers to them.</summary>
    private static readonly (string Type, string? Member, ScanSeverity Severity, string Category, string Description)[] ManagedCalls =
    [
        ("System.Windows.Forms.SendKeys", null, ScanSeverity.Block, "input-injection", "faked keyboard input"),
        ("System.Drawing.Graphics", "CopyFromScreen", ScanSeverity.Block, "screen-capture", "screen capture"),
        ("Windows.Graphics.Capture.GraphicsCaptureSession", null, ScanSeverity.Block, "screen-capture", "screen capture"),
        ("Windows.Graphics.Capture.Direct3D11CaptureFramePool", null, ScanSeverity.Block, "screen-capture", "screen capture"),
        ("System.Diagnostics.Process", "Start", ScanSeverity.Warning, "process", "starting programs"),
        ("System.Reflection.Assembly", "LoadFrom", ScanSeverity.Warning, "dynamic-code", "loading code at run time"),
        ("System.Reflection.Assembly", "LoadFile", ScanSeverity.Warning, "dynamic-code", "loading code at run time"),
        ("System.Reflection.Assembly", "Load", ScanSeverity.Warning, "dynamic-code", "loading code at run time"),
        ("System.Reflection.Emit.AssemblyBuilder", null, ScanSeverity.Warning, "dynamic-code", "generating code at run time"),
        ("System.Reflection.Emit.DynamicMethod", null, ScanSeverity.Warning, "dynamic-code", "generating code at run time"),
        ("System.Runtime.InteropServices.NativeLibrary", null, ScanSeverity.Warning, "native-code", "loading native libraries by name"),
        ("System.Runtime.InteropServices.Marshal", "GetDelegateForFunctionPointer", ScanSeverity.Warning, "native-code", "calling native function pointers"),
        ("System.Net.Http.HttpClient", null, ScanSeverity.Info, "network", "internet access"),
        ("System.Net.Sockets.Socket", null, ScanSeverity.Info, "network", "internet access"),
        ("System.Net.WebSockets.ClientWebSocket", null, ScanSeverity.Info, "network", "internet access"),
    ];

    public static ScanReport Scan(string pluginDirectory)
    {
        List<ScanFinding> findings = [];
        if (!Directory.Exists(pluginDirectory))
        {
            return ScanReport.Empty;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(pluginDirectory, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Take(MaxFiles)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ScanReport([new ScanFinding(ScanSeverity.Warning, "unreadable", "files that could not be listed", pluginDirectory)]);
        }

        foreach (string file in files)
        {
            string name = Path.GetRelativePath(pluginDirectory, file);
            try
            {
                using FileStream stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata)
                {
                    findings.Add(new ScanFinding(ScanSeverity.Warning, "native-code", "native code that cannot be inspected", name));
                    continue;
                }

                ScanManaged(pe.GetMetadataReader(), name, findings);
            }
            catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                findings.Add(new ScanFinding(ScanSeverity.Warning, "native-code", "a file that could not be inspected", name));
            }
        }

        // One line per kind of finding and file is enough; the same call is often made many times.
        return new ScanReport([.. findings.DistinctBy(f => (f.Severity, f.Category, f.Detail, f.File))]);
    }

    private static void ScanManaged(MetadataReader reader, string file, List<ScanFinding> findings)
    {
        foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
        {
            MethodImport import = reader.GetMethodDefinition(handle).GetImport();
            if (import.Module.IsNil)
            {
                continue;
            }

            string function = reader.GetString(import.Name);
            string library = reader.GetString(reader.GetModuleReference(import.Module).Name);
            if (Imports.TryGetValue(function, out var known))
            {
                findings.Add(new ScanFinding(known.Severity, known.Category, $"{known.Description} ({library}!{function})", file));
            }
            else if (!IsCommonLibrary(library))
            {
                findings.Add(new ScanFinding(ScanSeverity.Info, "native-code", $"calls into {library}", file));
            }
        }

        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            TypeReference type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            string typeName = reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
            string memberName = reader.GetString(member.Name);
            foreach (var call in ManagedCalls)
            {
                if (call.Type == typeName && (call.Member is null || call.Member == memberName))
                {
                    findings.Add(new ScanFinding(call.Severity, call.Category, call.Description, file));
                }
            }
        }
    }

    /// <summary>Operating-system libraries that nearly every program calls for harmless reasons.</summary>
    private static bool IsCommonLibrary(string library)
    {
        string name = Path.GetFileNameWithoutExtension(library);
        return name.Equals("kernel32", StringComparison.OrdinalIgnoreCase)
            || name.Equals("advapi32", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ntdll", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ole32", StringComparison.OrdinalIgnoreCase)
            || name.Equals("shell32", StringComparison.OrdinalIgnoreCase)
            || name.Equals("user32", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gdi32", StringComparison.OrdinalIgnoreCase)
            || name.Equals("crypt32", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("api-ms-win", StringComparison.OrdinalIgnoreCase)
            || name.Equals("libc", StringComparison.OrdinalIgnoreCase);
    }
}
