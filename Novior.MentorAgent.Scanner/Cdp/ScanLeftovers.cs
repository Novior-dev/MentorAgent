using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Novior.MentorAgent.Scanner.Cdp;

/// <summary>
/// Story 1636, slice B — what a scan leaves when its application dies without stopping: the browser, where no job
/// object closed it (<see cref="BrowserJob"/>), and its profile folder. Each profile holds a note of the application
/// that started the browser and of the browser, each by process id and start time. Before it starts its own browser,
/// the next scan closes the browser of a profile whose application is gone and deletes the profile; the profile of an
/// application that still runs is left alone. A process is recognised by its id and its start time together, so a
/// process that took the id of one that ended is never closed. On Linux and macOS the profile is readable by its user
/// only: a scan reads no note of another user's, and closes nothing of theirs.
/// </summary>
internal static class ScanLeftovers
{
    internal const string Prefix = "mentor-ui-scan-";
    internal const string NoteName = "mentor-ui-scan.owner";

    /// <summary>A profile without a note is a leftover once it is this old: its browser started and wrote none.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    private enum State { Running, Gone, Unknown }

    /// <summary>Creates a profile folder under <paramref name="root"/>, readable by this user only where that applies.</summary>
    internal static string CreateProfile(string root)
    {
        var profile = Path.Combine(root, Prefix + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(profile);
        else Directory.CreateDirectory(profile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return profile;
    }

    /// <summary>Notes in <paramref name="profile"/> this application and <paramref name="browser"/>.</summary>
    internal static void Note(string profile, Process browser)
    {
        using var self = Process.GetCurrentProcess();
        Note(profile, self.Id, StartTicks(self), browser.Id, StartTicks(browser));
    }

    internal static void Note(string profile, int owner, long ownerStart, int browser, long browserStart)
        => File.WriteAllLines(Path.Combine(profile, NoteName),
            [.. new long[] { owner, ownerStart, browser, browserStart }.Select(v => v.ToString(CultureInfo.InvariantCulture))]);

    /// <summary>
    /// Closes the browsers of the profiles under <paramref name="root"/> whose application is gone, and deletes those
    /// profiles; how many browsers it closed and profiles it deleted.
    /// </summary>
    internal static (int Closed, int Deleted) CleanUp(string root) => CleanUp(root, Process.GetProcessById);

    /// <summary>As <see cref="CleanUp(string)"/>, with <paramref name="byId"/> to find a process by its id: for the tests.</summary>
    internal static (int Closed, int Deleted) CleanUp(string root, Func<int, Process> byId)
    {
        int closed = 0, deleted = 0;
        string[] profiles;
        try
        {
            profiles = Directory.GetDirectories(root, Prefix + "*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }

        foreach (var profile in profiles)
        {
            if (Read(profile) is { } note)
            {
                if (StateOf(note[0], note[1], byId) != State.Gone) continue;   // its application still runs, or it is not ours
                if (Close(note[2], note[3], byId)) closed++;
            }
            else if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(profile) < Grace)
            {
                continue;   // a browser that is starting now
            }
            if (Delete(profile)) deleted++;
        }
        return (closed, deleted);
    }

    private static long[]? Read(string profile)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(profile, NoteName));
            if (lines.Length < 4) return null;
            var values = new long[4];
            for (var i = 0; i < 4; i++)
                if (!long.TryParse(lines[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) return null;
            return values;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The process with this id and this start time, held by a handle opened before its start time is read: while the
    /// handle is held, Windows gives the id to no other process, so what is checked is what is closed. <see langword="null"/>
    /// when no such process runs, or it cannot be read (<paramref name="state"/> says which).
    /// </summary>
    private static Process? Find(long id, long start, Func<int, Process> byId, out State state)
    {
        state = State.Unknown;
        if (id is <= 0 or > int.MaxValue) return null;
        Process process;
        try
        {
            process = byId((int)id);
        }
        catch (ArgumentException)
        {
            state = State.Gone;   // no process has that id
            return null;
        }

        try
        {
            _ = process.SafeHandle;
            state = process.HasExited ? State.Gone
                : Math.Abs(StartTicks(process) - start) < TimeSpan.TicksPerSecond ? State.Running : State.Gone;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            state = State.Unknown;   // another user's process, one that cannot be read, or one that ended meanwhile
        }

        if (state == State.Running) return process;
        process.Dispose();
        return null;
    }

    private static State StateOf(long id, long start, Func<int, Process> byId)
    {
        using var process = Find(id, start, byId, out var state);
        return state;
    }

    private static bool Close(long id, long start, Func<int, Process> byId)
    {
        // One process, checked and closed: never a second look-up of the id, which could find another process by then.
        using var process = Find(id, start, byId, out _);
        if (process is null) return false;
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool Delete(string profile)
    {
        // The browser's helpers may hold a file for a moment after it exits.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(profile, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
        return false;
    }

    private static long StartTicks(Process process) => process.StartTime.ToUniversalTime().Ticks;
}
