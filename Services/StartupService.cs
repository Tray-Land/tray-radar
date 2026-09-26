using Windows.ApplicationModel;

namespace TrayRadar.Services;

/// <summary>Wraps the MSIX "start with Windows" task declared in Package.appxmanifest.</summary>
internal static class StartupService
{
    private const string TaskId = "TrayRadarStartup";

    /// <summary>Returns the current state, or null when the app runs unpackaged.</summary>
    public static async Task<StartupTaskState?> GetStateAsync()
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            return task.State;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<StartupTaskState?> SetEnabledAsync(bool enabled)
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                return await task.RequestEnableAsync();
            }

            task.Disable();
            return task.State;
        }
        catch
        {
            return null;
        }
    }
}
