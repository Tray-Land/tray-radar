using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TrayRadar.Services;
using Windows.System;

namespace TrayRadar.Controls;

public sealed record LocationChoice(LocationMode Mode, double Lat, double Lon, string? Zip)
{
    public string Describe() => Mode == LocationMode.Zip
        ? $"ZIP {Zip}"
        : $"Device location ({Lat:0.00}, {Lon:0.00})";
}

/// <summary>Lets the user pick a location from the device or a US ZIP code (CONUS only).</summary>
public sealed partial class LocationPicker : UserControl
{
    public LocationPicker()
    {
        InitializeComponent();
    }

    /// <summary>Raised when a valid, in-coverage location has been chosen.</summary>
    public event EventHandler<LocationChoice>? LocationChosen;

    private async void DeviceButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Finding your location…");
        (double Lat, double Lon)? point = await LocationService.TryGetDeviceLocationAsync(
            TimeSpan.FromSeconds(15), error => StatusText.Text = error);
        SetBusy(false, point is null ? null : string.Empty);

        if (point is not { } p)
        {
            return;
        }

        if (!LocationService.IsInConus(p.Lat, p.Lon))
        {
            StatusText.Text = "Your location appears to be outside the contiguous United States, which Tray Radar doesn't cover yet.";
            return;
        }

        Choose(new LocationChoice(LocationMode.Device, p.Lat, p.Lon, null));
    }

    private async void ZipButton_Click(object sender, RoutedEventArgs e) => await UseZipAsync();

    private async void ZipBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await UseZipAsync();
        }
    }

    private async Task UseZipAsync()
    {
        string zip = ZipBox.Text.Trim();
        if (!ZipLookupService.IsWellFormed(zip))
        {
            StatusText.Text = "Enter a 5-digit US ZIP code.";
            return;
        }

        SetBusy(true, "Looking up ZIP code…");
        (double Lat, double Lon)? point = await ZipLookupService.TryLookupAsync(zip);
        SetBusy(false, string.Empty);

        if (point is not { } p)
        {
            StatusText.Text = $"ZIP {zip} wasn't found in the contiguous United States.";
            return;
        }

        Choose(new LocationChoice(LocationMode.Zip, p.Lat, p.Lon, zip));
    }

    private void Choose(LocationChoice choice)
    {
        StatusText.Text = $"Using {choice.Describe()}.";
        LocationChosen?.Invoke(this, choice);
    }

    private void SetBusy(bool busy, string? message = null)
    {
        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DeviceButton.IsEnabled = ZipButton.IsEnabled = ZipBox.IsEnabled = !busy;
        if (message is not null)
        {
            StatusText.Text = message;
        }
    }
}
