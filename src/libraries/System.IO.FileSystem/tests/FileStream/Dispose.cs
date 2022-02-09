// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Win32.SafeHandles;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace System.IO.Tests
{
    [Collection(nameof(DisableParallelization))] // make sure no other tests are calling GC.Collect()
    public class FileStream_Dispose : FileSystemTest
    {
        [Fact]
        public void DisposeClosesHandle()
        {
            SafeFileHandle handle;
            using (FileStream fs = new FileStream(GetTestFilePath(), FileMode.Create))
            {
                handle = fs.SafeFileHandle;
            }

            Assert.True(handle.IsClosed);
        }

        private class MyFileStream : FileStream
        {
            public MyFileStream(string path, FileMode mode)
                : base(path, mode)
            { }

            public MyFileStream(SafeFileHandle handle, FileAccess access, Action<bool> disposeMethod) : base(handle, access)
            {
                DisposeMethod = disposeMethod;
            }

            public Action<bool> DisposeMethod { get; set; }

            protected override void Dispose(bool disposing)
            {
                Action<bool> disposeMethod = DisposeMethod;

                if (disposeMethod != null)
                    disposeMethod(disposing);

                base.Dispose(disposing);
            }
        }

        [ConditionalFact(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        public void Dispose_CallsVirtualDisposeTrueArg_ThrowsDuringFlushWriteBuffer_DisposeThrows()
        {
            RemoteExecutor.Invoke(() =>
            {
                string fileName = GetTestFilePath();
                using (FileStream fscreate = new FileStream(fileName, FileMode.Create))
                {
                    fscreate.WriteByte(0);
                }
                bool writeDisposeInvoked = false;
                Action<bool> writeDisposeMethod = _ => writeDisposeInvoked = true;
                using (var fsread = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                {
                    Action act = () => // separate method to avoid JIT lifetime-extension issues
                    {
                        using (var fswrite = new MyFileStream(fsread.SafeFileHandle, FileAccess.Write, writeDisposeMethod))
                        {
                            fswrite.WriteByte(0);

                            // Normal dispose should call Dispose(true). Throws due to FS trying to flush write buffer
                            Assert.Throws<UnauthorizedAccessException>(() => fswrite.Dispose());
                            Assert.True(writeDisposeInvoked, "Expected Dispose(true) to be called from Dispose()");
                            writeDisposeInvoked = false;

                            // Only throws on first Dispose call
                            fswrite.Dispose();
                            Assert.True(writeDisposeInvoked, "Expected Dispose(true) to be called from Dispose()");
                            writeDisposeInvoked = false;
                        }
                        Assert.True(writeDisposeInvoked, "Expected Dispose(true) to be called from Dispose() again");
                        writeDisposeInvoked = false;
                    };
                    act();

                    for (int i = 0; i < 2; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                    Assert.False(writeDisposeInvoked, "Expected finalizer to have been suppressed");
                }
            }).Dispose();
        }

        private static bool IsPreciseGcSupportedAndRemoteExecutorSupported => PlatformDetection.IsPreciseGcSupported && RemoteExecutor.IsSupported;

        [ConditionalFact(nameof(IsPreciseGcSupportedAndRemoteExecutorSupported))]
        public void NoDispose_CallsVirtualDisposeFalseArg_ThrowsDuringFlushWriteBuffer_FinalizerWontThrow()
        {
            RemoteExecutor.Invoke(() =>
            {
                string fileName = GetTestFilePath();
                using (FileStream fscreate = new FileStream(fileName, FileMode.Create))
                {
                    fscreate.WriteByte(0);
                }
                bool writeDisposeInvoked = false;
                Action<bool> writeDisposeMethod = (disposing) =>
                {
                    writeDisposeInvoked = true;
                    Assert.False(disposing, "Expected false arg to Dispose(bool)");
                };
                using (var fsread = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                {
                    Action act = () => // separate method to avoid JIT lifetime-extension issues
                    {
                        var fswrite = new MyFileStream(fsread.SafeFileHandle, FileAccess.Write, writeDisposeMethod);
                        fswrite.WriteByte(0);
                    };
                    act();

                    // Dispose is not getting called here.
                    // instead, make sure finalizer gets called and doesnt throw exception
                    for (int i = 0; i < 2; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                    Assert.True(writeDisposeInvoked, "Expected finalizer to be invoked but not throw exception");
                }
            }).Dispose();
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsPreciseGcSupported))]
        public void Dispose_CallsVirtualDispose_TrueArg()
        {
            bool disposeInvoked = false;

            Action act = () => // separate method to avoid JIT lifetime-extension issues
            {
                using (MyFileStream fs = new MyFileStream(GetTestFilePath(), FileMode.Create))
                {
                    fs.DisposeMethod = (disposing) =>
                    {
                        disposeInvoked = true;
                        Assert.True(disposing, "Expected true arg to Dispose(bool)");
                    };

                    // Normal dispose should call Dispose(true)
                    fs.Dispose();
                    Assert.True(disposeInvoked, "Expected Dispose(true) to be called from Dispose()");

                    disposeInvoked = false;
                }

                // Second dispose leaving the using should still call dispose
                Assert.True(disposeInvoked, "Expected Dispose(true) to be called from Dispose() again");
                disposeInvoked = false;
            };
            act();

            // Make sure we suppressed finalization
            for (int i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(disposeInvoked, "Expected finalizer to have been suppressed");
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsPreciseGcSupported))]
        public void Finalizer_CallsVirtualDispose_FalseArg()
        {
            bool disposeInvoked = false;

            Action act = () => // separate method to avoid JIT lifetime-extension issues
            {
                new MyFileStream(GetTestFilePath(), FileMode.Create)
                {
                    DisposeMethod = (disposing) =>
                    {
                        disposeInvoked = true;
                        Assert.False(disposing, "Expected false arg to Dispose(bool)");
                    }
                };
            };
            act();

            for (int i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.True(disposeInvoked, "Expected finalizer to be invoked and set called");
        }

        [Fact]
        public void DisposeFlushesWriteBuffer()
        {
            string fileName = GetTestFilePath();
            using (FileStream fs = new FileStream(fileName, FileMode.Create))
            {
                fs.Write(TestBuffer, 0, TestBuffer.Length);
            }

            using (FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] buffer = new byte[TestBuffer.Length];
                Assert.Equal(buffer.Length, fs.Length);
                fs.Read(buffer, 0, buffer.Length);
                Assert.Equal(TestBuffer, buffer);
            }
        }

        public class DerivedFileStreamWithFinalizer : FileStream
        {
            public static int DisposeTrueCalled = 0;
            public static int DisposeFalseCalled = 0;

            public DerivedFileStreamWithFinalizer(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options)
                : base(path, mode, access, share, bufferSize, options)
            {
            }

            public DerivedFileStreamWithFinalizer(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync)
                : base(handle, access, bufferSize, isAsync)
            {
            }

            public DerivedFileStreamWithFinalizer(IntPtr handle, FileAccess access, bool ownsHandle)
#pragma warning disable CS0618 // Type or member is obsolete
                : base(handle, access, ownsHandle)
#pragma warning restore CS0618 // Type or member is obsolete
            {
            }

            ~DerivedFileStreamWithFinalizer() => Dispose(false);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    DisposeTrueCalled++;
                }
                else
                {
                    DisposeFalseCalled++;
                }

                base.Dispose(disposing);
            }
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsPreciseGcSupported))]
        public void FinalizeFlushesWriteBuffer()
            => VerifyFlushedBufferOnFinalization(
                filePath => new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: false));

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsPreciseGcSupported))]
        public void FinalizeFlushesWriteBufferForDerivedFileStreamCreatedFromPath()
            => VerifyFlushedBufferOnFinalization(
                filePath => new DerivedFileStreamWithFinalizer(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.None));

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsPreciseGcSupported))]
        public void FinalizeFlushesWriteBufferForDerivedFileStreamCreatedFromSafeFileHandle()
            => VerifyFlushedBufferOnFinalization(
                filePath => new DerivedFileStreamWithFinalizer(
                    File.OpenHandle(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, FileOptions.None),
                    FileAccess.Write, bufferSize: 4096, isAsync: false));

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsPreciseGcSupported))]
        public void FinalizeFlushesWriteBufferForDerivedFileStreamCreatedFromIntPtr()
             => VerifyFlushedBufferOnFinalization(
                filePath => new DerivedFileStreamWithFinalizer(
                    File.OpenHandle(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, FileOptions.None).DangerousGetHandle(),
                    FileAccess.Write,
                    ownsHandle: true));

        private void VerifyFlushedBufferOnFinalization(Func<string, FileStream> factory)
        {
            string fileName = GetTestFilePath();

            // use a separate method to be sure that fs isn't rooted at time of GC.
            Func<string, bool> leakFs = filePath =>
            {
                // we must specify useAsync:false, otherwise the finalizer just kicks off an async write.
                FileStream fs = factory(filePath);
                fs.Write(TestBuffer, 0, TestBuffer.Length);
                return fs.GetType() == typeof(DerivedFileStreamWithFinalizer);
            };

            int before = DerivedFileStreamWithFinalizer.DisposeFalseCalled;
            bool isDerivedFileStream = leakFs(fileName);

            GC.Collect();
            GC.WaitForPendingFinalizers();

            using (FileStream fsr = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] buffer = new byte[TestBuffer.Length];
                Assert.Equal(buffer.Length, fsr.Length);
                fsr.Read(buffer, 0, buffer.Length);
                Assert.Equal(TestBuffer, buffer);
            }

            if (isDerivedFileStream)
            {
                Assert.Equal(before + 1, DerivedFileStreamWithFinalizer.DisposeFalseCalled);
                Assert.Equal(0, DerivedFileStreamWithFinalizer.DisposeTrueCalled);
            }
        }

        // this type exists so DerivedFileStreamStrategy can be tested as well
        public class DerivedFileStream : FileStream
        {
            public DerivedFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options)
                : base(path, mode, access, share, bufferSize, options)
            {
            }
        }

        public static IEnumerable<object[]> GetFileStreamDisposeSuppressesStrategyFinalizationArgs()
        {
            foreach (int bufferSize in new[] { 1, 4096 })
            {
                foreach (FileOptions fileOptions in new[] { FileOptions.Asynchronous, FileOptions.None })
                {
                    yield return new object[] { bufferSize, fileOptions };
                }
            }
        }

        [ConditionalTheory(typeof(PlatformDetection), nameof(PlatformDetection.IsPreciseGcSupported))]
        [MemberData(nameof(GetFileStreamDisposeSuppressesStrategyFinalizationArgs))]
        public Task DisposeSuppressesStrategyFinalization(int bufferSize, FileOptions options)
            => VerifyStrategyFinalization(
                () => new FileStream(GetTestFilePath(), FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, options | FileOptions.DeleteOnClose),
                false);

        [ConditionalTheory(typeof(PlatformDetection), nameof(PlatformDetection.IsPreciseGcSupported))]
        [MemberData(nameof(GetFileStreamDisposeSuppressesStrategyFinalizationArgs))]
        public Task DisposeSuppressesStrategyFinalizationAsync(int bufferSize, FileOptions options)
            => VerifyStrategyFinalization(
                () => new FileStream(GetTestFilePath(), FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, options | FileOptions.DeleteOnClose),
                true);

        [ConditionalTheory(typeof(PlatformDetection), nameof(PlatformDetection.IsPreciseGcSupported))]
        [MemberData(nameof(GetFileStreamDisposeSuppressesStrategyFinalizationArgs))]
        public Task DerivedFileStreamDisposeSuppressesStrategyFinalization(int bufferSize, FileOptions options)
            => VerifyStrategyFinalization(
                () => new DerivedFileStream(GetTestFilePath(), FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, options | FileOptions.DeleteOnClose),
                false);

        [ConditionalTheory(typeof(PlatformDetection), nameof(PlatformDetection.IsPreciseGcSupported))]
        [MemberData(nameof(GetFileStreamDisposeSuppressesStrategyFinalizationArgs))]
        public Task DerivedFileStreamDisposeSuppressesStrategyFinalizationAsync(int bufferSize, FileOptions options)
            => VerifyStrategyFinalization(
                () => new DerivedFileStream(GetTestFilePath(), FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, options | FileOptions.DeleteOnClose),
                true);

        private static async Task VerifyStrategyFinalization(Func<FileStream> factory, bool useAsync)
        {
            WeakReference weakReference = await EnsureFileStreamIsNotRooted(factory, useAsync);
            Assert.True(weakReference.IsAlive);
            GC.Collect();
            Assert.False(weakReference.IsAlive);

            // separate method to avoid JIT lifetime-extension issues
            static async Task<WeakReference> EnsureFileStreamIsNotRooted(Func<FileStream> factory, bool useAsync)
            {
                FileStream fs = factory();
                WeakReference weakReference = new WeakReference(
                    (Stream)typeof(FileStream)
                        .GetField("_strategy", Reflection.BindingFlags.NonPublic | Reflection.BindingFlags.Instance)
                        .GetValue(fs),
                    trackResurrection: true);

                Assert.True(weakReference.IsAlive);

                if (useAsync)
                {
                    await fs.DisposeAsync();
                }
                {
                    fs.Dispose();
                }

                return weakReference;
            }
        }
    }
}
