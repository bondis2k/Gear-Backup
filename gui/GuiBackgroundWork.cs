using System;
using System.Threading;

namespace GearBackup.Gui
{
    // Owned by the UI thread. Workers receive only Request.Token; result handlers check
    // IsCurrent/CanPublish again on the UI thread, including inside queued callbacks.
    internal sealed class GuiBackgroundWork : IDisposable
    {
        Request current, active;
        bool closed;
        internal bool IsRunning { get { return active != null; } }
        internal Request Current { get { return current; } }

        internal Request Begin()
        {
            if (closed) throw new ObjectDisposedException("GuiBackgroundWork");
            Invalidate();
            return current = active = new Request(this);
        }

        // A user cancellation stays current so its completion can display "Cancelled".
        internal void Cancel() { if (active != null) active.Cancel(); }

        // Superseded requests must not publish either results or errors. Their async
        // owners still dispose them in finally, after the worker has stopped.
        internal void Invalidate() { current = null; Cancel(); active = null; }
        public void Dispose() { closed = true; Invalidate(); }

        internal sealed class Request : IDisposable
        {
            readonly GuiBackgroundWork owner;
            readonly CancellationTokenSource source = new CancellationTokenSource();
            internal readonly CancellationToken Token;
            internal bool IsDisposed { get; private set; }
            internal bool IsCurrent { get { return !owner.closed && ReferenceEquals(owner.current, this); } }
            internal bool CanPublish { get { return IsCurrent && !Token.IsCancellationRequested; } }
            internal bool IsCancellationRequested { get { return Token.IsCancellationRequested; } }
            internal Request(GuiBackgroundWork owner) { this.owner = owner; Token = source.Token; }
            internal void Cancel() { if (!IsDisposed) source.Cancel(); }
            public void Dispose()
            {
                if (IsDisposed) return;
                if (ReferenceEquals(owner.active, this)) owner.active = null;
                source.Dispose(); IsDisposed = true;
            }
        }
    }
}
