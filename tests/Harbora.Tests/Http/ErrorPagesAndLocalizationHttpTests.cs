using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Localization;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Two pieces of the pipeline whose whole job happens outside an action: the status-code
/// re-execution that turns a bare 404 into a page, and the request localisation that decides which
/// language that page is in.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ErrorPagesAndLocalizationHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    [Fact]
    public async Task A_mistyped_address_keeps_its_404_and_still_gets_a_page()
    {
        // ErrorPageTests already proves HomeController.HttpStatus builds the right model. The thing
        // it cannot prove is that anything ever calls it: without UseStatusCodePagesWithReExecute a
        // 404 never reaches an action at all and the browser shows an empty document.
        var response = await Panel.ClientFrom("203.0.113.90").GetAsync("/no-such-page-exists");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "re-execution must not turn the refusal into a 200");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("<!DOCTYPE html>", "the response body is the themed page, not nothing");
        html.Should().Contain("Harbora");
    }

    [Fact]
    public async Task A_refusal_from_an_action_is_re_executed_the_same_way()
    {
        Panel.GivenUser(fixture.WorkspaceId, "error-owner@example.com", Harbora.Domain.Common.SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.91", "error-owner@example.com");

        var response = await client.GetAsync($"/Apps/Details/{Guid.CreateVersion7()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("<!DOCTYPE html>");
    }

    [Fact]
    public async Task The_default_language_is_Persian_and_the_document_is_right_to_left()
    {
        var response = await Panel.ClientFrom("203.0.113.92").GetAsync("/account/login");

        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("""<html lang="fa" dir="rtl""");
    }

    [Fact]
    public async Task Accept_language_moves_the_page_to_English_and_left_to_right()
    {
        // The contrast is the assertion, not the English page on its own. With the localisation
        // middleware taken out, both requests render in whatever culture the machine running the
        // tests happens to have — and on an English machine a test that only looked at the English
        // request would pass with the thing it names deleted.
        var english = Panel.ClientFrom("203.0.113.93");
        english.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        var silent = Panel.ClientFrom("203.0.113.96");

        var negotiated = await (await english.GetAsync("/account/login")).Content.ReadAsStringAsync();
        var defaulted = await (await silent.GetAsync("/account/login")).Content.ReadAsStringAsync();

        negotiated.Should().Contain("""<html lang="en" dir="ltr""");
        defaulted.Should().Contain("""<html lang="fa" dir="rtl""",
            "asking for English has to be what changed the page, not the host's own locale");
    }

    [Fact]
    public async Task The_culture_cookie_wins_over_the_browsers_header()
    {
        // Program.cs inserts CookieRequestCultureProvider at position 0 on purpose: somebody who
        // chose a language in the panel has said something more specific than their browser has.
        var client = Panel.ClientFrom("203.0.113.94");
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));

        var chosen = await client.PostFormAsync("/account/language",
            await client.AntiforgeryTokenFrom("/account/login"), ("culture", "fa"), ("returnUrl", "/"));
        var page = await client.GetAsync("/account/login");

        chosen.StatusCode.Should().Be(HttpStatusCode.Found);
        (await page.Content.ReadAsStringAsync()).Should().Contain("""<html lang="fa" dir="rtl""",
            "the cookie the switcher wrote is read before Accept-Language");
    }

    [Fact]
    public async Task An_unsupported_language_falls_back_rather_than_failing()
    {
        var client = Panel.ClientFrom("203.0.113.95");
        client.DefaultRequestHeaders.Add("Cookie",
            CookieRequestCultureProvider.DefaultCookieName + "=" +
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("de")));

        var response = await client.GetAsync("/account/login");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("""<html lang="fa" dir="rtl""",
            "German is not one of the two supported cultures, so the default answers");
    }
}
