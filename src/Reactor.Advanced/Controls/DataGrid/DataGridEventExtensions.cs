using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Reactor.Data;

namespace Microsoft.UI.Reactor.Controls;

// DataGrid event-callback fluent extensions. Spec 039 §0.1 + §14 #1; relocated
// from the core ElementExtensions partial by spec 062 §7 Track B (B3) when the
// data grid moved into Reactor.Advanced. These live in the
// Microsoft.UI.Reactor.Controls namespace — the data grid's own namespace — so a
// consumer that already imports it (to name DataGridElement) keeps resolving the
// fluent with no source change; only the owning assembly moved, not the API.
public static class DataGridEventExtensions
{
    /// <summary>
    /// Wires the multi-select snapshot handler for <see cref="DataGridElement{T}"/>.
    /// Receives the full set of currently-selected <c>RowKey</c>s on every
    /// change (not added/removed deltas). Passing <c>null</c> clears.
    /// </summary>
    public static DataGridElement<T> SelectionChanged<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this DataGridElement<T> el, Action<IReadOnlySet<RowKey>>? handler) =>
        el with { OnSelectionChanged = handler };
}
