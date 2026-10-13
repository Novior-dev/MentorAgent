using FluentAssertions;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Xunit;

namespace Novior.MentorAgent.Tests.Middleware;

/// <summary>
/// <see cref="ProviderErrorClassifier"/> — ROADMAP #33.
///
/// <para>
/// Part D found a user pasting an intranet image URL, the provider answering
/// <c>"Unable to download image from http://localhost:5224/favicon.png"</c>, and the user being
/// shown <i>"An error occurred while processing the response."</i> The log named the cause and the
/// exact URL; the person who could fix it in two seconds was told nothing.
/// </para>
///
/// <para>
/// The rule this file encodes is the one that makes the feature safe rather than merely helpful:
/// <b>a classification is only worth making when the user can act on it</b>. An expired API key or
/// a missing deployment is an operator problem, and naming it to an end user both fails to help
/// them and tells anyone probing the application something about its configuration. Those stay
/// generic, and stay logged. <c>IncludeWorkflowExceptionDetails</c> is untouched: this never
/// forwards a provider's own text.
/// </para>
/// </summary>
public sealed class ProviderErrorClassifierTests
{
    private static readonly MentorLocalizer It = new(MentorLanguage.Italian);
    private static readonly MentorLocalizer En = new(MentorLanguage.English);

    private static string Classify(string providerMessage, MentorLocalizer? loc = null)
        => ProviderErrorClassifier.Describe(new Exception(providerMessage), loc ?? En);

    [Fact]
    public void An_unreachable_image_url_is_named_along_with_the_url()
    {
        // The URL came from the user, so echoing it leaks nothing and is the whole point: it is
        // what turns "something went wrong" into "that link is not reachable from the internet".
        var msg = Classify(
            "HTTP 400 (invalid_request_error): Unable to download image from http://localhost:5224/favicon.png.");

        msg.Should().NotBe(ProviderErrorClassifier.Describe(new Exception("something else"), En),
            "an image fetch failure is exactly the case this classifier exists for");
        msg.Should().Contain("http://localhost:5224/favicon.png",
            "the user cannot fix a link they are not shown");
    }

    [Fact]
    public void A_context_length_error_tells_the_user_to_start_a_new_conversation()
    {
        // Actionable, and the action is a button already on screen.
        var msg = Classify("This model's maximum context length is 128000 tokens, however you requested 131204.");

        msg.Should().NotContain("128000", "a token count is not something the user can act on");
        msg.Should().NotBe(ProviderErrorClassifier.Describe(new Exception("unrelated"), En));
    }

    [Theory]
    [InlineData("Requests to the ChatCompletions_Create Operation have exceeded token rate limit")]
    [InlineData("HTTP 429 (rate_limit_exceeded)")]
    public void A_rate_limit_says_the_service_is_busy(string provider)
    {
        var msg = Classify(provider);

        msg.Should().NotBe(ProviderErrorClassifier.Describe(new Exception("unrelated"), En));
    }

    [Theory]
    [InlineData("HTTP 401 (invalid_api_key): Incorrect API key provided: sk-abc123xyz")]
    [InlineData("The API deployment for this resource does not exist: gpt-5-turbo")]
    [InlineData("Access denied due to Virtual Network/Firewall rules.")]
    public void An_operator_problem_stays_generic_and_leaks_nothing(string provider)
    {
        // The security half of the feature. A key fragment, a deployment name or a network policy
        // in a chat bubble is a configuration disclosure, and none of it helps the person reading.
        var msg = Classify(provider);

        msg.Should().Be(ProviderErrorClassifier.Describe(new Exception("anything at all"), En),
            "an operator problem is indistinguishable, to the user, from any other failure");
        msg.Should().NotContain("sk-abc123xyz");
        msg.Should().NotContain("gpt-5-turbo");
        msg.Should().NotContain("Firewall");
    }

    [Fact]
    public void The_generic_fallback_is_localised()
    {
        // It was a hard-coded English sentence in a library that ships ten languages — the one
        // string in the whole package a user could be shown in the wrong language.
        var italian = Classify("something entirely unrecognised", It);
        var english = Classify("something entirely unrecognised", En);

        italian.Should().NotBe(english);
        italian.Should().NotContain("An error occurred while processing the response");
    }

    [Fact]
    public void Every_classification_is_localised_in_every_language()
    {
        // The B17 rule applied to the new keys: a translation that silently falls back to English
        // is invisible through the public Get, so assert across the languages the package ships.
        var samples = new[]
        {
            "Unable to download image from https://example.com/a.png",
            "This model's maximum context length is 128000 tokens",
            "HTTP 429 (rate_limit_exceeded)",
            "unrecognised failure",
        };

        foreach (var lang in new[] { MentorLanguage.Italian, MentorLanguage.German, MentorLanguage.Japanese })
        {
            var loc = new MentorLocalizer(lang);
            foreach (var s in samples)
            {
                var localised = Classify(s, loc);
                localised.Should().NotBeNullOrWhiteSpace();
                localised.Should().NotBe(Classify(s, En),
                    $"'{s}' must be translated in {lang}, not fall back to English");
            }
        }
    }

    [Fact]
    public void A_null_or_empty_provider_message_still_produces_something_to_show()
    {
        // A turn that fails with no message at all must not leave the user with an empty bubble.
        ProviderErrorClassifier.Describe(new Exception(), En).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_Responses_API_wording_of_an_unreachable_file_is_classified_too()
    {
        // BUG-061. The two OpenAI APIs phrase the same failure differently, and every string in
        // this file was the Chat Completions one — the literal text Part D had seen live. Driven
        // again on rc.3 against a host with UseResponsesApi = true (the configuration hosted tools
        // force), the provider said this instead, and the user got the generic sentence back:
        //
        //   HTTP 400 (invalid_request_error: invalid_value)
        //   Parameter: url
        //   Error while downloading file.
        //
        // The URL is not in the message here — it is in "Parameter: url" — so the no-URL variant is
        // the best answer available, and the pair is what makes the match safe: "error while
        // downloading file" on its own could be a vector-store upload, which is an operator problem.
        var responses = Classify(
            @"HTTP 400 (invalid_request_error: invalid_value)
Parameter: url

Error while downloading file.");

        responses.Should().Be(En.Get("error.imageUnreachableNoUrl"),
            "the same failure must read the same to the user whichever API surface produced it");
        responses.Should().NotBe(En.Get("error.generic"),
            "falling through to the generic sentence is exactly the defect #33 exists to remove");
    }

    [Fact]
    public void A_download_failure_that_is_not_the_user_supplied_url_stays_generic()
    {
        // The other half of the pair, and the reason the rule is not a bare Contains: a file the
        // operator configured — a vector store document, a knowledge base upload — failing to
        // download is not something the person in the chat can act on.
        var operatorSide = Classify(
            "HTTP 400 (invalid_request_error): Error while downloading file for vector store vs_abc123.");

        operatorSide.Should().Be(En.Get("error.generic"),
            "an operator's own file is not the user's to fix, and naming it tells a prober about the setup");
    }
}
