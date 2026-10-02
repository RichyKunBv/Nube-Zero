using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NubeZero.Server.Services
{
    public sealed class FileTransferQueueFullException : Exception
    {
        public FileTransferQueueFullException() : base("La cola de transferencias está llena.")
        {
        }
    }

    public sealed class FileTransferQueueService
    {
        private readonly object _sync = new object();
        private readonly Queue<QueueItem> _waiting = new Queue<QueueItem>();
        private readonly int _maxWaitingTransfers;
        private bool _workerRunning;

        public FileTransferQueueService(int maxWaitingTransfers = 3)
        {
            _maxWaitingTransfers = maxWaitingTransfers;
        }

        public Task EnqueueAsync(Func<Task> transfer)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool startWorker = false;

            lock (_sync)
            {
                if (_waiting.Count >= _maxWaitingTransfers)
                {
                    throw new FileTransferQueueFullException();
                }

                _waiting.Enqueue(new QueueItem(transfer, completion));
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
                    await item.Transfer();
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
            public QueueItem(Func<Task> transfer, TaskCompletionSource<bool> completion)
            {
                Transfer = transfer;
                Completion = completion;
            }

            public Func<Task> Transfer { get; }
            public TaskCompletionSource<bool> Completion { get; }
        }
    }
}
