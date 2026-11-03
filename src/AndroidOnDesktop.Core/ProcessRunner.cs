using System.Diagnostics;
using System.Text;

namespace AndroidOnDesktop.Core;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => (StandardOutput + Environment.NewLine + StandardError).Trim();
}

internal static class ProcessRunner
{
    internal static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("运行组件不存在，请先在设置中指定正确的程序路径。", executable);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    internal static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default, bool requireSuccess = true,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments) };
        if (environment is not null)
            foreach (var pair in environment) process.StartInfo.Environment[pair.Key] = pair.Value;
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"运行组件等待超时（{timeout.TotalSeconds:0} 秒）：{Path.GetFileName(executable)}");
        }
        var result = new ProcessResult(process.ExitCode, await stdout, await stderr);
        if (requireSuccess && result.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} 执行失败（{result.ExitCode}）：{Environment.NewLine}{result.Output}");
        return result;
    }

    internal static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
