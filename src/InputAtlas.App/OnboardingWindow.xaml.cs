using System.Windows;
using InputAtlas.Storage;

namespace InputAtlas.App;

public partial class OnboardingWindow : Window
{
    private readonly AppSettings _initial;

    public OnboardingWindow(AppSettings initial)
    {
        _initial = initial;
        Result = initial;
        InitializeComponent();
        LayoutChoice.SelectedIndex = initial.KeyboardLayout == KeyboardLayoutKind.Ansi104 ? 0 : 1;
        StartWithWindows.IsChecked = initial.StartWithWindows;
        KeepForever.IsChecked = initial.KeepFiveMinuteForever;
        UpdateButtons();
    }

    public AppSettings Result { get; private set; }

    private void BackClick(object sender, RoutedEventArgs e)
    {
        if (Steps.SelectedIndex > 0)
        {
            Steps.SelectedIndex--;
            UpdateButtons();
        }
    }

    private void NextClick(object sender, RoutedEventArgs e)
    {
        if (Steps.SelectedIndex < Steps.Items.Count - 1)
        {
            Steps.SelectedIndex++;
            UpdateButtons();
        }
    }

    private void StartClick(object sender, RoutedEventArgs e)
    {
        Result = _initial with
        {
            KeyboardLayout = LayoutChoice.SelectedIndex == 0 ? KeyboardLayoutKind.Ansi104 : KeyboardLayoutKind.Compact75,
            StartWithWindows = StartWithWindows.IsChecked == true,
            KeepFiveMinuteForever = KeepForever.IsChecked == true,
        };
        DialogResult = true;
    }

    private void UpdateButtons()
    {
        BackButton.IsEnabled = Steps.SelectedIndex > 0;
        var final = Steps.SelectedIndex == Steps.Items.Count - 1;
        NextButton.Visibility = final ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Visibility = final ? Visibility.Visible : Visibility.Collapsed;
    }
}

