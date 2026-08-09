using System.Windows;

namespace DesktopSonos;

public partial class PasswordWindow : Window
{
    private PasswordWindow(string share, string user)
    {
        InitializeComponent();
        PromptText.Text = $"Enter the password for {share}.";
        UserBox.Text = user;
    }

    private string? Result { get; set; }

    /// <summary>Returns the entered password, or null if the user cancelled.</summary>
    public static string? Prompt(Window owner, string share, string user)
    {
        var window = new PasswordWindow(share, user) { Owner = owner };
        window.ShowDialog();
        return window.Result;
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        Result = PasswordEntry.Password;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
