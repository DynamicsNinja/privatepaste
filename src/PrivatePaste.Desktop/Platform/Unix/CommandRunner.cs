using System.Diagnostics;
using System.Text;

namespace PrivatePaste.Desktop.Platform.Unix;

public readonly record struct CommandResult(int ExitCode, string Stdout);

/// <summary>Runs an external tool. Throws <see cref="System.ComponentModel.Win32Exception"/> when it is not installed.</summary>
public interface ICommandRunner
{
    /// <param name="stdin">Written to the tool's standard input (so secrets never appear on a command line), or null.</param>
    CommandResult Run(string file, string[] args, string? stdin);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public CommandResult Run(string file, string[] args, string? stdin)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();   // drained, never logged: it may echo a secret
        if (stdin is not null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        if (!p.WaitForExit(Timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{file} did not finish in {Timeout.TotalSeconds:0} s.");
        }
        return new CommandResult(p.ExitCode, stdout.GetAwaiter().GetResult());
    }
}
