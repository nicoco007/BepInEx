using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace BepInEx.Unity.IL2CPP.Threading;

internal sealed class Il2CppSynchronizationContext : SynchronizationContext
{
    private const int AWK_INITIAL_CAPACITY = 20;
    private readonly List<WorkRequest> asyncWorkQueue;
    private readonly List<WorkRequest> currentFrameWork = new(AWK_INITIAL_CAPACITY);
    private readonly int mainThreadID;
    private int trackedCount = 0;

    private Il2CppSynchronizationContext(int mainThreadID)
    {
        asyncWorkQueue = new List<WorkRequest>(AWK_INITIAL_CAPACITY);
        this.mainThreadID = mainThreadID;
    }

    private Il2CppSynchronizationContext(List<WorkRequest> queue, int mainThreadID)
    {
        asyncWorkQueue = queue;
        this.mainThreadID = mainThreadID;
    }

    // Send will process the call synchronously. If the call is processed on the main thread, we'll invoke it
    // directly here. If the call is processed on another thread it will be queued up like POST to be executed
    // on the main thread and it will wait. Once the main thread processes the work we can continue
    public override void Send(SendOrPostCallback callback, object state)
    {
        if (mainThreadID == Environment.CurrentManagedThreadId)
        {
            callback(state);
        }
        else
        {
            using ManualResetEvent waitHandle = new(false);

            lock (asyncWorkQueue)
            {
                asyncWorkQueue.Add(new WorkRequest(callback, state, waitHandle));
            }

            waitHandle.WaitOne();
        }
    }

    public override void OperationStarted() { Interlocked.Increment(ref trackedCount); }
    public override void OperationCompleted() { Interlocked.Decrement(ref trackedCount); }

    // Post will add the call to a task list to be executed later on the main thread then work will continue asynchronously
    public override void Post(SendOrPostCallback callback, object state)
    {
        lock (asyncWorkQueue)
        {
            asyncWorkQueue.Add(new WorkRequest(callback, state));
        }
    }

    // CreateCopy returns a new UnitySynchronizationContext object, but the queue is still shared with the original
    public override SynchronizationContext CreateCopy()
    {
        return new Il2CppSynchronizationContext(asyncWorkQueue, mainThreadID);
    }

    // Exec will execute tasks off the task list
    public void Exec()
    {
        lock (asyncWorkQueue)
        {
            currentFrameWork.AddRange(asyncWorkQueue);
            asyncWorkQueue.Clear();
        }

        // When you invoke work, remove it from the list to stop it being triggered again (case 1213602)
        while (currentFrameWork.Count > 0)
        {
            var work = currentFrameWork[0];
            currentFrameWork.RemoveAt(0);
            work.Invoke();
        }
    }

    private bool HasPendingTasks()
    {
        return asyncWorkQueue.Count != 0 || trackedCount != 0;
    }

    internal static void InitializeSynchronizationContext()
    {
        SetSynchronizationContext(new Il2CppSynchronizationContext(Environment.CurrentManagedThreadId));
    }

    internal static void ExecuteTasks()
    {
        if (Current is Il2CppSynchronizationContext context)
        {
            context.Exec();
        }
    }

    internal static bool ExecutePendingTasks(long millisecondsTimeout)
    {
        if (Current is not Il2CppSynchronizationContext context)
        {
            return true;
        }

        Stopwatch stopwatch = new();
        stopwatch.Start();

        while (context.HasPendingTasks())
        {
            if (stopwatch.ElapsedMilliseconds > millisecondsTimeout)
            {
                break;
            }

            context.Exec();
            Thread.Sleep(1);
        }

        return !context.HasPendingTasks();
    }

    private struct WorkRequest
    {
        private readonly SendOrPostCallback delagateCallback;
        private readonly object delagateState;
        private readonly ManualResetEvent waitHandle;

        public WorkRequest(SendOrPostCallback callback, object state, ManualResetEvent waitHandle = null)
        {
            delagateCallback = callback;
            delagateState = state;
            this.waitHandle = waitHandle;
        }

        public void Invoke()
        {
            try
            {
                delagateCallback(delagateState);
            }
            finally
            {
                waitHandle?.Set();
            }
        }
    }
}
