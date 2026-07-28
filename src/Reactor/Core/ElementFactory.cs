using Microsoft.UI.Reactor.Core.Internal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.UI.Reactor.Core;

/// <summary>
/// Bridges WinUI's ItemsRepeater/IElementFactory to Reactor's Reconciler.
/// GetElement calls the view builder then mounts; RecycleElement unmounts.
/// </summary>
/// <remarks>
/// Spec 042 Phase 1: <see cref="_mountedElements"/> is keyed by the
/// stable identity string from <see cref="ReactorRow"/>, not by realized
/// index. Insert-at-0 used to shift every entry's effective index by one
/// — that broke <see cref="RefreshRealizedItems"/>'s lookup contract
/// because the dictionary's int keys no longer matched the repeater's
/// new positions. Keying by string makes the mapping reorder-stable.
/// </remarks>
public sealed partial class ElementFactory<T> : IElementFactory
{
    private IReadOnlyList<T> _items;
    private Func<T, int, Element> _viewBuilder;
    private readonly Reconciler _reconciler;
    private readonly Action _requestRerender;
    private readonly ElementPool? _pool;
    // Optional state used when ItemsSource is the OC<ReactorRow> path
    // (spec 042). Lets GetElement translate an ItemsRepeater realized
    // index → stable key for _mountedElements lookup. Null when running
    // against the legacy Enumerable.Range path.
    private ReactorListState? _listState;

    // Reorder-stable element tracker keyed by ReactorRow.Key. See class doc.
    private readonly Dictionary<string, Element> _mountedElements =
        new(global::System.StringComparer.Ordinal);

    // Reverse lookup: realized WinUI control → key. Lets RecycleElement drop
    // the matching _mountedElements entry in O(1) when ItemsRepeater hands a
    // container back. Without this, entries accumulate one per unique key as
    // the user scrolls (every realize adds; recycle never removes), and on
    // any subsequent re-render RefreshRealizedItems walks stale entries
    // whose row.Index now points at a different logical row's container —
    // running Reconcile against a mismatched UIElement tree.
    private readonly Dictionary<UIElement, string> _keyByControl = new();

    // Recycle pool for proper WinUI ItemsRepeater integration. The framework
    // keeps every realized UIElement parented to the repeater forever and
    // expects the factory to cycle them — see ViewManager.cpp:865-869 in the
    // microsoft-ui-xaml-lift source: on realize, it skips Append if the
    // returned control is already parented to the repeater. So a recycled
    // container must come back out via GetElement to keep the working set
    // bounded; allocating fresh on every realize creates one orphan in
    // Children per call.
    //
    // Used as a stack (append/remove at the end) but stored as a List so
    // GetElement can SCAN it for a container whose last Element is reusable for
    // the row being realized. Blindly popping the newest entry orphans it
    // whenever the root element type flipped (issue #919): Reconcile mints a
    // different control, and the popped one can never be un-parented from the
    // repeater. Bounded by <see cref="PoolCapacity"/>, so the scan is short.
    private readonly List<PoolEntry> _recyclePool = new();

    // A parked container plus the Visibility it had before RecycleElement
    // collapsed it, so reuse restores exactly what the author asked for.
    private readonly record struct PoolEntry(UIElement Control, Visibility Visibility);

    // Retaining a container that cannot serve the current row is what bounds the
    // working set across a root-type flip (issue #919) — but it only ever pays
    // off for a row shape that comes BACK, and for a keyed list it never does:
    // ApplyItemIdentityKey stamps a per-item key on the row root, CanUpdate
    // rejects unequal keys, so scrolling forward through N distinct items would
    // retain N containers and make the scan quadratic. Cap the pool at twice the
    // largest realized window seen so far (the flip case needs exactly one
    // window; the second window is the incoming shape) with a small floor, and
    // evict oldest-first beyond that — an evicted container is parked collapsed
    // and untracked, which is the pre-#919 outcome and no worse.
    private int _maxRealized;
    private int PoolCapacity => global::System.Math.Max(32, _maxRealized * 2);

    // Last Element bound to a given realized control. On reuse from the
    // recycle pool, this is the oldElement passed to Reconciler.Reconcile so
    // the existing WinUI tree gets diffed-in-place against the new content
    // rather than thrown away and re-mounted.
    private readonly Dictionary<UIElement, Element> _lastElementByControl = new();

    // Test-only accessors for the regression fixture
    // ElementFactoryRecyclingFixtures.Factory_BookkeepingBoundedAcrossCycles.
    // Confirm that the four bookkeeping structures don't grow with the
    // number of realize/recycle cycles. Gated by InternalsVisibleTo on
    // Reactor.AppTests.Host (see Reactor.csproj).
    internal int DebugRecyclePoolCount => _recyclePool.Count;
    internal int DebugLastElementByControlCount => _lastElementByControl.Count;
    internal int DebugMountedElementsCount => _mountedElements.Count;
    internal int DebugKeyByControlCount => _keyByControl.Count;
    internal int DebugViewBuilderCacheCount => _viewBuilderCache.Count;
    internal bool DebugTryGetLastElementByControl(UIElement control, out Element? element)
        => _lastElementByControl.TryGetValue(control, out element!);

    // Issue #327 (Option A) test seam: the keyed-memo LRU's rebuild (Factory invocation)
    // counter and live entry count. The headless effectiveness fixture drives BuildOrCache
    // through N recycle cycles and asserts FactoryInvocations stops climbing once keys are
    // cached. Gated by InternalsVisibleTo on Reactor.Tests / Reactor.AppTests.Host.
    internal long DebugKeyedMemoFactoryInvocations => _keyedMemoCache.FactoryInvocations;
    internal int DebugKeyedMemoCacheCount => _keyedMemoCache.Count;

    // Per-key memoization of the last viewBuilder result. Critical for
    // WinUI ItemsView under <see cref="WinUI.UniformGridLayout"/>: window
    // resize causes the framework to recycle most realized containers
    // and immediately re-realize the same indices with the same item
    // refs. Without memoization, every realize calls the user's
    // viewBuilder afresh, producing a new ItemContainerElement(VStack(...))
    // tree whose Child ref differs from the previously bound Element →
    // <see cref="Element.ShallowEquals"/> returns false → the reconcile
    // fast-path skip never fires → the entire subtree's Update methods
    // walk and write WinUI properties on every resize tick. By returning
    // the same Element instance for the same (key, item ref, index)
    // tuple, Reconcile hits its ReferenceEquals(a, b) shortcut and the
    // Update entry returns null without descending. Net: zero per-row
    // work for resize-driven realize cycles, as long as the user's data
    // follows the standard "new object for new state" pattern (records,
    // immutable updates, etc.).
    private readonly Dictionary<string, ViewBuilderCacheEntry> _viewBuilderCache = new(global::System.StringComparer.Ordinal);
    private readonly struct ViewBuilderCacheEntry
    {
        public readonly T Item;
        public readonly int Index;
        public readonly Element Built;
        public ViewBuilderCacheEntry(T item, int index, Element built)
        { Item = item; Index = index; Built = built; }
    }

    // Issue #327 (Option A) — opt-in keyed memo LRU. When the viewBuilder returns a
    // KeyedMemoElement (author wrote `Memo(key, () => …)`), BuildOrCache resolves it here.
    // Keyed by the author's MemoKey with value equality, so the int-index VirtualList path
    // (where _viewBuilderCache's ReferenceEquals(item) guard never hits because each access
    // re-boxes the index) still serves the SAME inner Element instance across recycles. See
    // KeyedMemoCache for bound/eviction/invalidation.
    private readonly KeyedMemoCache _keyedMemoCache = new();

    // Issue #327 review — for a value-type T (the int-index VirtualList path) the
    // _viewBuilderCache lookup can never hit (its ReferenceEquals(item) guard re-boxes both
    // operands), so populating it is dead weight that also grows UNBOUNDED — one retained entry
    // per distinct key (index) as the user scrolls. JIT-folded per instantiation, so the guard is
    // free. The cross-recycle cache for value-type T is the bounded KeyedMemoCache (opt-in Memo).
    private static readonly bool s_valueTypeItem = typeof(T).IsValueType;

    /// <summary>
    /// Resolve the viewBuilder output for a (key, item, index) tuple,
    /// memoized by reference identity of <paramref name="item"/>. See
    /// <see cref="_viewBuilderCache"/> for the rationale.
    /// <para><c>keyed</c> is true on the spec-042 <see cref="ReactorRow"/>
    /// path, where <paramref name="key"/> is the author's <c>keySelector</c>
    /// projection (a stable per-item identity). When set, the projection is
    /// propagated to the row's top-level <see cref="Element.Key"/> — see
    /// <see cref="ApplyItemIdentityKey"/> (issue #326). It is false on the
    /// legacy int-index path, where the "key" is just the realized index and
    /// propagating it would force a control swap on every scroll.</para>
    /// </summary>
    internal Element BuildOrCache(string key, T item, int index, bool keyed)
    {
        if (_viewBuilderCache.TryGetValue(key, out var cached)
            && ReferenceEquals(cached.Item, item)
            && cached.Index == index)
        {
            return cached.Built;
        }
        var built = _viewBuilder(item, index);
        // Issue #327 (Option A): a KeyedMemoElement asserts its inner Factory is a pure
        // function of MemoKey. Resolve it through the factory-owned bounded LRU so a cache
        // HIT returns the SAME inner Element instance across container recycles → the next
        // Reconcile observes ReferenceEquals via Element.ShallowEquals and skips the per-row
        // reconcile descent; a MISS invokes Factory() exactly once. The identity-key stamp is
        // folded into the resolve (keyed path only) so the cached instance is the final one
        // returned on every subsequent hit (preserving ReferenceEquals).
        //
        // Only a "bare" wrapper is memoized: resolution returns the inner element, so any
        // modifiers / Key / Extensions applied ON the wrapper itself (the non-idiomatic
        // `Memo(k, …).Margin(8)` shape — modifiers belong inside the factory lambda) would be
        // dropped. A decorated wrapper instead falls through unchanged and is rendered by the
        // reconciler's transparent unwrap path (Mount/Update), which preserves those modifiers.
        if (built is KeyedMemoElement km
            && km.Modifiers is null && km.Key is null && km.Extensions is null)
            built = _keyedMemoCache.Resolve(km, keyed ? key : null);
        else if (keyed)
            built = ApplyItemIdentityKey(built, key);
        // Skip the never-hitting _viewBuilderCache for value-type T (see s_valueTypeItem): on the
        // int-index path it can only grow unbounded (one pinned row Element per scrolled index)
        // without ever serving a hit. Reference-type T (LazyVStack<record>, ItemsView resize, …)
        // still uses the ReferenceEquals fast-path, so keep populating there. (issue #327 review)
        if (!s_valueTypeItem)
            _viewBuilderCache[key] = new ViewBuilderCacheEntry(item, index, built);
        return built;
    }

    /// <summary>
    /// Issue #326 — propagate the author's per-item <c>keySelector</c>
    /// projection onto the row's top-level <see cref="Element.Key"/> so the
    /// recycle-on-reuse <see cref="Reconciler.Reconcile"/> path (see
    /// <see cref="GetElement"/>) observes a different key when a realized
    /// container is reused for a <em>different</em> logical item. That flips
    /// <see cref="Reconciler.CanUpdate"/> to false → Reactor takes its
    /// keyed-replacement path (unmount + fresh mount) instead of an in-place
    /// property diff, which resets the row's per-item Component
    /// <c>UseState</c> / <c>UseEffect</c> state. Without this, post-#324
    /// recycling reuses the same realized inner <c>Component&lt;T&gt;</c>
    /// across logical items and carries hook state from item A into item B.
    ///
    /// <para>An explicit author-supplied key (<c>row.WithKey(...)</c> inside
    /// the row builder) always wins: it is only applied when the built row's
    /// <see cref="Element.Key"/> is still null. Same-item re-renders
    /// (RefreshRealizedItems) keep the same key on both old and new elements,
    /// so <see cref="Reconciler.CanUpdate"/> stays true and the row diffs in
    /// place — state is preserved exactly when the logical item is unchanged.</para>
    /// </summary>
    internal static Element ApplyItemIdentityKey(Element built, string key)
        => built.Key is null ? built with { Key = key } : built;

    public ElementFactory(
        IReadOnlyList<T> items,
        Func<T, int, Element> viewBuilder,
        Reconciler reconciler,
        Action requestRerender,
        ElementPool? pool = null)
    {
        _items = items;
        _viewBuilder = viewBuilder;
        _reconciler = reconciler;
        _requestRerender = requestRerender;
        _pool = pool;
    }

    /// <summary>
    /// Update items and viewBuilder in place without replacing the factory.
    /// This avoids ItemsRepeater re-realizing all items (which causes
    /// "Cannot run layout in the middle of a collection change" crashes).
    /// Existing realized items stay mounted; they'll render new content
    /// on the next GetElement call (scroll or explicit refresh).
    /// </summary>
    internal void UpdateInPlace(IReadOnlyList<T> items, Func<T, int, Element> viewBuilder)
    {
        _items = items;
        _viewBuilder = viewBuilder;
        // A new viewBuilder closure may capture different external state (UseState
        // cells, Observable subscriptions, theme, etc.) than the one that produced the
        // cached <see cref="ViewBuilderCacheEntry.Built"/> entries. We can't see through
        // delegate captures cheaply, so invalidate conservatively here. Resize-driven
        // recycle/realize cycles still hit the cache because window resize doesn't run
        // the component render path → UpdateInPlace doesn't fire.
        _viewBuilderCache.Clear();
        // Issue #327 (Option A): same invalidation boundary for the keyed memo LRU — a new
        // viewBuilder closure may produce different inner content for the same MemoKey, so a
        // previously-cached inner instance must not be served (mirrors the clear above).
        _keyedMemoCache.Clear();
    }

    /// <summary>
    /// Spec 042 Phase 1: bind this factory to the <see cref="ReactorListState"/>
    /// owned by the parent <see cref="ItemsRepeater"/>'s host so
    /// GetElement can resolve a realized index → ReactorRow.Key for the
    /// reorder-stable <see cref="_mountedElements"/> lookup.
    /// </summary>
    internal void AttachListState(ReactorListState listState) => _listState = listState;

    /// <summary>
    /// After updating the factory in place, reconcile all currently realized
    /// items with the new viewBuilder output. This updates existing WinUI
    /// controls via property changes (no add/remove on the ItemsRepeater's
    /// Children collection).
    /// </summary>
    /// <summary>
    /// When set, RefreshRealizedItems is skipped if the predicate returns true.
    /// Used by DataGrid to suppress reconciliation during active scrolling.
    /// </summary>
    internal Func<bool>? ShouldSkipRefresh;

    internal void RefreshRealizedItems(Microsoft.UI.Xaml.Controls.ItemsRepeater repeater)
    {
        // If scrolling restarted after the render was dispatched, skip reconciliation.
        // The next settle timer will pick it up when scrolling truly stops.
        if (ShouldSkipRefresh?.Invoke() == true)
            return;

        // Snapshot the keys we currently believe are realized. The actual
        // realized set may have changed since the last GetElement, but the
        // ItemsRepeater authoritatively tells us per-key via TryGetElement
        // on the row's current index.
        var keys = _mountedElements.Keys.ToArray();
        foreach (var key in keys)
        {
            // Resolve key → current realized index via the host's list state
            // (or, when running on the legacy int path, treat the key as an
            // integer index for backwards compatibility).
            int currentIndex;
            if (_listState is not null)
            {
                if (!_listState.ByKey.TryGetValue(key, out var row))
                {
                    // Row was removed — drop tracking entry.
                    _mountedElements.Remove(key);
                    continue;
                }
                currentIndex = row.Index;
            }
            else
            {
                // Legacy int-key path: parse if possible, otherwise skip.
                if (!int.TryParse(key, out currentIndex))
                {
                    _mountedElements.Remove(key);
                    continue;
                }
            }

            var child = repeater.TryGetElement(currentIndex);
            if (child is null)
            {
                // The framework can return null from TryGetElement during
                // transient layout passes — e.g., the inner ItemsRepeater
                // is mid-relayout when an unrelated re-render (slider
                // scrub, theme change) walks down here. Permanently
                // dropping the key from <see cref="_mountedElements"/> in
                // that case used to strand row 0 (which UniformGridLayout
                // anchors and never recycles): RecycleElement never fires
                // for it, so once dropped it stays invisible to every
                // subsequent refresh and the row's content freezes at
                // whatever state value the user landed on at the moment
                // of the transient null. Skip this iteration but keep
                // the entry so the next refresh pass can pick it back up.
                continue;
            }

            if (!_mountedElements.TryGetValue(key, out var oldElement)) continue;
            if (currentIndex < 0 || currentIndex >= _items.Count) continue;

            var newElement = BuildOrCache(key, _items[currentIndex], currentIndex, keyed: _listState is not null);

            // Issue #919 — when CanUpdate is false, Reconcile unmounts `child` and mints a
            // REPLACEMENT control, but a realized ItemsRepeater child cannot be swapped from
            // managed code: ItemsRepeater is a FrameworkElement, not a Panel, so there is no
            // Children collection to assign into. The only in-place rescue is
            // TryAdoptRealizedReplacement, which requires the realized control to be a
            // component-wrapper Border. For every other shape (e.g. a DataGrid row flipping from
            // a Grid root to a FlexPanel root on expand) the replacement had nowhere to go: the
            // old control was detached, the replacement was never parented, and — because
            // _mountedElements[key] had already been advanced to `newElement` — the NEXT refresh
            // paired the new element with the still-realized old control, so CanUpdate returned
            // true and the handler dispatch hard-cast a Grid to a FlexPanel (InvalidCastException).
            //
            // Detect that case before mounting a doomed replacement: leave `child` and all of its
            // tracking untouched (the control still faithfully hosts `oldElement`) and ask the
            // framework to recycle + re-realize the row, where GetElement's return channel CAN
            // install a different control type.
            if (child is not Border && !_reconciler.CanUpdate(oldElement, newElement))
            {
                ScheduleReRealize(key);
                continue;
            }

            _mountedElements[key] = newElement;

            var replacement = _reconciler.Reconcile(oldElement, newElement, child, _requestRerender);
            if (replacement is not null && !ReferenceEquals(replacement, child))
            {
                // CanUpdate was false (the row's Element.Key changed — e.g. the
                // documented .WithKey($"{id}:{rev}") pattern — or a root type
                // change) → Reconcile unmounted `child` and built a fresh
                // `replacement`. The ItemsRepeater that still parents `child`
                // isn't a Panel, so we can't swap the realized slot the way the
                // GetElement framework return-channel does. Adopt the fresh
                // subtree into the still-parented wrapper when the shapes allow
                // it; otherwise keep the maps consistent so no stale entry
                // survives and the next scroll re-realize fixes the visual.
                // Without this, the old control was orphaned (stale state still
                // visible) and _lastElementByControl[child] pointed at an
                // element the control no longer hosted. (Issue #326 pr-review H1)
                if (_reconciler.TryAdoptRealizedReplacement(child, replacement))
                {
                    // `child` now hosts the fresh component subtree — tracking
                    // stays anchored on the still-realized `child`.
                    _lastElementByControl[child] = newElement;
                }
                else
                {
                    // Adoption failed, so `replacement` can never be installed. `child` was
                    // already unmounted inside Reconcile, so drop every tracking entry that
                    // points at it, tear the orphaned replacement down (otherwise its component
                    // effect cleanups leak — it is mounted but unreachable), and route the row
                    // back through the framework's realize channel. `child` cannot be
                    // un-parented from the repeater, so park it collapsed rather than leaving
                    // an unmounted ghost painted over the row. (Issue #919)
                    DetachFromParent(child);
                    ParkOrphan(child);
                    _keyByControl.Remove(child);
                    _lastElementByControl.Remove(child);
                    _mountedElements.Remove(key);
                    _reconciler.UnmountChild(replacement);
                    ScheduleReRealize(key);
                }
            }
            else
            {
                // In-place diff (same key) reused `child`. Keep the per-control
                // "last element" tracking in lockstep with _mountedElements.
                // Without this, a later RecycleElement→GetElement round-trip for
                // the same control would feed the pre-refresh Element to
                // Reconcile as oldElement and diff against a stale tree shape.
                // (PR #324 review)
                _lastElementByControl[child] = newElement;
            }
        }
    }

    // ── Deferred row re-realization (issue #919) ─────────────────────
    //
    // A realized ItemsRepeater container can only be *replaced* by the framework's own
    // realize channel (IElementFactory.GetElement), so when a row's root element type or key
    // changes in a way that cannot be diffed in place, we ask WinUI to recycle and re-realize
    // that row: swap the row's ReactorRow instance inside the internally-owned
    // ObservableCollection<ReactorRow>, which raises a Replace collection change.
    //
    // The swap is deferred onto the dispatcher because RefreshRealizedItems runs inside a
    // reconcile pass (often mid-layout), and mutating the items source there throws
    // "Cannot run layout in the middle of a collection change".
    private HashSet<string>? _pendingReRealize;
    private bool _reRealizeQueued;

    private void ScheduleReRealize(string key)
    {
        // Nothing to drive the swap through on the legacy (unkeyed) path — the row simply
        // keeps its current content until the framework recycles the container on scroll.
        if (_listState is null) return;

        (_pendingReRealize ??= new(global::System.StringComparer.Ordinal)).Add(key);
        if (_reRealizeQueued) return;

        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            // No dispatcher (headless harnesses): apply immediately. There is no layout pass
            // in flight in that configuration, so the collection change is safe.
            FlushReRealize();
            return;
        }

        _reRealizeQueued = true;
        if (!queue.TryEnqueue(() =>
        {
            _reRealizeQueued = false;
            FlushReRealize();
        }))
        {
            _reRealizeQueued = false;
            FlushReRealize();
        }
    }

    private void FlushReRealize()
    {
        var pending = _pendingReRealize;
        _pendingReRealize = null;
        if (pending is null || pending.Count == 0) return;

        var listState = _listState;
        if (listState is null) return;

        foreach (var key in pending)
        {
            if (!listState.ByKey.TryGetValue(key, out var row)) continue;
            var index = row.Index;
            if (index < 0 || index >= listState.Source.Count) continue;
            // The row may have moved (or been rebuilt) between scheduling and flushing.
            if (!ReferenceEquals(listState.Source[index], row)) continue;

            // A fresh instance is required: INotifyCollectionChanged consumers track items by
            // object identity, so replacing a row with itself is a no-op for the repeater.
            var fresh = new ReactorRow
            {
                Index = row.Index,
                Key = row.Key,
                PendingEnterAnimation = row.PendingEnterAnimation,
            };
            listState.ByKey[key] = fresh;
            listState.Source[index] = fresh;
        }
    }

    // <snippet:factory-shape>
    public UIElement GetElement(ElementFactoryGetArgs args)
    {
        // Resolve the realized data → (key, dataIndex). Three paths:
        //   1. Spec 042: args.Data is ReactorRow — read both off the row.
        //   2. Legacy: args.Data is int — index directly, synthetic key.
        //   3. Fallback: unknown shape, treat as index 0.
        string key;
        int index;
        bool keyed;
        switch (args.Data)
        {
            case ReactorRow row:
                key = row.Key;
                index = row.Index;
                keyed = true;
                break;
            case int i:
                index = i;
                key = i.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
                keyed = false;
                break;
            default:
                index = 0;
                key = "0";
                keyed = false;
                break;
        }

        if (index < 0 || index >= _items.Count)
            return new TextBlock { Text = "" };

        var item = _items[index];
        var element = BuildOrCache(key, item, index, keyed);

        UIElement? control;
        if (TryTakeCompatibleFromPool(element, out var reused, out var oldElement, out var parkedVisibility))
        {
            // Reuse a previously-recycled container. The framework still has
            // it parented to the ItemsRepeater, so the ViewManager.cpp:866
            // Append-skip kicks in and the visual tree stays stable.
            //
            // Undo the parking collapse from RecycleElement BEFORE reconciling.
            // Restore the exact pre-park value rather than forcing Visible: an
            // in-place diff whose Visibility modifier is unchanged writes
            // nothing, so forcing Visible would silently un-collapse a row the
            // author asked to hide.
            reused.Visibility = parkedVisibility;
            var replacement = _reconciler.Reconcile(oldElement, element, reused, _requestRerender);
            if (replacement is not null && !ReferenceEquals(replacement, reused))
            {
                // Pass-2 reuse: the row's key changed, so Reconcile unmounted the old
                // component (effect cleanups ran) and built a fresh wrapper. Move that
                // subtree back into the container we already have — returning the
                // replacement instead would strand `reused`, which cannot be un-parented
                // from an ItemsRepeater (see DetachFromParent). (Issues #326, #919.)
                if (_reconciler.TryAdoptRealizedReplacement(reused, replacement))
                {
                    control = reused;
                }
                else
                {
                    // Nothing can install `replacement` into `reused`. Park `reused`
                    // collapsed and forget it rather than leaving a live ghost row
                    // painted over the list. Do NOT return it to the pool: it was
                    // unmounted inside Reconcile, so its tracked Element no longer
                    // describes it.
                    DetachFromParent(reused);
                    ParkOrphan(reused);
                    _lastElementByControl.Remove(reused);
                    control = replacement;
                }
            }
            else
            {
                control = reused;
            }
        }
        else
        {
            control = _reconciler.Mount(element, _requestRerender);
        }

        _mountedElements[key] = element;
        if (control is not null)
        {
            _keyByControl[control] = key;
            _lastElementByControl[control] = element;
            if (_keyByControl.Count > _maxRealized) _maxRealized = _keyByControl.Count;

            // Issue #383: arm the multi-select checkmark flicker guard on the
            // realized container. Idempotent per container instance.
            // Intentionally scoped to ItemContainer (the ItemsView item-root
            // wrapper): LazyVStack/LazyHStack realize into plain panels via
            // ItemsRepeater, not ItemContainer, and the MultiSelectStates.Multiple
            // storyboard the guard collapses only ever runs for multi-select
            // ItemContainers — so widening this to all controls would be inert
            // work everywhere else. Do not "generalize" it.
            if (control is ItemContainer itemContainer)
                ItemContainerSelectionFlickerGuard.Ensure(itemContainer);
        }

        return control ?? new TextBlock { Text = "" };
    }
    // </snippet:factory-shape>

    // Pick a recycled container that can actually be reused for `element`,
    // newest-first (the most recently recycled container is the most likely to
    // be cache-warm and shape-identical). Two passes, in priority order:
    //
    //   1. CanUpdate → a pure in-place diff, the cheapest possible reuse.
    //   2. Two component wrappers of the same shape → CanUpdate is false (the
    //      row's key changed), so Reconcile unmounts the old component — running
    //      its effect cleanups — and mints a fresh wrapper, which
    //      TryAdoptRealizedReplacement then moves back into this still-parented
    //      Border. Same container, fresh per-item state. (Issue #326.)
    //      Adoption transplants only the component subtree, so the wrapper's own
    //      modifiers/extensions must already match or they would survive from the
    //      previous row.
    //
    // Issue #919: anything else must STAY in the pool. Handing it to Reconcile
    // would mint a different control, and the rejected one can never be removed
    // from an ItemsRepeater (see DetachFromParent) — so every root-type flip
    // used to strand one live, visible, arranged container per realized row,
    // unbounded, painting stale rows over the list. Leaving it pooled instead
    // bounds the working set at one realized window per root shape, which is
    // what WinUI's own RecyclePool does for multiple data templates.
    private bool TryTakeCompatibleFromPool(
        Element element,
        [NotNullWhen(true)] out UIElement? reused,
        [NotNullWhen(true)] out Element? oldElement,
        out Visibility parkedVisibility)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = _recyclePool.Count - 1; i >= 0; i--)
            {
                var entry = _recyclePool[i];
                if (!_lastElementByControl.TryGetValue(entry.Control, out var candidateElement))
                {
                    // Untracked pool entry (its tracking was dropped elsewhere) can
                    // never be reconciled — evict it so the scan stays short. It
                    // stays parented but collapsed, which is the best available
                    // outcome for a repeater child.
                    _recyclePool.RemoveAt(i);
                    ParkOrphan(entry.Control);
                    continue;
                }

                var usable = pass == 0
                    ? _reconciler.CanUpdate(candidateElement, element)
                    : entry.Control is Border
                        && candidateElement is ComponentElement oldComp
                        && element is ComponentElement newComp
                        && oldComp.ComponentType == newComp.ComponentType
                        && Equals(oldComp.Modifiers, newComp.Modifiers)
                        && Equals(oldComp.Extensions, newComp.Extensions);
                if (!usable) continue;

                _recyclePool.RemoveAt(i);
                reused = entry.Control;
                oldElement = candidateElement;
                parkedVisibility = entry.Visibility;
                return true;
            }
        }

        reused = null;
        oldElement = null;
        parkedVisibility = Visibility.Visible;
        return false;
    }

    // Park a container we can neither reuse nor un-parent. Collapsing is what
    // keeps it from rendering: an ItemsRepeater child it no longer owns is never
    // re-arranged, so it would otherwise keep painting at its last arranged
    // bounds on top of the live rows.
    private static void ParkOrphan(UIElement control) => control.Visibility = Visibility.Collapsed;

    // Detach a UIElement from whatever container it's parented to.
    //
    // NOTE: this canNOT detach a container realized directly by an ItemsRepeater.
    // Despite deriving from Panel in the C++ implementation, ItemsRepeater does
    // not project IPanel — `repeater is Panel` and even an ABI `.As<Panel>()`
    // both fail — so there is no Children collection to remove from and no public
    // API that un-parents a realized child. Callers must pair this with
    // ParkOrphan (collapse in place) for the repeater case. It is still the right
    // call for nested Panel/Border/ContentControl subtrees, so it's safe to call
    // on arbitrary recycled content.
    private static void DetachFromParent(UIElement control)
    {
        if (control is not FrameworkElement fe) return;
        switch (fe.Parent)
        {
            case Microsoft.UI.Xaml.Controls.Panel panel:
                panel.Children.Remove(fe);
                break;
            case Microsoft.UI.Xaml.Controls.Border border when ReferenceEquals(border.Child, fe):
                border.Child = null;
                break;
            case Microsoft.UI.Xaml.Controls.ContentControl cc when ReferenceEquals(cc.Content, fe):
                cc.Content = null;
                break;
        }
    }

    public void RecycleElement(ElementFactoryRecycleArgs args)
    {
        if (args.Element is null) return;

        // Drop the mounted-element tracking for this container so a later
        // RefreshRealizedItems can't run Reconcile against a stale Element
        // paired with a now-foreign realized child.
        if (_keyByControl.Remove(args.Element, out var stashedKey) && stashedKey is not null)
            _mountedElements.Remove(stashedKey);

        // DON'T UnmountChild — the WinUI tree stays alive and is reused on
        // the next GetElement call via Reconciler.Reconcile. ItemsRepeater
        // keeps the element parented either way (see ViewManager.cpp), so
        // tearing down Reactor state here would just be discarded work.
        // The _lastElementByControl entry stays valid for the next realize.
        //
        // Collapse while parked, remembering the visibility to restore. The
        // repeater stops arranging a recycled child but keeps it parented, so a
        // still-Visible one paints at its last arranged bounds — a ghost row over
        // the live list whenever the pool isn't drained in the same pass
        // (issue #919). This mirrors WinUI's own RecyclePool.
        var restore = args.Element is FrameworkElement fe ? fe.Visibility : Visibility.Visible;
        ParkOrphan(args.Element);
        _recyclePool.Add(new PoolEntry(args.Element, restore));

        TrimPool();
    }

    // Evict oldest-first once the pool exceeds the capacity its realized window
    // justifies. An evicted container is parked collapsed and untracked: it is
    // still parented (nothing can un-parent an ItemsRepeater child) but inert.
    // Without this, a keyed list — where every row root carries a per-item key,
    // so CanUpdate rejects every cross-item reuse — would retain one container
    // and one tracking entry per item scrolled past, and the reuse scan would
    // grow with it.
    private void TrimPool()
    {
        var capacity = PoolCapacity;
        if (_recyclePool.Count <= capacity) return;

        var excess = _recyclePool.Count - capacity;
        for (var i = 0; i < excess; i++)
        {
            var evicted = _recyclePool[i].Control;
            DetachFromParent(evicted);
            ParkOrphan(evicted);
            _lastElementByControl.Remove(evicted);
        }
        _recyclePool.RemoveRange(0, excess);
    }

}
