using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PuertsUnityMcp
{
    public static class UnityMcpMainThread
    {
        private static readonly ConcurrentQueue<Action> Pending = new ConcurrentQueue<Action>();
        private static int mainThreadId;

        public static bool IsMainThread => mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == mainThreadId;

        public static void Initialize()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static void Post(Action action)
        {
            if (action == null)
            {
                return;
            }

            Pending.Enqueue(action);
        }

        public static Task<T> InvokeAsync<T>(Func<T> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (IsMainThread)
            {
                try
                {
                    return Task.FromResult(action());
                }
                catch (Exception ex)
                {
                    var failed = new TaskCompletionSource<T>();
                    failed.SetException(ex);
                    return failed.Task;
                }
            }

            var completion = new TaskCompletionSource<T>();
            Pending.Enqueue(() =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return completion.Task;
        }

        public static Task InvokeAsync(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return InvokeAsync(() =>
            {
                action();
                return true;
            });
        }

        public static Task<T> InvokeAsync<T>(Func<Task<T>> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (IsMainThread)
            {
                try
                {
                    return action();
                }
                catch (Exception ex)
                {
                    var failed = new TaskCompletionSource<T>();
                    failed.SetException(ex);
                    return failed.Task;
                }
            }

            var completion = new TaskCompletionSource<T>();
            Pending.Enqueue(async () =>
            {
                try
                {
                    completion.SetResult(await action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return completion.Task;
        }

        public static void Drain(int maxActions = 256)
        {
            InitializeIfNeeded();

            var processed = 0;
            while (processed < maxActions && Pending.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[UnityMCP] Main thread action failed: " + ex);
                }

                processed++;
            }
        }

        private static void InitializeIfNeeded()
        {
            if (mainThreadId == 0)
            {
                Initialize();
            }
        }
    }
}
