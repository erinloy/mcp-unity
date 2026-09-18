using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using McpUnity.Unity;
using McpUnity.Utils;
using NUnit.Framework;

namespace McpUnity.Tests
{
    public class McpUnityServerRetryTests
    {
        [Test]
        public void DelayedStartRetryDelayUsesBoundedBackoff()
        {
            MethodInfo method = typeof(McpUnityServer).GetMethod(
                "GetDelayedStartDelaySeconds",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method, "McpUnityServer should expose a private retry delay helper for bounded restart backoff.");

            double DelayForAttempt(int attempt)
            {
                return (double)method.Invoke(null, new object[] { attempt });
            }

            Assert.AreEqual(0.25d, DelayForAttempt(0), 0.001d);
            Assert.AreEqual(0.25d, DelayForAttempt(1), 0.001d);
            Assert.AreEqual(0.5d, DelayForAttempt(2), 0.001d);
            Assert.AreEqual(1d, DelayForAttempt(3), 0.001d);
            Assert.AreEqual(2d, DelayForAttempt(4), 0.001d);
            Assert.AreEqual(3d, DelayForAttempt(5), 0.001d);
            Assert.AreEqual(5d, DelayForAttempt(6), 0.001d);
            Assert.AreEqual(5d, DelayForAttempt(10), 0.001d);
        }

        [Test]
        public void FailedStartCleanupStopsTheBackgroundTickBeforeTheNullServerBranch()
        {
            MethodInfo cleanup = typeof(McpUnityServer).GetMethod(
                "CleanupFailedStart",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Type tickType = typeof(McpLogger).Assembly.GetType("McpUnity.Utils.McpBackgroundTick");
            MethodInfo stop = tickType.GetMethod("Stop", BindingFlags.Public | BindingFlags.Static);

            Assert.NotNull(cleanup);
            Assert.NotNull(stop);
            List<IlInstruction> instructions = ReadInstructions(cleanup);
            IlInstruction stopCall = instructions.Find(instruction =>
                instruction.OpCode == OpCodes.Call && instruction.MetadataToken == stop.MetadataToken);
            IlInstruction nullServerBranch = instructions.Find(instruction => instruction.OpCode.FlowControl == FlowControl.Cond_Branch);
            IlInstruction nullServerReturn = instructions.Find(instruction =>
                instruction.Offset > nullServerBranch.Offset && instruction.OpCode == OpCodes.Ret);

            Assert.AreNotEqual(default(IlInstruction), stopCall, "Failed-start cleanup must call the background tick stop method.");
            Assert.AreNotEqual(default(IlInstruction), nullServerBranch, "Failed-start cleanup must branch for a null WebSocket server.");
            Assert.AreNotEqual(default(IlInstruction), nullServerReturn, "The null-server branch must return after clearing clients.");
            Assert.Less(stopCall.Offset, nullServerBranch.Offset, "The background tick must stop before the null-server early-return branch is evaluated.");
            Assert.Less(stopCall.Offset, nullServerReturn.Offset, "The background tick must stop before the null-server early return.");
        }

        private static List<IlInstruction> ReadInstructions(MethodInfo method)
        {
            byte[] bytes = method.GetMethodBody().GetILAsByteArray();
            var instructions = new List<IlInstruction>();
            int offset = 0;

            while (offset < bytes.Length)
            {
                int instructionOffset = offset;
                OpCode opCode = ReadOpCode(bytes, ref offset);
                int metadataToken = opCode.OperandType == OperandType.InlineMethod
                    ? BitConverter.ToInt32(bytes, offset)
                    : 0;

                instructions.Add(new IlInstruction(instructionOffset, opCode, metadataToken));
                offset += GetOperandSize(bytes, offset, opCode.OperandType);
            }

            return instructions;
        }

        private static OpCode ReadOpCode(byte[] bytes, ref int offset)
        {
            short value = bytes[offset++] == 0xfe
                ? (short)(0xfe00 | bytes[offset++])
                : (short)bytes[offset - 1];

            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(OpCode))
                {
                    OpCode opCode = (OpCode)field.GetValue(null);
                    if (opCode.Value == value)
                    {
                        return opCode;
                    }
                }
            }

            throw new InvalidOperationException($"Unknown IL opcode: 0x{value:X4}.");
        }

        private static int GetOperandSize(byte[] bytes, int offset, OperandType operandType)
        {
            switch (operandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    return sizeof(int) + BitConverter.ToInt32(bytes, offset) * sizeof(int);
                default:
                    throw new InvalidOperationException($"Unsupported IL operand type: {operandType}.");
            }
        }

        private readonly struct IlInstruction
        {
            public IlInstruction(int offset, OpCode opCode, int metadataToken)
            {
                Offset = offset;
                OpCode = opCode;
                MetadataToken = metadataToken;
            }

            public int Offset { get; }
            public OpCode OpCode { get; }
            public int MetadataToken { get; }
        }
    }
}
