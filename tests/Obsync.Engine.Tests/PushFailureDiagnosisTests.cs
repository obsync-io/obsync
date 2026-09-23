using Obsync.Engine;

namespace Obsync.Engine.Tests;

/// <summary>
/// What the user is told when a push is refused.
/// </summary>
/// <remarks>
/// The strings under test reach the dashboard, alert emails and exported reports, so a wrong one
/// sends somebody to fix the wrong thing. The stderr in these tests is the shape git and GitHub
/// actually emit — including the generic <c>! [remote rejected]</c> line that follows every
/// server-side refusal, which is exactly what made the old bare "rejected" match unsafe.
/// </remarks>
public sealed class PushFailureDiagnosisTests
{
    /// <summary>A ruleset refusal, as GitHub prints it. This is the case that shipped unhandled.</summary>
    private const string RuleViolation = """
        git push failed: remote: error: GH013: Repository rule violations found for refs/heads/main.
        remote:
        remote: - Changes must be made through a pull request.
        remote:
        To https://github.com/acme/schema-history.git
         ! [remote rejected] main -> main (push declined due to repository rule violations)
        error: failed to push some refs to 'https://github.com/acme/schema-history.git'
        """;

    /// <summary>Classic branch protection, which uses a different code and a different system.</summary>
    private const string ProtectedBranch = """
        git push failed: remote: error: GH006: Protected branch update failed for refs/heads/main.
        remote: error: Changes must be made through a pull request.
        To https://github.com/acme/schema-history.git
         ! [remote rejected] main -> main (protected branch hook declined)
        error: failed to push some refs to 'https://github.com/acme/schema-history.git'
        """;

    /// <summary>A genuinely stale local branch — the only case that should say "pull/merge".</summary>
    private const string NonFastForward = """
        git push failed: To https://github.com/acme/schema-history.git
         ! [rejected]        main -> main (non-fast-forward)
        error: failed to push some refs to 'https://github.com/acme/schema-history.git'
        hint: Updates were rejected because the tip of your current branch is behind
        """;

    [Fact]
    public void ARulesetRejection_IsRecognised_AndNamesTheRule()
    {
        var reason = PushFailureDiagnosis.Explain(RuleViolation);

        Assert.Contains("ruleset", reason, StringComparison.OrdinalIgnoreCase);
        // The bullet is the only part that says what to DO. Reporting "GH013" without it tells the
        // user a push failed and nothing more.
        Assert.Contains("Changes must be made through a pull request", reason);
        Assert.Contains("Pull request mode", reason);
    }

    [Fact]
    public void ARulesetRejection_IsNotMistakenForAStaleBranch()
    {
        // The regression this class exists for. Every server-side refusal contains
        // "! [remote rejected]", so a bare match on "rejected" diagnosed a repository POLICY as a
        // branch that had moved on, and sent the user to pull and merge a branch that was fine.
        var reason = PushFailureDiagnosis.Explain(RuleViolation);

        Assert.DoesNotContain("pull/merge", reason);
        Assert.DoesNotContain("commits Obsync does not have", reason);
    }

    [Fact]
    public void ClassicBranchProtection_KeepsItsOwnExplanation()
    {
        var reason = PushFailureDiagnosis.Explain(ProtectedBranch);

        Assert.Contains("protected", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pull request mode", reason);
        Assert.DoesNotContain("pull/merge", reason);
    }

    [Fact]
    public void AStaleBranch_StillSaysPullAndMerge()
    {
        // The tightened match must not lose the case it was written for.
        var reason = PushFailureDiagnosis.Explain(NonFastForward);

        Assert.Contains("pull/merge", reason);
    }

    [Fact]
    public void AnUnknownGitHubPolicyCode_IsReportedHonestly_NotGuessedAt()
    {
        // A code newer than this build, or an organisation policy nobody here has seen. Saying what
        // is definitely true beats guessing: a wrong diagnosis costs more than "look here".
        const string unknown = """
            git push failed: remote: error: GH099: Some future policy refused this push.
            To https://github.com/acme/schema-history.git
             ! [remote rejected] main -> main (policy declined)
            """;

        var reason = PushFailureDiagnosis.Explain(unknown);

        Assert.Contains("refused the push", reason);
        Assert.Contains("technical details", reason);
        Assert.DoesNotContain("pull/merge", reason);
    }

    [Fact]
    public void MultipleViolatedRules_AreAllReported()
    {
        const string several = """
            remote: error: GH013: Repository rule violations found for refs/heads/main.
            remote: - Changes must be made through a pull request.
            remote: - Required status check "build" is expected.
            """;

        var reason = PushFailureDiagnosis.Explain(several);

        Assert.Contains("Changes must be made through a pull request", reason);
        Assert.Contains("Required status check", reason);
    }

    [Fact]
    public void ARuleRejectionWithNoBullets_StillExplainsTheCause()
    {
        // GitHub does not always enumerate the rules; the explanation must not depend on it.
        var reason = PushFailureDiagnosis.Explain(
            "remote: error: GH013: Repository rule violations found for refs/heads/main.");

        Assert.Contains("ruleset", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pull request mode", reason);
    }

    [Theory]
    [InlineData("remote: Permission to acme/x.git denied to bot.", "write (Contents) permission")]
    [InlineData("fatal: Authentication failed for 'https://github.com/acme/x.git/'", "credentials")]
    [InlineData("remote: error: GH001: Large files detected.", "100 MB limit")]
    public void TheOtherKnownCauses_AreUnchanged(string error, string expected)
    {
        Assert.Contains(expected, PushFailureDiagnosis.Explain(error));
    }

    [Theory]
    // Obsync's own PR branch names end in eight hex characters of the job's GUID, and git echoes
    // the ref in every push failure — so "403" and "401" appeared as ordinary content in output
    // that had nothing to do with credentials. Matched as bare substrings, they beat the
    // non-fast-forward test below them and permanently misdiagnosed every push failure for roughly
    // one job in three hundred, sending the user to replace a perfectly valid token.
    [InlineData(
        " ! [rejected]        obsync/sales/a403f2c1 -> obsync/sales/a403f2c1 (non-fast-forward)\n"
        + "error: failed to push some refs to 'https://github.com/acme/x.git'")]
    [InlineData(
        " ! [rejected]        obsync/sales/9401bd7e -> obsync/sales/9401bd7e (non-fast-forward)\n"
        + "hint: Updates were rejected because the tip of your current branch is behind")]
    [InlineData(
        "fatal: unable to access 'https://github.com/acme/issue-403-fix.git/'\n"
        + "error: failed to push some refs (fetch first)")]
    public void AHexRefOrRepositoryNameContainingAStatusCode_IsNotReadAsACredentialProblem(string error)
    {
        var reason = PushFailureDiagnosis.Explain(error);

        Assert.DoesNotContain("write (Contents) permission", reason);
        Assert.DoesNotContain("access token is valid", reason);
    }

    [Theory]
    [InlineData("remote: HTTP 403 Forbidden while accessing https://github.com/acme/x.git", "write (Contents) permission")]
    [InlineData("fatal: unable to access 'https://github.com/acme/x.git/': The requested URL returned error: 401", "credentials")]
    public void ARealStatusCode_IsStillRecognised(string error, string expected)
    {
        // The narrowing above must not cost a genuine 401/403, which git prints delimited.
        Assert.Contains(expected, PushFailureDiagnosis.Explain(error));
    }

    [Fact]
    public void AnUnrecognisedError_FallsBackToItsFirstLine()
    {
        Assert.Equal("something entirely new", PushFailureDiagnosis.Explain("something entirely new\nand more"));
    }

    [Fact]
    public void NoError_DoesNotProduceAnEmptyMessage()
    {
        Assert.Contains("technical details", PushFailureDiagnosis.Explain(null));
        Assert.Contains("technical details", PushFailureDiagnosis.Explain("   "));
    }
}
