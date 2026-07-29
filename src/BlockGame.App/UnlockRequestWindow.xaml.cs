using System.Windows;
using BlockGame.Core.Services;

namespace BlockGame.App;

public partial class UnlockRequestWindow : Window
{
    public UnlockRequestWindow()
    {
        InitializeComponent();
        RequiredText.Text = ProtectionManager.UnlockConfirmationText;
        Loaded += (_, _) => ConfirmationTextBox.Focus();
    }

    public string ConfirmationText => ConfirmationTextBox.Text;

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
