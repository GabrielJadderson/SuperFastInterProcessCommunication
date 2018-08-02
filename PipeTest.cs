using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Super_Fast_Inter_Process_Communication
{
    public class PipeTest
    {
        public PipeTest()
        {
            //Demo();
            RunStressTest();
        }


        #region Demo

        void Demo()
        {
            string pipeName = "test" + new Random().Next();

            using (var inPipe = new InPipe(pipeName, true, Console.WriteLine))
            using (var outPipe = new OutPipe(pipeName, false))
            {
                outPipe.Write(new byte[] { 1, 2, 3 });
                outPipe.Write(new byte[] { 1, 2, 3, 4, 5 });
                Console.ReadLine();
            }
        }

        void RunStressTest()
        {
            var cancelSource = new CancellationTokenSource();
            StressTestReceive(cancelSource.Token);
            Task.Run(() => StressTestSend(cancelSource.Token));
            Console.ReadLine();
            cancelSource.Cancel();
        }

        void StressTestSend(CancellationToken cancelToken)
        {
            //Console.WriteLine("Running...");

            SHA1 hasher = SHA1.Create();
            const int hashLength = 20;

            var random = new Random();
            var start = Stopwatch.StartNew();

            using (var outPipe = new OutPipe("test", false))
                while (!cancelToken.IsCancellationRequested)
                {
                    int bufferLength = 20 + random.Next(60);
                    var buffer = new byte[bufferLength + hashLength];
                    random.NextBytes(buffer);
                    var hash = hasher.ComputeHash(buffer, 0, bufferLength);
                    Array.Copy(hash, 0, buffer, bufferLength, hashLength);

                    outPipe.Write(buffer);
                }

            Console.WriteLine("Done");
        }

        void StressTestReceive(CancellationToken cancelToken)
        {
            SHA1 hasher = SHA1.Create();
            const int hashLength = 20;
            int successes = 0, failures = 0;
            var start = Stopwatch.StartNew();
            bool firstMessage = true;

            var inPipe = new InPipe("test", true, msg =>
            {
                if (firstMessage) start.Restart();
                firstMessage = false;

                var hash = hasher.ComputeHash(msg, 0, msg.Length - hashLength);

                if (hash.SequenceEqual(msg.Skip(msg.Length - hashLength)))
                    successes++;
                else
                    failures++;

                if ((successes + failures) % 1000 == 0)
                {
                    Console.WriteLine("Successes: " + successes);
                    Console.WriteLine("Failures: " + failures);
                    Console.WriteLine("Performance:" + Math.Round(successes / start.Elapsed.TotalSeconds, 0) + " messages per second.");

                }

                Console.SetCursorPosition(0, 0);
            });

            cancelToken.Register(() => inPipe.Dispose());
        }

        #endregion Demo

        private class SafeMemoryMappedFile : SafeDisposable
        {
            readonly MemoryMappedFile _mmFile;
            readonly MemoryMappedViewAccessor _accessor;
            unsafe byte* _pointer;

            public int Length { get; private set; }

            public MemoryMappedViewAccessor Accessor {
                get { AssertSafe(); return _accessor; }
            }

            public unsafe byte* Pointer {
                get { AssertSafe(); return _pointer; }
            }

            public unsafe SafeMemoryMappedFile(MemoryMappedFile mmFile)
            {
                _mmFile = mmFile;
                _accessor = _mmFile.CreateViewAccessor();
                _pointer = (byte*)_accessor.SafeMemoryMappedViewHandle.DangerousGetHandle().ToPointer();
                Length = (int)_accessor.Capacity;
            }

            unsafe protected override void DisposeCore()
            {
                base.DisposeCore();
                _accessor.Dispose();
                _mmFile.Dispose();
                _pointer = null;
            }
        }

        private abstract class MutexFreePipe : SafeDisposable
        {
            protected const int MinimumBufferSize = 0x10000;
            protected readonly int MessageHeaderLength = sizeof(int);
            protected readonly int StartingOffset = sizeof(int) + sizeof(bool);

            public readonly string Name;
            protected readonly EventWaitHandle NewMessageSignal;
            protected SafeMemoryMappedFile Buffer;
            protected int Offset, Length;

            protected MutexFreePipe(string name, bool createBuffer)
            {
                Name = name;

                var mmFile = createBuffer
                    ? MemoryMappedFile.CreateNew(name + ".0", MinimumBufferSize, MemoryMappedFileAccess.ReadWrite)
                    : MemoryMappedFile.OpenExisting(name + ".0");

                Buffer = new SafeMemoryMappedFile(mmFile);
                NewMessageSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".signal");

                Length = Buffer.Length;
                Offset = StartingOffset;
            }

            protected override void DisposeCore()
            {
                base.DisposeCore();
                Buffer.Dispose();
                NewMessageSignal.Dispose();
            }
        }

        private class OutPipe : MutexFreePipe
        {
            int _messageNumber;
            int _bufferCount;
            readonly List<SafeMemoryMappedFile> _oldBuffers = new List<SafeMemoryMappedFile>();
            public int PendingBuffers => _oldBuffers.Count;

            public OutPipe(string name, bool createBuffer) : base(name, createBuffer)
            {
            }

            public unsafe void Write(byte[] data)
            {
                lock (DisposeLock)                 // If there are multiple threads, write just one message at a time
                {
                    AssertSafe();
                    if (data.Length > Length - Offset - 8)
                    {
                        // Not enough space left in the shared memory buffer to write the message.
                        WriteContinuation(data.Length);
                    }
                    WriteMessage(data);
                    NewMessageSignal.Set();    // Signal reader that a message is available
                }
            }

            unsafe void WriteMessage(byte[] block)
            {
                byte* ptr = Buffer.Pointer;
                byte* offsetPointer = ptr + Offset;

                var msgPointer = (int*)offsetPointer;
                *msgPointer = block.Length;

                Offset += MessageHeaderLength;
                offsetPointer += MessageHeaderLength;

                if (block != null && block.Length > 0)
                {
                    //MMF.Accessor.WriteArray (Offset, block, 0, block.Length);   // Horribly slow. No. No. No.
                    Marshal.Copy(block, 0, new IntPtr(offsetPointer), block.Length);
                    Offset += block.Length;
                }

                // Write the latest message number to the start of the buffer:
                int* iptr = (int*)ptr;
                *iptr = ++_messageNumber;
            }

            void WriteContinuation(int messageSize)
            {
                // First, allocate a new buffer:
                string newName = Name + "." + ++_bufferCount;
                int newLength = Math.Max(messageSize * 10, MinimumBufferSize);
                var newFile = new SafeMemoryMappedFile(MemoryMappedFile.CreateNew(newName, newLength, MemoryMappedFileAccess.ReadWrite));
                //Console.WriteLine("Allocated new buffer of " + newLength + " bytes");

                // Write a message to the old buffer indicating the address of the new buffer:
                WriteMessage(new byte[0]);

                // Keep the old buffer alive until the reader has indicated that it's seen it:
                _oldBuffers.Add(Buffer);

                // Make the new buffer current:
                Buffer = newFile;
                Length = newFile.Length;
                Offset = StartingOffset;

                // Release old buffers that have been read:
                foreach (var buffer in _oldBuffers.Take(_oldBuffers.Count - 1).ToArray())
                    lock (DisposeLock)
                        if (buffer.Accessor.ReadBoolean(4))
                        {
                            _oldBuffers.Remove(buffer);
                            buffer.Dispose();
                            //Console.WriteLine("Cleaned file");
                        }
            }

            protected override void DisposeCore()
            {
                base.DisposeCore();
                foreach (var buffer in _oldBuffers) buffer.Dispose();
            }
        }

        private class InPipe : MutexFreePipe
        {
            int _lastMessageProcessed;
            int _bufferCount;

            readonly Action<byte[]> _onMessage;

            public InPipe(string name, bool createBuffer, Action<byte[]> onMessage) : base(name, createBuffer)
            {
                _onMessage = onMessage;
                new Thread(Go).Start();
            }

            void Go()
            {
                int spinCycles = 0;
                while (true)
                {
                    int? latestMessageID = GetLatestMessageID();
                    if (latestMessageID == null) return;            // We've been disposed.

                    if (latestMessageID > _lastMessageProcessed)
                    {
                        Thread.MemoryBarrier();    // We need this because of lock-free implementation
                        byte[] msg = GetNextMessage();
                        if (msg == null) return;
                        if (msg.Length > 0 && _onMessage != null) _onMessage(msg);       // Zero-length msg will be a buffer continuation
                        spinCycles = 1000;
                    }
                    if (spinCycles == 0)
                    {
                        NewMessageSignal.WaitOne();
                        if (IsDisposed) return;
                    }
                    else
                    {
                        Thread.MemoryBarrier();    // We need this because of lock-free implementation
                        spinCycles--;
                    }
                }
            }

            unsafe int? GetLatestMessageID()
            {
                lock (DisposeLock)
                    lock (Buffer.DisposeLock)
                        return IsDisposed || Buffer.IsDisposed ? (int?)null : *((int*)Buffer.Pointer);
            }

            unsafe byte[] GetNextMessage()
            {
                _lastMessageProcessed++;

                lock (DisposeLock)
                {
                    if (IsDisposed) return null;

                    lock (Buffer.DisposeLock)
                    {
                        if (Buffer.IsDisposed) return null;

                        byte* offsetPointer = Buffer.Pointer + Offset;
                        var msgPointer = (int*)offsetPointer;

                        int msgLength = *msgPointer;

                        Offset += MessageHeaderLength;
                        offsetPointer += MessageHeaderLength;

                        if (msgLength == 0)
                        {
                            Buffer.Accessor.Write(4, true);   // Signal that we no longer need file
                            Buffer.Dispose();
                            string newName = Name + "." + ++_bufferCount;
                            Buffer = new SafeMemoryMappedFile(MemoryMappedFile.OpenExisting(newName));
                            Offset = StartingOffset;
                            return new byte[0];
                        }

                        Offset += msgLength;

                        //MMF.Accessor.ReadArray (Offset, msg, 0, msg.Length);    // too slow
                        var msg = new byte[msgLength];
                        Marshal.Copy(new IntPtr(offsetPointer), msg, 0, msg.Length);
                        return msg;
                    }
                }
            }

            protected override void DisposeCore()
            {
                NewMessageSignal.Set();
                base.DisposeCore();
            }
        }

        #region SafeDisposable

        public class SafeDisposable : IDisposable
        {
            public object DisposeLock = new object();
            public bool IsDisposed { get; private set; }

            public void Dispose()
            {
                lock (DisposeLock)
                    if (!IsDisposed)
                    {
                        IsDisposed = true;
                        DisposeCore();
                    }
            }

            protected virtual void DisposeCore()
            {
            }

            public void AssertSafe()
            {
                lock (DisposeLock)
                    if (IsDisposed)
                        throw new ObjectDisposedException(GetType().Name + " has been disposed");
            }
        }

        #endregion SafeDisposable
    }
}