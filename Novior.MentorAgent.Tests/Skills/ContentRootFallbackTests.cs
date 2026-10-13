using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Skills;

/// <summary>
/// <see cref="MentorSkillsBuilder"/> on a host whose <see cref="IHostEnvironment"/> does not have a
/// content root (BUG-054).
///
/// <para>
/// Every web host has one. **.NET MAUI does not** — <c>MauiHostEnvironment.ContentRootPath</c>
/// throws <see cref="NotImplementedException"/>, because a packaged desktop application has no
/// content directory to point at. The builder read it eagerly in its constructor, so on MAUI the
/// exception escaped through the DI container while the <c>ChatWidget</c> was being constructed and
/// the whole Blazor renderer died: the app showed *"An unhandled error has occurred"* and nothing
/// else, whether or not skills were switched on.
/// </para>
///
/// <para>
/// The fake here is the point. Every other test in the suite runs under a host that has a content
/// root, so no amount of testing the skills feature could have found this — the defect is in what
/// the constructor assumes about its host, not in what the feature does.
/// </para>
/// </summary>
public sealed class ContentRootFallbackTests
{
    /// <summary>Reproduces MAUI's environment: everything works except the content root.</summary>
    private sealed class NoContentRootEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "MauiApp";
        public string EnvironmentName { get; set; } = "Development";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();

        public string ContentRootPath
        {
            get => throw new NotImplementedException("The method or operation is not implemented.");
            set { }
        }
    }

    private static MentorSkillsBuilder Build(Action<MentorOptions>? configure = null)
    {
        var options = new MentorOptions
        {
            AppName = "MauiApp",
            // Abstractions declares no skills of its own, so anything found comes from the folder
            // under test rather than from the shared fixture app.
            ScanAssemblies = [typeof(Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext).Assembly],
        };
        configure?.Invoke(options);

        return new MentorSkillsBuilder(
            Microsoft.Extensions.Options.Options.Create(options),
            new NoContentRootEnvironment(),
            NullLogger<MentorSkillsBuilder>.Instance,
            NullLoggerFactory.Instance);
    }

    [Fact]
    public void Constructing_it_does_not_throw_when_the_host_has_no_content_root()
    {
        // The failing line was `_contentRoot = env.ContentRootPath;` in the constructor, so the
        // exception arrived before any decision about skills had been taken — including the
        // decision not to use them.
        var act = () => Build();

        act.Should().NotThrow<NotImplementedException>(
            "a host without a content root is a supported host: MAUI is in the README's own " +
            "supported render modes table");
    }

    [Fact]
    public void Constructing_it_does_not_throw_when_skills_are_switched_on()
    {
        // Skills off was the configuration MAUI died in, but a MAUI host is equally entitled to
        // turn them on — the instructions can be embedded or absolute.
        var act = () => Build(o =>
        {
            o.EnableSkills = true;
            o.SkillsFolder = "Skills";
        });

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Skill_discovery_still_runs_and_falls_back_to_the_application_directory()
    {
        // The fallback has to be somewhere real, or the fix is only "it does not throw" and the
        // feature is quietly dead. AppContext.BaseDirectory is where a MAUI app's content files
        // land, so it is the right answer rather than merely a safe one.
        var dir = Path.Combine(AppContext.BaseDirectory, "MauiFallbackSkills");
        Directory.CreateDirectory(Path.Combine(dir, "promo"));
        await File.WriteAllTextAsync(
            Path.Combine(dir, "promo", "SKILL.md"),
            "# Promozioni\n\nCome applicare uno sconto stagionale al catalogo.\n");

        try
        {
            var builder = Build(o =>
            {
                o.EnableSkills = true;
                o.SkillsFolder = "MauiFallbackSkills";
            });

            var skills = await builder.BuildAsync();

            skills.Select(s => s.Name).Should().Contain("promo",
                "a relative SkillsFolder has to resolve against the application directory when the " +
                "host cannot say where its content root is");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
