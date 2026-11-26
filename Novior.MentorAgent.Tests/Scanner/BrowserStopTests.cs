using System.ComponentModel;
using System.Diagnostics;
using FluentAssertions;
using Novior.MentorAgent.Scanner.Cdp;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Scanner;

/// <summary>
/// Closing the scan's browser does not fail on a helper that cannot be killed, and never leaves the job or the
/// profile behind (BUG-255).
///
/// <para>
/// A browser that did not exit within five seconds of being asked was killed with its whole process tree. A
/// helper already on its way out answers "access denied", and the kill reports it in an
/// <see cref="AggregateException"/>, which the stop did not expect: it left the browser's disposal, skipped
/// the job and the profile, and turned a scan that had worked into an error at its end.
/// </para>
/// </summary>
public sealed class BrowserStopTests
{
    [Fact]
    public void A_kill_that_could_not_reach_a_helper_on_its_way_out_is_not_an_error()
    {
        var stop = () => CdpBrowser.Stop(Process.GetCurrentProcess(), TimeSpan.Zero,
            _ => throw new AggregateException(new Win32Exception(5), new Win32Exception(5)));

        stop.Should().NotThrow("each process the kill could not reach was one that was exiting, or not ours");
    }

    [Fact]
    public void A_kill_that_failed_for_another_reason_too_is_still_an_error()
    {
        var stop = () => CdpBrowser.Stop(Process.GetCurrentProcess(), TimeSpan.Zero,
            _ => throw new AggregateException(new Win32Exception(5), new IOException("something else")));

        stop.Should().Throw<AggregateException>("only a kill whose every failure is harmless is let go");
    }

    [Fact]
    public void A_profile_whose_file_a_helper_holds_for_three_seconds_is_still_deleted()
    {
        // On a busy machine the helpers of a killed browser held its files longer than the 2 seconds the profile was
        // tried for, and the profile stayed until the next scan. Windows refuses to delete a file that is open.
        if (!OperatingSystem.IsWindows()) return;
        var profile = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mentor-profile-test-" + Guid.NewGuid().ToString("N"))).FullName;
        var held = new FileStream(Path.Combine(profile, "Cookies"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        // On a thread of its own: under the release gate's load a delay on the thread pool could come after the 5 seconds,
        // and the test failed with the code right.
        var release = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            held.Dispose();
        }) { IsBackground = true };
        release.Start();
        try
        {
            CdpBrowser.DeleteProfile(profile);

            Directory.Exists(profile).Should().BeFalse("the profile is tried for 5 seconds, longer than the helper holds it");
        }
        finally
        {
            release.Join();
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
    }

    [BrowserTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Rule", "page.scan-browser")]
    public async Task A_browser_that_does_not_stop_cleanly_still_closes_its_job_and_deletes_its_profile(bool harmless)
    {
        var browser = await CdpBrowser.StartAsync(CdpBrowser.Find(null)!, CancellationToken.None);
        var profile = browser.Profile!;
        browser.ExitWait = TimeSpan.Zero;   // not exited at once: killed
        browser.KillProcess = process =>
        {
            // Killed for real and exited, as the stop's own wait would see it; then the kill reports a failure.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
            throw harmless
                ? new AggregateException(new Win32Exception(5))
                : new InvalidDataException("a failure the stop does not know");
        };

        var dispose = async () => await browser.DisposeAsync();

        if (harmless)
            await dispose.Should().NotThrowAsync("a helper on its way out is not the scan's error");
        else
            await dispose.Should().ThrowAsync<InvalidDataException>("an unknown failure is not hidden");
        Directory.Exists(profile).Should().BeFalse("the profile goes also when the stop fails");
        if (OperatingSystem.IsWindows())
            browser.Job!.IsClosed.Should().BeTrue("the job closes also when the stop fails");
    }
}
