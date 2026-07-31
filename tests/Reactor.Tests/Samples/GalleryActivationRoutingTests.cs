using System;
using System.Linq;
using WinUIGalleryReactor;
using Xunit;

namespace Microsoft.UI.Reactor.Tests.Samples;

/// <summary>
/// Pins the activation priority order in
/// samples/ReactorGallery/DeepLink/GalleryActivationRouting.cs — which candidate string
/// wins when a launch carries more than one.
///
/// <para>The order is not arbitrary: a protocol URI is the only candidate the user
/// explicitly aimed at this app, so it must beat a stale launch argument; and a
/// *redirected* activation must not fall back to this process's own command line,
/// because that describes the original launch rather than the incoming link. Both are
/// silent-wrong-page bugs if they regress, and neither is visible in a URI-parsing test,
/// so each case below is written as a differential: the same call with one candidate
/// changed produces a different route.</para>
/// </summary>
public sealed class GalleryActivationRoutingTests
{
    const string Prefix = "reactor-gallery:///";

    static readonly string TagA = ControlRegistry.All[0].Tag;
    static readonly string TagB = ControlRegistry.All[1].Tag;
    static readonly string TagC = ControlRegistry.All[2].Tag;

    [Fact]
    public void ProtocolUri_BeatsLaunchArgumentsAndCommandLine()
    {
        var route = GalleryActivationRouting.Resolve(
            protocolUri: $"{Prefix}item/{TagA}",
            launchArguments: $"{Prefix}item/{TagB}",
            commandLineArgs: [$"{Prefix}item/{TagC}"]);

        Assert.Equal(new GalleryRoute(GalleryRouteKind.Control, TagA), route);

        // Differential: drop only the protocol URI and the next candidate wins, so this
        // is pinning precedence rather than "it returned something".
        Assert.Equal(
            new GalleryRoute(GalleryRouteKind.Control, TagB),
            GalleryActivationRouting.Resolve(null, $"{Prefix}item/{TagB}", [$"{Prefix}item/{TagC}"]));
    }

    [Fact]
    public void LaunchArguments_BeatCommandLine()
    {
        Assert.Equal(
            new GalleryRoute(GalleryRouteKind.Control, TagB),
            GalleryActivationRouting.Resolve(null, $"{Prefix}item/{TagB}", [$"{Prefix}item/{TagC}"]));

        Assert.Equal(
            new GalleryRoute(GalleryRouteKind.Control, TagC),
            GalleryActivationRouting.Resolve(null, null, [$"{Prefix}item/{TagC}"]));
    }

    [Fact]
    public void UnparseableHigherPriorityCandidate_FallsThroughRatherThanFailing()
    {
        // A protocol activation whose URI names nothing real must not shadow a usable
        // launch argument — returning null here would strand the user on Home.
        var route = GalleryActivationRouting.Resolve(
            protocolUri: $"{Prefix}item/not-a-real-control",
            launchArguments: $"{Prefix}item/{TagB}",
            commandLineArgs: null);

        Assert.Equal(new GalleryRoute(GalleryRouteKind.Control, TagB), route);
    }

    [Fact]
    public void NullCommandLineArgs_DisablesTheFallbackEntirely()
    {
        // This is how a *redirected* activation is resolved. Passing this process's argv
        // there would re-navigate to wherever the gallery was originally launched.
        Assert.Null(GalleryActivationRouting.Resolve(null, null, commandLineArgs: null));

        // Differential: the identical call with argv supplied does resolve, so the null
        // is doing the work rather than the inputs simply being unparseable.
        Assert.Equal(
            new GalleryRoute(GalleryRouteKind.Control, TagC),
            GalleryActivationRouting.Resolve(null, null, [$"{Prefix}item/{TagC}"]));
    }

    [Fact]
    public void CommandLine_ScansPastNonLinkArguments()
    {
        // Real command lines carry switches and the AppLifecycle marker alongside the URI.
        var route = GalleryActivationRouting.Resolve(
            protocolUri: null,
            launchArguments: null,
            commandLineArgs: ["--verbose", "not-a-link", $"{Prefix}item/{TagA}", $"{Prefix}item/{TagB}"]);

        // First match wins, so a later link cannot override an earlier one.
        Assert.Equal(new GalleryRoute(GalleryRouteKind.Control, TagA), route);
    }

    [Fact]
    public void NoUsableCandidate_ReturnsNull()
    {
        Assert.Null(GalleryActivationRouting.Resolve(null, null, []));
        Assert.Null(GalleryActivationRouting.Resolve("", "   ", ["--flag", "https://evil.example/item/button"]));
    }

    [Fact]
    public void SearchLinkSurvivesTheHandoff()
    {
        // The query is the payload for a /search link; a resolver that returned only the
        // route kind would still satisfy the tests above.
        var route = GalleryActivationRouting.Resolve($"{Prefix}search?q=toggle", null, null);

        Assert.Equal(GalleryRouteKind.Search, route!.Kind);
        Assert.Equal("toggle", route.Query);
    }
}
