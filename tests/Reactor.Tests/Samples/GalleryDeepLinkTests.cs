using System;
using System.Linq;
using WinUIGalleryReactor;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Samples;

/// <summary>
/// Contract tests for ReactorGallery's <c>reactor-gallery://</c> URI space
/// (samples/ReactorGallery/DeepLink/GalleryRoutes.cs, source-linked into this suite).
///
/// <para>Two things are being protected. First the *routing* — every pattern, the
/// reverse URI builder, and the round trip between them, so a renamed control or a
/// dropped pattern breaks the build rather than a user's bookmark. Second the *input
/// validation* — the resolver is handed URIs by the Windows shell, so the scheme check
/// and the registry allow-list are the boundary that stops an arbitrary string from
/// reaching the shell as a NavigationView tag.</para>
///
/// <para>Assertions are written differentially (a passing case paired with the failing
/// case that differs by exactly one thing) so deleting the code under test flips a
/// result rather than merely widening one.</para>
/// </summary>
public sealed class GalleryDeepLinkTests
{
    const string Prefix = "reactor-gallery:///";

    // ── Patterns ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("reactor-gallery:///")]
    [InlineData("reactor-gallery:///home")]
    [InlineData("reactor-gallery:///HOME")]
    public void Resolve_HomeLinks_LandOnHome(string uri)
    {
        Assert.True(GalleryRoutes.TryResolve(uri, out var route), uri);
        Assert.Equal(GalleryRouteKind.Home, route.Kind);
        Assert.Equal("home", route.Tag);
    }

    [Fact]
    public void Resolve_SettingsLink_LandsOnSettingsTag()
    {
        Assert.True(GalleryRoutes.TryResolve(Prefix + "settings", out var route));
        Assert.Equal(GalleryRouteKind.Settings, route.Kind);
        Assert.Equal("settings", route.Tag);
    }

    [Theory]
    [InlineData("item")]
    [InlineData("control")]
    public void Resolve_ControlLink_YieldsControlRouteUnderBothSegmentNames(string segment)
    {
        Assert.True(GalleryRoutes.TryResolve($"{Prefix}{segment}/toggle-switch", out var route), segment);
        Assert.Equal(GalleryRouteKind.Control, route.Kind);
        Assert.Equal("toggle-switch", route.Tag);
    }

    [Fact]
    public void Resolve_CategoryLink_YieldsCategoryRoute()
    {
        Assert.True(GalleryRoutes.TryResolve(Prefix + "category/basic-input", out var route));
        Assert.Equal(GalleryRouteKind.Category, route.Kind);
        Assert.Equal("basic-input", route.Tag);
    }

    [Fact]
    public void Resolve_SearchLink_CarriesTheDecodedQuery()
    {
        Assert.True(GalleryRoutes.TryResolve(Prefix + "search?q=toggle%20switch", out var route));
        Assert.Equal(GalleryRouteKind.Search, route.Kind);
        // The query — not merely "some search route" — is the payload the shell renders.
        Assert.Equal("toggle switch", route.Query);

        // A search link with no query is still a search route, with an empty query.
        // This is what separates "the ?q= parse ran" from "the parse echoed the URI".
        Assert.True(GalleryRoutes.TryResolve(Prefix + "search", out var bare));
        Assert.Equal(GalleryRouteKind.Search, bare.Kind);
        Assert.Equal(string.Empty, bare.Query);
    }

    [Fact]
    public void Resolve_TrailingSlashAndCasing_DoNotChangeTheRoute()
    {
        Assert.True(GalleryRoutes.TryResolve(Prefix + "item/button/", out var trailing));
        Assert.True(GalleryRoutes.TryResolve(Prefix + "Item/BUTTON", out var cased));

        Assert.Equal(new GalleryRoute(GalleryRouteKind.Control, "button"), trailing);
        Assert.Equal(new GalleryRoute(GalleryRouteKind.Control, "button"), cased);
    }

    [Fact]
    public void Resolve_FoldsAuthorityBackIntoPath()
    {
        // `reactor-gallery://item/button` (two slashes) parses with Host="item" and
        // AbsolutePath="/button". Without the authority fold that is a miss, so this
        // pairing fails the moment the fold is removed.
        Assert.True(GalleryRoutes.TryResolve("reactor-gallery://item/button", out var twoSlash));
        Assert.True(GalleryRoutes.TryResolve("reactor-gallery:///item/button", out var threeSlash));
        Assert.Equal(threeSlash, twoSlash);
        Assert.Equal("button", twoSlash.Tag);
    }

    [Fact]
    public void Resolve_PercentEncodedCategoryName_MatchesTheHyphenatedSlug()
    {
        Assert.True(GalleryRoutes.TryResolve(Prefix + "category/Date%20and%20Time", out var encoded));
        Assert.True(GalleryRoutes.TryResolve(Prefix + "category/date-and-time", out var slug));
        Assert.Equal(slug, encoded);
        Assert.Equal("date-and-time", encoded.Tag);
    }

    // ── Validation boundary ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_UnknownControlTag_IsRejectedWhileAKnownOneResolves()
    {
        // Differential: identical shape, only the tag differs. Removing the
        // ControlRegistry allow-list check makes the second assert fail.
        Assert.True(GalleryRoutes.TryResolve(Prefix + "item/button", out _));
        Assert.False(GalleryRoutes.TryResolve(Prefix + "item/not-a-real-control", out var rejected));
        Assert.Equal(GalleryRoutes.HomeRoute, rejected);
    }

    [Fact]
    public void Resolve_UnknownCategory_IsRejectedWhileAKnownOneResolves()
    {
        Assert.True(GalleryRoutes.TryResolve(Prefix + "category/layout", out _));
        Assert.False(GalleryRoutes.TryResolve(Prefix + "category/not-a-real-category", out _));
    }

    [Fact]
    public void Resolve_ControlSegmentCannotSmuggleAReservedTag()
    {
        // `settings` and `home` are real NavigationView tags but not controls, so they
        // must not be reachable through /item/. This is the concrete reason the
        // allow-list exists.
        Assert.False(GalleryRoutes.TryResolve(Prefix + "item/settings", out _));
        Assert.False(GalleryRoutes.TryResolve(Prefix + "item/home", out _));
    }

    [Theory]
    [InlineData("https://evil.example/item/button")]
    [InlineData("http://evil.example/item/button")]
    [InlineData("file:///item/button")]
    [InlineData("javascript:/item/button")]
    public void Resolve_ForeignScheme_IsRejected(string uri)
    {
        // Same path, wrong scheme. The reactor-gallery spelling below does resolve, so
        // a deleted scheme check turns these into passes and fails the test.
        Assert.False(GalleryRoutes.TryResolve(uri, out _), uri);
        Assert.True(GalleryRoutes.TryResolve(Prefix + "item/button", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("reactor-gallery:///nope/button")]
    [InlineData("reactor-gallery:///item")]
    [InlineData("reactor-gallery:///item/button/extra")]
    public void Resolve_UnmatchedInput_ReturnsFalseAndHome(string? uri)
    {
        Assert.False(GalleryRoutes.TryResolve(uri, out var route), uri ?? "<null>");
        Assert.Equal(GalleryRoutes.HomeRoute, route);
        Assert.Equal(GalleryRoutes.HomeRoute, GalleryRoutes.ResolveOrHome(uri));
    }

    [Fact]
    public void Resolve_BarePathWithoutScheme_IsAccepted()
    {
        // The command-line fallback hands through raw argv entries, which may be a bare
        // path. Both spellings must land on the same route.
        Assert.True(GalleryRoutes.TryResolve("/item/button", out var rooted));
        Assert.True(GalleryRoutes.TryResolve("item/button", out var relative));
        Assert.Equal(rooted, relative);
        Assert.Equal(GalleryRouteKind.Control, rooted.Kind);
    }

    // ── Reverse direction + round trip ──────────────────────────────────────

    [Fact]
    public void UriForTag_RoundTripsEveryRegisteredControl()
    {
        Assert.NotEmpty(ControlRegistry.All);

        foreach (var control in ControlRegistry.All)
        {
            var uri = GalleryRoutes.UriForTag(control.Tag);
            Assert.True(GalleryRoutes.TryResolve(uri, out var route), $"{control.Tag} → {uri}");
            Assert.Equal(GalleryRouteKind.Control, route.Kind);
            Assert.Equal(control.Tag, route.Tag);
        }
    }

    [Fact]
    public void UriForTag_RoundTripsEveryCategory()
    {
        Assert.NotEmpty(ControlRegistry.Categories);

        foreach (var category in ControlRegistry.Categories)
        {
            var slug = GalleryRoutes.CategorySlug(category);
            var uri = GalleryRoutes.UriForTag(slug);
            Assert.True(GalleryRoutes.TryResolve(uri, out var route), $"{category} → {uri}");
            Assert.Equal(GalleryRouteKind.Category, route.Kind);
            Assert.Equal(slug, route.Tag);
        }
    }

    [Fact]
    public void CategorySlug_IsLowercaseAndHyphenated()
    {
        // The slug is simultaneously the NavigationView tag and the /category/ segment;
        // GalleryShell builds its tags through this same method. A slug that kept its
        // spaces or casing would still "work" inside the shell but break every category
        // link, so pin the exact shape.
        Assert.Equal("date-and-time", GalleryRoutes.CategorySlug("Date and Time"));
        Assert.Equal("basic-input", GalleryRoutes.CategorySlug("Basic Input"));

        foreach (var category in ControlRegistry.Categories)
        {
            var slug = GalleryRoutes.CategorySlug(category);
            Assert.DoesNotContain(" ", slug, StringComparison.Ordinal);
            Assert.Equal(slug.ToLowerInvariant(), slug);
        }
    }

    [Fact]
    public void UriForTag_EmitsTheExpectedShapePerTagKind()
    {
        Assert.Equal(Prefix + "home", GalleryRoutes.UriForTag("home"));
        Assert.Equal(Prefix + "settings", GalleryRoutes.UriForTag("settings"));
        Assert.Equal(Prefix + "item/button", GalleryRoutes.UriForTag("button"));
        Assert.Equal(Prefix + "category/layout", GalleryRoutes.UriForTag("layout"));

        // Unknown / empty degrade to home rather than emitting a dead link.
        Assert.Equal(Prefix + "home", GalleryRoutes.UriForTag("not-a-real-tag"));
        Assert.Equal(Prefix + "home", GalleryRoutes.UriForTag(null));
    }

    [Fact]
    public void UriForSearch_EscapesTheQueryAndRoundTrips()
    {
        var uri = GalleryRoutes.UriForSearch("toggle switch & more");
        // Raw spaces / ampersands in a query string truncate the term on the way back
        // in, so the escaping is load-bearing rather than cosmetic.
        Assert.DoesNotContain(" ", uri, StringComparison.Ordinal);
        Assert.True(GalleryRoutes.TryResolve(uri, out var route));
        Assert.Equal(GalleryRouteKind.Search, route.Kind);
        Assert.Equal("toggle switch & more", route.Query);

        Assert.Equal(Prefix + "home", GalleryRoutes.UriForSearch("   "));
    }

    [Fact]
    public void UriForCurrentView_PrefersTheLiveSearchQueryOverTheSelectedTag()
    {
        // The shell shows search results whenever the box is non-empty, regardless of
        // the selected tag — the copied link has to match what's on screen.
        Assert.Equal(
            GalleryRoutes.UriForSearch("button"),
            GalleryRoutes.UriForCurrentView("layout", "button"));

        Assert.Equal(
            GalleryRoutes.UriForTag("layout"),
            GalleryRoutes.UriForCurrentView("layout", ""));

        Assert.NotEqual(
            GalleryRoutes.UriForCurrentView("layout", ""),
            GalleryRoutes.UriForCurrentView("layout", "button"));
    }

    [Fact]
    public void Scheme_MatchesThePackageManifestDeclaration()
    {
        // The packaged flavour declares this scheme in Package.appxmanifest and the
        // unpackaged one registers this same constant at runtime; if they drift, links
        // open the wrong app (or nothing) depending on how the gallery was installed.
        Assert.Equal("reactor-gallery", GalleryRoutes.Scheme);
        Assert.Equal("reactor-gallery:///", GalleryRoutes.UriPrefix);
        Assert.StartsWith(GalleryRoutes.UriPrefix, GalleryRoutes.UriForTag("button"), StringComparison.Ordinal);
    }
}
