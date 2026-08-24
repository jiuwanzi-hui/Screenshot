using System.Security;

namespace SnapCut.Mac.App;

internal static class MacLaunchAtStartupService
{
    private const string Label = "com.jiuwanzi.snapcut";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "LaunchAgents",
            Label + ".plist");
        if (!enabled)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 SnapCut 可执行文件路径。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var escaped = SecurityElement.Escape(executable);
        File.WriteAllText(
            path,
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Label}</string>
              <key>ProgramArguments</key>
              <array><string>{escaped}</string></array>
              <key>RunAtLoad</key><true/>
              <key>ProcessType</key><string>Interactive</string>
            </dict>
            </plist>
            """);
    }
}
