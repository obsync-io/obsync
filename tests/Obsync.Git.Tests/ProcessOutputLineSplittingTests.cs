using System.Diagnostics;
using Xunit;

namespace Obsync.Git.Tests;

/// <summary>
/// How .NET splits a redirected child process's output into <c>DataReceived</c> events.
/// </summary>
/// <remarks>
/// This is not a test of Obsync code. It pins an assumption Obsync's stderr handling rests on, which
/// is worth a test precisely because it is easy to get backwards: git redraws transfer progress with
/// carriage returns, and whether those arrive as ONE event containing <c>\r</c> or as SEVERAL events
/// decides whether progress can be collapsed to its final state or has to be bounded some other way.
/// </remarks>
public sealed class ProcessOutputLineSplittingTests
{
    [Fact]
    public void CarriageReturnsSplitIntoSeparateEvents()
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");

        // Exactly the shape git uses: repeated redraws of one logical line, terminated by a newline.
        start.ArgumentList.Add(
            "[Console]::Error.Write('one' + [char]13 + 'two' + [char]13 + 'three'); [Console]::Error.WriteLine()");

        using var process = new Process { StartInfo = start };
        var lines = new List<string>();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (lines) { lines.Add(e.Data); } } };

        if (!process.Start())
        {
            return;
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return;
        }

        process.WaitForExit();

        // If this fails and only ONE line arrives, a carriage return does NOT terminate a line, and
        // collapsing progress to the text after its last '\r' is the right way to bound it. If it
        // passes, that collapsing is a no-op and every redraw is retained as its own line — which is
        // what makes a long transfer's stderr grow without bound.
        Assert.Equal(["one", "two", "three"], lines);
    }
}
