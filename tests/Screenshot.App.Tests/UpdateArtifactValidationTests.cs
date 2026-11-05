using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Screenshot.App.Tests;

public sealed class UpdateArtifactValidationTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing7z", false)]
    [InlineData("wrongGiteeTag", false)]
    [InlineData("wrongHash", false)]
    [InlineData("wrongSize", false)]
    [InlineData("staleHistory", false)]
    [InlineData("legacyMismatch", false)]
    [InlineData("localTest", true)]
    [InlineData("localTestWrongHash", false)]
    [InlineData("localTestAsStable", false)]
    [InlineData("stableAsLocalTest", false)]
    [InlineData("localTestStableHistory", false)]
    public async Task PackagingRejectsIncompleteOrInconsistentUpdateArtifacts(string scenario, bool expectedSuccess)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "installer/verify-update-artifacts.ps1"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-update-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = new JsonObject { ["version"] = "4.0.2" };
            var isLocalTest = scenario.StartsWith("localTest", StringComparison.Ordinal);
            if (isLocalTest) manifest["prerelease"] = true;
            var assets = new JsonArray { new JsonObject { ["name"] = "SnapCut-Update.json" }, new JsonObject { ["name"] = "Screenshot-Update.json" } };
            foreach (var (kind, name) in new[] { ("installer", "SnapCut-Setup-4.0.2-win-x64.exe"),
                ("portable", "SnapCut-Portable-4.0.2-win-x64.zip"), ("portable7z", "SnapCut-Portable-4.0.2-win-x64.7z") })
            {
                var bytes = "test release artifact"u8.ToArray();
                await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
                manifest[kind] = new JsonObject { ["fileName"] = name, ["size"] = bytes.Length,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)),
                    ["githubUrl"] = "https://github.com/jiuwanzi-hui/Screenshot/releases/download/v4.0.2/" + name,
                    ["giteeUrl"] = "https://gitee.com/wwangyunhui/screenshot/releases/download/v4.0.2/" + name };
                assets.Add(new JsonObject { ["name"] = name });
            }
            if (scenario == "missing7z") File.Delete(Path.Combine(directory, manifest["portable7z"]!["fileName"]!.GetValue<string>()));
            if (scenario == "wrongGiteeTag") manifest["installer"]!["giteeUrl"] = "https://gitee.com/old.exe";
            if (scenario is "wrongHash" or "localTestWrongHash") manifest["installer"]!["sha256"] = new string('0', 64);
            if (scenario == "wrongSize") manifest["installer"]!["size"] = 99;
            await File.WriteAllTextAsync(Path.Combine(directory, "SnapCut-Update.json"), manifest.ToJsonString());
            await File.WriteAllTextAsync(Path.Combine(directory, "Screenshot-Update.json"), scenario == "legacyMismatch" ? "{}" : manifest.ToJsonString());
            var history = new JsonArray
            {
                new JsonObject { ["tag_name"] = scenario == "staleHistory" ? "v4.0.1" : "v4.0.2", ["assets"] = assets,
                    ["prerelease"] = isLocalTest && scenario != "localTestStableHistory",
                    ["body"] = "更新截图、翻译与工具栏；中文、English、العربية。" },
                new JsonObject { ["tag_name"] = "v4.0.0", ["draft"] = false, ["prerelease"] = false },
            };
            // Real release histories have multiple entries and literal UTF-8
            // text without a BOM, including under Windows PowerShell 5.1.
            await File.WriteAllTextAsync(Path.Combine(directory, "SnapCut-Releases.json"), history.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            // Windows PowerShell must discover its own modules rather than
            // inherit PowerShell 7's module directories from the build shell.
            start.Environment.Remove("PSModulePath");
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(root.FullName, "installer/verify-update-artifacts.ps1"), "-Version", "4.0.2", "-Directory", directory }) start.ArgumentList.Add(arg);
            if ((isLocalTest && scenario != "localTestAsStable") || scenario == "stableAsLocalTest") start.ArgumentList.Add("-LocalTest");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True((process.ExitCode == 0) == expectedSuccess, await output + await error);
        }
        finally { Directory.Delete(directory, true); }
    }
}
