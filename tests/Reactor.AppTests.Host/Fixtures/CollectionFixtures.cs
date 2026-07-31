using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Microsoft.UI.Reactor.AppTests.Host.Fixtures;

internal static class CollectionFixtures
{
    private record Animal(string Name, string Species);

    private static readonly Animal[] Animals =
    [
        new("Rex", "Dog"),
        new("Whiskers", "Cat"),
        new("Polly", "Parrot"),
        new("Nemo", "Fish"),
        new("Buddy", "Dog"),
    ];

    internal static Element ListViewTyped(RenderContext ctx) =>
        VStack(
            TextBlock("Animals List").AutomationId("AnimalsTitle"),
            ListView(Animals,
                keySelector: a => a.Name,
                viewBuilder: (animal, idx) =>
                    HStack(
                        TextBlock($"{idx + 1}.").AutomationId($"AnimalIdx{idx}"),
                        TextBlock(animal.Name).AutomationId($"AnimalName{idx}"),
                        TextBlock($"({animal.Species})").AutomationId($"AnimalSpecies{idx}")
                    )
            ).Height(300).AutomationId("AnimalsList")
        );

    // Issue #951 — keyed rows used to announce Reactor's internal identity
    // ("Row[0]=<key>") as their UIA Name. The keys below are literal GUID text,
    // matching the report's worst case: a key selector over database ids, whose
    // leak is both unreadable and unique per row. Item views are composite on
    // purpose — a bare TextBlock lets the container compose its own name from
    // the text and hides the bug entirely.
    private record Fruit(string Id, string Label);

    internal const string LeakKeyPrefix = "e951a000000000000000000000000";

    private static readonly Fruit[] Fruits =
    [
        new($"{LeakKeyPrefix}001", "Apples"),
        new($"{LeakKeyPrefix}002", "Bananas"),
        new($"{LeakKeyPrefix}003", "Carrots"),
    ];

    private static Element FruitRow(Fruit f, int idx) =>
        HStack(
            Border(TextBlock($"{idx + 1}")).Size(28, 28),
            TextBlock(f.Label)
        );

    internal static Element KeyedListItemNames(RenderContext ctx) =>
        VStack(
            TextBlock("Keyed item names").AutomationId("KeyedNamesTitle"),
            // No author-declared name: rows must simply be unnamed, never named
            // after the row's key.
            ListView(Fruits, keySelector: f => f.Id, viewBuilder: FruitRow)
                .Height(160).AutomationId("KeyedUnnamedList"),
            // Author-declared name: .AutomationName on the item view is the
            // supported way to name a row, and it has to survive to the real
            // cross-process UIA tree.
            ListView(Fruits, keySelector: f => f.Id,
                viewBuilder: (f, idx) => FruitRow(f, idx).AutomationName($"Fruit {f.Label}"))
                .Height(160).AutomationId("KeyedNamedList")
        );
}
