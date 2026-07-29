using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Reactor.Navigation;

namespace WinUIGalleryReactor;

/// <summary>What a resolved <c>reactor-gallery://</c> link points at.</summary>
public enum GalleryRouteKind
{
    /// <summary>The landing page.</summary>
    Home,

    /// <summary>The app settings page.</summary>
    Settings,

    /// <summary>The search-results view, driven by <see cref="GalleryRoute.Query"/>.</summary>
    Search,

    /// <summary>A category grid (<c>/category/basic-input</c>).</summary>
    Category,

    /// <summary>An individual control page (<c>/item/button</c>).</summary>
    Control,
}

/// <summary>
/// A resolved deep link. <see cref="Tag"/> is the value the shell feeds straight
/// into <c>NavigationView.SelectedTag</c>, so callers never have to re-map kinds
/// onto tags.
/// </summary>
public sealed record GalleryRoute(GalleryRouteKind Kind, string Tag, string? Query = null);

/// <summary>
/// The gallery's <c>reactor-gallery://</c> URI space, both directions.
///
/// <para>Shape mirrors the WinUI Gallery's <c>windows-gallery:///item/Button</c>:</para>
/// <code>
/// reactor-gallery:///                      → home
/// reactor-gallery:///home
/// reactor-gallery:///settings
/// reactor-gallery:///search?q=button
/// reactor-gallery:///category/basic-input
/// reactor-gallery:///item/button
/// </code>
///
/// <para>Deliberately free of any WinRT / WinUI dependency: it is the piece that
/// interprets untrusted input (a URI handed to us by the shell), so it is
/// source-linked into <c>Reactor.Tests</c> and unit-tested headlessly.</para>
/// </summary>
public static class GalleryRoutes
{
    /// <summary>The custom URI scheme, without the <c>:</c>.</summary>
    public const string Scheme = "reactor-gallery";

    /// <summary>Scheme prefix including the empty authority, e.g. <c>reactor-gallery:///</c>.</summary>
    public const string UriPrefix = Scheme + ":///";

    /// <summary>Tag the shell uses for the landing page.</summary>
    public const string HomeTag = "home";

    /// <summary>Tag the shell uses for the settings page.</summary>
    public const string SettingsTag = "settings";

    /// <summary>Route used whenever a link is missing, malformed, or points at something unknown.</summary>
    public static GalleryRoute HomeRoute { get; } = new(GalleryRouteKind.Home, HomeTag);

    static readonly DeepLinkMap<GalleryRoute> Map = new DeepLinkMap<GalleryRoute>()
        .Map("/", _ => HomeRoute)
        .Map("/home", _ => HomeRoute)
        .Map("/settings", _ => new GalleryRoute(GalleryRouteKind.Settings, SettingsTag))
        .Map("/search", a => new GalleryRoute(GalleryRouteKind.Search, HomeTag, a.Query("q", string.Empty)))
        .Map("/category/{name}", a => new GalleryRoute(GalleryRouteKind.Category, Slug(a.GetString("name"))))
        .Map("/item/{tag}", a => new GalleryRoute(GalleryRouteKind.Control, Slug(a.GetString("tag"))))
        // `/control/...` reads more naturally than `/item/...` for anyone hand-writing
        // a link; keep both so neither guess is wrong.
        .Map("/control/{tag}", a => new GalleryRoute(GalleryRouteKind.Control, Slug(a.GetString("tag"))));

    static readonly HashSet<string> ControlTags =
        ControlRegistry.All.Select(c => c.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> CategorySlugs =
        ControlRegistry.Categories.Select(CategorySlug).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Slug used in a URI (and as a NavigationView tag) for a category display name.</summary>
    public static string CategorySlug(string category) => Slug(category);

    /// <summary>
    /// Canonical tag form: percent-decoded, trimmed, lowercased, spaces folded to
    /// hyphens. Percent-decoding is what lets <c>/category/Basic%20Input</c> land on
    /// the same route as <c>/category/basic-input</c>. It is safe to decode here
    /// because every decoded value is then checked against a fixed allow-list of
    /// registry tags — no decoded string ever reaches the shell unvetted.
    /// </summary>
    static string Slug(string? value) =>
        Uri.UnescapeDataString((value ?? string.Empty).Trim())
            .Trim()
            .ToLowerInvariant()
            .Replace(' ', '-');

    /// <summary>
    /// Resolve a deep link. Accepts a full <c>reactor-gallery://</c> URI (the
    /// protocol-activation payload) or a bare path such as <c>/item/button</c>.
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> — rather than silently landing on home — whenever the
    /// input is empty, carries a foreign scheme, matches no pattern, or names a
    /// control/category that isn't in <see cref="ControlRegistry"/>. Callers decide
    /// what a miss means; <see cref="HomeRoute"/> is the usual fallback. Validating
    /// against the registry matters because the argument is attacker-influenced:
    /// without it, <c>reactor-gallery:///item/settings</c> would hand an arbitrary
    /// string to the NavigationView as a selected tag.
    /// </remarks>
    public static bool TryResolve(string? uriOrPath, out GalleryRoute route)
    {
        route = HomeRoute;
        if (string.IsNullOrWhiteSpace(uriOrPath))
            return false;

        if (!TryNormalize(uriOrPath!, out var path))
            return false;

        var result = Map.Resolve(path);
        if (!result.Matched || result.Routes.Length == 0)
            return false;

        var candidate = result.Routes[^1];
        switch (candidate.Kind)
        {
            case GalleryRouteKind.Control when !ControlTags.Contains(candidate.Tag):
            case GalleryRouteKind.Category when !CategorySlugs.Contains(candidate.Tag):
                return false;
        }

        route = candidate;
        return true;
    }

    /// <summary>
    /// Resolve a deep link, falling back to <see cref="HomeRoute"/> when it does
    /// not match.
    /// </summary>
    public static GalleryRoute ResolveOrHome(string? uriOrPath) =>
        TryResolve(uriOrPath, out var route) ? route : HomeRoute;

    /// <summary>
    /// Reduce an incoming link to the path (+ query) the map understands, rejecting
    /// foreign schemes.
    /// </summary>
    static bool TryNormalize(string uriOrPath, out string path)
    {
        path = string.Empty;
        var trimmed = uriOrPath.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
                return false;

            // `reactor-gallery:///item/button` parses with an empty authority and
            // AbsolutePath="/item/button", but `reactor-gallery://item/button` puts
            // "item" in Host. Fold the authority back into the path so both spellings
            // — and the shorthand a human is most likely to type — resolve the same.
            path = string.IsNullOrEmpty(uri.Host)
                ? uri.AbsolutePath
                : "/" + uri.Host + uri.AbsolutePath;
            path += uri.Query;
            return true;
        }

        // A relative path (`/item/button`, `item/button`) — reject anything that still
        // looks like it carries a scheme so `https://evil.example/item/button` can't
        // sneak through the relative arm.
        if (trimmed.Contains(':', StringComparison.Ordinal))
            return false;

        path = trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
        return true;
    }

    /// <summary>
    /// Build the shareable link for a NavigationView tag — the inverse of
    /// <see cref="TryResolve"/>.
    /// </summary>
    public static string UriForTag(string? tag)
    {
        var slug = Slug(tag);
        if (slug.Length == 0 || slug == HomeTag) return UriPrefix + HomeTag;
        if (slug == SettingsTag) return UriPrefix + SettingsTag;
        if (CategorySlugs.Contains(slug)) return UriPrefix + "category/" + slug;
        if (ControlTags.Contains(slug)) return UriPrefix + "item/" + slug;
        return UriPrefix + HomeTag;
    }

    /// <summary>Build the shareable link for a search query.</summary>
    public static string UriForSearch(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? UriPrefix + HomeTag
            : UriPrefix + "search?q=" + Uri.EscapeDataString(query!.Trim());

    /// <summary>
    /// Build the link for whatever the shell is currently showing. A live search
    /// query wins over the selected tag, matching what the user actually sees.
    /// </summary>
    public static string UriForCurrentView(string? tag, string? searchQuery) =>
        string.IsNullOrWhiteSpace(searchQuery) ? UriForTag(tag) : UriForSearch(searchQuery);
}
