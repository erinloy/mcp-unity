using System;
using System.Reflection;
using System.Runtime.InteropServices;
using McpUnity.Utils;
using NUnit.Framework;
using UnityEditor;

namespace McpUnity.Tests
{
    public class McpBackgroundTickLifecycleTests
    {
#if UNITY_EDITOR_WIN
        private bool _wasTickRunning;

        [SetUp]
        public void PreserveBackgroundTickState()
        {
            _wasTickRunning = GetTimerId() != UIntPtr.Zero;
        }

        [TearDown]
        public void RestoreBackgroundTickState()
        {
            MethodInfo lifecycleMethod = GetTickType().GetMethod(
                _wasTickRunning ? "Start" : "Stop",
                BindingFlags.Public | BindingFlags.Static);
            lifecycleMethod.Invoke(null, null);
        }
#endif

        [Test]
        public void BackgroundTickRequiresExplicitLifecycleMethods()
        {
            Type tickType = typeof(McpLogger).Assembly.GetType("McpUnity.Utils.McpBackgroundTick");

            Assert.NotNull(tickType);
            Assert.IsNull(
                tickType.GetCustomAttribute<InitializeOnLoadAttribute>(),
                "The background tick must not start automatically when the editor assembly loads.");
            Assert.NotNull(tickType.GetMethod("Start", BindingFlags.Public | BindingFlags.Static));
            Assert.NotNull(tickType.GetMethod("Stop", BindingFlags.Public | BindingFlags.Static));
        }

        [Test]
        public void StopCanBeCalledMoreThanOnce()
        {
            Type tickType = typeof(McpLogger).Assembly.GetType("McpUnity.Utils.McpBackgroundTick");
            MethodInfo stop = tickType.GetMethod("Stop", BindingFlags.Public | BindingFlags.Static);

            Assert.DoesNotThrow(() => stop.Invoke(null, null));
            Assert.DoesNotThrow(() => stop.Invoke(null, null));
        }

#if UNITY_EDITOR_WIN
        [Test]
        public void StartDoesNotCreateAnotherTimerWhenAlreadyStarted()
        {
            Type tickType = GetTickType();
            MethodInfo start = tickType.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
            MethodInfo stop = tickType.GetMethod("Stop", BindingFlags.Public | BindingFlags.Static);

            stop.Invoke(null, null);
            try
            {
                start.Invoke(null, null);
                UIntPtr firstTimerId = GetTimerId();
                start.Invoke(null, null);

                Assert.AreNotEqual(UIntPtr.Zero, firstTimerId);
                Assert.AreEqual(firstTimerId, GetTimerId());
            }
            finally
            {
                stop.Invoke(null, null);
            }
        }

        [Test]
        public void TimerInteropCapturesLastWin32Error()
        {
            Type tickType = GetTickType();
            MethodInfo setTimer = tickType.GetMethod(
                "SetTimer",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(setTimer);
            DllImportAttribute import = setTimer.GetCustomAttribute<DllImportAttribute>();
            Assert.NotNull(import);
            Assert.IsTrue(import.SetLastError);
        }

        private static Type GetTickType()
        {
            return typeof(McpLogger).Assembly.GetType("McpUnity.Utils.McpBackgroundTick");
        }

        private static UIntPtr GetTimerId()
        {
            FieldInfo timerId = GetTickType().GetField("_timerId", BindingFlags.NonPublic | BindingFlags.Static);
            return (UIntPtr)timerId.GetValue(null);
        }
#endif
    }
}
