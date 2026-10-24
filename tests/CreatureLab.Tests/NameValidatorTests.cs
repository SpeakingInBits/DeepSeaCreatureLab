using CreatureLab.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatureLab.Tests;

public sealed class NameValidatorTests
{
    private static readonly NameValidator Validator = new(["# comment", "ass", "cock", "crap", "*fuck", ""], maxLength: 20);

    [Theory]
    [InlineData("  Gloop   the\tDestroyer ", "Gloop the Destroyer")]
    [InlineData("Glo‮op", "Gloop")]           // right-to-left override removed
    [InlineData("Zero​width", "Zerowidth")]   // zero-width space removed
    [InlineData("Line\nbreak", "Line break")]
    [InlineData("🐙 Octo", "🐙 Octo")]
    public void Clean_NormalizesWhatVisitorsType(string raw, string expected)
    {
        Assert.Equal(expected, NameValidator.Clean(raw));
    }

    [Fact]
    public void CreatureName_IsRequired()
    {
        var result = Validator.Check("   ", required: true, "creature name");

        Assert.False(result.IsValid);
        Assert.Equal("Give your creature a name!", result.Error);
    }

    [Fact]
    public void ArtistName_IsOptional()
    {
        var result = Validator.Check("", required: false, "artist name");

        Assert.True(result.IsValid);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Names_AreLimitedTo20Characters()
    {
        Assert.True(Validator.Check(new string('a', 20), true, "creature name").IsValid);

        var tooLong = Validator.Check(new string('a', 21), true, "creature name");
        Assert.False(tooLong.IsValid);
        Assert.Contains("20", tooLong.Error);
    }

    [Theory]
    [InlineData("Big Ass Fish")]
    [InlineData("big A$$ fish")]       // letter swaps
    [InlineData("COCK fish")]          // case
    [InlineData("Craaaap")]            // stretched letters
    [InlineData("fuckfish")]           // * entries match inside words
    [InlineData("Fuuuuck Fish")]
    [InlineData("f.u.c.k")]
    [InlineData("Fück")]               // accents
    public void BlocksListedWords(string name)
    {
        var result = Validator.Check(name, required: true, "creature name");

        Assert.False(result.IsValid);
        Assert.Contains("creature name", result.Error);
    }

    [Theory]
    [InlineData("Bass Buddy")]
    [InlineData("Assassin Bug")]
    [InlineData("Cockle")]
    [InlineData("Scrappy Snapper")]
    [InlineData("Peacock Fish")]
    public void AllowsWordsThatOnlyContainAShortListedWord(string name)
    {
        Assert.True(Validator.Check(name, required: true, "creature name").IsValid);
    }

    [Theory]
    [InlineData("Sea Cucumber")]
    [InlineData("Hermit Crab")]
    [InlineData("Shell Shocker")]
    [InlineData("Cockle Shell")]
    [InlineData("Grape Jelly")]
    [InlineData("Sparse Squid")]
    [InlineData("Torpedo Ray")]
    [InlineData("Glass Eel")]
    [InlineData("Dickens the Fish")]
    [InlineData("Saltwater Sam")]
    [InlineData("Swanky Squid")]
    [InlineData("Therapist Fish")]
    [InlineData("Gloop the Destroyer")]
    [InlineData("a mystery diver")]
    public void ShippedWordList_AllowsEverydaySeaCreatureNames(string name)
    {
        Assert.True(ShippedList().Check(name, required: true, "creature name").IsValid);
    }

    [Fact]
    public void ShippedWordList_IsLoaded()
    {
        Assert.True(ShippedList().WordCount > 50);
    }

    private static NameValidator ShippedList() =>
        NameValidator.FromFile(Path.Combine(AppContext.BaseDirectory, "profanity.txt"), 20, NullLogger.Instance);
}
