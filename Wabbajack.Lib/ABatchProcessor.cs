﻿using System;
using System.IO;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Wabbajack.Common;
using Wabbajack.VirtualFileSystem;

namespace Wabbajack.Lib
{
    public abstract class ABatchProcessor : IBatchProcessor
    {
        public WorkQueue Queue { get; private set; }
        private bool _configured = false;

        public void Dispose()
        {
            Queue?.Shutdown();
        }

        public Context VFS { get; private set; }

        protected StatusUpdateTracker UpdateTracker { get; private set; }

        private Subject<float> _percentCompleted { get; } = new Subject<float>();

        /// <summary>
        /// The current progress of the entire processing system on a scale of 0.0 to 1.0
        /// </summary>
        public IObservable<float> PercentCompleted => _percentCompleted;

        private Subject<string> _textStatus { get; } = new Subject<string>();

        /// <summary>
        /// The current status of the processor as a text string
        /// </summary>
        public IObservable<string> TextStatus => _textStatus;

        private Subject<CPUStatus> _queueStatus { get; } = new Subject<CPUStatus>();
        public IObservable<CPUStatus> QueueStatus => _queueStatus;

        private Subject<bool> _isRunning { get; } = new Subject<bool>();
        public IObservable<bool> IsRunning => _isRunning;
        
        private Thread _processorThread { get; set; }

        protected void ConfigureProcessor(int steps, int threads = 0)
        {
            if (_configured)
                throw new InvalidDataException("Can't configure a processor twice");
            Queue = new WorkQueue(threads);
            UpdateTracker = new StatusUpdateTracker(steps);
            Queue.Status.Subscribe(_queueStatus);
            UpdateTracker.Progress.Subscribe(_percentCompleted);
            UpdateTracker.StepName.Subscribe(_textStatus);
            VFS = new Context(Queue) { UpdateTracker = UpdateTracker };
            _configured = true;
        }

        public static int RecommendQueueSize(string folder)
        {
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            using (var queue = new WorkQueue())
            {
                Utils.Log($"Benchmarking {folder}");
                var raw_speed = Utils.TestDiskSpeed(queue, folder);
                Utils.Log($"{raw_speed.ToFileSizeString()}/sec for {folder}");
                int speed = (int)(raw_speed / 1024 / 1024);

                // Less than 100MB/sec, stick with two threads.
                return speed < 100 ? 2 : Math.Min(Environment.ProcessorCount, speed / 100 * 2);
            }
        }

        protected abstract bool _Begin();
        public Task<bool> Begin()
        {
            _isRunning.OnNext(true);
            var _tcs = new TaskCompletionSource<bool>();
            if (_processorThread != null)
            {
                throw new InvalidDataException("Can't start the processor twice");
            }

            _processorThread = new Thread(() =>
            {
                try
                {
                    _tcs.SetResult(_Begin());
                }
                catch (Exception ex)
                {
                    _tcs.SetException(ex);
                }
                finally
                {
                    _isRunning.OnNext(false);
                }
            });
            _processorThread.Priority = ThreadPriority.BelowNormal;
            _processorThread.Start();
            return _tcs.Task;
        }

        public void Terminate()
        {
            
            Queue?.Shutdown();
            //_processorThread?.Abort();
            _isRunning.OnNext(false);
        }
    }
}
