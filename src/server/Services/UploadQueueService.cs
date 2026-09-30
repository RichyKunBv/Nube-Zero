using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NubeZero.Server.Services
{
    public sealed class UploadQueueFullException : Exception
    {
        public UploadQueueFullException() : base("La cola de subidas está llena.")
        {
        }
    }

    public sealed class UploadQueueService
    {
        private readonly object _sync = new object();
        private readonly Queue<QueueItem> _waiting = new Queue<QueueItem>();
        private readonly int _maxWaitingUploads;
        private bool _workerRunning;

        public UploadQueueService(int maxWaitingUploads = 3)
        {
            _maxWaitingUploads = maxWaitingUploads;
        }

        public Task EnqueueAsync(Func<Task> upload)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool startWorker = false;

            lock (_sync)
            {
                if (_waiting.Count >= _maxWaitingUploads)
                {
                    throw new UploadQueueFullException();
                }

                _waiting.Enqueue(new QueueItem(upload, completion));
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    startWorker = true;
                }
            }

            if (startWorker)
            {
                _ = ProcessQueueAsync();
            }

            return completion.Task;
        }

        private async Task ProcessQueueAsync()
        {
            while (true)
            {
                QueueItem item;
                lock (_sync)
                {
                    if (_waiting.Count == 0)
                    {
                        _workerRunning = false;
                        return;
                    }

                    item = _waiting.Dequeue();
                }

                try
                {
                    await item.Upload();
                    item.Completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }

        private sealed class QueueItem
        {
            public QueueItem(Func<Task> upload, TaskCompletionSource<bool> completion)
            {
                Upload = upload;
                Completion = completion;
            }

            public Func<Task> Upload { get; }
            public TaskCompletionSource<bool> Completion { get; }
        }
    }
}