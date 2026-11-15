using System.Windows;
using RadioDeck.Services.Localization;

namespace RadioDeck.Views;

/// <summary>Asks for the settings PIN; closes after a limited number of wrong entries.</summary>
public partial class PinDialog : Window
{
    private readonly Func<string, bool> _verify;
    private int _attemptsLeft;

    public PinDialog(Func<string, bool> verify, int attempts)
    {
        InitializeComponent();
        _verify = verify;
        _attemptsLeft = attempts;
        ContentRendered += (_, _) => PinBox.Focus();
    }

    /// <summary>The dialog closed because every attempt was wrong.</summary>
    public bool AttemptsExhausted { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_verify(PinBox.Password))
        {
            DialogResult = true;
            return;
        }

        PinBox.Clear();
        if (--_attemptsLeft <= 0)
        {
            AttemptsExhausted = true;
            DialogResult = false;
            return;
        }

        ErrorText.Text = _attemptsLeft == 1 ? Loc.T("Pin_WrongOne") : Loc.T("Pin_WrongMany", _attemptsLeft);
        PinBox.Focus();
    }
}
