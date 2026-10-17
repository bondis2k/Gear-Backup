using System;
using GearBackup.Gui;

partial class Tests
{
    [Test("Background requests: supersession, cancellation and disposal", Gui = true)]
    static void TestBackgroundRequests()
    {
        using (var work = new GuiBackgroundWork())
        {
            var first = work.Begin();
            var token = first.Token;
            Assert(work.IsRunning && first.CanPublish, "the current worker can publish");
            var second = work.Begin();
            Assert(token.IsCancellationRequested && !first.IsCurrent && !first.IsDisposed,
                "supersession cancels the old worker without disposing a source it may still use");
            Assert(token.WaitHandle.WaitOne(0), "a cancelled worker can still use its token before completion");
            first.Dispose();
            Assert(work.IsRunning && second.CanPublish, "old completion cannot clear the replacement worker");
            work.Cancel();
            Assert(second.IsCurrent && !second.CanPublish, "user cancellation permits a cancellation notice but blocks results");
            second.Dispose(); second.Dispose();
            Assert(!work.IsRunning && second.IsCurrent && second.IsDisposed, "completion is idempotent and retains request identity for queued UI callbacks");
            var third = work.Begin();
            work.Invalidate();
            Assert(!third.IsCurrent && !work.IsRunning && third.IsCancellationRequested, "invalidation blocks stale errors as well as results");
            third.Dispose();
            var last = work.Begin();
            work.Dispose();
            Assert(!last.IsCurrent && last.IsCancellationRequested && !last.IsDisposed, "closing cancels work and leaves disposal to its async owner");
            last.Dispose();
            bool rejected = false;
            try { work.Begin(); } catch (ObjectDisposedException) { rejected = true; }
            Assert(rejected, "a closed owner cannot start new work");
        }
    }
}
