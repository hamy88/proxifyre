using ProxiFyre.Configuration;
using ProxiFyreUI.Infrastructure;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ProxiFyreUI.Forms
{
    internal partial class MainForm : Form
    {
        private static readonly string[] EngineLogLevelConfigValues = { "Error", "Warning", "Info", "Debug", "All" };
        private readonly UiSettingsStore _settingsStore;
        private readonly IEngineLocator _engineLocator;
        private readonly IProxiFyreServiceController _serviceController;
        private readonly ConfigurationWorkspace _workspace;
        private readonly ILogTailer _logTailer;
        private readonly DiagnosticsBuilder _diagnosticsBuilder;
        private readonly UserVisibleErrorFormatter _errorFormatter;
        private readonly ConfigurationApplyCoordinator _applyCoordinator;
        private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();
        private readonly Queue<string> _logLines = new Queue<string>();
        private readonly Icon _applicationIcon;
        private readonly string _baseWindowTitle;
        private UiSettings _settings;
        private EngineLocation _engineLocation;
        private ServiceStatusInfo _serviceStatus = new ServiceStatusInfo(ProxiFyreServiceState.Unknown);
        private bool _suppressChanges;
        private bool _operationInProgress;
        private bool _applyWorkflowInProgress;
        private bool _engineStartupFailed;
        private bool _packetFilterUnavailable;
        private bool _exiting;
        private bool _exitRequested;
        private bool _exitCloseScheduled;
        private bool _cleanedUp;
        private int _operationDepth;
        private int _configurationDialogDepth;
        private int _refreshingStatus;
        private int _logViewGeneration;

        public MainForm()
            : this(new UiSettingsStore(), new EngineLocator(), new ProxiFyreServiceController(),
                  new ConfigurationWorkspace(), new LogTailer(), new DiagnosticsBuilder())
        {
        }

        internal MainForm(UiSettingsStore settingsStore, IEngineLocator engineLocator,
            IProxiFyreServiceController serviceController, ConfigurationWorkspace workspace,
            ILogTailer logTailer, DiagnosticsBuilder diagnosticsBuilder)
        {
            _settingsStore = settingsStore;
            _engineLocator = engineLocator;
            _serviceController = serviceController;
            _workspace = workspace;
            _logTailer = logTailer;
            _diagnosticsBuilder = diagnosticsBuilder;
            _errorFormatter = new UserVisibleErrorFormatter(_diagnosticsBuilder);
            _applyCoordinator = new ConfigurationApplyCoordinator(_workspace, _serviceController,
                IsRegisteredServiceEngine);
            _settings = _settingsStore.Load();

            InitializeComponent();
            _workspace.DirtyStateChanged += Workspace_DirtyStateChanged;
            _logTailer.LinesRead += LogTailer_LinesRead;
            _logTailer.StatusChanged += LogTailer_StatusChanged;
            followLogsCheckBox.Checked = _settings.FollowLogs;
            minimizeToTrayCheckBox.Checked = _settings.MinimizeToTray;
            var guiVersion = GetFileVersion(Assembly.GetExecutingAssembly().Location);
            guiVersionValueLabel.Text = guiVersion;
            _baseWindowTitle = string.Equals(guiVersion, "不可用", StringComparison.Ordinal)
                ? "ProxiFyre"
                : "ProxiFyre v" + guiVersion;
            Text = _baseWindowTitle;
            architectureValueLabel.Text = GetArchitectureDescription();
            _applicationIcon = LoadApplicationIcon();
            Icon = _applicationIcon;
            notifyIcon.Icon = _applicationIcon;
            serviceRefreshTimer.Start();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            _engineLocation = _engineLocator.Resolve(_settings);
            UpdateResolvedPaths();
            if (_engineLocation.IsResolved)
            {
                try
                {
                    LoadWorkspace();
                }
                catch (Exception ex)
                {
                    ShowError("无法加载配置。", ex);
                }
            }
            else
            {
                configurationStateValueLabel.Text = "未解析引擎";
                changesStateValueLabel.Text = "请选择 ProxiFyre.exe";
            }

            await RefreshServiceStatusAsync();
            UpdateAllStates();
        }

        private void LoadWorkspace()
        {
            var previousEnginePath = _workspace.EnginePath;
            var loaded = _workspace.Load(_engineLocation);
            CompleteWorkspaceLoad(loaded, previousEnginePath);
        }

        private void CompleteWorkspaceLoad(WorkspaceLoadResult loaded, string previousEnginePath)
        {
            BindConfiguration();
            if (!PathsEqual(previousEnginePath, _workspace.EnginePath))
            {
                ResetLogViewForEngineChange();
                StartLogTailerIfNeeded();
            }
            else if (followLogsCheckBox.Checked && !_logTailer.IsRunning)
            {
                StartLogTailerIfNeeded();
            }
            if (!loaded.LiveFileExists)
            {
                var source = loaded.LoadedFromSample
                    ? "活动 app-config.json 缺失。已将示例加载为未保存的工作配置。"
                    : "活动 app-config.json 缺失。已创建空白的未保存配置。";
                MessageBox.Show(this, source + "\r\n\r\n请至少添加一条有效的代理规则，然后在启动服务前保存。",
                    "配置尚未保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void BindConfiguration()
        {
            var configuration = _workspace.Configuration;
            if (configuration == null)
                return;

            _suppressChanges = true;
            try
            {
                var logLevelIndex = Array.FindIndex(EngineLogLevelConfigValues,
                    value => string.Equals(value, configuration.LogLevel,
                        StringComparison.OrdinalIgnoreCase));
                logLevelComboBox.SelectedIndex = logLevelIndex >= 0 ? logLevelIndex : 2;
                bypassLanCheckBox.Checked = configuration.BypassLan;
                RefreshRoutingGrid();
                RefreshExclusionsList();
            }
            finally
            {
                _suppressChanges = false;
            }
            UpdateAllStates();
        }

        private void ReadRootControlsIntoConfiguration()
        {
            if (_workspace.Configuration == null)
                return;
            var logLevelIndex = logLevelComboBox.SelectedIndex;
            _workspace.Configuration.LogLevel = logLevelIndex >= 0 &&
                                               logLevelIndex < EngineLogLevelConfigValues.Length
                ? EngineLogLevelConfigValues[logLevelIndex]
                : "Info";
            _workspace.Configuration.BypassLan = bypassLanCheckBox.Checked;
        }

        private void RefreshRoutingGrid(int selectIndex = -1)
        {
            routingGrid.Rows.Clear();
            var configuration = _workspace.Configuration;
            if (configuration?.Proxies == null)
                return;

            var validation = _workspace.Validate();
            for (var index = 0; index < configuration.Proxies.Count; index++)
            {
                var rule = configuration.Proxies[index];
                var warnings = validation.Issues
                    .Where(issue => issue.ProxyRuleIndex == index)
                    .Select(issue => issue.Message)
                    .Distinct()
                    .ToArray();
                var rowIndex = routingGrid.Rows.Add(
                    index + 1,
                    FormatApplications(rule?.AppNames),
                    rule?.Socks5ProxyEndpoint ?? string.Empty,
                    FormatAuthentication(rule),
                    FormatTransport(rule?.Socks5Transport),
                    FormatValues(rule?.SupportedProtocols, "TCP、UDP（默认）"),
                    FormatValues(rule?.SupportedAddressFamilies, "IPv4、IPv6（默认）"),
                    string.Join("; ", warnings));
                routingGrid.Rows[rowIndex].Tag = index;
                if (warnings.Length > 0)
                    routingGrid.Rows[rowIndex].DefaultCellStyle.BackColor = Color.FromArgb(255, 249, 222);
            }

            if (selectIndex >= 0 && selectIndex < routingGrid.Rows.Count)
            {
                routingGrid.ClearSelection();
                routingGrid.Rows[selectIndex].Selected = true;
                routingGrid.CurrentCell = routingGrid.Rows[selectIndex].Cells[0];
            }
            UpdateRoutingButtons();
        }

        private void RefreshExclusionsList(int selectIndex = -1)
        {
            exclusionsListBox.Items.Clear();
            var exclusions = _workspace.Configuration?.Excludes;
            if (exclusions == null)
                return;
            foreach (var exclusion in exclusions)
                exclusionsListBox.Items.Add(exclusion ?? "<空条目>");
            if (selectIndex >= 0 && selectIndex < exclusionsListBox.Items.Count)
                exclusionsListBox.SelectedIndex = selectIndex;
            UpdateExclusionButtons();
        }

        private void MarkConfigurationChanged()
        {
            // Busy-state checks belong before a mutation. Once the model has changed it must
            // always be marked dirty, including when a modal dialog's nested message loop let
            // a service operation begin between the initial guard and the commit.
            if (_suppressChanges)
                return;
            _workspace.MarkDirty();
            UpdateAllStates();
        }

        private bool CanCommitDialogMutation(ProxiFyreConfiguration configuration)
        {
            if (!ConfigurationMutationBlocked && ReferenceEquals(_workspace.Configuration, configuration))
                return true;

            statusStripLabel.Text =
                "由于对话框打开期间发生了服务或配置操作，本次编辑未被应用。";
            return false;
        }

        private DialogResult ShowConfigurationDialog(Form dialog)
        {
            return ShowConfigurationDialog(() => dialog.ShowDialog(this));
        }

        private DialogResult ShowConfigurationDialog(CommonDialog dialog)
        {
            return ShowConfigurationDialog(() => dialog.ShowDialog(this));
        }

        private DialogResult ShowConfigurationDialog(Func<DialogResult> showDialog)
        {
            _configurationDialogDepth++;
            UpdateAllStates();
            try
            {
                return showDialog();
            }
            finally
            {
                _configurationDialogDepth--;
                UpdateAllStates();
                ScheduleDeferredExitIfReady();
            }
        }

        private void Workspace_DirtyStateChanged(object sender, EventArgs e)
        {
            UpdateAllStates();
        }

        private void RootConfigurationControlChanged(object sender, EventArgs e)
        {
            if (_suppressChanges || ConfigurationMutationBlocked || _workspace.Configuration == null)
                return;
            ReadRootControlsIntoConfiguration();
            MarkConfigurationChanged();
        }

        private int SelectedRuleIndex
        {
            get
            {
                if (routingGrid.SelectedRows.Count == 0)
                    return -1;
                return routingGrid.SelectedRows[0].Tag is int index ? index : -1;
            }
        }

        private void AddRuleButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked || !EnsureWorkspaceAvailable())
                return;
            var configuration = _workspace.Configuration;
            var count = configuration.Proxies.Count;
            using (var editor = new ProxyRuleEditorForm(null, count, count + 1))
            {
                editor.Text = "添加代理规则";
                if (ShowConfigurationDialog(editor) != DialogResult.OK)
                    return;
                if (!CanCommitDialogMutation(configuration))
                    return;
                configuration.Proxies.Add(editor.ResultRule);
                MarkConfigurationChanged();
                RefreshRoutingGrid(configuration.Proxies.Count - 1);
            }
        }

        private void EditRuleButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked)
                return;
            var index = SelectedRuleIndex;
            if (index < 0 || _workspace.Configuration?.Proxies == null)
                return;
            var configuration = _workspace.Configuration;
            using (var editor = new ProxyRuleEditorForm(configuration.Proxies[index], index,
                configuration.Proxies.Count))
            {
                editor.Text = "编辑代理规则";
                if (ShowConfigurationDialog(editor) != DialogResult.OK)
                    return;
                if (!CanCommitDialogMutation(configuration))
                    return;
                configuration.Proxies[index] = editor.ResultRule;
                MarkConfigurationChanged();
                RefreshRoutingGrid(index);
            }
        }

        private void DuplicateRuleButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked)
                return;
            var index = SelectedRuleIndex;
            if (index < 0 || _workspace.Configuration?.Proxies == null)
                return;
            var clone = new ConfigurationSerializer().CloneRule(
                _workspace.Configuration.Proxies[index]);
            _workspace.Configuration.Proxies.Insert(index + 1, clone);
            MarkConfigurationChanged();
            RefreshRoutingGrid(index + 1);
        }

        private void RemoveRuleButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked)
                return;
            var index = SelectedRuleIndex;
            if (index < 0 || _workspace.Configuration?.Proxies == null)
                return;
            var configuration = _workspace.Configuration;
            if (MessageBox.Show(this, "移除所选代理规则？", "移除代理规则",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            if (!CanCommitDialogMutation(configuration))
                return;
            configuration.Proxies.RemoveAt(index);
            MarkConfigurationChanged();
            RefreshRoutingGrid(Math.Min(index, configuration.Proxies.Count - 1));
        }

        private void MoveRule(int direction)
        {
            if (ConfigurationMutationBlocked)
                return;
            var index = SelectedRuleIndex;
            var target = index + direction;
            var rules = _workspace.Configuration?.Proxies;
            if (rules == null || index < 0 || target < 0 || target >= rules.Count)
                return;
            var rule = rules[index];
            rules.RemoveAt(index);
            rules.Insert(target, rule);
            MarkConfigurationChanged();
            RefreshRoutingGrid(target);
        }

        private void MoveUpRuleButton_Click(object sender, EventArgs e) => MoveRule(-1);
        private void MoveDownRuleButton_Click(object sender, EventArgs e) => MoveRule(1);

        private void RoutingGrid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (!ConfigurationMutationBlocked && e.RowIndex >= 0)
                EditRuleButton_Click(sender, EventArgs.Empty);
        }

        private void RoutingGrid_SelectionChanged(object sender, EventArgs e)
        {
            UpdateRoutingButtons();
        }

        private void RoutingGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (ConfigurationMutationBlocked)
            {
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Delete)
            {
                RemoveRuleButton_Click(sender, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Up)
            {
                MoveRule(-1);
                e.Handled = true;
            }
            else if (e.Alt && e.KeyCode == Keys.Down)
            {
                MoveRule(1);
                e.Handled = true;
            }
        }

        private void UpdateRoutingButtons()
        {
            var index = SelectedRuleIndex;
            var count = _workspace.Configuration?.Proxies?.Count ?? 0;
            var canEdit = !ConfigurationMutationBlocked;
            editRuleButton.Enabled = canEdit && index >= 0;
            duplicateRuleButton.Enabled = canEdit && index >= 0;
            removeRuleButton.Enabled = canEdit && index >= 0;
            moveUpRuleButton.Enabled = canEdit && index > 0;
            moveDownRuleButton.Enabled = canEdit && index >= 0 && index < count - 1;
            addRuleButton.Enabled = canEdit && _workspace.Configuration != null;
            routingGrid.Enabled = canEdit;
        }

        private void AddRunningExclusionButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked || !EnsureWorkspaceAvailable())
                return;
            var configuration = _workspace.Configuration;
            using (var picker = new ProcessPickerForm())
            {
                if (ShowConfigurationDialog(picker) == DialogResult.OK)
                    AddExclusions(configuration, picker.SelectedEntries);
            }
        }

        private void AddExecutableExclusionButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked || !EnsureWorkspaceAvailable())
                return;
            var configuration = _workspace.Configuration;
            using (var dialog = CreateExecutableDialog())
            {
                if (ShowConfigurationDialog(dialog) == DialogResult.OK)
                    AddExclusions(configuration, dialog.FileNames.Select(Path.GetFileName));
            }
        }

        private void AddPathExclusionButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked || !EnsureWorkspaceAvailable())
                return;
            var configuration = _workspace.Configuration;
            using (var dialog = CreateExecutableDialog())
            {
                if (ShowConfigurationDialog(dialog) == DialogResult.OK)
                    AddExclusions(configuration, dialog.FileNames);
            }
        }

        private void AddManualExclusionButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked || !EnsureWorkspaceAvailable())
                return;
            var configuration = _workspace.Configuration;
            using (var dialog = new InputDialog("添加排除项",
                "输入进程名称或路径子串。排除项匹配有意设计得比较宽松："))
            {
                if (ShowConfigurationDialog(dialog) == DialogResult.OK)
                    AddExclusions(configuration, new[] { dialog.Value });
            }
        }

        private void AddExclusions(ProxiFyreConfiguration configuration, IEnumerable<string> values)
        {
            if (!CanCommitDialogMutation(configuration))
                return;
            var exclusions = configuration.Excludes;
            var added = false;
            foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (exclusions.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
                    continue;
                exclusions.Add(value);
                added = true;
            }
            if (!added)
                return;
            MarkConfigurationChanged();
            RefreshExclusionsList(exclusions.Count - 1);
        }

        private void RemoveExclusionButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked)
                return;
            var index = exclusionsListBox.SelectedIndex;
            if (index < 0 || _workspace.Configuration?.Excludes == null)
                return;
            var configuration = _workspace.Configuration;
            if (MessageBox.Show(this, "移除所选排除项？", "移除排除项",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            if (!CanCommitDialogMutation(configuration))
                return;
            configuration.Excludes.RemoveAt(index);
            MarkConfigurationChanged();
            RefreshExclusionsList(Math.Min(index, configuration.Excludes.Count - 1));
        }

        private void ExclusionsListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (ConfigurationMutationBlocked)
            {
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Delete)
            {
                RemoveExclusionButton_Click(sender, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void ExclusionsListBox_SelectedIndexChanged(object sender, EventArgs e) => UpdateExclusionButtons();

        private void UpdateExclusionButtons()
        {
            var canEdit = !ConfigurationMutationBlocked && _workspace.Configuration != null;
            addRunningExclusionButton.Enabled = canEdit;
            addExecutableExclusionButton.Enabled = canEdit;
            addPathExclusionButton.Enabled = canEdit;
            addManualExclusionButton.Enabled = canEdit;
            removeExclusionButton.Enabled = canEdit && exclusionsListBox.SelectedIndex >= 0;
            exclusionsListBox.Enabled = canEdit;
        }

        private async void StartServiceButton_Click(object sender, EventArgs e)
        {
            if (_operationInProgress)
                return;

            SetOperationState(true);
            try
            {
                if (!await EnsureInstalledServiceEngineCurrentAsync("启动") ||
                    !CanUseSavedConfiguration("启动"))
                    return;
                var expectedEnginePath = _workspace.EnginePath;
                Func<bool> registrationGuard = () =>
                    IsRegisteredServiceEngine(expectedEnginePath);
                await RunServiceOperationAsync(() => _serviceController.StartAsync(
                        expectedEnginePath, registrationGuard,
                        ApplicationConstants.ServiceOperationTimeout, _lifetimeCancellation.Token), true,
                    true, expectedEnginePath, registrationGuard);
            }
            finally
            {
                SetOperationState(false);
            }
        }

        private async void StopServiceButton_Click(object sender, EventArgs e)
        {
            await RunServiceOperationAsync(() => _serviceController.StopAsync(
                ApplicationConstants.ServiceOperationTimeout, _lifetimeCancellation.Token), false);
        }

        private async void RestartServiceButton_Click(object sender, EventArgs e)
        {
            if (_operationInProgress)
                return;

            SetOperationState(true);
            try
            {
                if (!await EnsureInstalledServiceEngineCurrentAsync("重启") ||
                    !CanUseSavedConfiguration("重启"))
                    return;
                var expectedEnginePath = _workspace.EnginePath;
                Func<bool> registrationGuard = () =>
                    IsRegisteredServiceEngine(expectedEnginePath);
                await RunServiceOperationAsync(() => _serviceController.RestartAsync(
                        expectedEnginePath, registrationGuard,
                        ApplicationConstants.ServiceOperationTimeout, _lifetimeCancellation.Token), true,
                    true, expectedEnginePath, registrationGuard);
            }
            finally
            {
                SetOperationState(false);
            }
        }

        private async void InstallServiceButton_Click(object sender, EventArgs e)
        {
            if (_operationInProgress)
                return;

            SetOperationState(true);
            try
            {
                await InstallServiceAsync();
            }
            finally
            {
                SetOperationState(false);
            }
        }

        private async Task<bool> InstallServiceAsync()
        {
            await RefreshServiceStatusAsync();
            if (!_serviceStatus.IsInstallationKnown)
            {
                MessageBox.Show(this,
                    "无法确认 Windows 服务的安装状态。未执行安装命令。",
                    "服务状态不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (_serviceStatus.IsInstalled)
            {
                MessageBox.Show(this, "ProxiFyre 服务已安装。",
                    "服务已安装", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (_engineLocation == null || !_engineLocation.IsResolved)
            {
                MessageBox.Show(this, "请先解析有效的 ProxiFyre.exe，再安装服务。",
                    "未解析引擎", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var installed = await RunServiceOperationAsync(() => _serviceController.InstallAsync(
                _engineLocation.Path, ApplicationConstants.ServiceOperationTimeout, _lifetimeCancellation.Token), false);
            return installed;
        }

        private async void UninstallServiceButton_Click(object sender, EventArgs e)
        {
            if (_operationInProgress)
                return;

            SetOperationState(true);
            try
            {
                var verifiedStatus = await GetServiceStatusForMutationAsync();
                if (verifiedStatus == null)
                    return;
                if (!verifiedStatus.IsInstalled)
                {
                    MessageBox.Show(this, "ProxiFyre 服务未安装。",
                        "服务未安装", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string registrationSnapshot;
                try
                {
                    registrationSnapshot = _engineLocator.GetRegisteredServiceImagePath();
                }
                catch (Exception ex)
                {
                    ShowError("无法读取服务注册信息。未执行卸载命令。", ex);
                    return;
                }
                EngineLocation registeredEngine;
                EngineLocation protectedRegisteredEngine;
                try
                {
                    registeredEngine = _engineLocator.Resolve(_settings);
                    protectedRegisteredEngine = registeredEngine.Source ==
                                                ProxiFyreUI.Infrastructure.EngineLocationSource.ServiceRegistration &&
                                                registeredEngine.IsResolved
                        ? _engineLocator.ValidateProtectedUninstallSelection(registeredEngine.Path)
                        : registeredEngine;
                }
                catch (Exception ex)
                {
                    ShowError("无法解析已注册的服务可执行文件。未执行卸载命令。", ex);
                    return;
                }
                var useRegisteredEngine = registeredEngine.Source ==
                                          ProxiFyreUI.Infrastructure.EngineLocationSource.ServiceRegistration &&
                                          registeredEngine.IsResolved &&
                                          protectedRegisteredEngine.IsResolved;
                var uninstallEngine = registeredEngine;
                Func<bool> registrationSnapshotGuard;
                string expectedRegisteredEnginePath = null;

                if (useRegisteredEngine)
                {
                    registrationSnapshotGuard = () =>
                        IsServiceRegistrationSnapshotCurrent(registrationSnapshot);
                    expectedRegisteredEnginePath = registeredEngine.Path;
                    if (MessageBox.Show(this,
                        "是否卸载 ProxiFyre Windows 服务？\r\n\r\n配置、备份和日志文件不会被删除。",
                        "卸载服务", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                        return;
                }
                else
                {
                    uninstallEngine = _engineLocator.ResolveProtectedUninstallFallback(_settings,
                        _engineLocation?.Path);
                    if (!uninstallEngine.IsResolved)
                        uninstallEngine = BrowseForProtectedUninstallEngine();
                    if (uninstallEngine == null || !uninstallEngine.IsResolved)
                        return;

                    var registrationProblem = !string.IsNullOrWhiteSpace(protectedRegisteredEngine.Error)
                        ? protectedRegisteredEngine.Error
                        : "已安装的服务没有包含可用的注册可执行文件路径。";
                    var fallbackMessage = registrationProblem + "\r\n\r\n" +
                        "为移除服务注册，ProxiFyreUI 可以使用这个来自受保护每计算机位置的" +
                        "已验证可执行文件：\r\n\r\n" +
                        uninstallEngine.Path + "\r\n\r\n" +
                        "它只会以唯一固定参数启动：uninstall\r\n" +
                        "不会使用服务注册中的任何命令行参数。" +
                        "配置、备份和日志文件不会被删除。\r\n\r\n是否继续？";
                    if (MessageBox.Show(this, fallbackMessage, "使用受保护回退方案卸载服务",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                        return;

                    var finalStatus = await GetServiceStatusForMutationAsync();
                    if (finalStatus == null || !finalStatus.IsInstalled)
                    {
                        MessageBox.Show(this,
                            "服务安装在开始卸载前已发生变化。未执行任何命令。",
                            "服务状态已更改", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (!IsFallbackServiceRegistrationSnapshotCurrent(registrationSnapshot))
                    {
                        ShowServiceEngineChanged("确认卸载期间服务注册或已注册可执行文件已发生变化。" +
                                                 "未执行任何命令；请检查当前注册信息后重试。");
                        return;
                    }
                    registrationSnapshotGuard = () =>
                        IsFallbackServiceRegistrationSnapshotCurrent(registrationSnapshot);
                }

                await RunServiceOperationAsync(() => _serviceController.UninstallAsync(
                        uninstallEngine.Path, registrationSnapshotGuard,
                        ApplicationConstants.ServiceOperationTimeout, _lifetimeCancellation.Token),
                    false, true, expectedRegisteredEnginePath,
                    registrationSnapshotGuard);
            }
            finally
            {
                SetOperationState(false);
            }
        }

        private async Task<bool> RunServiceOperationAsync(Func<Task<ServiceOperationResult>> operation,
            bool marksAppliedOnSuccess, bool showSuccess = true,
            string expectedRegisteredEnginePath = null,
            Func<bool> serviceRegistrationGuard = null)
        {
            var expectedConfiguration = marksAppliedOnSuccess ? _workspace.CurrentFingerprint : null;
            SetOperationState(true);
            try
            {
                if (!string.IsNullOrWhiteSpace(expectedRegisteredEnginePath) &&
                    !IsRegisteredServiceEngine(expectedRegisteredEnginePath))
                {
                    ShowServiceEngineChanged("由于已注册的 ProxiFyre 可执行文件已发生变化，" +
                                             "服务操作未启动。请刷新引擎位置后重试。");
                    return false;
                }
                if (serviceRegistrationGuard != null && !serviceRegistrationGuard())
                {
                    ShowServiceEngineChanged("操作开始前服务注册或已注册可执行文件已发生变化。" +
                                             "未执行任何命令；请检查当前注册信息后重试。");
                    return false;
                }

                var result = await operation();
                ObserveServiceStatus(result.Status ??
                    await _serviceController.GetStatusAsync(_lifetimeCancellation.Token));
                if (result.Success)
                {
                    ClearServiceFailureIndicators();
                    var engineStillCurrent = !marksAppliedOnSuccess ||
                                             IsRegisteredServiceEngine(expectedRegisteredEnginePath);
                    var appliedStateConfirmed = !marksAppliedOnSuccess ||
                                                (result.ConfirmsConfigurationReloaded &&
                                                 engineStillCurrent && _serviceStatus.IsRunning &&
                                                 _workspace.TryMarkApplied(expectedConfiguration));
                    if (!appliedStateConfirmed)
                    {
                        _workspace.InvalidateAppliedConfirmation();
                        statusStripLabel.Text = !engineStillCurrent
                            ? "服务操作已完成，但已注册可执行文件已发生变化；应用状态未知。"
                            : !result.ConfirmsConfigurationReloaded
                                ? "服务正在运行，但此操作未重新加载 app-config.json。请重启服务以应用并确认已保存的配置。"
                                : "服务操作已完成，但无法确认已保存的配置已应用。";
                    }
                    else if (showSuccess)
                    {
                        statusStripLabel.Text = result.Message;
                    }
                }
                else
                {
                    RecordServiceFailure(result);
                    statusStripLabel.Text = result.Message ?? "服务操作失败。";
                    ShowServiceFailure(result);
                }
                UpdateAllStates();
                return result.Success;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                ShowError("服务操作失败。", ex);
                return false;
            }
            finally
            {
                SetOperationState(false);
                await RefreshServiceStatusAsync();
            }
        }

        private void ShowServiceFailure(ServiceOperationResult result)
        {
            var recent = _logLines.Where(IsWarningOrError).TakeLastCompatible(8);
            var message = _errorFormatter.FormatServiceFailure(result, recent, CurrentSecrets());
            MessageBox.Show(this, message, "ProxiFyre 服务", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void ReloadButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked)
                return;

            SetOperationState(true);
            try
            {
                if (!ConfirmDiscardUnsaved("从磁盘重新加载配置"))
                    return;
                LoadWorkspace();
                statusStripLabel.Text = "已从磁盘重新加载配置。";
            }
            catch (Exception ex)
            {
                ShowError("无法重新加载配置。", ex);
            }
            finally
            {
                SetOperationState(false);
            }
        }

        private void ValidateButton_Click(object sender, EventArgs e)
        {
            if (!EnsureWorkspaceAvailable())
                return;
            ReadRootControlsIntoConfiguration();
            var validation = _workspace.Validate();
            RefreshRoutingGrid(SelectedRuleIndex);
            MessageBox.Show(this, ConfigurationWorkspace.FormatValidation(validation),
                validation.HasErrors ? "配置无效" : "配置验证",
                MessageBoxButtons.OK, validation.HasErrors ? MessageBoxIcon.Error :
                    validation.HasWarnings ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            UpdateAllStates();
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            if (ConfigurationMutationBlocked)
                return;

            SetOperationState(true);
            try
            {
                var save = SaveWithPrompts();
                if (save?.Status == WorkspaceSaveStatus.Saved)
                {
                    statusStripLabel.Text = _serviceStatus.IsRunning
                        ? "配置已保存。请重启服务以应用。"
                        : "配置已保存，将在下次服务启动时使用。";
                    UpdateAllStates();
                }
            }
            finally
            {
                SetOperationState(false);
            }
        }

        private async void ApplyRestartButton_Click(object sender, EventArgs e)
        {
            if (_operationInProgress || _applyWorkflowInProgress)
                return;

            // Acquire the UI-level apply gate before SaveWithPrompts. Saving creates/rotates the
            // sole recovery backup; a second click or Save during the first status refresh must
            // not overwrite it before the apply/rollback decision is complete.
            _applyWorkflowInProgress = true;
            SetOperationState(true);
            try
            {
                if (!await EnsureInstalledServiceEngineCurrentAsync("应用配置"))
                    return;

                var save = SaveWithPrompts();
                if (save == null || save.Status != WorkspaceSaveStatus.Saved)
                    return;

                await RefreshServiceStatusAsync();
                var installIfMissing = false;
                if (_serviceStatus.IsInstallationKnown && !_serviceStatus.IsInstalled)
                {
                    var install = MessageBox.Show(this,
                        "配置已保存，但 ProxiFyre 服务未安装。\r\n\r\n是否立即安装并启动它？",
                        "已保存但未应用", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    installIfMissing = install == DialogResult.Yes;
                }

                ConfigurationApplyResult applied;
                try
                {
                    applied = await _applyCoordinator.ApplySavedConfigurationAsync(save,
                        _engineLocation?.Path, installIfMissing, false,
                        ApplicationConstants.ServiceOperationTimeout, _lifetimeCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ShowError("配置已保存，但无法应用。", ex);
                    return;
                }
                finally
                {
                    await RefreshServiceStatusAsync();
                }

                if (applied.Outcome == ConfigurationApplyOutcome.Applied)
                {
                    ClearServiceFailureIndicators();
                    statusStripLabel.Text = "配置已应用；服务报告正在运行。";
                    UpdateAllStates();
                    return;
                }

                if (applied.Outcome == ConfigurationApplyOutcome.SavedServiceNotInstalled)
                {
                    statusStripLabel.Text = "配置已保存；请安装并启动服务以应用。";
                    UpdateAllStates();
                    return;
                }

                if (applied.Outcome == ConfigurationApplyOutcome.ServiceEngineChanged)
                {
                    ShowServiceEngineChanged(applied.ServiceResult == null
                        ? "已注册的 ProxiFyre 可执行文件在服务操作前已发生变化。未尝试更改服务。"
                        : "已注册的 ProxiFyre 可执行文件在服务操作期间已发生变化。已保存的配置未被标记为已应用。");
                    return;
                }

                if (applied.Outcome == ConfigurationApplyOutcome.ConfigurationReloadNotConfirmed)
                {
                    statusStripLabel.Text =
                        "服务正在运行，但它已在其他位置启动，因此本次应用未重新加载 app-config.json。请再次运行“应用并重启”。";
                    UpdateAllStates();
                    return;
                }

                if (applied.Outcome == ConfigurationApplyOutcome.DependencyUnavailable)
                {
                    if (applied.ServiceResult != null)
                        ShowServiceFailure(applied.ServiceResult);
                    RecordServiceFailure(applied.ServiceResult);
                    statusStripLabel.Text =
                        "配置已保存，但 Windows Packet Filter 不可用。未尝试回滚配置。";
                    UpdateAllStates();
                    return;
                }

                if (applied.Outcome == ConfigurationApplyOutcome.InstallFailed)
                {
                    if (applied.ServiceResult != null)
                        ShowServiceFailure(applied.ServiceResult);
                    RecordServiceFailure(applied.ServiceResult);
                    statusStripLabel.Text = "配置已保存，但无法安装服务。已保留备份。";
                    UpdateAllStates();
                    return;
                }

                if (applied.Outcome == ConfigurationApplyOutcome.ServiceStatusUnavailable)
                {
                    if (applied.ServiceResult != null)
                        ShowServiceFailure(applied.ServiceResult);
                    RecordServiceFailure(applied.ServiceResult);
                    statusStripLabel.Text = applied.ServiceResult?.Message ??
                        "配置已保存，但无法确认 Windows 服务状态。未尝试更改服务。";
                    UpdateAllStates();
                    return;
                }

                if (applied.Outcome == ConfigurationApplyOutcome.SavedConfigurationChangedBeforeApply ||
                    applied.Outcome == ConfigurationApplyOutcome.SavedConfigurationChangedDuringApply)
                {
                    statusStripLabel.Text = applied.Outcome ==
                        ConfigurationApplyOutcome.SavedConfigurationChangedBeforeApply
                        ? "app-config.json 在服务操作前已更改；已保存的编辑未应用。请从磁盘重新加载。"
                        : "app-config.json 在服务操作期间已更改；应用状态未知。请从磁盘重新加载。";
                    UpdateAllStates();
                    return;
                }

                if (applied.Outcome != ConfigurationApplyOutcome.ServiceRestartFailed)
                {
                    statusStripLabel.Text = "配置已保存，但未应用。";
                    UpdateAllStates();
                    return;
                }

                if (applied.ServiceResult != null)
                    ShowServiceFailure(applied.ServiceResult);
                RecordServiceFailure(applied.ServiceResult);

                if (!save.BackupCreated || string.IsNullOrWhiteSpace(save.BackupPath))
                {
                    statusStripLabel.Text = "配置已保存，但服务重启失败。没有可用的旧活动文件进行回滚。";
                    return;
                }

                var rollback = MessageBox.Show(this,
                    "新配置已保存，但服务未能成功重启。\r\n\r\n" +
                    "是否从备份恢复上一配置，并尝试一次受控重启？",
                    "应用失败", MessageBoxButtons.YesNo, MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1);
                if (rollback != DialogResult.Yes)
                {
                    statusStripLabel.Text = "配置已保存，但服务重启失败。备份已保留用于恢复。";
                    return;
                }

                ConfigurationApplyResult rolledBack;
                try
                {
                    rolledBack = await _applyCoordinator.RollbackAndRestartAsync(save,
                        ApplicationConstants.ServiceOperationTimeout, _lifetimeCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    await RefreshServiceStatusAsync();
                }

                if (rolledBack.Outcome == ConfigurationApplyOutcome.RollbackFailed)
                {
                    ShowError("无法恢复上一配置。备份未被删除。",
                        rolledBack.RollbackError);
                    statusStripLabel.Text = "应用和回滚均失败；请检查配置和日志。";
                    return;
                }

                if (rolledBack.Outcome == ConfigurationApplyOutcome.RolledBackConfigurationChanged)
                {
                    // TryRollback already replaced the working model with the backup. Keep the
                    // controls in sync even though the live file then changed again externally.
                    BindConfiguration();
                    statusStripLabel.Text =
                        "回滚文件在重启期间已更改；应用状态未知。请从磁盘重新加载。";
                    UpdateAllStates();
                    return;
                }

                if (rolledBack.Outcome == ConfigurationApplyOutcome.ServiceEngineChanged)
                {
                    ShowServiceEngineChanged(
                        "已注册的 ProxiFyre 可执行文件在回滚前已更改。已保存的配置仍待处理。");
                    BindConfiguration();
                    return;
                }

                if (rolledBack.Outcome == ConfigurationApplyOutcome.RolledBackServiceEngineChanged)
                {
                    ShowServiceEngineChanged(rolledBack.ServiceResult == null
                        ? "上一配置已恢复，但已注册的 ProxiFyre 可执行文件在重启前已更改。应用状态未知。"
                        : "上一配置已恢复，但已注册的 ProxiFyre 可执行文件在回滚重启期间已更改。应用状态未知。");
                    BindConfiguration();
                    return;
                }

                if (rolledBack.Outcome ==
                    ConfigurationApplyOutcome.RolledBackConfigurationReloadNotConfirmed)
                {
                    BindConfiguration();
                    statusStripLabel.Text =
                        "上一配置已恢复，但正在运行的服务未重新加载它。请重启服务以确认恢复后的配置。";
                    UpdateAllStates();
                    return;
                }

                BindConfiguration();
                var rollbackRestarted = rolledBack.Outcome == ConfigurationApplyOutcome.RolledBackAndRunning;
                if (!rollbackRestarted && rolledBack.ServiceResult != null)
                    ShowServiceFailure(rolledBack.ServiceResult);
                if (rollbackRestarted)
                    ClearServiceFailureIndicators();
                else
                    RecordServiceFailure(rolledBack.ServiceResult);
                statusStripLabel.Text = rollbackRestarted
                    ? "上一配置已恢复，服务正在运行。"
                    : "上一配置已恢复，但服务重启仍然失败。";
                UpdateAllStates();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowError("应用流程未完成。", ex);
            }
            finally
            {
                _applyWorkflowInProgress = false;
                SetOperationState(false);
            }
        }

        private WorkspaceSaveResult SaveWithPrompts()
        {
            if (!EnsureWorkspaceAvailable())
                return null;
            ReadRootControlsIntoConfiguration();
            var validation = _workspace.Validate();
            RefreshRoutingGrid(SelectedRuleIndex);
            if (validation.HasErrors)
            {
                MessageBox.Show(this, ConfigurationWorkspace.FormatValidation(validation),
                    "配置未保存", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return new WorkspaceSaveResult { Status = WorkspaceSaveStatus.ValidationFailed, Validation = validation };
            }
            if (validation.HasWarnings && MessageBox.Show(this,
                ConfigurationWorkspace.FormatValidation(validation) + "\r\n\r\n尽管存在这些警告，仍要保存吗？",
                "配置警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return null;

            var result = _workspace.Save(false);
            if (result.Status == WorkspaceSaveStatus.ExternalChangeDetected)
            {
                var choice = MessageBox.Show(this,
                    "app-config.json 在加载后于 ProxiFyreUI 之外发生了变化。\r\n\r\n" +
                    "是 — 从磁盘重新加载并放弃当前编辑\r\n" +
                    "否 — 用当前编辑覆盖外部版本\r\n" +
                    "取消 — 保持两者不变",
                    "外部配置更改", MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button3);
                if (choice == DialogResult.Yes)
                {
                    try { LoadWorkspace(); }
                    catch (Exception ex) { ShowError("无法加载外部配置。", ex); }
                    return null;
                }
                if (choice == DialogResult.No)
                    result = _workspace.Save(true);
                else
                    return null;
            }

            if (result.Status == WorkspaceSaveStatus.Failed)
                ShowError("活动配置未被更改。", result.Error);
            else if (result.Status == WorkspaceSaveStatus.Saved)
                BindConfiguration();
            UpdateAllStates();
            return result;
        }

        private async void BrowseEngineButton_Click(object sender, EventArgs e)
        {
            if (_operationInProgress)
                return;

            SetOperationState(true);
            try
            {
                var verifiedStatus = await GetServiceStatusForMutationAsync();
                if (verifiedStatus == null)
                    return;
                if (verifiedStatus.IsInstalled)
                {
                    MessageBox.Show(this,
                        "GUI 使用已安装服务注册的可执行文件。请先卸载或重新注册服务，再选择其他引擎。",
                        "已安装服务控制引擎位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (!ConfirmDiscardUnsaved("更改 ProxiFyre 引擎位置"))
                    return;

                using (var dialog = new OpenFileDialog
                {
                    Title = "选择 ProxiFyre 引擎",
                    Filter = "ProxiFyre 引擎 (" + ProxiFyrePaths.EngineExecutableName + ")|" +
                             ProxiFyrePaths.EngineExecutableName,
                    CheckFileExists = true,
                    Multiselect = false
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                        return;
                    var location = _engineLocator.ValidateUserSelection(dialog.FileName);
                    if (!location.IsResolved)
                    {
                        MessageBox.Show(this, location.Error, "无效引擎", MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return;
                    }

                    try
                    {
                        var previousEnginePath = _workspace.EnginePath;
                        var loaded = _workspace.Load(location);
                        _engineLocation = location;
                        _settings.SelectedEnginePath = location.Path;
                        TrySaveSettings();
                        UpdateResolvedPaths();
                        CompleteWorkspaceLoad(loaded, previousEnginePath);
                    }
                    catch (Exception ex)
                    {
                        ShowError("无法加载所选引擎的配置。" +
                                  "上一引擎和工作配置已保留。", ex);
                    }
                }
            }
            finally
            {
                SetOperationState(false);
            }
        }

        private void OpenConfigurationFolderButton_Click(object sender, EventArgs e)
        {
            OpenFolder(Path.GetDirectoryName(_workspace.ConfigurationPath), "配置");
        }

        private void OpenLogFolderButton_Click(object sender, EventArgs e)
        {
            OpenFolder(_workspace.LogDirectoryPath, "日志");
        }

        private void OpenLogFileButton_Click(object sender, EventArgs e)
        {
            try
            {
                var filePath = _logTailer.CurrentFilePath;
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    filePath = LogFileLocator.FindLatest(_workspace.LogDirectoryPath);

                if (string.IsNullOrWhiteSpace(filePath))
                {
                    MessageBox.Show(this,
                        "尚无 ProxiFyre 日志文件可用。请启动服务，或在日志创建后重新加载“日志”选项卡。",
                        "日志文件不可用", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true });
                statusStripLabel.Text = "已打开 " + Path.GetFileName(filePath) + "。";
            }
            catch (Exception ex)
            {
                ShowError("无法打开当前日志文件。", ex);
            }
        }

        private void CopyDiagnosticsButton_Click(object sender, EventArgs e)
        {
            try
            {
                var diagnostics = _diagnosticsBuilder.Build(new DiagnosticsContext
                {
                    EnginePath = _workspace.EnginePath,
                    ConfigurationPath = _workspace.ConfigurationPath,
                    LogDirectoryPath = _workspace.LogDirectoryPath,
                    ServiceStatus = _serviceStatus,
                    Validation = _workspace.LastValidation,
                    RecentMessages = _logLines.ToArray(),
                    Secrets = _workspace.Configuration?.Proxies?
                        .Select(rule => rule?.Password)
                        .Where(password => !string.IsNullOrEmpty(password))
                });
                Clipboard.SetText(diagnostics);
                statusStripLabel.Text = "脱敏后的诊断信息已复制到剪贴板。";
            }
            catch (Exception ex)
            {
                ShowError("无法复制诊断信息。", ex);
            }
        }

        private void StartLogTailerIfNeeded()
        {
            if (followLogsCheckBox.Checked && !string.IsNullOrWhiteSpace(_workspace.LogDirectoryPath))
                _logTailer.Start(_workspace.LogDirectoryPath);
        }

        private void ResetLogViewForEngineChange()
        {
            Interlocked.Increment(ref _logViewGeneration);
            _logTailer.Stop();
            _logLines.Clear();
            logRichTextBox.Clear();
            logStatusLabel.Text = followLogsCheckBox.Checked
                ? "正在加载所选引擎的日志…"
                : "已暂停跟随。所选引擎的日志视图已清空。";
        }

        private void FollowLogsCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            _settings.FollowLogs = followLogsCheckBox.Checked;
            TrySaveSettings();
            if (followLogsCheckBox.Checked)
                StartLogTailerIfNeeded();
            else
            {
                _logTailer.Stop();
                logStatusLabel.Text = "已暂停跟随。现有视图已保留。";
            }
        }

        private void LogTailer_LinesRead(object sender, LogLinesEventArgs e)
        {
            var viewGeneration = Volatile.Read(ref _logViewGeneration);
            TryPostToUi(() =>
            {
                if (viewGeneration != Volatile.Read(ref _logViewGeneration) ||
                    e.Generation != _logTailer.Generation ||
                    !PathsEqual(e.SourceDirectoryPath, _workspace.LogDirectoryPath) ||
                    !PathsEqual(Path.GetDirectoryName(e.FilePath), e.SourceDirectoryPath))
                    return;
                if (e.Reset)
                    _logLines.Clear();
                foreach (var line in e.Lines)
                {
                    _logLines.Enqueue(line);
                    while (_logLines.Count > ApplicationConstants.MaximumLogLines)
                        _logLines.Dequeue();
                }
                RenderLogs();
            });
        }

        private void LogTailer_StatusChanged(object sender, LogStatusEventArgs e)
        {
            var viewGeneration = Volatile.Read(ref _logViewGeneration);
            TryPostToUi(() =>
            {
                if (viewGeneration == Volatile.Read(ref _logViewGeneration) &&
                    e.Generation == _logTailer.Generation &&
                    PathsEqual(e.SourceDirectoryPath, _workspace.LogDirectoryPath))
                    logStatusLabel.Text = e.Status;
            });
        }

        private void TryPostToUi(Action action)
        {
            if (action == null || _cleanedUp || Disposing || IsDisposed || !IsHandleCreated)
                return;

            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!_cleanedUp && !Disposing && !IsDisposed)
                        action();
                }));
            }
            catch (ObjectDisposedException)
            {
                // The window handle was destroyed between the guard and BeginInvoke.
            }
            catch (InvalidOperationException)
            {
                // Form shutdown can remove the handle while a timer callback is scheduling.
            }
        }

        private void LogFilterChanged(object sender, EventArgs e) => RenderLogs();

        private void RenderLogs()
        {
            var filter = logSearchTextBox.Text.Trim();
            var level = MapLogFilterDisplayValue(logLevelFilterComboBox.SelectedItem?.ToString());
            var visible = _logLines.Where(line =>
                (filter.Length == 0 || line.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0) &&
                LogLevelMatcher.Matches(line, level));
            logRichTextBox.Lines = visible.ToArray();
            if (followLogsCheckBox.Checked && logRichTextBox.TextLength > 0)
            {
                logRichTextBox.SelectionStart = logRichTextBox.TextLength;
                logRichTextBox.ScrollToCaret();
            }
        }

        private static string MapLogFilterDisplayValue(string value)
        {
            switch (value)
            {
                case "错误": return "Error";
                case "警告": return "Warning";
                case "信息": return "Info";
                case "调试": return "Debug";
                default: return "All levels";
            }
        }

        private void CopySelectedLogButton_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(logRichTextBox.SelectedText))
                Clipboard.SetText(logRichTextBox.SelectedText);
        }

        private void CopyVisibleLogButton_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(logRichTextBox.Text))
                Clipboard.SetText(logRichTextBox.Text);
        }

        private void ClearLogViewButton_Click(object sender, EventArgs e)
        {
            _logLines.Clear();
            logRichTextBox.Clear();
            logStatusLabel.Text = "视图已清空。服务日志文件未被删除。";
        }

        private void ReloadLogsButton_Click(object sender, EventArgs e)
        {
            _logTailer.Reload(_workspace.LogDirectoryPath);
        }

        private async Task<bool> EnsureInstalledServiceEngineCurrentAsync(string operation)
        {
            var verifiedStatus = await GetServiceStatusForMutationAsync();
            if (verifiedStatus == null)
                return false;
            if (verifiedStatus.State == ProxiFyreServiceState.DeletionPending)
            {
                MessageBox.Show(this,
                    "ProxiFyre 服务已标记为待删除。请等待 Windows 完成删除后再尝试" +
                    operation + "。",
                    "服务待删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (!verifiedStatus.IsInstalled)
                return true;

            var registeredEngine = GetRegisteredServiceEngine();
            if (registeredEngine == null)
                return false;

            var workspaceMatches = PathsEqual(_workspace.EnginePath, registeredEngine.Path) &&
                                   _workspace.Configuration != null;
            var formMatches = PathsEqual(_engineLocation?.Path, registeredEngine.Path);
            if (workspaceMatches && formMatches)
                return true;

            if (_workspace.IsDirty && MessageBox.Show(this,
                "已安装服务现在指向另一个 ProxiFyre 可执行文件：\r\n\r\n" +
                registeredEngine.Path + "\r\n\r\n是否放弃上一引擎的未保存编辑，并在尝试" +
                operation + "前加载已注册引擎？",
                "服务引擎已更改", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                statusStripLabel.Text = "服务操作已取消；未保存的编辑已保留。";
                return false;
            }

            try
            {
                var previousEnginePath = _workspace.EnginePath;
                var loaded = _workspace.Load(registeredEngine);
                _engineLocation = registeredEngine;
                UpdateResolvedPaths();
                CompleteWorkspaceLoad(loaded, previousEnginePath);
                statusStripLabel.Text = "已加载服务注册可执行文件的配置。";
                return true;
            }
            catch (Exception ex)
            {
                ShowError("无法加载已注册服务引擎的配置。" +
                          "上一引擎和工作配置已保留，且未尝试更改服务。",
                    ex);
                return false;
            }
        }

        private async Task<ServiceStatusInfo> GetServiceStatusForMutationAsync()
        {
            ServiceStatusInfo verifiedStatus;
            try
            {
                verifiedStatus = await _serviceController.GetStatusAsync(_lifetimeCancellation.Token);
                ObserveServiceStatus(verifiedStatus);
                UpdateAllStates();
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                verifiedStatus = new ServiceStatusInfo(ProxiFyreServiceState.Error, ex.Message);
                ObserveServiceStatus(verifiedStatus);
                UpdateAllStates();
            }

            if (verifiedStatus.IsInstallationKnown)
                return verifiedStatus;

            MessageBox.Show(this,
                "无法确认 Windows 服务的安装状态。未尝试更改服务。",
                "服务状态不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        private EngineLocation GetRegisteredServiceEngine()
        {
            var location = _engineLocator.Resolve(_settings);
            if (location.Source == ProxiFyreUI.Infrastructure.EngineLocationSource.ServiceRegistration && location.IsResolved)
                return location;

            var detail = location.Source == ProxiFyreUI.Infrastructure.EngineLocationSource.ServiceRegistration &&
                         !string.IsNullOrWhiteSpace(location.Error)
                ? "\r\n\r\n" + location.Error
                : string.Empty;
            MessageBox.Show(this,
                "无法验证已安装 ProxiFyre 服务注册的可执行文件。" +
                "未尝试更改服务。" + detail,
                "已注册引擎不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        private EngineLocation BrowseForProtectedUninstallEngine()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "从受保护位置选择 ProxiFyre 引擎以卸载服务",
                Filter = "ProxiFyre 引擎 (" + ProxiFyrePaths.EngineExecutableName + ")|" +
                         ProxiFyrePaths.EngineExecutableName,
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return null;

                var location = _engineLocator.ValidateProtectedUninstallSelection(dialog.FileName);
                if (location.IsResolved)
                    return location;

                MessageBox.Show(this, location.Error,
                    "不可用的卸载可执行文件", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        private bool IsServiceRegistrationSnapshotCurrent(string expectedImagePath)
        {
            try
            {
                return string.Equals(expectedImagePath,
                    _engineLocator.GetRegisteredServiceImagePath(), StringComparison.Ordinal);
            }
            catch
            {
                // A fallback executable is allowed only while the unusable registration can be
                // shown to be the same one the user confirmed.
                return false;
            }
        }

        private bool IsFallbackServiceRegistrationSnapshotCurrent(string expectedImagePath)
        {
            if (!IsServiceRegistrationSnapshotCurrent(expectedImagePath))
                return false;
            try
            {
                var current = _engineLocator.Resolve(_settings);
                if (current.Source !=
                        ProxiFyreUI.Infrastructure.EngineLocationSource.ServiceRegistration ||
                    !current.IsResolved)
                    return true;
                return !_engineLocator.ValidateProtectedUninstallSelection(current.Path).IsResolved;
            }
            catch
            {
                return false;
            }
        }

        private bool IsRegisteredServiceEngine(string expectedPath)
        {
            try
            {
                return _engineLocator.IsRegisteredServiceExecutable(expectedPath);
            }
            catch
            {
                // Registration access failures and malformed values must never preserve an
                // applied-state confirmation or authorize a lifecycle operation.
                return false;
            }
        }

        private void ShowServiceEngineChanged(string message)
        {
            statusStripLabel.Text = message;
            MessageBox.Show(this, message, "服务引擎已更改", MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            UpdateAllStates();
        }

        private void ObserveServiceStatus(ServiceStatusInfo status)
        {
            _serviceStatus = status ?? new ServiceStatusInfo(ProxiFyreServiceState.Unknown);
            if (!_serviceStatus.IsRunning ||
                (_workspace.HasConfirmedApplied &&
                 (!_workspace.IsFingerprintCurrent(_workspace.CurrentFingerprint) ||
                  !IsRegisteredServiceEngine(_workspace.EnginePath))))
                _workspace.InvalidateAppliedConfirmation();
            if (_serviceStatus.IsRunning ||
                _serviceStatus.State == ProxiFyreServiceState.NotInstalled ||
                _serviceStatus.State == ProxiFyreServiceState.DeletionPending)
                ClearServiceFailureIndicators();
        }

        private void RecordServiceFailure(ServiceOperationResult result)
        {
            if (_serviceStatus.IsRunning)
            {
                ClearServiceFailureIndicators();
                return;
            }

            _packetFilterUnavailable = result?.FailureKind ==
                                       ServiceOperationFailureKind.DependencyUnavailable;
            _engineStartupFailed = ServiceOperationFailureClassifier.IsEngineStartupFailure(result);
        }

        private void ClearServiceFailureIndicators()
        {
            _engineStartupFailed = false;
            _packetFilterUnavailable = false;
        }

        private async void ServiceRefreshTimer_Tick(object sender, EventArgs e)
        {
            await RefreshServiceStatusAsync();
        }

        private async Task RefreshServiceStatusAsync()
        {
            if (Interlocked.Exchange(ref _refreshingStatus, 1) != 0)
                return;
            try
            {
                ObserveServiceStatus(await _serviceController.GetStatusAsync(_lifetimeCancellation.Token));
                UpdateAllStates();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ObserveServiceStatus(new ServiceStatusInfo(ProxiFyreServiceState.Error, ex.Message));
                UpdateAllStates();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshingStatus, 0);
            }
        }

        private void UpdateAllStates()
        {
            if (IsDisposed)
                return;
            serviceStateValueLabel.Text = ServiceStateDisplayName(_serviceStatus.State);
            serviceStateValueLabel.ForeColor = _serviceStatus.IsRunning ? Color.DarkGreen :
                _serviceStatus.State == ProxiFyreServiceState.Error ? Color.DarkRed : SystemColors.ControlText;
            trayServiceStatusMenuItem.Text = "服务：" + ServiceStateDisplayName(_serviceStatus.State);
            notifyIcon.Text = TruncateNotifyText("ProxiFyre — 服务：" + ServiceStateDisplayName(_serviceStatus.State));

            var validation = _workspace.Configuration == null ? null : _workspace.Validate();
            configurationStateValueLabel.Text = validation == null ? "未加载" :
                validation.HasErrors ? "配置无效" : "配置有效";
            configurationStateValueLabel.ForeColor = validation?.HasErrors == true ? Color.DarkRed :
                validation != null ? Color.DarkGreen : SystemColors.ControlText;

            if (_packetFilterUnavailable)
                changesStateValueLabel.Text = "数据包过滤器不可用";
            else if (_engineStartupFailed)
                changesStateValueLabel.Text = "引擎启动失败";
            else if (_workspace.IsDirty)
                changesStateValueLabel.Text = "存在未保存更改";
            else if (_workspace.RestartRequired && _serviceStatus.IsRunning)
                changesStateValueLabel.Text = "已保存，需要重启";
            else if (_workspace.RestartRequired)
                changesStateValueLabel.Text = "已保存；服务未运行";
            else if (_serviceStatus.IsRunning && validation?.HasErrors == false && _workspace.HasConfirmedApplied)
                changesStateValueLabel.Text = "已应用";
            else if (_serviceStatus.IsRunning && validation?.HasErrors == false)
                changesStateValueLabel.Text = "运行中；应用状态未知";
            else
                changesStateValueLabel.Text = "已保存；服务未运行";

            Text = _baseWindowTitle + (_workspace.IsDirty ? " *" : string.Empty);
            var pending = _serviceStatus.State == ProxiFyreServiceState.StartPending ||
                          _serviceStatus.State == ProxiFyreServiceState.StopPending ||
                          _serviceStatus.State == ProxiFyreServiceState.PausePending ||
                          _serviceStatus.State == ProxiFyreServiceState.ContinuePending ||
                          _serviceStatus.State == ProxiFyreServiceState.DeletionPending;
            startServiceButton.Enabled = !_operationInProgress && !pending &&
                _serviceStatus.IsInstalled && _serviceStatus.State == ProxiFyreServiceState.Stopped;
            stopServiceButton.Enabled = !_operationInProgress && !pending &&
                (_serviceStatus.State == ProxiFyreServiceState.Running || _serviceStatus.State == ProxiFyreServiceState.Paused);
            restartServiceButton.Enabled = !_operationInProgress && !pending && _serviceStatus.IsInstalled;
            installServiceButton.Enabled = !_operationInProgress && _serviceStatus.IsInstallationKnown &&
                !_serviceStatus.IsInstalled &&
                _engineLocation?.IsResolved == true;
            var canUninstallService = !_operationInProgress && !pending && _serviceStatus.IsInstalled &&
                                      _serviceStatus.IsInstallationKnown;
            headerUninstallServiceButton.Enabled = canUninstallService;
            uninstallServiceButton.Enabled = canUninstallService;
            browseEngineButton.Enabled = !_operationInProgress && _serviceStatus.IsInstallationKnown &&
                !_serviceStatus.IsInstalled;
            trayStartMenuItem.Enabled = startServiceButton.Enabled;
            trayStopMenuItem.Enabled = stopServiceButton.Enabled;
            trayRestartMenuItem.Enabled = restartServiceButton.Enabled;
            saveButton.Enabled = !_operationInProgress && _workspace.Configuration != null;
            applyRestartButton.Enabled = saveButton.Enabled &&
                                         _serviceStatus.State != ProxiFyreServiceState.DeletionPending;
            reloadButton.Enabled = !_operationInProgress && _workspace.HasConfigurationPath;
            validateButton.Enabled = !_operationInProgress && _workspace.Configuration != null;
            logLevelComboBox.Enabled = !ConfigurationMutationBlocked && _workspace.Configuration != null;
            bypassLanCheckBox.Enabled = !ConfigurationMutationBlocked && _workspace.Configuration != null;
            trayExitMenuItem.Enabled = !_exitRequested && _configurationDialogDepth == 0;
            UpdateRoutingButtons();
            UpdateExclusionButtons();
        }

        private void SetOperationState(bool busy)
        {
            if (busy)
                _operationDepth++;
            else if (_operationDepth > 0)
                _operationDepth--;

            _operationInProgress = _operationDepth > 0 || _applyWorkflowInProgress;
            UseWaitCursor = _operationInProgress;
            operationProgressBar.Visible = _operationInProgress;
            UpdateAllStates();
            ScheduleDeferredExitIfReady();
        }

        private bool ConfigurationMutationBlocked => _operationInProgress || _applyWorkflowInProgress;

        private void UpdateResolvedPaths()
        {
            enginePathTextBox.Text = _engineLocation?.Path ?? "未解析";
            configurationPathTextBox.Text = _engineLocation?.ConfigurationPath ?? "未解析";
            logPathTextBox.Text = _engineLocation?.LogDirectoryPath ?? "未解析";
            engineVersionValueLabel.Text = GetFileVersion(_engineLocation?.Path);
        }

        private bool EnsureWorkspaceAvailable()
        {
            if (_workspace.Configuration != null)
                return true;
            MessageBox.Show(this, "请先解析 ProxiFyre.exe 并加载配置。",
                "配置不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private bool CanUseSavedConfiguration(string operation)
        {
            if (string.IsNullOrWhiteSpace(_workspace.ConfigurationPath) || !File.Exists(_workspace.ConfigurationPath))
            {
                MessageBox.Show(this, "请先保存有效的 app-config.json，然后再尝试" + operation + "服务。",
                    "配置缺失", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            ValidationResult liveValidation;
            bool hasExternalChanges;
            try
            {
                liveValidation = _workspace.ValidateLiveFile();
                hasExternalChanges = _workspace.HasExternalChanges();
            }
            catch (Exception ex)
            {
                ShowError("无法读取已保存的配置，服务未执行" + operation + "操作。", ex);
                return false;
            }
            if (liveValidation.HasErrors)
            {
                MessageBox.Show(this,
                    "已保存的 app-config.json 无效。服务未执行" + operation + "操作。\r\n\r\n" +
                    ConfigurationWorkspace.FormatValidation(liveValidation, false),
                    "已保存配置无效", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (hasExternalChanges && MessageBox.Show(this,
                "app-config.json 在 ProxiFyreUI 之外发生了变化。外部文件有效，但此窗口显示的是较旧版本。\r\n\r\n" +
                "是否继续使用外部更改后的文件？",
                "外部配置更改", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return false;
            if (_workspace.IsDirty && MessageBox.Show(this,
                "存在未保存的编辑。服务将使用磁盘上当前的配置，而不是这些编辑。\r\n\r\n是否继续？",
                "未保存的编辑", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return false;
            return true;
        }

        private void SettingsPreferenceChanged(object sender, EventArgs e)
        {
            _settings.MinimizeToTray = minimizeToTrayCheckBox.Checked;
            TrySaveSettings();
        }

        private bool ConfirmDiscardUnsaved(string action)
        {
            return !_workspace.IsDirty || MessageBox.Show(this,
                "存在未保存的配置更改。继续将丢弃这些更改。\r\n\r\n是否继续" + action + "？",
                "放弃未保存的更改", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private void OpenFolder(string path, string description)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new DirectoryNotFoundException(description + "文件夹未解析。");
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowError("无法打开" + description + "文件夹。", ex);
            }
        }

        private void TrySaveSettings()
        {
            try { _settingsStore.Save(_settings); }
            catch (Exception ex) { statusStripLabel.Text = "无法保存 UI 首选项：" + ex.Message; }
        }

        private void TrayOpenMenuItem_Click(object sender, EventArgs e)
        {
            ShowMainWindow();
        }

        internal void RestoreFromSecondaryLaunch()
        {
            if (Disposing || IsDisposed)
                return;

            ShowMainWindow();
            statusStripLabel.Text = "ProxiFyre 已在运行；已激活现有窗口。";
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        private void NotifyIcon_DoubleClick(object sender, EventArgs e) => TrayOpenMenuItem_Click(sender, e);
        private void TrayStartMenuItem_Click(object sender, EventArgs e) => StartServiceButton_Click(sender, e);
        private void TrayStopMenuItem_Click(object sender, EventArgs e) => StopServiceButton_Click(sender, e);
        private void TrayRestartMenuItem_Click(object sender, EventArgs e) => RestartServiceButton_Click(sender, e);
        private void TrayOpenLogsMenuItem_Click(object sender, EventArgs e)
        {
            TrayOpenMenuItem_Click(sender, e);
            mainTabControl.SelectedTab = logsTabPage;
        }

        private void TrayExitMenuItem_Click(object sender, EventArgs e)
        {
            if (_operationInProgress || _applyWorkflowInProgress || _configurationDialogDepth > 0)
            {
                _exitRequested = true;
                trayExitMenuItem.Enabled = false;
                statusStripLabel.Text = _configurationDialogDepth > 0
                    ? "已请求退出。请先完成打开的编辑器；ProxiFyreUI 关闭前会确认未保存的更改。"
                    : "已请求退出。当前服务操作完成后，ProxiFyreUI 将关闭。";
                return;
            }

            _exiting = true;
            Close();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.WindowsShutDown)
            {
                // Windows is already terminating the interactive session. Do not put a modal
                // unsaved-edits prompt in the shutdown path; just cancel local work and release
                // UI resources best-effort.
                Cleanup();
                return;
            }

            if (!_exiting && e.CloseReason == CloseReason.UserClosing && _settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                statusStripLabel.Text = "ProxiFyreUI 仍在通知区域运行。服务未被停止。";
                return;
            }

            if (_operationInProgress || _applyWorkflowInProgress || _configurationDialogDepth > 0)
            {
                e.Cancel = true;
                if (_exiting)
                    _exitRequested = true;
                _exiting = false;
                statusStripLabel.Text = _exitRequested
                    ? (_configurationDialogDepth > 0
                        ? "已请求退出。请先完成打开的编辑器；ProxiFyreUI 关闭前会确认未保存的更改。"
                        : "已请求退出。当前服务操作完成后，ProxiFyreUI 将关闭。")
                    : "请先完成打开的编辑器，或等待当前服务操作完成后再关闭 ProxiFyreUI。";
                UpdateAllStates();
                return;
            }

            if (!ConfirmDiscardUnsaved("退出 ProxiFyreUI"))
            {
                e.Cancel = true;
                _exiting = false;
                _exitRequested = false;
                UpdateAllStates();
                return;
            }
            Cleanup();
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e) => Cleanup();

        private void Cleanup()
        {
            if (_cleanedUp)
                return;
            _cleanedUp = true;
            serviceRefreshTimer.Stop();
            _lifetimeCancellation.Cancel();
            _logTailer.Dispose();
            _serviceController.Dispose();
            notifyIcon.Visible = false;
            notifyIcon.Icon = null;
            Icon = null;
            notifyIcon.Dispose();
            _applicationIcon.Dispose();
            _lifetimeCancellation.Dispose();
        }

        private void ScheduleDeferredExitIfReady()
        {
            if (!_exitRequested || _operationInProgress || _applyWorkflowInProgress ||
                _configurationDialogDepth > 0 ||
                _exitCloseScheduled || _cleanedUp || Disposing || IsDisposed || !IsHandleCreated)
                return;

            _exitCloseScheduled = true;
            BeginInvoke((Action)(() =>
            {
                _exitCloseScheduled = false;
                if (!_exitRequested || _operationInProgress || _applyWorkflowInProgress ||
                    _configurationDialogDepth > 0 ||
                    _cleanedUp || Disposing || IsDisposed)
                    return;
                _exiting = true;
                Close();
            }));
        }

        private static string FormatApplications(IList<string> applications)
        {
            if (applications == null || applications.Count == 0)
                return "无匹配应用程序";
            var display = applications.Select(value => value != null && value.Length == 0
                ? "所有未匹配应用程序"
                : string.IsNullOrWhiteSpace(value) ? "<无效空条目>" : value);
            return string.Join("、", display);
        }

        private static string FormatAuthentication(ProxyRule rule)
        {
            return !string.IsNullOrEmpty(rule?.Username) || !string.IsNullOrEmpty(rule?.Password)
                ? "已配置凭据"
                : "无";
        }

        private static string FormatTransport(string transport)
        {
            return string.Equals(transport, "TLS", StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(transport) && transport.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0)
                ? "TLS"
                : "TCP";
        }

        private static string FormatValues(IList<string> values, string fallback)
        {
            return values == null ? fallback : values.Count == 0 ? "<无>" : string.Join("、", values);
        }

        private static bool IsWarningOrError(string line)
        {
            string level;
            if (LogLevelMatcher.TryGetLevel(line, out level))
                return string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase);
            return line.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool PathsEqual(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                return false;
            try
            {
                return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                return false;
            }
        }

        private static OpenFileDialog CreateExecutableDialog()
        {
            return new OpenFileDialog
            {
                Title = "选择可执行文件",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true
            };
        }

        private static string GetFileVersion(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "不可用";
            try { return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "不可用"; }
            catch { return "不可用"; }
        }

        private static Icon LoadApplicationIcon()
        {
            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ??
                       (Icon)SystemIcons.Application.Clone();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException ||
                                       ex is UnauthorizedAccessException)
            {
                return (Icon)SystemIcons.Application.Clone();
            }
        }

        private static string GetArchitectureDescription()
        {
            var os = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ??
                     Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ??
                     (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit");
            var process = Environment.Is64BitProcess ?
                (string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64",
                    StringComparison.OrdinalIgnoreCase) ? "ARM64" : "x64") : "x86";
            return os + " OS; " + process + " GUI";
        }

        private static string TruncateNotifyText(string value)
        {
            return value.Length <= 63 ? value : value.Substring(0, 63);
        }

        private static string ServiceStateDisplayName(ProxiFyreServiceState state)
        {
            switch (state)
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

        private void ShowError(string summary, Exception exception)
        {
            var message = _errorFormatter.FormatException(summary, exception, CurrentSecrets());
            MessageBox.Show(this, message, "ProxiFyre UI", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private IEnumerable<string> CurrentSecrets()
        {
            return _workspace.Configuration?.Proxies?
                       .Where(rule => rule != null && !string.IsNullOrEmpty(rule.Password))
                       .Select(rule => rule.Password) ?? Enumerable.Empty<string>();
        }
    }
}
