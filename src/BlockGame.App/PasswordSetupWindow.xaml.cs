using System.Globalization;
using System.Windows;
using BlockGame.Core.Models;
using BlockGame.Core.Services;

namespace BlockGame.App;

public partial class PasswordSetupWindow : Window
{
    private readonly ConfigStore _configStore;
    private readonly AuditLog _auditLog;

    public PasswordSetupWindow(ConfigStore configStore, AuditLog auditLog)
    {
        _configStore = configStore;
        _auditLog = auditLog;
        InitializeComponent();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Password != ConfirmPasswordBox.Password)
        {
            MessageBox.Show("两次输入的密码不一致。", "首次设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(DelayHoursTextBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out double hours)
            || hours < (1d / 60d)
            || hours > 24d * 30d)
        {
            MessageBox.Show("冷静期必须在 1 分钟到 30 天之间。", "首次设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            AppConfig config = _configStore.Load();
            config.Password = PasswordHasher.Create(PasswordBox.Password);
            config.PasswordThrottle = new PasswordThrottle();
            config.UnlockDelayMinutes = Math.Max(1, (int)Math.Round(hours * 60d));
            config.SetupCompleted = true;
            config.ProtectionEnabled = false;
            config.ProtectionLocked = false;
            _configStore.Save(config);
            _auditLog.Append(new AuditEntry
            {
                EventType = "SetupCompleted",
                Message = "首次设置已完成。"
            });
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "首次设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
