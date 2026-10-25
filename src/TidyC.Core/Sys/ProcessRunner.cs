using System.Diagnostics;
using System.Text;

namespace TidyC.Core.Sys;

public sealed class ProcessResult
{
    public ProcessResult(int exitCode, string standardOutput, string standardError, bool timedOut)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        TimedOut = timedOut;
    }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public bool TimedOut { get; }

    public bool Succeeded => !TimedOut && ExitCode == 0;

    public override string ToString() =>
        $"exit={ExitCode}, timedOut={TimedOut}, stdout={Truncate(StandardOutput)}, stderr={Truncate(StandardError)}";

    private static string Truncate(string value) =>
        value.Length <= 400 ? value.Trim() : value.Trim().Substring(0, 400) + "…";
}

/// <summary>Seam for <c>powercfg</c> / <c>DISM</c> / <c>vssadmin</c> so tests never shell out.</summary>
public interface IProcessRunner
{
    ProcessResult Run(string fileName, string arguments, TimeSpan? timeout = null);
}

public sealed class ShellProcessRunner : IProcessRunner
{
    public static readonly ShellProcessRunner Instance = new();

    public ProcessResult Run(string fileName, string arguments, TimeSpan? timeout = null)
    {
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromMinutes(30);

        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var stdoutDone = new AutoResetEvent(false);
        using var stderrDone = new AutoResetEvent(false);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutDone.Set();
            }
            else
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrDone.Set();
            }
            else
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, "进程未能启动", timedOut: false);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            bool exited = process.WaitForExit((int)Math.Min(int.MaxValue, effectiveTimeout.TotalMilliseconds));
            if (!exited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                WaitHandle.WaitAll(new[] { stdoutDone, stderrDone }, TimeSpan.FromSeconds(2));
                return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), timedOut: true);
            }

            WaitHandle.WaitAll(new[] { stdoutDone, stderrDone }, TimeSpan.FromSeconds(5));
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), timedOut: false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, stdout.ToString(), ex.Message, timedOut: false);
        }
    }
}
