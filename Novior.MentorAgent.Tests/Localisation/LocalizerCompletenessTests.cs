using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Docs;
using Xunit;

namespace Novior.MentorAgent.Tests.Localisation;

/// <summary>
/// Every localiser key, in every one of the ten supported languages.
///
/// <para>
/// A missing translation is the quietest defect in the package. <see cref="MentorLocalizer.Get"/>
/// falls back to English, so nothing throws, nothing is logged, and the only symptom is an English
/// sentence appearing in the middle of an Italian widget — which the developer who added the key
/// will never see, because they tested in English. There are ten languages and well over a hundred
/// keys, so the only way to know is to check all of them mechanically.
/// </para>
///
/// <para>
/// The placeholder checks are the same failure one level down. A translator who drops the
/// <c>{0}</c> from "wait {0} seconds" produces a sentence that reads perfectly and tells the user
/// nothing, and `string.Format` will not complain about an argument nobody used.
/// </para>
/// </summary>
public sealed class LocalizerCompletenessTests
{
    /// <summary>The ten ISO codes the localiser promises, taken from the enum rather than retyped.</summary>
    private static readonly string[] Cultures =
        [.. Enum.GetValues<MentorLanguage>().Select(l => l.ToIsoCode())];

    /// <summary>
    /// The string table, read straight out of the private field.
    ///
    /// <para>
    /// Reflection rather than the public <c>Get</c>, because <c>Get</c> is the thing that hides the
    /// defect: it answers in English for a key that has no Italian, so no amount of calling it can
    /// tell a translated key from an untranslated one. The table is what has to be complete.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Table =
        (Dictionary<string, Dictionary<string, string>>)typeof(MentorLocalizer)
            .GetField("_strings", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static IEnumerable<int> Placeholders(string text)
        => Regex.Matches(text, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order();

    // ── The table is complete ───────────────────────────────────────────────────

    [Fact]
    public void The_table_was_found_and_is_not_empty()
    {
        // Guards the reflection above: if the field is ever renamed, every other test in this file
        // would pass vacuously over an empty dictionary.
        Table.Should().NotBeEmpty();
        Table.Should().HaveCountGreaterThan(50, "the localiser carries the whole widget's chrome");
    }

    [Fact]
    public void Ten_languages_are_supported_and_all_ten_have_distinct_codes()
    {
        Cultures.Should().HaveCount(10);
        Cultures.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_key_is_translated_into_every_language()
    {
        // The headline. Nothing else in the suite would catch a key added with only an English
        // entry — the fallback makes it look correct in every test written in English.
        var missing = Table
            .SelectMany(entry => Cultures
                .Where(c => !entry.Value.ContainsKey(c))
                .Select(c => $"{entry.Key} → {c}"))
            .ToList();

        string.Join(Environment.NewLine, missing).Should().BeEmpty();
    }

    [Fact]
    public void No_translation_is_blank()
    {
        // A present-but-empty entry is worse than a missing one: the fallback does not fire, and
        // the widget renders an empty button.
        var blank = Table
            .SelectMany(entry => entry.Value
                .Where(kv => string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => $"{entry.Key} → {kv.Key}"))
            .ToList();

        string.Join(Environment.NewLine, blank).Should().BeEmpty();
    }

    [Fact]
    public void No_language_carries_a_key_the_others_do_not()
    {
        // A stray culture code — a typo like "pr" for "pt" — would look like a translation and
        // never be served to anyone.
        var stray = Table
            .SelectMany(entry => entry.Value.Keys
                .Where(c => !Cultures.Contains(c))
                .Select(c => $"{entry.Key} → '{c}'"))
            .ToList();

        string.Join(Environment.NewLine, stray).Should().BeEmpty();
    }

    // ── Placeholders survive translation ────────────────────────────────────────

    [Fact]
    public void Every_translation_keeps_the_placeholders_the_english_one_has()
    {
        // "Please wait {0} seconds" translated as "Attendi qualche secondo" reads perfectly and
        // tells the user nothing, and string.Format never complains about an unused argument.
        var wrong = new List<string>();

        foreach (var (key, langs) in Table)
        {
            if (!langs.TryGetValue("en", out var english)) continue;
            var expected = Placeholders(english).ToList();

            foreach (var (culture, text) in langs)
            {
                var actual = Placeholders(text).ToList();
                if (!expected.SequenceEqual(actual))
                    wrong.Add($"{key} → {culture}: expected {{{string.Join("},{", expected)}}}, " +
                              $"found {{{string.Join("},{", actual)}}}");
            }
        }

        string.Join(Environment.NewLine, wrong).Should().BeEmpty();
    }

    [Fact]
    public void At_least_one_key_actually_uses_a_placeholder()
    {
        // Keeps the check above from being vacuous if the formatting keys are ever restructured.
        Table.Values
             .Where(l => l.TryGetValue("en", out var en) && Placeholders(en).Any())
             .Should().NotBeEmpty();
    }

    // ── Every key the code asks for exists ──────────────────────────────────────

    [Fact]
    public void Every_key_the_packages_ask_for_is_in_the_table()
    {
        // The other direction, and the one with a visible symptom: Get returns the key itself when
        // it is unknown, so a typo renders literally — "confirm.titel" as a dialog heading.
        var used = Regex.Matches(RepoFiles.SourceText, @"\.Get\(\s*""([a-z][a-z0-9._]*)""")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Order()
            .ToList();

        used.Should().NotBeEmpty("the packages do call the localiser");

        string.Join(", ", used.Where(k => !Table.ContainsKey(k))).Should().BeEmpty();
    }

    // ── Lookup behaviour ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorLanguage.English, "status.online", "online")]
    [InlineData(MentorLanguage.Italian, "header.reset", "Nuova conversazione")]
    [InlineData(MentorLanguage.French, "confirm.ok", "Confirmer")]
    [InlineData(MentorLanguage.German, "header.close", "Schließen")]
    [InlineData(MentorLanguage.Japanese, "tour.done", "完了")]
    [InlineData(MentorLanguage.Chinese, "dash.refresh", "刷新")]
    public void A_key_resolves_in_the_language_it_was_asked_for(
        MentorLanguage language, string key, string expected)
    {
        new MentorLocalizer(language).Get(key).Should().Be(expected);
    }

    [Fact]
    public void An_unknown_key_comes_back_as_itself()
    {
        // Deliberate: a visible "confirm.titel" in the UI is a bug report. Returning empty would
        // ship a blank button nobody notices.
        new MentorLocalizer(MentorLanguage.Italian).Get("nothing.like.this")
            .Should().Be("nothing.like.this");
    }

    [Fact]
    public void Every_language_can_resolve_every_key_without_falling_back()
    {
        // The end-to-end version of the completeness check, through the public API: with the table
        // complete, no language should ever land on the English fallback. Comparing against the
        // English string is how a silently-untranslated key would show up — except where the word
        // really is the same in both, which several deliberately are.
        var suspicious = new List<string>();

        foreach (var language in Enum.GetValues<MentorLanguage>())
        {
            if (language == MentorLanguage.English) continue;
            var localizer = new MentorLocalizer(language);

            foreach (var (key, langs) in Table)
            {
                var resolved = localizer.Get(key);
                if (!langs.TryGetValue(language.ToIsoCode(), out var declared))
                    suspicious.Add($"{key} → {language} fell back");
                else if (resolved != declared)
                    suspicious.Add($"{key} → {language} resolved to something else");
            }
        }

        string.Join(Environment.NewLine, suspicious).Should().BeEmpty();
    }

    // ── The enum ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MentorLanguage.English, "en")]
    [InlineData(MentorLanguage.Italian, "it")]
    [InlineData(MentorLanguage.French, "fr")]
    [InlineData(MentorLanguage.German, "de")]
    [InlineData(MentorLanguage.Spanish, "es")]
    [InlineData(MentorLanguage.Portuguese, "pt")]
    [InlineData(MentorLanguage.Dutch, "nl")]
    [InlineData(MentorLanguage.Polish, "pl")]
    [InlineData(MentorLanguage.Japanese, "ja")]
    [InlineData(MentorLanguage.Chinese, "zh")]
    public void Each_language_maps_to_its_iso_639_1_code(MentorLanguage language, string expected)
    {
        // These codes reach the browser's speech APIs and the `lang` attribute, so they are a wire
        // format rather than a display string.
        language.ToIsoCode().Should().Be(expected);
    }

    [Fact]
    public void A_value_outside_the_enum_falls_back_to_english()
    {
        // Reachable from configuration binding, which will happily cast an out-of-range integer.
        ((MentorLanguage)99).ToIsoCode().Should().Be("en");
    }

    [Fact]
    public void An_out_of_range_language_still_produces_a_usable_localizer()
    {
        new MentorLocalizer((MentorLanguage)99).Get("confirm.ok").Should().Be("Confirm");
    }

    [Fact]
    public void The_full_name_is_the_enum_name()
    {
        MentorLanguage.Portuguese.ToFullName().Should().Be("Portuguese");
    }

    // ── Formatting keys are usable as formats ───────────────────────────────────

    [Theory]
    [InlineData(MentorLanguage.English)]
    [InlineData(MentorLanguage.Italian)]
    [InlineData(MentorLanguage.Japanese)]
    public void A_formatting_key_can_actually_be_formatted(MentorLanguage language)
    {
        // The keys carrying {0}/{1} are passed through string.Format at the call site. A stray
        // unescaped brace in a translation throws FormatException there — mid-turn, in the user's
        // language only.
        var localizer = new MentorLocalizer(language);

        var act = () =>
        {
            _ = string.Format(localizer.Get("ratelimit.error"), 30);
            _ = string.Format(localizer.Get("message.toolong"), 2000);
            _ = string.Format(localizer.Get("tour.step"), 2, 5);
            _ = string.Format(localizer.Get("confirm.message"), "cancel order 7");
            _ = string.Format(localizer.Get("auth.role.required"), "Admin");
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Every_formatting_key_in_the_table_survives_being_formatted_in_every_language()
    {
        // The exhaustive version: any key with placeholders, in all ten languages, given as many
        // arguments as its English form declares.
        var failures = new List<string>();

        foreach (var (key, langs) in Table)
        {
            if (!langs.TryGetValue("en", out var english)) continue;
            var count = Placeholders(english).Count();
            if (count == 0) continue;

            var args = Enumerable.Range(0, count).Select(i => (object)$"arg{i}").ToArray();

            foreach (var language in Enum.GetValues<MentorLanguage>())
            {
                try { _ = string.Format(new MentorLocalizer(language).Get(key), args); }
                catch (FormatException ex) { failures.Add($"{key} → {language}: {ex.Message}"); }
            }
        }

        string.Join(Environment.NewLine, failures).Should().BeEmpty();
    }
}
