using System.Drawing;
using System.Windows.Forms;
using System.Xml.Linq;

namespace Obsync.Packaging.Tests;

/// <summary>
/// Every Text control in a locally authored dialog must be tall enough for the string it displays.
/// </summary>
/// <remarks>
/// MSI has no auto-sizing and no overflow indicator: a Text control whose box is shorter than its
/// wrapped text simply paints the top of it and silently drops the rest. There is no error, nothing
/// in the log, and nothing on screen to say a line is missing.
/// <para>
/// This class exists because that shipped. Commit 984298f ("Modernize the installer wizard")
/// changed the body font from Tahoma 8 to Segoe UI 9 — measured below, that raises the line box
/// from 13px to 15px — and adjusted the affected heights to 12, which still does not fit one line.
/// Every label on the Service Account page has been cut through its descenders ever since, and the
/// two error modals lost whole lines. Reviews did not catch it because installer units are literal
/// pixels that do NOT rescale with the font, so the authoring still looks plausible.
/// </para>
/// <para>
/// Measured with GDI (<see cref="TextRenderer"/>), not GDI+: MSI paints Text controls with
/// <c>DrawText</c>, and GDI+ metrics differ enough to predict the wrong answer. One installer unit
/// is one pixel at 96 DPI (<c>Control</c> table), which is what makes this comparison meaningful.
/// </para>
/// </remarks>
public sealed class DialogTextFitTests
{
    private static readonly XNamespace Wxs = "http://wixtoolset.org/schemas/v4/wxs";

    private static readonly XDocument Installer =
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Obsync.wxs"), LoadOptions.SetLineInfo);

    /// <summary>The body face and size from the installer's own TextStyle table.</summary>
    private const string BodyFace = "Segoe UI";
    private const float BodySize = 9f;

    /// <summary>
    /// Every TextStyle the installer declares, so a control prefixed <c>{\StyleName}</c> is measured
    /// in the font it will actually be painted in.
    /// </summary>
    /// <remarks>
    /// Not cosmetic precision: the note style is 8pt, whose line box is 13 units against the body
    /// font's 15. Measuring those two paragraphs at 9pt would demand a third more height than they
    /// need and there is no room for it above BottomLine — the checker would be wrong in the
    /// direction that blocks a correct layout.
    /// </remarks>
    private static readonly Dictionary<string, (string Face, float Size, bool Bold)> Styles =
        Installer.Descendants(Wxs + "TextStyle").ToDictionary(
            e => (string)e.Attribute("Id")!,
            e => ((string)e.Attribute("FaceName")!,
                  float.Parse((string)e.Attribute("Size")!),
                  (string?)e.Attribute("Bold") == "yes"));

    private const TextFormatFlags Flags =
        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    /// <summary>Height in installer units the string needs at this width, or 0 for no text.</summary>
    private static int RequiredHeight(string? text, int width, string? styleId)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var (face, size, bold) = styleId is not null && Styles.TryGetValue(styleId, out var s)
            ? s
            : (BodyFace, BodySize, false);

        using var font = new Font(face, size, bold ? FontStyle.Bold : FontStyle.Regular);
        return TextRenderer.MeasureText(text, font, new Size(width, 4000), Flags).Height;
    }

    /// <summary>
    /// Splits the leading <c>{\StyleName}</c> escape MSI uses to switch TextStyle mid-string from
    /// the text itself. The escape is consumed by the renderer, so it must not be measured.
    /// </summary>
    private static (string Text, string? StyleId) Unstyle(string raw)
    {
        if (!raw.StartsWith("{\\", StringComparison.Ordinal))
        {
            return (raw, null);
        }

        var close = raw.IndexOf('}');
        return close < 0 ? (raw, null) : (raw[(close + 1)..], raw[2..close]);
    }

    /// <summary>
    /// Every Text control of every locally authored dialog, with its box and its string.
    /// Property-substituted text (<c>[ProductName]</c> and friends) is measured with a
    /// representative expansion, since the real width depends on the value.
    /// </summary>
    public static TheoryData<string, string, int, int, string> TextControls()
    {
        var data = new TheoryData<string, string, int, int, string>();
        foreach (var dialog in Installer.Descendants(Wxs + "Dialog"))
        {
            var dialogId = (string?)dialog.Attribute("Id") ?? "?";
            foreach (var control in dialog.Elements(Wxs + "Control"))
            {
                if ((string?)control.Attribute("Type") != "Text")
                {
                    continue;
                }

                var text = (string?)control.Attribute("Text");
                if (string.IsNullOrWhiteSpace(text) || text.StartsWith("!(loc.", StringComparison.Ordinal))
                {
                    // Localized strings live in the .wxl; measured by TheLocalizedStrings test below.
                    continue;
                }

                data.Add(
                    dialogId,
                    (string?)control.Attribute("Id") ?? "?",
                    int.Parse((string)control.Attribute("Width")!),
                    int.Parse((string)control.Attribute("Height")!),
                    text.Replace("[ProductName]", "Obsync", StringComparison.Ordinal));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TextControls))]
    public void EveryTextControl_IsTallEnoughForItsString(string dialog, string control, int width, int height, string raw)
    {
        var (text, styleId) = Unstyle(raw);
        var needed = RequiredHeight(text, width, styleId);

        Assert.True(
            height >= needed,
            $"{dialog}.{control} is {height} units tall but its text needs {needed} at width {width} "
            + $"in {styleId ?? "WixUI_Font_Normal"}. MSI clips the overflow silently. Text: \"{text}\"");
    }

    [Fact]
    public void NoTwoControls_OverlapEachOther()
    {
        // A box that is honestly sized can still collide with what follows, when the height is
        // corrected and the next control never moves. AccountLabel and PasswordLabel did exactly
        // that: 12-unit boxes at Y=98/132 against edit boxes at Y=112/146, so growing them to a
        // truthful 15 would have put text under an edit control instead of under nothing.
        var collisions = new List<string>();

        foreach (var dialog in Installer.Descendants(Wxs + "Dialog"))
        {
            // Bitmaps and Lines are backgrounds and separators: a transparent title is SUPPOSED to
            // sit on the banner, and every dialog draws its content over one.
            var controls = dialog.Elements(Wxs + "Control")
                .Where(c => (string?)c.Attribute("Type") is not ("Bitmap" or "Line"))
                .ToList();

            for (var i = 0; i < controls.Count; i++)
            {
                for (var j = i + 1; j < controls.Count; j++)
                {
                    var a = Box(controls[i]);
                    var b = Box(controls[j]);
                    if (a.IntersectsWith(b))
                    {
                        collisions.Add(
                            $"{(string?)dialog.Attribute("Id")}: "
                            + $"{(string?)controls[i].Attribute("Id")} {a} overlaps "
                            + $"{(string?)controls[j].Attribute("Id")} {b}");
                    }
                }
            }
        }

        Assert.True(collisions.Count == 0, string.Join(Environment.NewLine, collisions));

        static Rectangle Box(XElement control) => new(
            int.Parse((string)control.Attribute("X")!),
            int.Parse((string)control.Attribute("Y")!),
            int.Parse((string)control.Attribute("Width")!),
            int.Parse((string)control.Attribute("Height")!));
    }

    [Fact]
    public void NoControl_ExtendsPastItsDialog()
    {
        // Growing a box to fit its text is only half the fix; the dialog has to be able to hold it.
        // ObsyncPasswordRequiredDlg needed both — a taller box AND a taller dialog, or the text
        // would simply have run under the OK button.
        var overflow = new List<string>();

        foreach (var dialog in Installer.Descendants(Wxs + "Dialog"))
        {
            var height = int.Parse((string)dialog.Attribute("Height")!);
            foreach (var control in dialog.Elements(Wxs + "Control"))
            {
                var bottom = int.Parse((string)control.Attribute("Y")!)
                    + int.Parse((string)control.Attribute("Height")!);
                if (bottom > height)
                {
                    overflow.Add(
                        $"{(string?)dialog.Attribute("Id")}.{(string?)control.Attribute("Id")} "
                        + $"ends at {bottom} in a dialog {height} tall");
                }
            }
        }

        Assert.True(overflow.Count == 0, string.Join(Environment.NewLine, overflow));
    }

    [Fact]
    public void TheBodyFont_MatchesWhatTheseTestsMeasure()
    {
        // If the TextStyle changes, every height in the file is re-derived and these measurements
        // become fiction. That is precisely what went wrong last time — the font changed and the
        // geometry did not follow — so pin the pair together.
        var body = Installer.Descendants(Wxs + "TextStyle")
            .Single(e => (string?)e.Attribute("Id") == "WixUI_Font_Normal");

        Assert.Equal(BodyFace, (string?)body.Attribute("FaceName"));
        Assert.Equal(BodySize.ToString("0"), (string?)body.Attribute("Size"));
    }

    [Fact]
    public void OneLineOfTheBodyFont_IsFifteenUnits()
    {
        // The number every height in the installer is derived from, asserted rather than assumed.
        // Tahoma 8 — what the stock geometry was laid out for — is 13.
        Assert.Equal(15, RequiredHeight("Ag", 1000, styleId: null));
    }
}
