using System.Collections.Generic;

namespace Microsoft.UI.Reactor.Tests.Architecture.GenericProbe;

/// <summary>Stand-in "forbidden" type for the generic-instantiation boundary meta-test.</summary>
internal sealed class ForbiddenMarker;

/// <summary>
/// Probe whose IL references <see cref="ForbiddenMarker"/> <b>only</b> as a
/// generic type argument — never directly constructed, and never named in a
/// field/method signature. <c>new List&lt;ForbiddenMarker&gt;()</c>, the
/// <c>Add</c> call, and the <c>get_Count</c> call all encode as a
/// <c>MemberReference</c> whose parent is a <c>TypeSpecification</c>, which is
/// exactly the case the boundary scanner used to miss.
/// <see cref="CoreControlFamilyBoundaryTests"/> scans this type to prove the
/// generic-instantiation detection is not vacuous.
/// </summary>
internal static class GenericRefHolder
{
    internal static int Probe()
    {
        // Populate via the collection initializer so the list contents are
        // initialized (and the List<ForbiddenMarker>.Add member-ref — parent
        // TypeSpecification — is emitted) without ever directly constructing a
        // ForbiddenMarker, keeping ForbiddenMarker a generic-argument-only ref.
        var list = new List<ForbiddenMarker> { default! };
        return list.Count;
    }
}
