using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HAWindowsBridge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, @"Local\HAWindowsBridge", out bool first);
        if (!first)
        {
            MessageBox.Show("HA Windows Bridge is already running.", "HA Windows Bridge");
            return;
        }
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new BridgeContext());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "HA Windows Bridge could not start",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class BridgeContext : ApplicationContext
{
    private readonly BridgeConfig _config = BridgeConfig.Load();
    private readonly Metrics _metrics = new();
    private readonly HomeAssistantClient _client;
    private readonly Control _dispatcher = new();
    private readonly DisplayPowerWindow _displayWindow;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _pushStop;
    private Task? _pushTask;
    private string _pushUrl = "";
    private bool _pushConnected;
    private bool _stopping;
    private SettingsForm? _settings;

    public BridgeContext()
    {
        _client = new HomeAssistantClient(_config);
        _ = _dispatcher.Handle;
        _displayWindow = new DisplayPowerWindow(_metrics);
        SystemEvents.SessionSwitch += SessionChanged;

        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("Open Home Assistant", null, (_, _) => OpenHomeAssistant());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application, Text = "HA Windows Bridge",
            ContextMenuStrip = menu, Visible = true
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        if (!_config.IsConfigured)
            _dispatcher.BeginInvoke((Action)OpenSettings);
        _ = Task.Run(RunAsync);
    }

    private void SessionChanged(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason == SessionSwitchReason.SessionLock) _metrics.SessionLocked = true;
        if (args.Reason == SessionSwitchReason.SessionUnlock) _metrics.SessionLocked = false;
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!_config.IsConfigured)
                {
                    SetStatus("Open Settings to connect");
                }
                else
                {
                    var (url, network) = await _client.FindServerAsync(_stop.Token);
                    await _client.EnsureRegistrationAsync(url, _stop.Token);
                    await _client.SendSensorsAsync(url, _metrics.Sample(), _stop.Token);
                    StartNotifications(url);
                    SetStatus(network + (_pushConnected ? " connected" : " (push reconnecting)"));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                StopNotifications();
                SetStatus("Disconnected: " + ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_config.IntervalSeconds, 15, 300)),
                    _stop.Token);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void StartNotifications(string url)
    {
        if (_pushTask is { IsCompleted: false } && _pushUrl == url) return;
        StopNotifications();
        _pushUrl = url;
        _pushStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        CancellationToken cancellation = _pushStop.Token;
        _pushTask = Task.Run(async () =>
        {
            try
            {
                await _client.ListenAsync(url, () => _pushConnected = true, ShowNotification,
                    cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { SetStatus("Push disconnected: " + ex.Message); }
            finally { _pushConnected = false; }
        }, cancellation);
    }

    private void StopNotifications()
    {
        _pushStop?.Cancel();
        _pushStop?.Dispose();
        _pushStop = null;
        _pushTask = null;
        _pushConnected = false;
        _pushUrl = "";
    }

    private void ShowNotification(string title, string message)
    {
        OnUi(() =>
        {
            _tray.BalloonTipTitle = title[..Math.Min(title.Length, 60)];
            _tray.BalloonTipText = message[..Math.Min(message.Length, 240)];
            _tray.ShowBalloonTip(6000);
        });
    }

    private void SetStatus(string status) => OnUi(() =>
    {
        string label = status.Length > 110 ? status[..110] : status;
        _statusItem.Text = label;
        _tray.Text = ("HA Windows Bridge: " + status)[..Math.Min(63,
            ("HA Windows Bridge: " + status).Length)];
        if (_settings is { IsDisposed: false }) _settings.SetStatus(label);
    });

    private void OnUi(Action action)
    {
        if (_stopping || _dispatcher.IsDisposed) return;
        try { _dispatcher.BeginInvoke((Action)(() => { if (!_stopping) action(); })); }
        catch (InvalidOperationException) { /* Message loop has ended. */ }
    }

    private void OpenSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm(_config, () =>
        {
            StopNotifications();
            _config.Save();
        });
        _settings.SetStatus(_statusItem.Text);
        _settings.Show();
        _settings.Activate();
    }

    private void OpenHomeAssistant()
    {
        string url = !string.IsNullOrWhiteSpace(_config.LocalUrl)
            ? _config.LocalUrl : _config.ExternalUrl;
        if (url.Length == 0) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void Quit()
    {
        if (_stopping) return;
        _stopping = true;
        _stop.Cancel();
        StopNotifications();
        SystemEvents.SessionSwitch -= SessionChanged;
        _displayWindow.Dispose();
        _settings?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _dispatcher.Dispose();
        ExitThread();
    }
}

internal sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly BridgeConfig _config;
    private readonly Action _saved;
    private readonly TextBox _local = new() { Dock = DockStyle.Fill };
    private readonly TextBox _external = new() { Dock = DockStyle.Fill };
    private readonly TextBox _token = new()
        { Dock = DockStyle.Fill, UseSystemPasswordChar = true,
          PlaceholderText = "Leave blank to keep the existing token" };
    private readonly NumericUpDown _interval = new()
        { Minimum = 15, Maximum = 300, Dock = DockStyle.Left, Width = 90 };
    private readonly CheckBox _autostart = new() { Text = "Start with Windows", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Text = "Not connected" };

    public SettingsForm(BridgeConfig config, Action saved)
    {
        _config = config;
        _saved = saved;
        Text = "HA Windows Bridge — Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 320);
        Size = new Size(660, 350);

        _local.Text = config.LocalUrl;
        _external.Text = config.ExternalUrl;
        _interval.Value = Math.Clamp(config.IntervalSeconds, 15, 300);
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            _autostart.Checked = key?.GetValue("HAWindowsBridge") is not null;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7,
            Padding = new Padding(14), AutoSize = false
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 7; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 6 ? 50 : 37));

        AddRow(grid, 0, "Local HA URL", _local);
        AddRow(grid, 1, "External HA URL", _external);
        AddRow(grid, 2, "Access token", _token);
        AddRow(grid, 3, "Report every (s)", _interval);
        grid.Controls.Add(_autostart, 1, 4);
        grid.Controls.Add(_status, 1, 5);
        var save = new Button { Text = "Save and connect", Width = 140, Height = 32 };
        save.Click += (_, _) => SaveSettings();
        grid.Controls.Add(save, 1, 6);
        Controls.Add(grid);
        AcceptButton = save;
    }

    public void SetStatus(string status) => _status.Text = status;

    private static void AddRow(TableLayoutPanel grid, int row, string title, Control field)
    {
        grid.Controls.Add(new Label
        {
            Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        grid.Controls.Add(field, 1, row);
    }

    private void SaveSettings()
    {
        try
        {
            string local = BridgeConfig.NormalizeUrl(_local.Text, false);
            string external = BridgeConfig.NormalizeUrl(_external.Text, true);
            if (local.Length == 0 && external.Length == 0)
                throw new ArgumentException("Enter at least one Home Assistant URL.");
            if (_token.Text.Length == 0 && _config.TokenCipher.Length == 0)
                throw new ArgumentException("Enter a long-lived access token.");

            _config.LocalUrl = local;
            _config.ExternalUrl = external;
            _config.IntervalSeconds = (int)_interval.Value;
            if (_token.Text.Length > 0) _config.SetToken(_token.Text);
            _saved();

            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (_autostart.Checked)
                key.SetValue("HAWindowsBridge", "\"" + Environment.ProcessPath + "\"");
            else
                key.DeleteValue("HAWindowsBridge", false);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Settings could not be saved",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
