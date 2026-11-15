using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RadioDeck.ViewModels;

namespace RadioDeck;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private FrameworkElement? _pttMouseOwner;
    private bool _placed;

    public MainWindow() : this(new MainViewModel())
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        var author = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        VersionText.Text = string.IsNullOrWhiteSpace(author)
            ? MainViewModel.VersionText
            : $"{MainViewModel.VersionText} · {author}";

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        Deactivated += MainWindow_Deactivated;
        Closed += MainWindow_Closed;
        SizeChanged += (_, _) => KeepOnScreen();

        // The window follows its content; with many radios or a small screen the content is scaled down.
        _vm.PropertyChanged += ViewModel_PropertyChanged;
        _vm.Radios.CollectionChanged += Radios_CollectionChanged;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        FitToScreen();
        _vm.OnWindowLoaded();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsGridMode) or nameof(MainViewModel.GridColumns))
            Dispatcher.BeginInvoke(FitToScreen, DispatcherPriority.Loaded);
    }

    private void Radios_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(FitToScreen, DispatcherPriority.Loaded);

    private void FitToScreen()
    {
        var work = SystemParameters.WorkArea;
        var chromeWidth = Math.Max(0, ActualWidth - Root.ActualWidth * CurrentScale() - Root.Margin.Left - Root.Margin.Right);
        var chromeHeight = Math.Max(0, ActualHeight - Root.ActualHeight * CurrentScale() - Root.Margin.Top - Root.Margin.Bottom);

        Root.LayoutTransform = Transform.Identity;
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wanted = Root.DesiredSize;
        if (wanted.Width <= 0 || wanted.Height <= 0) return;

        var scale = Math.Min(1.0, Math.Min(
            (work.Width - chromeWidth - 16) / wanted.Width,
            (work.Height - chromeHeight - 16) / wanted.Height));
        if (scale < 0.999)
            Root.LayoutTransform = new ScaleTransform(Math.Max(0.4, scale), Math.Max(0.4, scale));

        Dispatcher.BeginInvoke(KeepOnScreen, DispatcherPriority.Loaded);
    }

    private double CurrentScale() => Root.LayoutTransform is ScaleTransform scale ? scale.ScaleX : 1.0;

    private void KeepOnScreen()
    {
        if (!IsLoaded || WindowState != WindowState.Normal || ActualWidth <= 0) return;
        var work = SystemParameters.WorkArea;
        if (!_placed)
        {
            // First show: at the right edge of the screen, vertically centred.
            _placed = true;
            Left = work.Right - ActualWidth - 8;
            Top = work.Top + Math.Max(8, (work.Height - ActualHeight) / 2);
            return;
        }

        if (Left + ActualWidth > work.Right) Left = Math.Max(work.Left, work.Right - ActualWidth - 8);
        if (Top + ActualHeight > work.Bottom) Top = Math.Max(work.Top, work.Bottom - ActualHeight - 8);
        if (Left < work.Left) Left = work.Left;
        if (Top < work.Top) Top = work.Top;
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        // Only reset mouse-based PTT on defocus (mouse capture is lost).
        // Keyboard PTT continues via GlobalKeyboardHook (WH_KEYBOARD_LL).
        ReleaseMousePtt();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Typing a frequency must work even when the PTT key is a character key.
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            return;
        }

        if (_vm.IsPttKey(e.Key == Key.System ? e.SystemKey : e.Key))
            e.Handled = true;
    }

    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (_vm.IsPttKey(e.Key == Key.System ? e.SystemKey : e.Key))
            e.Handled = true;
    }

    private void PttButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SiteViewModel radio)
        {
            ReleaseMousePtt();
            if (!radio.CanPtt || !fe.CaptureMouse()) return;
            _pttMouseOwner = fe;
            fe.LostMouseCapture += PttButton_LostMouseCapture;
            _vm.SetMousePtt(radio);
            e.Handled = true;
        }
    }

    private void PttButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMousePtt();
        e.Handled = true;
    }

    private void PttButton_LostMouseCapture(object sender, MouseEventArgs e) => ReleaseMousePtt();

    private void ReleaseMousePtt()
    {
        var owner = _pttMouseOwner;
        _pttMouseOwner = null;
        if (owner != null)
        {
            owner.LostMouseCapture -= PttButton_LostMouseCapture;
            if (owner.IsMouseCaptured) owner.ReleaseMouseCapture();
        }
        _vm.SetMousePtt(null);
    }

    private void FrequencyText_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (sender is FrameworkElement fe && fe.DataContext is SiteViewModel radio)
        {
            radio.BeginFrequencyEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void FrequencyEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible) return;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void FrequencyEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SiteViewModel radio)
            return;

        if (e.Key == Key.Enter)
        {
            if (radio.SetFrequencyCommand.CanExecute(null))
                radio.SetFrequencyCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (radio.CancelFrequencyEditCommand.CanExecute(null))
                radio.CancelFrequencyEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= ViewModel_PropertyChanged;
        _vm.Radios.CollectionChanged -= Radios_CollectionChanged;
        foreach (Window window in Application.Current.Windows)
            if (!ReferenceEquals(window, this)) window.Close();
        _vm.Dispose();
    }
}
