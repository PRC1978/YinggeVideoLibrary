using System.Windows.Threading;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    DispatcherTimer? settingsSaveTimer;
    bool settingsSaveQueued;
    bool settingsSaveInProgress;
    bool closeAfterSettingsSave;

    // A settings click updates the control immediately. The expensive config
    // merge and PostgreSQL write run after the next paint and coalesce rapid
    // changes, instead of blocking the UI thread for every toggle.
    void QueueSettingsSave()
    {
        // Invalidate any older asynchronous save before it can replace a newer
        // in-memory setting with its earlier snapshot.
        configSaveVersion++;
        settingsSaveQueued = true;
        if (settingsSaveInProgress) return;
        settingsSaveTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        settingsSaveTimer.Tick -= SettingsSaveTimerTick;
        settingsSaveTimer.Tick += SettingsSaveTimerTick;
        settingsSaveTimer.Stop();
        settingsSaveTimer.Start();
    }

    void SettingsSaveTimerTick(object? sender, EventArgs e)
    {
        settingsSaveTimer?.Stop();
        _ = DrainSettingsSavesAsync();
    }

    bool DelayCloseForSettingsSave()
    {
        if (!settingsSaveQueued && !settingsSaveInProgress) return false;
        closeAfterSettingsSave = true;
        settingsSaveTimer?.Stop();
        _ = DrainSettingsSavesAsync();
        return true;
    }

    async Task DrainSettingsSavesAsync()
    {
        if (settingsSaveInProgress) return;
        settingsSaveInProgress = true;
        try
        {
            // Give WPF a chance to paint the newly selected value first.
            await Dispatcher.Yield(DispatcherPriority.Background);
            while (settingsSaveQueued)
            {
                settingsSaveQueued = false;
                try { await SaveBatchConfigAsync(BatchChangeKind.Status); }
                catch (Exception ex)
                {
                    settingsSaveQueued = true;
                    closeAfterSettingsSave = false;
                    Log("设置保存失败：" + ex.Message);
                    ThemeMessageBox.Show(this, "设置未能保存，请重试：" + ex.Message, "映格视频库");
                    return;
                }
            }
        }
        finally
        {
            settingsSaveInProgress = false;
            if (closeAfterSettingsSave && !settingsSaveQueued)
            {
                closeAfterSettingsSave = false;
                _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
            }
        }
    }
}
