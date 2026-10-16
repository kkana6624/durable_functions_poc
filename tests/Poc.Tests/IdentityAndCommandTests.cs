using System.Diagnostics;
using Poc.Core;
using Xunit;

namespace Poc.Tests;

public class IdentityAndCommandTests
{
    [Fact]
    public void IdsAreStableSafeAndDistinguishCase()
    {
        var id = WorkflowIdentity.JobId("Feasibility_001");
        Assert.Equal(id, WorkflowIdentity.JobId("Feasibility_001"));
        Assert.Equal(id, WorkflowIdentity.InstanceId("Feasibility_001"));
        Assert.Matches("^poc-[a-f0-9]{40}$", id);
        Assert.NotEqual(id, WorkflowIdentity.JobId("feasibility_001"));
    }

    [Fact]
    public void FingerprintChangesWhenTaskConfigurationChanges()
    {
        var request = new WorkflowRequest("run");
        var original = WorkflowIdentity.Fingerprint(request, "light");
        Assert.NotEqual(original, WorkflowIdentity.Fingerprint(request with { FailTaskIndex = 1 }, "light"));
        Assert.NotEqual(original, WorkflowIdentity.Fingerprint(request with { HeavyDelaySeconds = 20 }, "light"));
        Assert.NotEqual(original, WorkflowIdentity.Fingerprint(request, "heavy"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void RejectsInvalidCommandDelay(int delay) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsTaskCommand.Build(delay, false));

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 42)]
    public async Task GeneratedCommandRunsThroughCmdAndWindowsPowerShell(bool fail, int exitCode)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows cmd and Windows PowerShell 5.1.");
        var directory = Path.Combine(Path.GetTempPath(), "poc-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var command = WindowsTaskCommand.Build(1, fail);
            Assert.StartsWith("cmd /d /c ", command);
            var start = new ProcessStartInfo("cmd.exe", command[4..])
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(exitCode, process.ExitCode);
            var resultPath = Path.Combine(directory, "result.txt");
            if (fail) Assert.False(File.Exists(resultPath));
            else Assert.Equal("1", (await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            // Only this test's freshly created, absolute temporary directory is removed.
            Directory.Delete(directory, recursive: true);
        }
    }
}
