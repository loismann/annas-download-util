using AnnasArchive.API.Helpers;
using Microsoft.Extensions.Configuration;

namespace AnnasArchive.Tests.Helpers;

/// <summary>
/// Who a book gets sent to, and everything that follows from it.
///
/// <para>These routes email a file to a real address and tag the book with an
/// owner. Getting the target wrong is not a 500 — it is a book arriving on the
/// wrong person's Kindle with a success message on screen.</para>
///
/// <para>The risk being closed was <b>four</b> resolvers, each with a catch-all
/// <c>else</c>, which did not agree: an unrecognised target got Mom's email, Mom's
/// Dropbox folder and <b>Dad's</b> owner tag. Nothing was reachable, because
/// validation happened to allow exactly the two values every branch was written for
/// — so the entire safety margin was that nobody had added a third person yet.</para>
/// </summary>
public class KindleTargetTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    private static IConfiguration BothAddresses() => Config(
        ("Email:DadsKindleEmail", "dad@kindle.test"),
        ("Email:MomsKindleEmail", "mom@kindle.test"));

    // ─── who a target is ──────────────────────────────────────────────────

    [Theory]
    [InlineData("dad", "Dad")]
    [InlineData("mom", "Mom")]
    public void A_known_target_resolves_to_its_household_member(string key, string expected)
    {
        KindleTarget.For(key)!.HouseholdName.Should().Be(expected);
    }

    /// <summary>
    /// Matched case-insensitively. The three resolvers this replaced all lowercased
    /// before comparing, so rejecting "Dad" here would have been a behaviour change
    /// dressed up as a tidy-up.
    /// </summary>
    [Theory]
    [InlineData("Dad")]
    [InlineData("DAD")]
    [InlineData(" dad ")]
    public void Casing_and_surrounding_space_do_not_change_who_it_is(string key)
    {
        KindleTarget.For(key)!.HouseholdName.Should().Be("Dad");
    }

    /// <summary>
    /// <b>The point of the whole class.</b> An unknown target is nobody, not a
    /// default. Every resolver this replaced answered with a real person here.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("paul")]
    [InlineData("Grandma")]
    [InlineData("dad;mom")]
    public void An_unknown_target_is_nobody_rather_than_a_default(string? key)
    {
        KindleTarget.For(key).Should().BeNull();
    }

    // ─── everything that follows from it ──────────────────────────────────

    /// <summary>
    /// The three facts about a recipient move together. They used to be three
    /// independent conditionals, which is how the email and the tag ended up
    /// defaulting to <i>different people</i>.
    /// </summary>
    [Theory]
    [InlineData("dad", "dad@kindle.test", "/dad_downloads", "Dad's Books")]
    [InlineData("mom", "mom@kindle.test", "/mom_downloads", "Mom's Books")]
    public void Address_folder_and_tag_all_name_the_same_person(
        string key, string email, string folder, string tag)
    {
        var target = KindleTarget.For(key)!;

        target.EmailAddress(BothAddresses()).Should().Be(email);
        target.DropboxFolder.Should().Be(folder);
        target.BookTag.Should().Be(tag);
    }

    /// <summary>
    /// The specific inconsistency that existed: <c>GetKindleEmailForTarget</c> fell
    /// through to Mom and <c>GetKindleTargetTag</c> fell through to Dad, so one
    /// unrecognised target would have emailed one person and credited the other.
    /// Asserted as a pair, because either alone looks harmless.
    /// </summary>
    [Fact]
    public void An_unknown_target_cannot_email_one_person_and_tag_another()
    {
        var email = () => SendToTargetHelpers.GetKindleEmailForTarget("paul", BothAddresses());
        var tag = () => LibraryHelpers.GetKindleTargetTag("paul");

        email.Should().Throw<InvalidOperationException>().WithMessage("*not a Kindle target*");
        tag.Should().Throw<InvalidOperationException>().WithMessage("*not a Kindle target*");
    }

    /// <summary>A missing address names the setting rather than sending nowhere —
    /// an empty "to" is the kind of thing an SMTP server accepts.</summary>
    [Fact]
    public void An_unconfigured_address_names_the_setting_it_wants()
    {
        var send = () => KindleTarget.For("mom")!.EmailAddress(Config());

        send.Should().Throw<InvalidOperationException>().WithMessage("*Email:MomsKindleEmail*");
    }

    // ─── the validator the routes actually call ───────────────────────────

    [Theory]
    [InlineData("dad")]
    [InlineData("mom")]
    [InlineData("Mom")]
    public void The_validator_accepts_exactly_the_targets_that_resolve(string target)
    {
        SendToTargetHelpers.ValidateKindleTarget(target).Should().BeNull();
    }

    /// <summary>
    /// Validation and resolution are the same question asked once. They were two
    /// separate lists of string comparisons, which is the arrangement that lets a
    /// third person be added to one and not the other.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("paul")]
    [InlineData("everyone")]
    public void The_validator_refuses_everything_that_resolves_to_nobody(string? target)
    {
        SendToTargetHelpers.ValidateKindleTarget(target).Should().Contain("Invalid target");
        KindleTarget.For(target).Should().BeNull();
    }

    /// <summary>The message lists the real targets, so adding a third person updates
    /// the error without anybody remembering to.</summary>
    [Fact]
    public void The_refusal_names_the_targets_that_exist()
    {
        SendToTargetHelpers.ValidateKindleTarget("nobody")
            .Should().Contain("'dad'").And.Contain("'mom'");
    }
}
