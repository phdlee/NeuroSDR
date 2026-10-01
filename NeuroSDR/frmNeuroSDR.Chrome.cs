using NeuroSDR.Controls;
using NeuroSDR.Settings;
using System.Diagnostics;

namespace NeuroSDR;

public partial class frmNeuroSDR
{
    private AppChromeMenu? _appChromeMenu;

    private void AttachAppChrome()
    {
        Icon = AppIcons.Application;
        ShowIcon = true;
        Text = $"NeuroSDR {AppIcons.ProductVersion}";
        AppIcons.ApplySetButton(_setupButton);
        AppIcons.ApplyRxButton(_startButton, running: false);
        AppIcons.ApplyFindButton(_siteFindButton);
        AppIcons.ApplyFavoriteButton(_favoriteButton);
        AppIcons.ApplyOpenIqButton(_openIqButton);
        AppIcons.ApplyRecordButton(_recordButton, recording: false);
        AppIcons.ApplyMemoryButton(_memoryButton);
        EnsureAppChromeMenu();
        DarkNativeTheme.Apply(this);
    }

    private void EnsureAppChromeMenu()
    {
        if (_appChromeMenu is not null) return;
        _appChromeMenu = new AppChromeMenu(this);
        _appChromeMenu.AttachSettingsMenu(ExportSettingsFile, ImportSettingsFile, ResetSettingsToDefaults);
    }

    private void ExportSettingsFile()
    {
        try { CaptureSettingsFromUi(); }
        catch { /* still export last in-memory snapshot */ }

        using var dialog = new SaveFileDialog
        {
            Title = "Export NeuroSDR settings",
            Filter = "NeuroSDR settings (*.json)|*.json|JSON (*.json)|*.json",
            FileName = $"NeuroSDR-settings-{DateTime.Now:yyyyMMdd}.json",
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = "json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, AppSettingsStore.Serialize(_appSettings));
            _statusLabel.Text = $"Settings exported · {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not export settings.\r\n{ex.Message}", "NeuroSDR",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportSettingsFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import NeuroSDR settings",
            Filter = "NeuroSDR settings (*.json)|*.json|JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string json;
        try { json = File.ReadAllText(dialog.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not read the file.\r\n{ex.Message}", "NeuroSDR",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (!AppSettingsStore.TryLoadFromJson(json, out var imported, out var error))
        {
            MessageBox.Show(this, $"This is not a valid NeuroSDR settings file.\r\n{error}", "NeuroSDR",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (MessageBox.Show(this,
                "Import this settings file and restart NeuroSDR?\r\nCurrent settings will be replaced.",
                "Import settings",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            AppSettingsStore.Save(imported);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save imported settings.\r\n{ex.Message}", "NeuroSDR",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        RestartAfterSettingsFileChange();
    }

    private void ResetSettingsToDefaults()
    {
        if (MessageBox.Show(this,
                "Reset all NeuroSDR settings to factory defaults and restart?\r\nWindow, device, DSP, SCENE, and plugin settings will be cleared.",
                "Reset settings",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            AppSettingsStore.Reset();
            AppSettingsStore.Save(AppSettingsStore.FactoryDefaults());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not reset settings.\r\n{ex.Message}", "NeuroSDR",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        RestartAfterSettingsFileChange();
    }

    private void RestartAfterSettingsFileChange()
    {
        _settingsResetRequested = true;
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
            catch
            {
                MessageBox.Show(this,
                    "Settings were saved. Close and start NeuroSDR again to apply them.",
                    "NeuroSDR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        else
        {
            MessageBox.Show(this,
                "Settings were saved. Close and start NeuroSDR again to apply them.",
                "NeuroSDR", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        Close();
    }

    private void ShowAppChromeMenu()
    {
        EnsureAppChromeMenu();
        var x = Left + 8;
        var y = Top + SystemInformation.CaptionHeight;
        _appChromeMenu!.Show(new Point(x, y));
    }

    protected override void WndProc(ref Message m)
    {
        const int wmNcLButtonDown = 0x00A1;
        const int wmNcLButtonDblClk = 0x00A3;
        const int wmSysCommand = 0x0112;
        const int htSysMenu = 3;
        const int scMouseMenu = 0xF090;
        const int scKeyMenu = 0xF100;

        if (m.Msg is wmNcLButtonDown or wmNcLButtonDblClk && (int)m.WParam == htSysMenu)
        {
            ShowAppChromeMenu();
            return;
        }

        if (m.Msg == wmSysCommand)
        {
            var command = (int)m.WParam & 0xFFF0;
            if (command is scMouseMenu or scKeyMenu)
            {
                ShowAppChromeMenu();
                return;
            }
        }

        base.WndProc(ref m);
    }
}
