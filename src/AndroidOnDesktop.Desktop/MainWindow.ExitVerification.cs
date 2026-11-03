using AndroidOnDesktop.Core;

namespace AndroidOnDesktop.Desktop;

public partial class MainWindow
{
    internal async Task<ExitProbeResult> VerifyExitForSmokeAsync(string mode, CancellationToken cancellationToken)
    {
        if (mode is not ("existing" or "start" or "cancel-start"))
            throw new ArgumentException("退出检查模式应为 existing、start 或 cancel-start。", nameof(mode));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => closed.TrySetResult();
        Task? start = null;
        var applicationReady = false;
        if (mode == "existing")
            await ConnectOnStartupAsync();
        else
        {
            start = RunAsync("启动 Android", StartAndroidAsync, save: false, cancellationToken: cancellationToken);
            if (mode == "cancel-start")
            {
                while (!_runtime.OwnsEmulator)
                {
                    if (start.IsCompleted) throw new InvalidOperationException("未启动可管理的 Android：" + OperationText.Text);
                    await Task.Delay(50, cancellationToken);
                }
            }
            else await start;
        }
        if (!_runtime.OwnsEmulator) throw new InvalidOperationException("退出检查未获得本项目 Android 的生命周期管理权。");
        if (mode != "cancel-start")
        {
            await VerifyLaunchForSmokeAsync("org.androidondesktop.smoke", cancellationToken);
            applicationReady = LastSessionUpdate?.State == AppSessionState.Running;
            if (!applicationReady) throw new InvalidOperationException("样例应用未进入运行状态：" + OperationText.Text);
        }
        var managedBeforeClose = _runtime.OwnsEmulator;
        var operationPendingAtClose = _operation is not null;
        Close();
        // Repeated close requests must not bypass the asynchronous cleanup.
        Close();
        await closed.Task.WaitAsync(cancellationToken);
        if (start is not null) await start.WaitAsync(cancellationToken);
        return new(mode, managedBeforeClose, applicationReady, operationPendingAtClose, _allowClose, !_runtime.OwnsEmulator);
    }
}

internal sealed record ExitProbeResult(string Mode, bool ManagedBeforeClose, bool ApplicationReady,
    bool OperationPendingAtClose, bool CleanupCompletedBeforeClose, bool ManagementReleased);
