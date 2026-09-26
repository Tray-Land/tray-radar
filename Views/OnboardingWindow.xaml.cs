using Microsoft.UI.Xaml;
using TrayRadar.Controls;
using TrayRadar.Services;
using WinUIEx;

namespace TrayRadar.Views;

/// <summary>First-run window: US-only notice and location choice.</summary>
public sealed partial class OnboardingWindow : WindowEx
{
    private LocationChoice? _choice;

    public OnboardingWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/AppIcon.ico");
        WindowPlacementService.CenterOnPrimary(this, 480, 560);
    }

    /// <summary>Raised after the user finishes onboarding and settings are saved.</summary>
    public event EventHandler? Completed;

    private void Picker_LocationChosen(object? sender, LocationChoice choice)
    {
        _choice = choice;
        FinishButton.IsEnabled = true;
    }

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (_choice is not { } c)
        {
            return;
        }

        SettingsService.SetHome(c.Mode, c.Lat, c.Lon, c.Zip);
        SettingsService.IsOnboarded = true;
        Completed?.Invoke(this, EventArgs.Empty);
        Close();
    }
}
