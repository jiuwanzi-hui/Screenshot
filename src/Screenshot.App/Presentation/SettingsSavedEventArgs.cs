using Screenshot.App.Core;

namespace Screenshot.App.Presentation;

public sealed class SettingsSavedEventArgs : EventArgs
{
    public SettingsSavedEventArgs(AppSettings settings)
    {
        Settings = settings;
    }

    public AppSettings Settings { get; }
}
