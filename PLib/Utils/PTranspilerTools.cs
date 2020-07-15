﻿/*
 * Copyright 2020 Peter Han
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software
 * and associated documentation files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use, copy, modify, merge, publish,
 * distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all copies or
 * substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
 * BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using Harmony;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace PeterHan.PLib {
	/// <summary>
	/// A utility class with transpiler tools.
	/// </summary>
	internal static class PTranspilerTools {
		/// <summary>
		/// The opcodes that branch control conditionally.
		/// </summary>
		private static readonly ISet<OpCode> BRANCH_CODES;

		/// <summary>
		/// Opcodes to load an integer onto the stack.
		/// </summary>
		internal static readonly OpCode[] LOAD_INT = {
			OpCodes.Ldc_I4_M1, OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_2,
			OpCodes.Ldc_I4_3, OpCodes.Ldc_I4_4, OpCodes.Ldc_I4_5, OpCodes.Ldc_I4_6,
			OpCodes.Ldc_I4_7, OpCodes.Ldc_I4_8
		};

		static PTranspilerTools() {
			// OpCode has a GetHashCode method!
			BRANCH_CODES = new HashSet<OpCode> {
				OpCodes.Beq,
				OpCodes.Beq_S,
				OpCodes.Bge,
				OpCodes.Bge_S,
				OpCodes.Bge_Un,
				OpCodes.Bge_Un_S,
				OpCodes.Bgt,
				OpCodes.Bgt_S,
				OpCodes.Bgt_Un,
				OpCodes.Bgt_Un_S,
				OpCodes.Ble,
				OpCodes.Ble_S,
				OpCodes.Ble_Un,
				OpCodes.Ble_Un_S,
				OpCodes.Blt,
				OpCodes.Blt_S,
				OpCodes.Blt_Un,
				OpCodes.Blt_Un_S,
				OpCodes.Bne_Un,
				OpCodes.Bne_Un_S,
				OpCodes.Brfalse,
				OpCodes.Brfalse_S,
				OpCodes.Brtrue,
				OpCodes.Brtrue_S,
			};
		}

		/// <summary>
		/// Compares the method parameters and throws ArgumentException if they do not match.
		/// </summary>
		/// <param name="victim">The victim method.</param>
		/// <param name="paramTypes">The method's parameter types.</param>
		/// <param name="newMethod">The replacement method.</param>
		internal static void CompareMethodParams(MethodInfo victim, Type[] paramTypes,
				MethodInfo newMethod) {
			var newTypes = GetParameterTypes(newMethod);
			if (!newMethod.IsStatic)
				newTypes = PushDeclaringType(newTypes, newMethod.DeclaringType);
			if (!victim.IsStatic)
				paramTypes = PushDeclaringType(paramTypes, victim.DeclaringType);
			int n = paramTypes.Length;
			// Argument count check
			if (newTypes.Length != n)
				throw new ArgumentException(("New method {0} ({1:D} arguments) does not " +
					"match method {2} ({3:D} arguments)").F(newMethod.Name, newTypes.Length,
					victim.Name, n));
			// Argument type check
			for (int i = 0; i < n; i++)
				if (!newTypes[i].IsAssignableFrom(paramTypes[i]))
					throw new ArgumentException(("Argument {0:D}: New method type {1} does " +
						"not match old method type {2}").F(i, paramTypes[i].FullName,
						newTypes[i].FullName));
			if (!victim.ReturnType.IsAssignableFrom(newMethod.ReturnType))
				throw new ArgumentException(("New method {0} (returns {1}) does not match " +
					"method {2} (returns {3})").F(newMethod.Name, newMethod.
					ReturnType, victim.Name, victim.ReturnType));
		}

		/// <summary>
		/// Pushes the default value for a type. This method does not work on compound value
		/// types or by-ref types, as those need a local.
		/// </summary>
		/// <param name="generator">The IL generator where the opcodes will be emitted.</param>
		/// <param name="type">The type of the value to generate.</param>
		/// <returns>true if instructions were pushed (all basic types and reference types),
		/// or false otherwise (by ref type or compound value type).</returns>
		private static bool GenerateBasicLoad(ILGenerator generator, Type type) {
			bool ok = true;
			if (type == null)
				throw new ArgumentNullException("type");
			if (type.IsByRef)
				ok = false;
			else if (type == typeof(int) || type == typeof(char) || type == typeof(short) ||
					type == typeof(uint) || type == typeof(ushort) || type == typeof(byte) ||
					type == typeof(sbyte) || type == typeof(bool))
				// Numbers all default to 0, booleans to false
				generator.Emit(OpCodes.Ldc_I4_0);
			else if (type == typeof(long) || type == typeof(ulong))
				generator.Emit(OpCodes.Ldc_I8, 0L);
			else if (type == typeof(float))
				generator.Emit(OpCodes.Ldc_R4, 0.0f);
			else if (type == typeof(double))
				generator.Emit(OpCodes.Ldc_R8, 0.0);
			else if (type == typeof(string))
				generator.Emit(OpCodes.Ldstr, "");
			else if (!type.IsValueType)
				// All reference types (including Nullable)
				generator.Emit(OpCodes.Ldnull);
			else
				ok = false;
			return ok;
		}

		/// <summary>
		/// Creates a local if necessary, and generates initialization code for the default
		/// value of the specified type. The resulting value ends up on the stack in a form
		/// that it would be used for the method argument.
		/// </summary>
		/// <param name="generator">The IL generator where the opcodes will be emitted.</param>
		/// <param name="type">The type to load and initialize.</param>
		internal static void GenerateDefaultLoad(ILGenerator generator, Type type) {
			if (!GenerateBasicLoad(generator, type)) {
				// This method will fail if there are more than 255 local variables, oh no!
				if (type.IsByRef) {
					var baseType = type.GetElementType();
					var localVariable = generator.DeclareLocal(baseType);
					int index = localVariable.LocalIndex;
					if (GenerateBasicLoad(generator, baseType))
						// Reference type or basic type
						generator.Emit(OpCodes.Stloc_S, index);
					else {
						// Value types not handled by it
						generator.Emit(OpCodes.Ldloca_S, index);
						generator.Emit(OpCodes.Initobj, type);
					}
					generator.Emit(OpCodes.Ldloca_S, index);
				} else {
					var localVariable = generator.DeclareLocal(type);
					int index = localVariable.LocalIndex;
					// Is a value type
					generator.Emit(OpCodes.Ldloca_S, index);
					generator.Emit(OpCodes.Initobj, type);
					generator.Emit(OpCodes.Ldloc_S, index);
				}
			}
		}

		/// <summary>
		/// Gets the method's parameter types.
		/// </summary>
		/// <param name="method">The method to query.</param>
		/// <returns>The type of each parameter of the method.</returns>
		internal static Type[] GetParameterTypes(this MethodInfo method) {
			if (method == null)
				throw new ArgumentNullException("method");
			var pm = method.GetParameters();
			int n = pm.Length;
			var types = new Type[n];
			for (int i = 0; i < n; i++)
				types[i] = pm[i].ParameterType;
			return types;
		}

		/// <summary>
		/// Checks to see if an instruction opcode is a branch instruction.
		/// </summary>
		/// <param name="opcode">The opcode to check.</param>
		/// <returns>true if it is a branch, or false otherwise.</returns>
		internal static bool IsConditionalBranchInstruction(OpCode opcode) {
			return BRANCH_CODES.Contains(opcode);
		}

		/// <summary>
		/// Adds a logger to all unhandled exceptions.
		/// 
		/// Not for production use.
		/// </summary>
		internal static void LogAllExceptions() {
			AppDomain.CurrentDomain.UnhandledException += OnThrown;
		}

		/// <summary>
		/// Adds a logger to all failed assertions. The assertions will still fail, but a stack
		/// trace will be printed for each failed assertion.
		/// 
		/// Not for production use.
		/// </summary>
		internal static void LogAllFailedAsserts() {
			var inst = HarmonyInstance.Create("PeterHan.PLib.LogFailedAsserts");
			MethodBase assert;
			var handler = new HarmonyMethod(typeof(PTranspilerTools), nameof(OnAssertFailed));
			try {
				// Assert(bool)
				assert = typeof(Debug).GetMethodSafe("Assert", true, typeof(bool));
				if (assert != null)
					inst.Patch(assert, handler);
				// Assert(bool, object)
				assert = typeof(Debug).GetMethodSafe("Assert", true, typeof(bool), typeof(
					object));
				if (assert != null)
					inst.Patch(assert, handler);
				// Assert(bool, object, UnityEngine.Object)
				assert = typeof(Debug).GetMethodSafe("Assert", true, typeof(bool), typeof(
					object), typeof(UnityEngine.Object));
				if (assert != null)
					inst.Patch(assert, handler);
			} catch (Exception e) {
				PUtil.LogException(e);
			}
		}

		/// <summary>
		/// Modifies a load instruction to load the specified constant, using short forms if
		/// possible.
		/// </summary>
		/// <param name="instruction">The instruction to modify.</param>
		/// <param name="newValue">The new i4 constant to load.</param>
		internal static void ModifyLoadI4(CodeInstruction instruction, int newValue) {
			if (newValue >= -1 && newValue <= 8) {
				// Short form: constant
				instruction.opcode = LOAD_INT[newValue + 1];
				instruction.operand = null;
			} else if (newValue >= byte.MinValue && newValue <= byte.MaxValue) {
				// Short form: 0-255
				instruction.opcode = OpCodes.Ldc_I4_S;
				instruction.operand = (byte)newValue;
			} else {
				// Long form
				instruction.opcode = OpCodes.Ldc_I4;
				instruction.operand = newValue;
			}
		}

		/// <summary>
		/// Logs a failed assertion that is about to occur.
		/// </summary>
		internal static void OnAssertFailed(bool condition) {
			if (!condition) {
				Debug.LogError("Assert is about to fail:");
				Debug.LogError(new System.Diagnostics.StackTrace().ToString());
			}
		}

		/// <summary>
		/// An optional handler for all unhandled exceptions.
		/// </summary>
		internal static void OnThrown(object sender, UnhandledExceptionEventArgs e) {
			if (!e.IsTerminating) {
				Debug.LogError("Unhandled exception on Thread " + System.Threading.Thread.
					CurrentThread.Name);
				if (e.ExceptionObject is Exception ex)
					Debug.LogException(ex);
				else
					Debug.LogError(e.ExceptionObject);
			}
		}

		/// <summary>
		/// Inserts the declaring instance type to the front of the specified array.
		/// </summary>
		/// <param name="types">The parameter types.</param>
		/// <param name="declaringType">The type which declared this method.</param>
		/// <returns>The types with declaringType inserted at the beginning.</returns>
		internal static Type[] PushDeclaringType(Type[] types, Type declaringType) {
			int n = types.Length;
			// Allow special case of passing "this" as first static arg
			var newParamTypes = new Type[n + 1];
			newParamTypes[0] = declaringType;
			for (int i = 0; i < n; i++)
				newParamTypes[i + 1] = types[i];
			return newParamTypes;
		}
	}
}
