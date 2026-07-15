using System.Diagnostics;
using System.Text;

namespace CcusageDesktop.Services;

/// <summary>Runs the globally-installed `ccusage` CLI and returns its JSON output.</summary>
public static class CcusageCli
{
    public static async Task<string> RunAsync(string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            // ccusage is an npm global shim (ccusage.cmd) — must go through cmd.exe on Windows.
            FileName = "cmd.exe",
            Arguments = $"/d /c ccusage {arguments} --json --no-color",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ccusage process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            var stderr = await stderrTask;
            throw new InvalidOperationException(
                $"ccusage {arguments} failed (exit {process.ExitCode}): {Truncate(stderr, 500)}");
        }

        return stdout;
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
