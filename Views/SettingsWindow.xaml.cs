using System.Windows;
using System.Windows.Input;
using RadioDeck.Services.Localization;
using RadioDeck.ViewModels;

namespace RadioDeck.Views;

public partial class SettingsWindow : Window
{
    private bool _capturingKey;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void CapturePttKey_Click(object sender, RoutedEventArgs e)
    {
        _capturingKey = true;
        CaptureHint.Text = Loc.T("Ptt_CaptureHint");
        Focus();
    }

    private void StopCapture(string message = "")
    {
        _capturingKey = false;
        CaptureHint.Text = message;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturingKey)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };

        if (key == Key.Escape)
        {
            StopCapture();
            return;
        }

        var name = key.ToString();
        if (!SettingsViewModel.TryParseKey(name, out _))
        {
            StopCapture(Loc.T("Ptt_KeyUnusable"));
            return;
        }

        if (DataContext is SettingsViewModel vm) vm.PttKey = name;
        StopCapture();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        if (_capturingKey) StopCapture();
        base.OnDeactivated(e);
    }

    private void NewPin_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.NewPin = NewPinBox.Password;
    }

    private void NewPinRepeat_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.NewPinRepeat = NewPinRepeatBox.Password;
    }
}
