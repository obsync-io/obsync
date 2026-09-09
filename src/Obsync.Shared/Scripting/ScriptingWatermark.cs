namespace Obsync.Shared.Scripting;

/// <summary>
/// A watermark and the conditions under which it may be trusted.
/// </summary>
/// <remarks>
/// A bare timestamp cannot express any of the things that have to hold for a skip to be safe, so the
/// decision was unconditional in exactly the places it should have been conditional. This record is
/// the validity record that replaces it: the value, plus what has to still be true for the value to
/// mean anything.
/// </remarks>
/// <param name="Value">
/// The maximum <c>sys.objects.modify_date</c> scripted for this type. An opaque server-local
/// datetime, compared verbatim and never converted between time zones.
/// </param>
/// <param name="Fingerprint">
/// The <see cref="EmissionFingerprint"/> in force when this watermark was written, or <c>null</c>
/// for a row written before fingerprints existed. A row whose fingerprint differs from the current
/// one cannot be trusted: the objects below it were scripted by a configuration that no longer
/// produces the same bytes.
/// </param>
public sealed record ScriptingWatermark(DateTime Value, string? Fingerprint);
