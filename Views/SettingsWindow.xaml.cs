using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TrayRadar.Controls;
using TrayRadar.Services;
using Windows.ApplicationModel;
using WinUIEx;

namespace TrayRadar.Views;

public sealed partial class SettingsWindow : WindowEx
{
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/AppIcon.ico");
        WindowPlacementService.CenterOnPrimary(this, 480, 680);

        UpdateCurrentLocationText();
        StyleBox.SelectedIndex = (int)SettingsService.BasemapStyle;
        OpacitySlider.Value = Math.Round(SettingsService.RadarOpacity * 100);
        LocationPinToggle.IsOn = SettingsService.ShowLocationPin;
        _ = LoadStartupStateAsync();
        _loading = false;
    }

    private void UpdateCurrentLocationText()
    {
        CurrentLocationText.Text = SettingsService.LocationMode switch
        {
            LocationMode.Zip when SettingsService.Zip is { } zip => $"Currently using ZIP {zip}.",
            LocationMode.Device => "Currently using your device location.",
            _ => "No location set.",
        };
    }

    private void Picker_LocationChosen(object? sender, LocationChoice choice)
    {
        SettingsService.SetHome(choice.Mode, choice.Lat, choice.Lon, choice.Zip);
        UpdateCurrentLocationText();
    }

    private void StyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && StyleBox.SelectedIndex >= 0)
        {
            SettingsService.BasemapStyle = (BasemapStyle)StyleBox.SelectedIndex;
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading)
        {
            SettingsService.RadarOpacity = e.NewValue / 100.0;
        }
    }

    private void LocationPinToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            SettingsService.ShowLocationPin = LocationPinToggle.IsOn;
        }
    }

    private void ResetViewButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.ResetDefaultView();
        ResetDoneText.Text = "Default view reset to your location.";
    }

    private async Task LoadStartupStateAsync()
    {
        StartupTaskState? state = await StartupService.GetStateAsync();
        _loading = true;
        ApplyStartupState(state);
        _loading = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        StartupTaskState? state = await StartupService.SetEnabledAsync(StartupToggle.IsOn);
        _loading = true;
        ApplyStartupState(state);
        _loading = false;
    }

    private void ApplyStartupState(StartupTaskState? state)
    {
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;

        string? message = state switch
        {
            null => "Start with Windows is only available when Tray Radar is installed as a package.",
            StartupTaskState.DisabledByUser => "Startup was turned off in Task Manager or Windows Settings > Apps > Startup. Turn it back on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "This setting is managed by your organization.",
            _ => null,
        };
        StartupStatusText.Text = message ?? string.Empty;
        StartupStatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
