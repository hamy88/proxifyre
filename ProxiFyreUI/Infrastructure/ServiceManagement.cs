using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ProxiFyre.Configuration;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProxiFyreUI.Infrastructure
{
    public enum ProxiFyreServiceState
    {
        NotInstalled,
        Stopped,
        StartPending,
        Running,
        StopPending,
        Paused,
        PausePending,
        ContinuePending,
        Unknown,
        Error,
        DeletionPending
    }

    public enum ServiceOperationFailureKind
    {
        None,
        General,
        TimedOut,
        DependencyUnavailable,
        StartupFailed
    }

    public enum ServiceStartupObservation
    {
        Pending,
        Running,
        Failed
    }

    public static class ServiceStartupStateClassifier
    {
        public static ServiceStartupObservation Classify(ProxiFyreServiceState state)
        {
            if (state == ProxiFyreServiceState.Running)
                return ServiceStartupObservation.Running;
            return state == ProxiFyreServiceState.StartPending
                ? ServiceStartupObservation.Pending
                : ServiceStartupObservation.Failed;
        }
    }

    public static class ServiceStartupCompletionEvaluator
    {
        public static ServiceOperationResult Evaluate(ServiceStatusInfo finalStatus)
        {
            if (finalStatus != null && finalStatus.IsRunning)
            {
                return ServiceOperationResult.Completed(finalStatus,
                    "ProxiFyre 服务正在运行。", confirmsConfigurationReloaded: true);
            }

            var status = finalStatus ?? new ServiceStatusInfo(ProxiFyreServiceState.Error,
                "无法读取最终服务状态。");
            return ServiceOperationResult.Failed(status,
                "服务已达到“运行中”，但无法确认其最终运行状态。" +
                "请查看最近的引擎日志以了解根本错误。",
                null, null, false, ServiceOperationFailureKind.StartupFailed);
        }
    }

    public static class ServiceStopCompletionEvaluator
    {
        public static ServiceOperationResult Evaluate(ServiceStatusInfo finalStatus)
        {
            var status = finalStatus ?? new ServiceStatusInfo(ProxiFyreServiceState.Error,
                "无法读取最终服务状态。");
            if (status.State == ProxiFyreServiceState.Stopped)
            {
                return ServiceOperationResult.Completed(status,
                    "ProxiFyre 服务已停止。");
            }
            if (status.State == ProxiFyreServiceState.NotInstalled)
            {
                return ServiceOperationResult.Completed(status,
                    "ProxiFyre 服务已不再安装。");
            }

            return ServiceOperationResult.Failed(status,
                "服务已达到“已停止”，但无法确认其最终停止状态。");
        }
    }

    public static class ServiceOperationBudget
    {
        public static TimeSpan Remaining(TimeSpan timeout, TimeSpan elapsed)
        {
            if (timeout <= TimeSpan.Zero || elapsed >= timeout)
                return TimeSpan.Zero;
            return timeout - elapsed;
        }
    }

    public enum ServicePreLaunchDecision
    {
        Allowed,
        TimedOut,
        GuardRejected
    }

    /// <summary>
    /// Makes the final mutation decision against one shared operation clock. The second deadline
    /// check is intentional: registry/SCM identity verification can itself consume the budget.
    /// </summary>
    public static class ServicePreLaunchPolicy
    {
        public static ServicePreLaunchDecision Evaluate(TimeSpan timeout,
            Func<TimeSpan> elapsedProvider, Func<bool> registrationGuard)
        {
            if (elapsedProvider == null)
                throw new ArgumentNullException(nameof(elapsedProvider));
            if (ServiceOperationBudget.Remaining(timeout, elapsedProvider()) <= TimeSpan.Zero)
                return ServicePreLaunchDecision.TimedOut;

            if (registrationGuard != null)
            {
                try
                {
                    if (!registrationGuard())
                        return ServicePreLaunchDecision.GuardRejected;
                }
                catch
                {
                    return ServicePreLaunchDecision.GuardRejected;
                }
            }

            return ServiceOperationBudget.Remaining(timeout, elapsedProvider()) > TimeSpan.Zero
                ? ServicePreLaunchDecision.Allowed
                : ServicePreLaunchDecision.TimedOut;
        }
    }

    public static class ServiceUninstallCompletionEvaluator
    {
        public static ServiceOperationResult Evaluate(ServiceStatusInfo finalStatus,
            string details, int exitCode)
        {
            var status = finalStatus ?? new ServiceStatusInfo(ProxiFyreServiceState.Error,
                "无法读取最终服务状态。");
            if (exitCode != 0)
            {
                return ServiceOperationResult.Failed(status,
                    "服务卸载命令失败。", details, exitCode);
            }

            if (status.State == ProxiFyreServiceState.NotInstalled)
            {
                return ServiceOperationResult.Completed(status,
                    "ProxiFyre 服务已卸载。配置和日志已保留。",
                    details, exitCode);
            }

            if (status.State == ProxiFyreServiceState.DeletionPending)
            {
                return ServiceOperationResult.Completed(status,
                    "ProxiFyre 服务已标记为待删除。Windows 将在打开的" +
                    "服务管理句柄关闭后完成删除。配置和日志已保留。",
                    details, exitCode);
            }

            // DeleteService succeeds before SCM necessarily removes the service record. If a
            // different process still owns an open service handle, a stopped record may remain
            // queryable until that handle closes even though the uninstall command succeeded.
            if (status.State == ProxiFyreServiceState.Stopped)
            {
                return ServiceOperationResult.Completed(status,
                    "卸载命令已成功，但 Windows 仍在完成服务删除。" +
                    "如果已停止的服务仍可见，请关闭其他服务管理工具。" +
                    "配置和日志已保留。", details, exitCode);
            }

            return ServiceOperationResult.Failed(status,
                "服务卸载命令已完成，但 Windows 未确认服务已删除" +
                "或待删除。", details, exitCode);
        }
    }

    public static class ServiceOperationFailureClassifier
    {
        public static ServiceOperationFailureKind Classify(Exception exception)
        {
            var native = exception as Win32Exception ?? exception?.InnerException as Win32Exception;
            return native != null && (native.NativeErrorCode == 1068 || native.NativeErrorCode == 1075)
                ? ServiceOperationFailureKind.DependencyUnavailable
                : ServiceOperationFailureKind.General;
        }

        public static bool IsEngineStartupFailure(ServiceOperationResult result)
        {
            if (result == null)
                return false;
            if (result.FailureKind == ServiceOperationFailureKind.StartupFailed)
                return true;

            // A timeout is an engine-start failure only while SCM still reports the start
            // transition. Restart can also time out while stopping or before Start begins.
            return result.FailureKind == ServiceOperationFailureKind.TimedOut &&
                   result.Status?.State == ProxiFyreServiceState.StartPending;
        }
    }

    public sealed class ServiceStatusInfo
    {
        public ServiceStatusInfo(ProxiFyreServiceState state, string error = null)
        {
            State = state;
            Error = error;
        }

        public ProxiFyreServiceState State { get; }
        public string Error { get; }
        public bool IsInstallationKnown => State != ProxiFyreServiceState.Error &&
                                           State != ProxiFyreServiceState.Unknown;
        public bool IsInstalled => IsInstallationKnown && State != ProxiFyreServiceState.NotInstalled;
        public bool HasConcreteInstalledState => IsInstalled &&
                                                 State != ProxiFyreServiceState.DeletionPending;
        public bool IsRunning => State == ProxiFyreServiceState.Running;

        public override string ToString()
        {
            switch (State)
            {
                case ProxiFyreServiceState.NotInstalled: return "未安装";
                case ProxiFyreServiceState.Stopped: return "已停止";
                case ProxiFyreServiceState.StartPending: return "正在启动";
                case ProxiFyreServiceState.Running: return "运行中";
                case ProxiFyreServiceState.StopPending: return "正在停止";
                case ProxiFyreServiceState.Paused: return "已暂停";
                case ProxiFyreServiceState.PausePending: return "正在暂停";
                case ProxiFyreServiceState.ContinuePending: return "正在继续";
                case ProxiFyreServiceState.DeletionPending: return "待删除";
                case ProxiFyreServiceState.Error: return "错误";
                default: return "未知";
            }
        }
    }

    public sealed class ServiceOperationResult
    {
        private ServiceOperationResult(bool success, ServiceStatusInfo status, string message,
            string details, int? exitCode, bool timedOut, ServiceOperationFailureKind failureKind,
            bool confirmsConfigurationReloaded)
        {
            Success = success;
            Status = status;
            Message = message;
            Details = details;
            ExitCode = exitCode;
            TimedOut = timedOut;
            FailureKind = failureKind;
            ConfirmsConfigurationReloaded = confirmsConfigurationReloaded;
        }

        public bool Success { get; }
        public ServiceStatusInfo Status { get; }
        public string Message { get; }
        public string Details { get; }
        public int? ExitCode { get; }
        public bool TimedOut { get; }
        public ServiceOperationFailureKind FailureKind { get; }
        public bool ConfirmsConfigurationReloaded { get; }

        public static ServiceOperationResult Completed(ServiceStatusInfo status, string message,
            string details = null, int? exitCode = null,
            bool confirmsConfigurationReloaded = false)
        {
            return new ServiceOperationResult(true, status, message, details, exitCode, false,
                ServiceOperationFailureKind.None, confirmsConfigurationReloaded);
        }

        public static ServiceOperationResult Failed(ServiceStatusInfo status, string message,
            string details = null, int? exitCode = null, bool timedOut = false,
            ServiceOperationFailureKind failureKind = ServiceOperationFailureKind.General)
        {
            if (timedOut && failureKind == ServiceOperationFailureKind.General)
                failureKind = ServiceOperationFailureKind.TimedOut;
            return new ServiceOperationResult(false, status, message, details, exitCode, timedOut,
                failureKind, false);
        }
    }

    public interface IProxiFyreServiceController : IDisposable
    {
        Task<ServiceStatusInfo> GetStatusAsync(CancellationToken cancellationToken);
        Task<ServiceOperationResult> StartAsync(string expectedEnginePath,
            Func<bool> preLaunchGuard, TimeSpan timeout, CancellationToken cancellationToken);
        Task<ServiceOperationResult> StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
        Task<ServiceOperationResult> RestartAsync(string expectedEnginePath,
            Func<bool> preLaunchGuard, TimeSpan timeout, CancellationToken cancellationToken);
        Task<ServiceOperationResult> InstallAsync(string enginePath, TimeSpan timeout, CancellationToken cancellationToken);
        Task<ServiceOperationResult> UninstallAsync(string enginePath,
            Func<bool> preLaunchGuard, TimeSpan timeout, CancellationToken cancellationToken);
    }

    public sealed class ProxiFyreServiceController : IProxiFyreServiceController
    {
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private readonly IWindowsPacketFilterProbe _packetFilterProbe;
        private bool _disposed;

        public ProxiFyreServiceController()
            : this(new WindowsPacketFilterProbe())
        {
        }

        public ProxiFyreServiceController(IWindowsPacketFilterProbe packetFilterProbe)
        {
            _packetFilterProbe = packetFilterProbe ??
                                 throw new ArgumentNullException(nameof(packetFilterProbe));
        }

        public Task<ServiceStatusInfo> GetStatusAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return Task.Run(() => GetStatusCore(cancellationToken), cancellationToken);
        }

        public Task<ServiceOperationResult> StartAsync(string expectedEnginePath,
            Func<bool> preLaunchGuard, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ValidateServiceMutationRequest(expectedEnginePath, preLaunchGuard);
            return RunExclusiveAsync(() => StartCore(expectedEnginePath, preLaunchGuard,
                timeout, cancellationToken), cancellationToken);
        }

        public Task<ServiceOperationResult> StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            return RunExclusiveAsync(() => StopCore(timeout, Stopwatch.StartNew(),
                cancellationToken), cancellationToken);
        }

        public Task<ServiceOperationResult> RestartAsync(string expectedEnginePath,
            Func<bool> preLaunchGuard, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ValidateServiceMutationRequest(expectedEnginePath, preLaunchGuard);
            return RunExclusiveAsync(() => RestartCore(expectedEnginePath, preLaunchGuard,
                timeout, cancellationToken), cancellationToken);
        }

        public Task<ServiceOperationResult> InstallAsync(string enginePath, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ValidateEnginePathArgument(enginePath);
            return RunExclusiveAsync(() => InstallCore(enginePath, timeout, cancellationToken), cancellationToken);
        }

        public Task<ServiceOperationResult> UninstallAsync(string enginePath,
            Func<bool> preLaunchGuard, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ValidateEnginePathArgument(enginePath);
            if (preLaunchGuard == null)
                throw new ArgumentNullException(nameof(preLaunchGuard));
            return RunExclusiveAsync(() => UninstallCore(enginePath, preLaunchGuard, timeout,
                cancellationToken), cancellationToken);
        }

        private async Task<ServiceOperationResult> RunExclusiveAsync(Func<ServiceOperationResult> operation, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private ServiceOperationResult StartCore(string expectedEnginePath,
            Func<bool> preLaunchGuard, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var operationClock = Stopwatch.StartNew();
            var status = GetStatusCore(cancellationToken);
            if (!status.IsInstallationKnown)
                return InstallationStateUnavailable(status);
            if (!status.IsInstalled)
                return ServiceOperationResult.Failed(status, "ProxiFyre 服务未安装。");
            if (status.State == ProxiFyreServiceState.Running)
                return ServiceOperationResult.Completed(status, "ProxiFyre 服务已在运行。");

            LifecycleExecutableLease payloadLease;
            try
            {
                payloadLease = AcquireTrustedEnginePayload(expectedEnginePath);
            }
            catch (Exception ex) when (IsPayloadValidationException(ex))
            {
                return FailureFromException("无法启动服务。", ex, cancellationToken);
            }

            using (payloadLease)
            {
                var locationFailure = GetUnprotectedServiceLocationFailure(status,
                    expectedEnginePath);
                if (locationFailure != null)
                    return locationFailure;

                var dependencyFailure = GetPacketFilterDependencyFailure(status);
                if (dependencyFailure != null)
                    return dependencyFailure;

                try
                {
                    return StartVerifiedCore(preLaunchGuard, timeout, operationClock,
                        cancellationToken);
                }
                catch (Exception ex) when (IsServiceException(ex))
                {
                    // Keep the verified payload leased while the final failure status is read.
                    return FailureFromException("无法启动服务。", ex,
                        cancellationToken);
                }
            }
        }

        private ServiceOperationResult StartVerifiedCore(Func<bool> preLaunchGuard,
            TimeSpan timeout, Stopwatch operationClock, CancellationToken cancellationToken)
        {
            var status = GetStatusCore(cancellationToken);
            if (!status.IsInstallationKnown)
                return InstallationStateUnavailable(status);
            if (!status.IsInstalled)
                return ServiceOperationResult.Failed(status, "ProxiFyre 服务未安装。");
            if (status.State == ProxiFyreServiceState.Running)
                return ServiceOperationResult.Completed(status, "ProxiFyre 服务已在运行。");

            using (var controller = new ServiceController(ProxiFyrePaths.ServiceName))
            {
                var launchDecision = ServicePreLaunchPolicy.Evaluate(timeout,
                    () => operationClock.Elapsed, preLaunchGuard);
                if (launchDecision == ServicePreLaunchDecision.TimedOut)
                    return ServiceOperationResult.Failed(status,
                        "启动操作在服务启动前已超时。",
                        null, null, true);
                if (launchDecision == ServicePreLaunchDecision.GuardRejected)
                {
                    return ServiceOperationResult.Failed(status,
                        "已注册的 ProxiFyre 可执行文件在服务启动前已更改。" +
                        "未发送启动请求。");
                }
                controller.Start();
                var remaining = ServiceOperationBudget.Remaining(timeout, operationClock.Elapsed);
                if (remaining <= TimeSpan.Zero)
                {
                    return ServiceOperationResult.Failed(GetStatusCore(cancellationToken),
                        "服务未在超时前进入“运行中”。", null, null, true);
                }
                var startup = WaitForStartup(controller, remaining, cancellationToken);
                if (startup == ServiceStartupWaitResult.TimedOut)
                {
                    var current = GetStatusCore(cancellationToken);
                    return ServiceOperationResult.Failed(current,
                        "服务未在超时前进入“运行中”。", null, null, true);
                }
                if (startup == ServiceStartupWaitResult.Failed)
                {
                    var current = GetStatusCore(cancellationToken);
                    return ServiceOperationResult.Failed(current,
                        "服务在启动过程中停止。请查看最近的引擎日志以了解根本错误。",
                        null, null, false, ServiceOperationFailureKind.StartupFailed);
                }
            }

            return ServiceStartupCompletionEvaluator.Evaluate(GetStatusCore(cancellationToken));
        }

        private ServiceOperationResult StopCore(TimeSpan timeout, Stopwatch operationClock,
            CancellationToken cancellationToken, Func<bool> preStopGuard = null)
        {
            var status = GetStatusCore(cancellationToken);
            if (!status.IsInstallationKnown)
                return InstallationStateUnavailable(status);
            if (!status.IsInstalled)
                return ServiceOperationResult.Failed(status, "ProxiFyre 服务未安装。");
            if (status.State == ProxiFyreServiceState.Stopped)
                return ServiceOperationResult.Completed(status, "ProxiFyre 服务已停止。");

            try
            {
                using (var controller = new ServiceController(ProxiFyrePaths.ServiceName))
                {
                    controller.Refresh();
                    // SCM accepts Stop directly for a paused service. Continuing first creates
                    // a ContinuePending race where a subsequent Stop can be rejected.
                    // Do not send a duplicate control when another caller has already started
                    // the stop transition, or when the service stopped after our first query.
                    if (controller.Status != ServiceControllerStatus.Stopped &&
                        controller.Status != ServiceControllerStatus.StopPending)
                    {
                        var stopDecision = ServicePreLaunchPolicy.Evaluate(timeout,
                            () => operationClock.Elapsed, preStopGuard);
                        if (stopDecision == ServicePreLaunchDecision.TimedOut)
                        {
                            return ServiceOperationResult.Failed(GetStatusCore(cancellationToken),
                                "停止操作在服务停止前已超时。",
                                null, null, true);
                        }
                        if (stopDecision == ServicePreLaunchDecision.GuardRejected)
                        {
                            return ServiceOperationResult.Failed(GetStatusCore(cancellationToken),
                                "已注册的 ProxiFyre 可执行文件在服务停止前已更改。" +
                                "未发送停止请求。");
                        }
                        controller.Stop();
                    }
                    var remaining = ServiceOperationBudget.Remaining(timeout,
                        operationClock.Elapsed);
                    if (remaining <= TimeSpan.Zero ||
                        !WaitForStatus(controller, ServiceControllerStatus.Stopped, remaining,
                            cancellationToken))
                    {
                        var current = GetStatusCore(cancellationToken);
                        return ServiceOperationResult.Failed(current,
                            "服务未在超时前进入“已停止”。", null, null, true);
                    }
                }

                return ServiceStopCompletionEvaluator.Evaluate(GetStatusCore(cancellationToken));
            }
            catch (Exception ex) when (IsServiceException(ex))
            {
                return FailureFromException("无法停止服务。", ex, cancellationToken);
            }
        }

        private ServiceOperationResult RestartCore(string expectedEnginePath,
            Func<bool> preLaunchGuard, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            var status = GetStatusCore(cancellationToken);
            if (!status.IsInstallationKnown)
                return InstallationStateUnavailable(status);
            if (!status.IsInstalled)
                return ServiceOperationResult.Failed(status, "ProxiFyre 服务未安装。");

            LifecycleExecutableLease payloadLease;
            try
            {
                payloadLease = AcquireTrustedEnginePayload(expectedEnginePath);
            }
            catch (Exception ex) when (IsPayloadValidationException(ex))
            {
                return FailureFromException("无法重启服务。", ex,
                    cancellationToken);
            }

            // Acquire before Stop so every executable/configuration file used by the service
            // remains the exact payload that was revalidated for the whole restart attempt.
            using (payloadLease)
            {
                var locationFailure = GetUnprotectedServiceLocationFailure(status,
                    expectedEnginePath);
                if (locationFailure != null)
                    return locationFailure;

                var dependencyFailure = GetPacketFilterDependencyFailure(status);
                if (dependencyFailure != null)
                    return dependencyFailure;

                try
                {
                    if (status.State != ProxiFyreServiceState.Stopped)
                    {
                        var stopBudget = ServiceOperationBudget.Remaining(timeout, started.Elapsed);
                        if (stopBudget <= TimeSpan.Zero)
                        {
                            return ServiceOperationResult.Failed(status,
                                "重启操作在服务停止前已超时。",
                                null, null, true);
                        }
                        var stop = StopCore(timeout, started, cancellationToken, preLaunchGuard);
                        if (!stop.Success)
                            return stop;
                    }

                    var remaining = ServiceOperationBudget.Remaining(timeout, started.Elapsed);
                    if (remaining <= TimeSpan.Zero)
                        return ServiceOperationResult.Failed(GetStatusCore(cancellationToken),
                            "重启操作在服务启动前已超时。", null, null, true);

                    var start = StartVerifiedCore(preLaunchGuard, timeout, started,
                        cancellationToken);
                    return start.Success
                        ? ServiceOperationResult.Completed(start.Status,
                            "ProxiFyre 服务已成功重启。",
                            confirmsConfigurationReloaded: start.ConfirmsConfigurationReloaded)
                        : start;
                }
                catch (Exception ex) when (IsServiceException(ex))
                {
                    // Keep the verified payload leased while the final failure status is read.
                    return FailureFromException("无法重启服务。", ex,
                        cancellationToken);
                }
            }
        }

        private ServiceOperationResult InstallCore(string enginePath, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var operationClock = Stopwatch.StartNew();
            var status = GetStatusCore(cancellationToken);
            if (!status.IsInstallationKnown)
                return InstallationStateUnavailable(status);
            if (status.State == ProxiFyreServiceState.DeletionPending)
            {
                return ServiceOperationResult.Failed(status,
                    "ProxiFyre 服务已标记为待删除。请等待 Windows 完成删除后再安装。");
            }
            if (status.IsInstalled)
            {
                return ServiceOperationResult.Completed(status,
                    "ProxiFyre 服务已安装。");
            }

            LifecycleExecutableLease payloadLease;
            try
            {
                payloadLease = AcquireTrustedEnginePayload(enginePath);
            }
            catch (Exception ex) when (IsPayloadValidationException(ex))
            {
                return FailureFromException("无法安装服务。", ex,
                    cancellationToken);
            }

            using (payloadLease)
            {
                var locationFailure = GetUnprotectedServiceLocationFailure(status, enginePath);
                if (locationFailure != null)
                    return locationFailure;

                var dependencyFailure = GetPacketFilterDependencyFailure(status);
                return dependencyFailure ??
                       RunLifecycleCommandCore(enginePath, "install", timeout, operationClock,
                           cancellationToken, payloadAlreadyLeased: true);
            }
        }

        private ServiceOperationResult UninstallCore(string enginePath, Func<bool> preLaunchGuard,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            var status = GetStatusCore(cancellationToken);
            if (!status.IsInstallationKnown)
                return InstallationStateUnavailable(status);
            if (status.State == ProxiFyreServiceState.NotInstalled)
            {
                return ServiceOperationResult.Completed(status,
                    "ProxiFyre 服务已卸载。配置和日志已保留。");
            }
            if (status.State == ProxiFyreServiceState.DeletionPending)
            {
                return ServiceOperationResult.Completed(status,
                    "ProxiFyre 服务已标记为待删除。Windows 将在打开的" +
                    "服务管理句柄关闭后完成删除。");
            }

            LifecycleExecutableLease payloadLease;
            try
            {
                payloadLease = AcquireTrustedEnginePayload(enginePath);
            }
            catch (Exception ex) when (IsPayloadValidationException(ex))
            {
                return FailureFromException("无法运行服务卸载命令。", ex,
                    cancellationToken);
            }

            using (payloadLease)
            {
                var locationFailure = GetUnprotectedServiceLocationFailure(status, enginePath,
                    isUninstall: true);
                if (locationFailure != null)
                    return locationFailure;

                if (ServiceOperationBudget.Remaining(timeout, started.Elapsed) <= TimeSpan.Zero)
                    return LifecycleCommandTimedOut("uninstall", cancellationToken);

                if (status.State != ProxiFyreServiceState.Stopped)
                {
                    var stopBudget = ServiceOperationBudget.Remaining(timeout, started.Elapsed);
                    if (stopBudget <= TimeSpan.Zero)
                    {
                        return ServiceOperationResult.Failed(status,
                            "卸载操作在服务停止前已超时。",
                            null, null, true);
                    }

                    var stop = StopCore(timeout, started, cancellationToken, preLaunchGuard);
                    if (!stop.Success)
                        return stop;

                    status = stop.Status ?? GetStatusCore(cancellationToken);
                    if (status.State == ProxiFyreServiceState.NotInstalled ||
                        status.State == ProxiFyreServiceState.DeletionPending)
                    {
                        return ServiceOperationResult.Completed(status,
                            status.State == ProxiFyreServiceState.NotInstalled
                                ? "ProxiFyre 服务已卸载。配置和日志已保留。"
                                : "ProxiFyre 服务已标记为待删除。Windows 将在打开的" +
                                  "服务管理句柄关闭后完成删除。");
                    }
                    if (status.State != ProxiFyreServiceState.Stopped)
                    {
                        return ServiceOperationResult.Failed(status,
                            "服务未能保持停止状态，因此未运行卸载命令。");
                    }
                }

                if (ServiceOperationBudget.Remaining(timeout, started.Elapsed) <= TimeSpan.Zero)
                {
                    return ServiceOperationResult.Failed(status,
                        "卸载操作在卸载命令运行前已超时。",
                        null, null, true);
                }

                return RunLifecycleCommandCore(enginePath, "uninstall", timeout, started,
                    cancellationToken, preLaunchGuard, true);
            }
        }

        private ServiceOperationResult GetPacketFilterDependencyFailure(ServiceStatusInfo serviceStatus)
        {
            var dependency = _packetFilterProbe.GetStatus();
            if (dependency.IsAvailable)
                return null;

            return ServiceOperationResult.Failed(serviceStatus, dependency.StartupMessage,
                dependency.Details, null, false, ServiceOperationFailureKind.DependencyUnavailable);
        }

        private ServiceOperationResult RunLifecycleCommandCore(string enginePath, string command,
            TimeSpan timeout, Stopwatch operationClock, CancellationToken cancellationToken,
            Func<bool> preLaunchGuard = null, bool payloadAlreadyLeased = false)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ServiceOperationBudget.Remaining(timeout, operationClock.Elapsed) <=
                    TimeSpan.Zero)
                    return LifecycleCommandTimedOut(command, cancellationToken);

                var startInfo = new ProcessStartInfo
                {
                    FileName = enginePath,
                    Arguments = command,
                    WorkingDirectory = Path.GetDirectoryName(enginePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var executableLease = payloadAlreadyLeased
                           ? null
                           : AcquireTrustedEnginePayload(enginePath))
                using (var process = new Process { StartInfo = startInfo })
                {
                    process.OutputDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                    process.ErrorDataReceived += (sender, args) => { if (args.Data != null) lock (error) error.AppendLine(args.Data); };
                    var launchDecision = ServicePreLaunchPolicy.Evaluate(timeout,
                        () => operationClock.Elapsed, preLaunchGuard);
                    if (launchDecision == ServicePreLaunchDecision.TimedOut)
                        return LifecycleCommandTimedOut(command, cancellationToken);
                    if (launchDecision == ServicePreLaunchDecision.GuardRejected)
                    {
                        return ServiceOperationResult.Failed(GetStatusCore(cancellationToken),
                            "服务注册在卸载命令运行前已更改。" +
                            "未启动卸载命令。");
                    }
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    while (true)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            TerminateLifecycleProcess(process);
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        var remaining = ServiceOperationBudget.Remaining(timeout,
                            operationClock.Elapsed);
                        if (remaining > TimeSpan.Zero)
                        {
                            var waitMilliseconds = Math.Max(1,
                                Math.Min(150, (int)Math.Ceiling(remaining.TotalMilliseconds)));
                            if (process.WaitForExit(waitMilliseconds))
                                break;
                            continue;
                        }

                        TerminateLifecycleProcess(process);
                        var timedOutStatus = GetStatusCore(CancellationToken.None);
                        return ServiceOperationResult.Failed(timedOutStatus,
                            $"服务 {command} 命令已超时。", CombineOutput(output, error), null, true);
                    }

                    process.WaitForExit();
                    var exitCode = process.ExitCode;
                    var status = GetStatusCore(cancellationToken);
                    var details = CombineOutput(output, error);
                    if (command == "uninstall")
                        return ServiceUninstallCompletionEvaluator.Evaluate(status, details, exitCode);

                    // Never treat Error/Unknown as proof that a lifecycle command worked. The
                    // UI remains conservative on those states, while install success requires
                    // an actual installed state from SCM.
                    if (exitCode == 0 && status.HasConcreteInstalledState)
                    {
                        return ServiceOperationResult.Completed(status,
                            "ProxiFyre 服务已安装。", details, exitCode);
                    }

                    return ServiceOperationResult.Failed(status,
                        $"服务 {command} 命令失败。", details, exitCode);
                }
            }
            catch (Exception ex) when (IsServiceException(ex) || IsPayloadValidationException(ex))
            {
                return FailureFromException($"无法运行服务 {command} 命令。", ex, cancellationToken);
            }
        }

        private ServiceOperationResult LifecycleCommandTimedOut(string command,
            CancellationToken cancellationToken)
        {
            return ServiceOperationResult.Failed(GetStatusCore(cancellationToken),
                $"服务 {command} 命令在启动前已超时。",
                null, null, true);
        }

        private static void TerminateLifecycleProcess(Process process)
        {
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }

            try
            {
                if (process.WaitForExit(5000))
                    process.WaitForExit(); // Drain asynchronous stdout/stderr callbacks.
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static string CombineOutput(StringBuilder output, StringBuilder error)
        {
            lock (output)
            lock (error)
            {
                return (output.ToString() + error.ToString()).Trim();
            }
        }

        private static ServiceOperationResult InstallationStateUnavailable(ServiceStatusInfo status)
        {
            return ServiceOperationResult.Failed(status,
                "无法确认 ProxiFyre 服务的安装状态。");
        }

        private ServiceOperationResult FailureFromException(string message, Exception exception, CancellationToken cancellationToken)
        {
            ServiceStatusInfo status;
            try { status = GetStatusCore(cancellationToken); }
            catch { status = new ServiceStatusInfo(ProxiFyreServiceState.Error, exception.Message); }

            return ServiceOperationResult.Failed(status, FriendlyServiceError(message, exception),
                exception.ToString(), null, false,
                ServiceOperationFailureClassifier.Classify(exception));
        }

        private static string FriendlyServiceError(string prefix, Exception exception)
        {
            var native = exception as Win32Exception ?? exception.InnerException as Win32Exception;
            if (native != null)
            {
                if (native.NativeErrorCode == 5)
                    return prefix + " 访问被拒绝；请以管理员身份运行 ProxiFyreUI。";
                if (native.NativeErrorCode == 1072)
                    return prefix + " 服务已标记为待删除；请关闭服务管理工具后重试。";
                if (native.NativeErrorCode == 2)
                    return prefix + " 找不到已注册的服务可执行文件。";
                if (native.NativeErrorCode == 1068 || native.NativeErrorCode == 1075)
                    return prefix + " Windows Packet Filter (NDISRD) 依赖不可用。" +
                           "请从 " + WindowsPacketFilterStatus.DownloadUrl +
                           " 安装 WinpkFilter，如提示请重启 Windows，然后重试。";
            }

            return prefix + " " + exception.Message;
        }

        private static ServiceStatusInfo GetStatusCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using (var controller = new ServiceController(ProxiFyrePaths.ServiceName))
                {
                    var status = controller.Status;
                    return new ServiceStatusInfo(MapStatus(status));
                }
            }
            catch (Exception ex) when (IsMissingService(ex))
            {
                return new ServiceStatusInfo(ProxiFyreServiceState.NotInstalled);
            }
            catch (Exception ex) when (IsMarkedForDeletion(ex))
            {
                return new ServiceStatusInfo(ProxiFyreServiceState.DeletionPending);
            }
            catch (Exception ex) when (IsServiceException(ex))
            {
                return new ServiceStatusInfo(ProxiFyreServiceState.Error, ex.Message);
            }
        }

        private static bool WaitForStatus(ServiceController controller, ServiceControllerStatus target,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                controller.Refresh();
                if (controller.Status == target)
                    return true;
                Thread.Sleep(150);
            }

            controller.Refresh();
            return controller.Status == target;
        }

        private enum ServiceStartupWaitResult
        {
            Running,
            Failed,
            TimedOut
        }

        private static ServiceStartupWaitResult WaitForStartup(ServiceController controller,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                controller.Refresh();
                var observation = ServiceStartupStateClassifier.Classify(MapStatus(controller.Status));
                if (observation == ServiceStartupObservation.Running)
                    return ServiceStartupWaitResult.Running;
                if (observation == ServiceStartupObservation.Failed)
                    return ServiceStartupWaitResult.Failed;
                Thread.Sleep(150);
            }

            controller.Refresh();
            var finalObservation = ServiceStartupStateClassifier.Classify(MapStatus(controller.Status));
            if (finalObservation == ServiceStartupObservation.Running)
                return ServiceStartupWaitResult.Running;
            return finalObservation == ServiceStartupObservation.Failed
                ? ServiceStartupWaitResult.Failed
                : ServiceStartupWaitResult.TimedOut;
        }

        private static ProxiFyreServiceState MapStatus(ServiceControllerStatus status)
        {
            switch (status)
            {
                case ServiceControllerStatus.Stopped: return ProxiFyreServiceState.Stopped;
                case ServiceControllerStatus.StartPending: return ProxiFyreServiceState.StartPending;
                case ServiceControllerStatus.StopPending: return ProxiFyreServiceState.StopPending;
                case ServiceControllerStatus.Running: return ProxiFyreServiceState.Running;
                case ServiceControllerStatus.Paused: return ProxiFyreServiceState.Paused;
                case ServiceControllerStatus.PausePending: return ProxiFyreServiceState.PausePending;
                case ServiceControllerStatus.ContinuePending: return ProxiFyreServiceState.ContinuePending;
                default: return ProxiFyreServiceState.Unknown;
            }
        }

        private static bool IsMissingService(Exception exception)
        {
            var native = exception as Win32Exception ?? exception.InnerException as Win32Exception;
            return native != null && native.NativeErrorCode == 1060;
        }

        private static bool IsMarkedForDeletion(Exception exception)
        {
            var native = exception as Win32Exception ?? exception.InnerException as Win32Exception;
            return native != null && native.NativeErrorCode == 1072;
        }

        private static bool IsServiceException(Exception exception)
        {
            return exception is InvalidOperationException || exception is Win32Exception ||
                   exception is System.ServiceProcess.TimeoutException || exception is UnauthorizedAccessException;
        }

        private static bool IsPayloadValidationException(Exception exception)
        {
            return exception is IOException || exception is UnauthorizedAccessException ||
                   exception is ArgumentException || exception is NotSupportedException ||
                   exception is Win32Exception;
        }

        private static LifecycleExecutableLease AcquireTrustedEnginePayload(string enginePath)
        {
            return LifecycleExecutableLease.Acquire(
                EngineExecutableValidator.GetLifecyclePayloadPaths(enginePath),
                EngineExecutableValidator.IsTrustedPayloadFile);
        }

        private static ServiceOperationResult GetUnprotectedServiceLocationFailure(
            ServiceStatusInfo status, string enginePath, bool isUninstall = false)
        {
#if DEBUG
            // Repository builds intentionally run from a developer-writable output directory.
            return null;
#else
            string reason;
            if (ServiceInstallLocationPolicy.IsProtected(enginePath, out reason))
                return null;

            var remediation = isUninstall
                ? "对于 MSI 安装，请从 Windows 已安装应用中移除 ProxiFyre。" +
                  "对于手动服务，请将官方完整版本放入受管理员保护的" +
                  "固定卷目录，然后重试卸载。"
                : status != null && status.IsInstalled
                ? "请停止并卸载现有服务，退出此 GUI，然后使用官方安装程序将完整版本复制或解压到" +
                  "受保护的每计算机目录（通常位于 Program Files 下）。从该副本启动 GUI，" +
                  "并重新安装服务。"
                : "请使用官方安装程序将完整版本安装到受保护的每计算机目录" +
                  "（通常位于 Program Files 下），从该副本启动 GUI，" +
                  "并安装服务。GUI 不会就地改写用户控制目录的权限。";
            return ServiceOperationResult.Failed(status,
                "特权 ProxiFyre 服务操作无法从标准用户可写的" +
                "位置执行代码。" + remediation, reason);
#endif
        }

        private static void ValidateServiceMutationRequest(string expectedEnginePath,
            Func<bool> preLaunchGuard)
        {
            ValidateEnginePathArgument(expectedEnginePath);
            if (preLaunchGuard == null)
                throw new ArgumentNullException(nameof(preLaunchGuard));
        }

        private static void ValidateEnginePathArgument(string enginePath)
        {
            if (string.IsNullOrWhiteSpace(enginePath) ||
                !ProxiFyrePaths.IsEngineExecutable(enginePath) ||
                !IsFullyQualifiedWindowsPath(enginePath))
            {
                throw new ArgumentException(
                    "需要完整的 ProxiFyre.exe 路径。", nameof(enginePath));
            }

            // Force the same canonicalization checks used by the payload lease without requiring
            // an SCM ImagePath representation. A direct executable path may legitimately contain
            // spaces without surrounding quotes.
            Path.GetFullPath(enginePath);
        }

        private static bool IsFullyQualifiedWindowsPath(string path)
        {
            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == Path.DirectorySeparatorChar ||
                 path[2] == Path.AltDirectorySeparatorChar))
                return true;

            return path.Length >= 3 &&
                   (path[0] == Path.DirectorySeparatorChar ||
                    path[0] == Path.AltDirectorySeparatorChar) &&
                   (path[1] == Path.DirectorySeparatorChar ||
                    path[1] == Path.AltDirectorySeparatorChar) &&
                   path[2] != Path.DirectorySeparatorChar &&
                   path[2] != Path.AltDirectorySeparatorChar;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ProxiFyreServiceController));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            // The application can close while a background SCM wait is unwinding. Do not
            // dispose the semaphore here: RunExclusiveAsync must still be able to Release it.
            // It has no native resource until its WaitHandle is requested, which this class
            // never does, and the process is exiting after this lifetime-scoped controller.
        }
    }
}
