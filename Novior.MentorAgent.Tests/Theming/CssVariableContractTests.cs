using System.Text.RegularExpressions;
using FluentAssertions;
using Novior.MentorAgent.Tests.Docs;
using Xunit;

namespace Novior.MentorAgent.Tests.Theming;

/// <summary>
/// The theming contract: the CSS custom properties the README publishes, against the stylesheet
/// that is supposed to declare them and the code that is supposed to read them.
///
/// <para>
/// B18 covered theming from the component's side — which override block each <c>MentorTheme</c>
/// emits, and what a <c>PrimaryColor</c> derives. This is the other half, and it is the half a
/// component test cannot see: bUnit renders markup, never a stylesheet, so a rule reading a variable
/// that nothing declares renders exactly like one that works.
/// </para>
///
/// <para>
/// That failure mode is the reason this file exists. <c>var(--x)</c> with no declaration and no
/// fallback is <em>invalid at computed-value time</em>: the browser does not ignore the declaration
/// and keep the previous value, it sets the property to <c>unset</c>. For an inherited property like
/// <c>color</c> that means <c>inherit</c> — so a typo'd or never-declared variable does not produce a
/// broken-looking page, it produces a plausible one in the wrong colour, with nothing in the console.
/// </para>
///
/// <para>
/// The README calls its reference table "the complete set", and for <c>MentorTheme.Custom</c> it is
/// "also the complete list of what you are responsible for". That is a hard claim in both directions:
/// a documented variable that is not declared is a setting with no effect, and a declared variable
/// that is not documented is one a Custom-theme host will never know to set.
/// </para>
/// </summary>
public sealed class CssVariableContractTests
{
    // ── The stylesheet ──────────────────────────────────────────────────────────

    private static readonly string CssPath =
        Path.Combine(RepoFiles.Root, RepoFiles.Abstractions, "wwwroot", "css", "MentorAgent.css");

    private static readonly string Css = File.ReadAllText(CssPath);

    /// <summary>
    /// The body of the first block whose selector matches, brace-balanced so a nested block
    /// (a media query wrapping a rule) does not truncate at the inner close.
    /// </summary>
    private static string Block(string css, string selector)
    {
        var head = new Regex($@"(?m)^\s*{Regex.Escape(selector)}\s*\{{").Match(css);
        head.Success.Should().BeTrue($"the stylesheet should contain a '{selector}' block");

        var depth = 0;
        for (var i = head.Index + head.Length - 1; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0)
                return css[(head.Index + head.Length)..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{selector}'.");
    }

    /// <summary>Custom-property declarations in a block — name → value, comments and nesting aside.</summary>
    private static Dictionary<string, string> Declarations(string block)
        => Regex.Matches(block, @"(?m)^\s*(--bm-[a-z0-9-]+)\s*:\s*([^;]+);")
                .ToDictionary(m => m.Groups[1].Value, m => Normalise(m.Groups[2].Value));

    /// <summary>
    /// Whitespace and case folded away, and a leading zero added to bare decimals — CSS treats
    /// <c>.55</c> and <c>0.55</c> as the same value, and the README and the stylesheet each pick a
    /// style. Comparing them literally would report a difference that does not exist.
    /// </summary>
    private static string Normalise(string value)
        => Regex.Replace(
            Regex.Replace(value.Trim(), @"\s+", "").ToLowerInvariant(),
            @"(?<=^|[(,:\s])\.(?=\d)", "0.");

    private static readonly Dictionary<string, string> RootVars = Declarations(Block(Css, ":root"));

    private static readonly string DashBlock = Block(Css, ".bm-dash");
    private static readonly string DarkSchemeBlock = Block(Css, "@media (prefers-color-scheme: dark)");

    /// <summary>
    /// Every custom property the five packages actually <em>read</em> — from the stylesheet's own
    /// rules and from inline styles in the components, which is where the chart series colours live.
    /// </summary>
    private static readonly HashSet<string> Consumed =
        [.. Regex.Matches(RepoFiles.SourceText, @"var\(\s*(--bm-[a-z0-9-]+)")
                 .Select(m => m.Groups[1].Value)];

    private static readonly HashSet<string> DeclaredAnywhere =
        [.. Regex.Matches(Css, @"(?m)^\s*(--bm-[a-z0-9-]+)\s*:")
                 .Select(m => m.Groups[1].Value)];

    // ── The README ──────────────────────────────────────────────────────────────

    private static readonly string Readme = RepoFiles.Readme("core");

    /// <summary>The slice of the core README between two headings, so each table is read in isolation.</summary>
    private static string Section(string from, string to)
    {
        var start = Readme.IndexOf(from, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"the README should still have a '{from}' heading");
        var end = Readme.IndexOf(to, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(-1, $"the README should still have a '{to}' heading after '{from}'");
        return Readme[start..end];
    }

    /// <summary>Table rows of the form <c>| `--bm-x` | `value` | … |</c> — name → the first value cell.</summary>
    private static Dictionary<string, string?> DocumentedRows(string section)
        => Regex.Matches(section, @"(?m)^\|\s*`(--bm-[a-z0-9-]+)`\s*\|\s*([^|]*)\|")
                .ToDictionary(
                    m => m.Groups[1].Value,
                    m => Backtick(m.Groups[2].Value));

    /// <summary>The backticked literal in a cell, or null when the cell is prose (<c>*(3-layer)*</c>).</summary>
    private static string? Backtick(string cell)
    {
        var m = Regex.Match(cell, "`([^`]+)`");
        return m.Success ? Normalise(m.Groups[1].Value) : null;
    }

    private static readonly Dictionary<string, string?> DocumentedWidget =
        DocumentedRows(Section("### CSS variables — full reference",
                               "### The admin dashboard is themed separately"));

    private static readonly string DashSection =
        Section("### The admin dashboard is themed separately", "\n---");

    // ── Widget variables: the two directions of "the complete set" ──────────────

    [Fact]
    public void The_readme_and_the_stylesheet_agree_on_what_the_variables_are()
    {
        // Guards every other test in this file: both sides are parsed, so a regex that silently
        // matched nothing would make the whole suite pass vacuously.
        DocumentedWidget.Should().HaveCountGreaterThan(25);
        RootVars.Should().HaveCountGreaterThan(25);
    }

    [Fact]
    public void Every_documented_variable_is_declared_on_root()
    {
        // A documented variable the stylesheet never declares is a setting with no effect: the host
        // overrides it, nothing reads it, and the README said it would work.
        var missing = DocumentedWidget.Keys.Where(v => !RootVars.ContainsKey(v)).Order();

        string.Join(", ", missing).Should().BeEmpty();
    }

    [Fact]
    public void Every_declared_variable_is_documented()
    {
        // The other direction, and the one that matters for MentorTheme.Custom — the README calls the
        // table "the complete list of what you are responsible for", so an undocumented variable is
        // one a Custom-theme host cannot know to set and will find unstyled.
        var undocumented = RootVars.Keys.Where(v => !DocumentedWidget.ContainsKey(v)).Order();

        string.Join(", ", undocumented).Should().BeEmpty();
    }

    [Fact]
    public void Every_documented_default_is_the_value_the_stylesheet_actually_uses()
    {
        // The defaults are quoted as fact. A host reads `--bm-panel-w: 400px` and sizes a layout
        // around it; if the stylesheet says 380 the README has cost them a debugging session.
        var wrong = DocumentedWidget
            .Where(d => d.Value is not null && RootVars.TryGetValue(d.Key, out var actual)
                        && actual != d.Value)
            .Select(d => $"{d.Key}: README says {d.Value}, stylesheet says {RootVars[d.Key]}")
            .Order();

        string.Join(Environment.NewLine, wrong).Should().BeEmpty();
    }

    // ── The failure a component test cannot see ─────────────────────────────────

    [Fact]
    public void Every_variable_the_packages_read_is_declared_somewhere()
    {
        // The headline. `var(--x)` with no declaration and no fallback is invalid at computed-value
        // time, so the property becomes `unset` — for `color`, that is `inherit`. The element does
        // not lose its colour, it silently takes its parent's, and no tool reports it.
        var undeclared = Consumed.Where(v => !DeclaredAnywhere.Contains(v)).Order();

        string.Join(", ", undeclared).Should().BeEmpty();
    }

    [Fact]
    public void Every_declared_variable_is_read_by_something()
    {
        // A variable in the reference table that nothing consumes is the same broken promise seen
        // from the other end: the host sets it, the widget looks identical. Inline styles in the
        // components count — that is where the chart series colours are read.
        var unread = DeclaredAnywhere.Where(v => !Consumed.Contains(v)).Order();

        string.Join(", ", unread).Should().BeEmpty();
    }

    // ── Theme overrides land on variables that exist ────────────────────────────

    [Fact]
    public void Every_variable_a_theme_override_emits_is_declared_by_the_stylesheet()
    {
        // BuildThemeVars writes an inline <style> block. A name that drifted from the stylesheet's
        // would override nothing at all, and the theme would look like the default — which is
        // exactly what a reader would assume "Default" means and never question.
        var widget = RepoFiles.SourceFiles.Single(f => f.EndsWith("ChatWidget.razor.cs", StringComparison.Ordinal));
        var emitted = Regex.Matches(File.ReadAllText(widget), @"(--bm-[a-z0-9-]+):")
                           .Select(m => m.Groups[1].Value)
                           .Distinct()
                           .Order()
                           .ToList();

        emitted.Should().HaveCountGreaterThan(10, "the dark and minimal themes both override a palette");

        string.Join(", ", emitted.Where(v => !DeclaredAnywhere.Contains(v))).Should().BeEmpty();
    }

    [Fact]
    public void The_primary_colour_derives_exactly_the_variables_the_readme_lists()
    {
        // The README's snippet comments name five. They are the contract for "you get a coherent
        // accent from one setting" — one missing and the FAB or the focus ring keeps the old hue.
        var snippet = Section("`PrimaryColor` is applied on top", "### CSS variables — full reference");
        var promised = Regex.Matches(snippet, @"(--bm-[a-z0-9-]+)")
                            .Select(m => m.Groups[1].Value).Distinct().Order().ToList();

        promised.Should().HaveCount(5);

        var widget = RepoFiles.SourceFiles.Single(f => f.EndsWith("ChatWidget.razor.cs", StringComparison.Ordinal));
        var source = File.ReadAllText(widget);

        string.Join(", ", promised.Where(v => !source.Contains($"{v}:", StringComparison.Ordinal)))
              .Should().BeEmpty();
    }

    // ── The dashboard is a separate palette ─────────────────────────────────────

    [Fact]
    public void The_dashboard_variables_are_scoped_to_the_dashboard_not_to_root()
    {
        // Deliberate: the dashboard lives on an operator page, not inside the widget, and must not
        // inherit the widget's dark glass. A --bm-dash-* that leaked onto :root would tint whatever
        // else the host renders.
        var dash = Declarations(DashBlock);

        dash.Should().NotBeEmpty();
        string.Join(", ", dash.Keys.Where(RootVars.ContainsKey)).Should().BeEmpty();
    }

    [Fact]
    public void Every_dashboard_variable_is_redefined_for_the_dark_scheme()
    {
        // The dashboard flips automatically under prefers-color-scheme. One variable left behind is
        // the worst outcome available — a light panel colour on a dark background, or dark text on
        // dark, rather than a consistently wrong theme.
        var light = Declarations(DashBlock).Keys.Where(k => k.StartsWith("--bm-dash-", StringComparison.Ordinal));
        var dark = Declarations(DarkSchemeBlock);

        string.Join(", ", light.Where(v => !dark.ContainsKey(v))).Should().BeEmpty();
    }

    [Fact]
    public void Every_documented_dashboard_and_chart_variable_exists_in_both_schemes()
    {
        // The README publishes a Light and a Dark column for all thirteen. Both are claims.
        var documented = Regex.Matches(DashSection, @"(?m)^\|\s*`(--bm-[a-z0-9-]+)`")
                              .Select(m => m.Groups[1].Value).ToList();

        documented.Should().HaveCount(13, "six dashboard variables and seven chart series");

        // Every dark-scheme block in the file, so the dashboard palette and the chart series — which
        // live in two separate media queries — are both counted, and the light side is what is left
        // once they are taken out.
        var blocks = Regex.Matches(Css, @"@media \(prefers-color-scheme: dark\)")
            .Select(m => Block(Css[m.Index..], "@media (prefers-color-scheme: dark)"))
            .ToList();

        blocks.Should().HaveCountGreaterThan(1, "the dashboard palette and the chart series flip separately");

        var dark = string.Join('\n', blocks);
        var light = blocks.Aggregate(Css, (acc, b) => acc.Replace(b, ""));

        string.Join(", ", documented.Where(v => !light.Contains($"{v}:", StringComparison.Ordinal)))
              .Should().BeEmpty("every documented variable needs a light value");
        string.Join(", ", documented.Where(v => !dark.Contains($"{v}:", StringComparison.Ordinal)))
              .Should().BeEmpty("every documented variable needs a dark value");
    }

    [Fact]
    public void The_documented_dashboard_values_are_the_stylesheets_own()
    {
        var rows = Regex.Matches(DashSection, @"(?m)^\|\s*`(--bm-dash-[a-z0-9-]+)`\s*\|\s*`([^`]+)`\s*\|");
        var dash = Declarations(DashBlock);

        rows.Should().HaveCount(6);

        var wrong = rows
            .Where(m => dash.TryGetValue(m.Groups[1].Value, out var actual)
                        && actual != Normalise(m.Groups[2].Value))
            .Select(m => $"{m.Groups[1].Value}: README {m.Groups[2].Value}, stylesheet {dash[m.Groups[1].Value]}");

        string.Join(Environment.NewLine, wrong).Should().BeEmpty();
    }

    // ── Fonts (BUG-150) ─────────────────────────────────────────────────────────

    private static readonly string FontsDir =
        Path.Combine(RepoFiles.Root, RepoFiles.Abstractions, "wwwroot", "fonts");

    /// <summary>Each <c>@font-face</c> block: family, weight range and the file its <c>src</c> names.</summary>
    private static readonly List<(string Family, string Weight, string Src)> FontFaces =
        [.. Regex.Matches(Css, @"@font-face\s*\{(?<body>[^}]*)\}")
                 .Select(m => m.Groups["body"].Value)
                 .Select(body => (
                     Regex.Match(body, @"font-family:\s*'([^']+)'").Groups[1].Value,
                     Regex.Match(body, @"font-weight:\s*([^;]+);").Groups[1].Value.Trim(),
                     Regex.Match(body, @"src:\s*url\('([^']+)'\)\s*format\('woff2'\)").Groups[1].Value))];

    [Fact]
    public void The_stylesheet_requests_nothing_from_another_origin()
    {
        // Up to rc.14 the first line was an @import from fonts.googleapis.com: a request to a third
        // party on every page that rendered the widget, refused under `default-src 'self'`, absent
        // offline, and a transfer of the visitor's address that some deployments must not make.
        var external = Regex.Matches(Css, @"(?:@import|url\()\s*['""]?\s*((?:https?:)?//[^'"")\s]+)")
                            .Select(m => m.Groups[1].Value);

        string.Join(", ", external).Should().BeEmpty();
        Css.Should().NotContain("@import", "an @import of the package's own files would only add a request");
    }

    [Fact]
    public void Every_font_face_names_a_woff2_that_ships_beside_the_stylesheet()
    {
        // The path is relative to the stylesheet, so it holds under any _content base path, in a
        // WASM index.html and in a MAUI WebView alike. A file that is missing is not an error the page
        // shows: the browser falls back to the next family and the widget looks slightly wrong.
        FontFaces.Should().HaveCount(4, "two scripts (Latin, Latin Extended) for each of the two families");

        foreach (var (family, _, src) in FontFaces)
        {
            src.Should().StartWith("../fonts/", $"{family} must load from the package");
            var file = Path.Combine(FontsDir, src["../fonts/".Length..]);

            File.Exists(file).Should().BeTrue($"{src} must ship in Novior.MentorAgent.Abstractions/wwwroot/fonts");
            File.ReadAllBytes(file).Take(4).Should().Equal("wOF2"u8.ToArray(), $"{src} must be a WOFF2 file");
        }
    }

    [Fact]
    public void The_font_faces_and_the_font_variables_name_the_same_two_families()
    {
        // The README names Sora and JetBrains Mono, and a host overriding --bm-font needs those names
        // to still be the right ones. A family declared under another name is never used.
        FontFaces.Select(f => f.Family).Distinct().Order()
                 .Should().Equal("JetBrains Mono", "Sora");
        RootVars["--bm-font"].Should().Contain("sora");
        RootVars["--bm-font-mono"].Should().Contain("jetbrainsmono");
    }

    [Fact]
    public void The_font_faces_cover_every_weight_the_stylesheet_uses()
    {
        // The fonts are variable: one file per script serves the whole axis declared in the face. A
        // weight outside it is synthesised by the browser (a smeared bold), and 650 exists only on an
        // axis — a set of static files would round it.
        var used = Regex.Matches(Css, @"font-weight:\s*(\d{3})\s*;")
                        .Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();

        foreach (var (family, weight, _) in FontFaces)
        {
            var range = weight.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            range.Should().HaveCount(2, $"{family} is a variable font and declares its axis");
            used.Where(w => w < range[0] || w > range[1])
                .Should().BeEmpty($"every weight in the stylesheet must fall inside {family}'s axis {weight}");
        }
    }

    [Fact]
    public void Each_font_ships_with_its_licence()
    {
        // SIL Open Font License 1.1, condition 2: the fonts may be redistributed provided each copy
        // carries the copyright notice and the licence. The texts are the ones published with the files.
        var licences = new Dictionary<string, string>
        {
            ["OFL-Sora.txt"]          = "Copyright 2019 The Sora Project Authors",
            ["OFL-JetBrainsMono.txt"] = "Copyright 2020 The JetBrains Mono Project Authors",
        };

        foreach (var (file, copyright) in licences)
        {
            var text = File.ReadAllText(Path.Combine(FontsDir, file));
            text.Should().StartWith(copyright).And.Contain("SIL OPEN FONT LICENSE Version 1.1");
        }
    }

    // ── The assets ship from one package ────────────────────────────────────────

    [Fact]
    public void The_widget_stylesheet_and_script_ship_from_exactly_one_package()
    {
        // A second copy under another package id is served at a second _content path and referenced
        // by nothing. It ships in that package, it drifts, and the first thing anyone editing the
        // theme does is find the wrong file — edit it, rebuild, and see no change at all.
        var copies = RepoFiles.Packages
            .SelectMany(p => new[] { "css/MentorAgent.css", "js/MentorAgent.js" }
                .Select(asset => (p, asset, path: Path.Combine(RepoFiles.Root, p, "wwwroot", asset))))
            .Where(x => File.Exists(x.path))
            .Select(x => $"{x.p}/wwwroot/{x.asset}")
            .Order();

        string.Join(Environment.NewLine, copies).Should().Be(
            $"{RepoFiles.Abstractions}/wwwroot/css/MentorAgent.css" + Environment.NewLine +
            $"{RepoFiles.Abstractions}/wwwroot/js/MentorAgent.js");
    }

    [Fact]
    public void The_injected_links_point_at_the_package_that_owns_the_files()
    {
        // The `?v=` bump trap in the README depends on this path being right, and a WASM host copies
        // it by hand into index.html.
        var widget = RepoFiles.SourceFiles.Single(f => f.EndsWith("ChatWidget.razor", StringComparison.Ordinal));
        var markup = File.ReadAllText(widget);

        var referenced = Regex.Matches(markup, @"_content/([A-Za-z.]+)/")
                              .Select(m => m.Groups[1].Value).Distinct().ToList();

        // _content/<PackageId>/: the folder name is the package id.
        string.Join(", ", referenced).Should().Be(RepoFiles.Abstractions);
    }
}
