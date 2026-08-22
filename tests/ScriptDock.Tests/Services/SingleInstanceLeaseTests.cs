using System;
using System.Threading;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

public sealed class SingleInstanceLeaseTests
{
    [Fact]
    public void ASecondThreadCannotOwnTheAppNamespaceConcurrently()
    {
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? ownerFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                Assert.True(SingleInstanceLease.TryAcquire(out var lease));
                using (lease)
                {
                    acquired.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex)
            {
                ownerFailure = ex;
                acquired.Set();
            }
        });

        owner.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Null(ownerFailure);

        Assert.False(SingleInstanceLease.TryAcquire(out var duplicate));
        Assert.Null(duplicate);

        release.Set();
        Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(ownerFailure);
    }
}
