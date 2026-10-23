using System.Diagnostics;
using System.ComponentModel;
using Hako.Core.Playtime;

namespace Hako;

internal sealed class PlaytimeHelperLauncher : IPlaytimeHelperLauncher
{
    public Task<bool> TryStartAsync(string journalPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return Task.FromResult(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--playtime-helper");
        startInfo.ArgumentList.Add(journalPath);
        try
        {
            using var helper = Process.Start(startInfo);
            return Task.FromResult(helper is not null);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }
}
