using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BlockGame.Uninstall;

public partial class TransientPasswordBox : UserControl
{
    public TransientPasswordBox()
    {
        InitializeComponent();
        Unloaded += (_, _) => HidePlainPassword(releaseMouseCapture: true);
    }

    public string Password => MaskedPasswordBox.Password;

    public void Clear()
    {
        HidePlainPassword(releaseMouseCapture: true);
        MaskedPasswordBox.Clear();
    }

    public new bool Focus() => MaskedPasswordBox.Focus();

    private void MaskedPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (PlainPasswordSurface.Visibility == Visibility.Visible)
        {
            PlainPasswordText.Text = MaskedPasswordBox.Password;
        }
    }

    private void RevealButton_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        PlainPasswordText.Text = MaskedPasswordBox.Password;
        PlainPasswordSurface.Visibility = Visibility.Visible;
        RevealButton.Tag = "Revealed";
        _ = RevealButton.CaptureMouse();
        e.Handled = true;
    }

    private void RevealButton_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        HidePlainPassword(releaseMouseCapture: true);
        e.Handled = true;
    }

    private void RevealButton_LostMouseCapture(object sender, MouseEventArgs e)
        => HidePlainPassword(releaseMouseCapture: false);

    private void RevealButton_MouseLeave(object sender, MouseEventArgs e)
        => HidePlainPassword(releaseMouseCapture: true);

    private void HidePlainPassword(bool releaseMouseCapture)
    {
        PlainPasswordSurface.Visibility = Visibility.Collapsed;
        PlainPasswordText.Text = string.Empty;
        RevealButton.Tag = null;
        if (releaseMouseCapture && RevealButton.IsMouseCaptured)
        {
            RevealButton.ReleaseMouseCapture();
        }
    }
}
