using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TrayRadar.Controls;
using TrayRadar.Services;
using Windows.ApplicationModel;

namespace TrayRadar.Views;

/// <summary>Settings shown inside the radar flyout, over the map. Changes apply immediately.</summary>
public sealed partial class SettingsPage : Page
{
    private readonly Action _goBack;
    private bool _loading = true;

    public SettingsPage(Action goBack)
    {
        _goBack = goBack;
        InitializeComponent();

        VersionText.Text = $"Tray Radar {GetVersionText()}";
        UpdateCurrentLocationText();
        StyleBox.SelectedIndex = (int)SettingsService.BasemapStyle;
        OpacitySlider.Value = Math.Round(SettingsService.RadarOpacity * 100);
        LocationPinToggle.IsOn = SettingsService.ShowLocationPin;
        _loading = false;
    }

    /// <summary>Re-reads state that can change outside the app (startup may be toggled in Windows Settings).</summary>
    public void OnShown()
    {
        ResetDoneText.Text = string.Empty;
        _ = LoadStartupStateAsync();
        BackButton.Focus(FocusState.Programmatic);
    }

    private static string GetVersionText()
    {
        try
        {
            PackageVersion v = Package.Current.Id.Version;
            return $"version {v.Major}.{v.Minor}.{v.Build}";
        }
        catch (InvalidOperationException)
        {
            // Not running with package identity.
            return $"version {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "unknown"}";
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _goBack();

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
