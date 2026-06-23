namespace Microsoft.UI.Reactor.Core;

/// <summary>
/// An opt-in, always-equal wrapper for the delegate (callback) portion of a
/// component's props, so callback identity never forces a re-render.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Component{TProps}"/> memoizes against <c>!Equals(oldProps, newProps)</c>
/// in its <c>ShouldUpdate</c> method. For <c>record</c> props that is a field-by-field
/// compare, and <c>Action</c>/<c>Func</c> delegate fields
/// compare by <em>reference</em>. A parent typically passes a freshly-allocated delegate
/// (a lambda or local function) on every render, so a child's props compare unequal and
/// the child re-renders even though no observable data changed.
/// </para>
/// <para>
/// Historically apps worked around this by hand-writing <c>Equals</c>/<c>GetHashCode</c>
/// on every props record, listing only the data fields — ~10 lines each, with a silent
/// stale-UI bug if you forgot a field. Wrap the callbacks in <see cref="Callbacks{T}"/>
/// instead: its <see cref="Equals(Callbacks{T})"/> is constant <c>true</c> and
/// <see cref="GetHashCode"/> is constant <c>0</c>, so the outer record's auto-generated
/// equality treats the callbacks slot as always-equal — data fields still drive the memo
/// decision, callbacks never do.
/// </para>
/// <code>
/// public sealed record StepCardCallbacks(
///     Action&lt;int, string&gt; OnPromptChanged,
///     Action&lt;StepModel&gt; OnRun);
///
/// public sealed record StepCardProps(
///     StepModel Step,
///     bool IsGenerating,
///     Callbacks&lt;StepCardCallbacks&gt; Cb);   // ← no manual Equals needed
/// </code>
/// <para>
/// <strong>Stale-delegate guarantee.</strong> "Always-equal for diffing" must not mean
/// "stale delegate at invoke". When the reconciler skips a child's re-render because the
/// data is unchanged, it still refreshes the child's <see cref="Component{TProps}.Props"/>
/// with the latest props (see <c>Reconciler.ReconcileComponent</c>). So a handler that
/// reads <c>Props.Cb.Value.OnRun</c> <em>live</em> at event time always invokes the
/// <em>current</em> delegate, never the memoized one. Read callbacks off <c>Props</c> at
/// dispatch time — do not capture <c>Props.Cb.Value.OnRun</c> into a local at render time.
/// </para>
/// </remarks>
/// <typeparam name="T">
/// The payload carrying the callbacks. Usually a small record (or a single delegate)
/// holding only <c>Action</c>/<c>Func</c> delegate members.
/// </typeparam>
/// <param name="Value">The wrapped callbacks payload. Read it live at dispatch time.</param>
public sealed record Callbacks<T>(T Value)
{
    /// <summary>
    /// Always returns <c>true</c> for any non-null peer: two <see cref="Callbacks{T}"/>
    /// instances are considered equal regardless of the delegates they carry, so callback
    /// identity is excluded from the owning record's memo comparison.
    /// </summary>
    public bool Equals(Callbacks<T>? other) => other is not null;

    /// <summary>
    /// Always returns <c>0</c> so the always-equal contract is consistent with hashing
    /// (equal values must hash equally).
    /// </summary>
    public override int GetHashCode() => 0;

    /// <summary>
    /// Wraps a callbacks payload so it can be assigned directly to a
    /// <see cref="Callbacks{T}"/> props field without calling the constructor.
    /// </summary>
    public static implicit operator Callbacks<T>(T value) => new(value);
}

/// <summary>
/// Factory helpers for <see cref="Callbacks{T}"/> that allow the payload type to be
/// inferred at the call site.
/// </summary>
public static class Callbacks
{
    /// <summary>
    /// Creates a <see cref="Callbacks{T}"/> wrapper with <typeparamref name="T"/> inferred
    /// from <paramref name="value"/>.
    /// </summary>
    public static Callbacks<T> Of<T>(T value) => new(value);
}
