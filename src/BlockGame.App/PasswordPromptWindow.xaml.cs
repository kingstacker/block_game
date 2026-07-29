using System.Windows;
using System.Windows.Input;

namespace BlockGame.App;

public partial class PasswordPromptWindow : Window
{
    public PasswordPromptWindow(string actionDescription)
    {
        InitializeComponent();
        ActionDescriptionText.Text = actionDescription;
        Loaded += (_, _) => InputPasswordBox.Focus();
    }

    public string Password => InputPasswordBox.Password;

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(InputPasswordBox.Password))
        {
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ConfirmButton_Click(sender, e);
        }
    }
}
