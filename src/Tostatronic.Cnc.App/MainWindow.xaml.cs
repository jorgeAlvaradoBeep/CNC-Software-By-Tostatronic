using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tostatronic.Cnc.Core.Motion;

namespace Tostatronic.Cnc.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.ConsoleScrollRequested += () =>
        {
            if (ConsoleList.Items.Count > 0)
                ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
        };
    }

    private static (Axis Axis, int Dir)? ParseJogTag(object? tag) => (tag as string) switch
    {
        "X+" => (Axis.X, +1),
        "X-" => (Axis.X, -1),
        "Y+" => (Axis.Y, +1),
        "Y-" => (Axis.Y, -1),
        "Z+" => (Axis.Z, +1),
        "Z-" => (Axis.Z, -1),
        _ => null,
    };

    private void Jog_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button b && ParseJogTag(b.Tag) is { } j)
            _vm.JogPress(j.Axis, j.Dir);
    }

    private void Jog_Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button b && ParseJogTag(b.Tag) is { } j)
            _vm.JogRelease(j.Axis);
    }

    private void Jog_Leave(object sender, MouseEventArgs e)
    {
        if (sender is Button b && ParseJogTag(b.Tag) is { } j)
            _vm.JogRelease(j.Axis);
    }

    private void Step_Checked(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (sender is RadioButton rb)
        {
            string tag = rb.Tag as string ?? "";
            _vm.JogStepMm = tag.Length == 0
                ? null
                : double.Parse(tag, CultureInfo.InvariantCulture);
        }
    }

    private void Ports_DropDownOpened(object? sender, EventArgs e) => _vm.RefreshPortsCommand.Execute(null);

    private static (Axis Axis, int Dir)? KeyToJog(Key key) => key switch
    {
        Key.Left => (Axis.X, -1),
        Key.Right => (Axis.X, +1),
        Key.Up => (Axis.Y, +1),
        Key.Down => (Axis.Y, -1),
        Key.PageUp => (Axis.Z, +1),
        Key.PageDown => (Axis.Z, -1),
        _ => null,
    };

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;
        if (KeyToJog(e.Key) is not { } j) return;
        e.Handled = true;
        if (!e.IsRepeat)
            _vm.JogPress(j.Axis, j.Dir);
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (KeyToJog(e.Key) is not { } j) return;
        e.Handled = true;
        _vm.JogRelease(j.Axis);
    }

    private void OpenCam_Click(object sender, RoutedEventArgs e)
    {
        new CamWindow(_vm) { Owner = this }.Show();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _vm.Shutdown();
    }
}
