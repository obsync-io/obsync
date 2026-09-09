using Obsync.Shared.Models;
using Obsync.Shared.Objects;

namespace Obsync.Shared.Scripting;

/// <summary>
/// An object Obsync has observed, more than once and under identical evidence, that it cannot
/// script.
/// </summary>
/// <remarks>
/// This is an observation, not a human acknowledgement. At the moment of a skip, a transient failure
/// and a permanent one are indistinguishable — the difference is only visible over time. An object
/// skipped again, for the same reason, at the same <c>modify_date</c>, is not going to succeed next
/// time either, and holding its type's watermark hostage for it costs more on every subsequent run
/// forever.
/// </remarks>
/// <param name="Identity">The object.</param>
/// <param name="ModifyDate">
/// The <c>modify_date</c> the failure was observed at. This is the release condition: a changed date
/// is the only evidence that anything about the object is different, so quarantine ends there and
/// the object is attempted again.
/// </param>
/// <param name="Reason">
/// The skip reason as the provider reported it. Part of the evidence: the same object failing for a
/// DIFFERENT reason is a new observation, not a confirmation of the old one.
/// </param>
/// <param name="FirstSeenAt">When the failure was first recorded — answers "how long has this been broken".</param>
/// <param name="LastSeenAt">When it was last observed.</param>
public sealed record QuarantinedObject(
    ScriptedObjectIdentity Identity,
    DateTime ModifyDate,
    string Reason,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
