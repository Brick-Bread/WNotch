using System.Diagnostics;
using Notch.Platform.Agents;

namespace Notch.Platform.Tests;

public class ProcessDirectoryTests
{
    [Fact]
    public void Reads_the_folder_this_program_runs_in()
    {
        string expected = Directory.GetCurrentDirectory().TrimEnd('\\');

        string? folder = ProcessDirectory.TryRead(Environment.ProcessId);

        Assert.Equal(expected, folder, ignoreCase: true);
    }

    [Fact]
    public void Reads_the_folder_another_program_was_started_in()
    {
        string folder = Path.Combine(Path.GetTempPath(), "notch-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using Process child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 20 127.0.0.1 > nul")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            string? read = null;
            for (int attempt = 0; attempt < 20 && read is null; attempt++)
            {
                read = ProcessDirectory.TryRead(child.Id);
                if (read is null)
                {
                    Thread.Sleep(100);
                }
            }

            Assert.Equal(folder, read, ignoreCase: true);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            Directory.Delete(folder);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(999_999_999)]
    public void Gives_null_for_a_program_that_cannot_be_read(int pid)
    {
        Assert.Null(ProcessDirectory.TryRead(pid));
    }
}
