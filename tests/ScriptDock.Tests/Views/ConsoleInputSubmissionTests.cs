using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class ConsoleInputSubmissionTests
{
    [Fact]
    public void SuccessfulSendKeepsNewTextAndRejectsReentrantSubmission()
    {
        var submission = new ConsoleInputSubmission();

        Assert.True(submission.TryBegin("first", out var snapshot));
        Assert.False(submission.TryBegin("new text", out _));

        Assert.Equal("new text", submission.Complete(snapshot, sent: true, "new text"));
        Assert.True(submission.TryBegin("new text", out _));
    }

    [Fact]
    public void FailedSendRestoresSnapshotBeforeTextTypedWhileWaiting()
    {
        var submission = new ConsoleInputSubmission();
        Assert.True(submission.TryBegin("first", out var snapshot));

        Assert.Equal("first\nnew text", submission.Complete(snapshot, sent: false, "new text"));
    }
}
