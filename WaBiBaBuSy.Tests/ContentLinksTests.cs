using WaBiBaBuSy.Models.Content;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ContentLinksTests
{
    [Fact]
    public void ThreeNonEmptyGroups()
    {
        Assert.Equal(new[] { "Free GIFs & sprites", "Make with AI", "Clean up" }, ContentLinks.Groups.Select(g => g.Title));
        Assert.All(ContentLinks.Groups, g => Assert.NotEmpty(g.Links));
    }

    [Fact]
    public void EveryLinkIsHttpsWithNameAndTip() =>
        Assert.All(ContentLinks.Groups.SelectMany(g => g.Links), l =>
        {
            Assert.True(Uri.TryCreate(l.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, l.Url);
            Assert.False(string.IsNullOrWhiteSpace(l.Name));
            Assert.False(string.IsNullOrWhiteSpace(l.Tip));
        });

    [Fact]
    public void LinksAreUnique() =>
        Assert.Equal(ContentLinks.Groups.SelectMany(g => g.Links).Count(),
                     ContentLinks.Groups.SelectMany(g => g.Links).Select(l => l.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count());
}
