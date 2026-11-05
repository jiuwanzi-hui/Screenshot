using System.Threading;
using System.IO;
using WinForms = System.Windows.Forms;
using Screenshot.App.Core;

namespace Screenshot.App.Capture;

internal static class PngSaveLocationService
{
    public static async Task<string?> ResolveAsync(
        PngSaveLocationMode mode,
        string defaultDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDirectory);
        if (mode != PngSaveLocationMode.AskEveryTime)
        {
            return defaultDirectory;
        }

        using var featureActivity = Infrastructure.FeatureActivity.Begin();
        return await RunOnStaThreadAsync(() =>
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = Presentation.UiLocalization.Get("选择 PNG 保存位置"),
                UseDescriptionForTitle = true,
            };
            if (Directory.Exists(defaultDirectory))
            {
                dialog.InitialDirectory = defaultDirectory;
            }

            return dialog.ShowDialog() == WinForms.DialogResult.OK
                ? dialog.SelectedPath
                : null;
        });
    }

    private static Task<T> RunOnStaThreadAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "SnapCut PNG Folder Picker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
