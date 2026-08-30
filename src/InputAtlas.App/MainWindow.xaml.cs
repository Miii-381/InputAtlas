using System.ComponentModel;
using System.Windows;
using InputAtlas.Core;
using InputAtlas.Storage;

namespace InputAtlas.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private bool _trayNoticeShown;

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    public void AllowClose()
    {
        _allowClose = true;
        Close();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        if (!_trayNoticeShown)
        {
            _trayNoticeShown = true;
            MessageBox.Show("InputAtlas 已隐藏到托盘并继续记录。可从托盘菜单真正退出。", "InputAtlas");
        }
    }

    private void KeyboardSelected(InputId input) => _viewModel.SelectInput(input);

    private async void AnsiLayoutClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ChangeLayoutAsync(KeyboardLayoutKind.Ansi104);

    private async void CompactLayoutClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ChangeLayoutAsync(KeyboardLayoutKind.Compact75);

    private async void ToggleCaptureClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ToggleCaptureAsync();

    private async void SaveNowClick(object sender, RoutedEventArgs e) =>
        await _viewModel.SaveNowAsync();

    private async void ExportClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ExportTodayAsync();

    private async void BackupClick(object sender, RoutedEventArgs e) =>
        await _viewModel.CreateBackupAsync();

    private async void RestoreClick(object sender, RoutedEventArgs e) =>
        await _viewModel.RestoreBackupAsync();

    private async void DeleteRangeClick(object sender, RoutedEventArgs e) =>
        await _viewModel.DeleteSelectedHistoryAsync();

    private async void AutostartClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox checkBox)
        {
            await _viewModel.SetStartWithWindowsAsync(checkBox.IsChecked == true);
        }
    }
}
