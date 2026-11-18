using System.Diagnostics;

namespace Assistant.Tests;

/// <summary>Creates directory links for tests that need a link escaping a folder.</summary>
internal static class DirectoryLinks
{
    /// <summary>
    /// Deletes a folder tree that may contain links. Links are removed first, on their own: a recursive delete
    /// cannot remove a junction, and must never be allowed to reach through one into its target.
    /// </summary>
    public static void DeleteTree(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateDirectories(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, ReturnSpecialDirectories = false }).ToList())
        {
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null) Directory.Delete(path, recursive: false);
        }
        Directory.Delete(folder, recursive: true);
    }

    /// <summary>
    /// Creates a directory link: a junction on Windows (needs no privilege, unlike a symlink), a symlink elsewhere.
    /// Returns false when the link cannot be made on this machine, so the caller can skip the real-disk check.
    /// </summary>
    public static bool TryCreate(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
