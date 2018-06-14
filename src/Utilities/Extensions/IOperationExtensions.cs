﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Analyzer.Utilities.Extensions
{
    internal static class IOperationExtensions
    {
        /// <summary>
        /// Gets the receiver type for an invocation expression (i.e. type of 'A' in invocation 'A.B()')
        /// If the invocation actually involves a conversion from A to some other type, say 'C', on which B is invoked,
        /// then this method returns type A if <paramref name="beforeConversion"/> is true, and C if false.
        /// </summary>
        public static INamedTypeSymbol GetReceiverType(this IInvocationOperation invocation, Compilation compilation, bool beforeConversion, CancellationToken cancellationToken)
        {
            if (invocation.Instance != null)
            {
                return beforeConversion ?
                    GetReceiverType(invocation.Instance.Syntax, compilation, cancellationToken) :
                    invocation.Instance.Type as INamedTypeSymbol;
            }
            else if (invocation.TargetMethod.IsExtensionMethod && invocation.TargetMethod.Parameters.Length > 0)
            {
                var firstArg = invocation.Arguments.FirstOrDefault();
                if (firstArg != null)
                {
                    return beforeConversion ?
                        GetReceiverType(firstArg.Value.Syntax, compilation, cancellationToken) :
                        firstArg.Type as INamedTypeSymbol;
                }
                else if (invocation.TargetMethod.Parameters[0].IsParams)
                {
                    return invocation.TargetMethod.Parameters[0].Type as INamedTypeSymbol;
                }
            }

            return null;
        }

        private static INamedTypeSymbol GetReceiverType(SyntaxNode receiverSyntax, Compilation compilation, CancellationToken cancellationToken)
        {
            var model = compilation.GetSemanticModel(receiverSyntax.SyntaxTree);
            var typeInfo = model.GetTypeInfo(receiverSyntax, cancellationToken);
            return typeInfo.Type as INamedTypeSymbol;
        }

        public static bool HasConstantValue(this IOperation operation, string comparand, StringComparison comparison)
        {
            var constantValue = operation.ConstantValue;
            if (!constantValue.HasValue)
            {
                return false;
            }

            if (operation.Type == null || operation.Type.SpecialType != SpecialType.System_String)
            {
                return false;
            }

            return string.Equals((string)constantValue.Value, comparand, comparison);
        }

        public static bool HasNullConstantValue(this IOperation operation)
        {
            return operation.ConstantValue.HasValue && operation.ConstantValue.Value == null;
        }

        public static bool TryGetBoolConstantValue(this IOperation operation, out bool constantValue)
        {
            if (operation.ConstantValue.HasValue && operation.ConstantValue.Value is bool value)
            {
                constantValue = value;
                return true;
            }

            constantValue = false;
            return false;
        }

        public static bool HasConstantValue(this IOperation operation, long comparand)
        {
            return operation.HasConstantValue(unchecked((ulong)(comparand)));
        }

        public static bool HasConstantValue(this IOperation operation, ulong comparand)
        {
            var constantValue = operation.ConstantValue;
            if (!constantValue.HasValue)
            {
                return false;
            }

            if (operation.Type == null || operation.Type.IsErrorType())
            {
                return false;
            }

            if (operation.Type.IsPrimitiveType())
            {
                return HasConstantValue(constantValue, operation.Type, comparand);
            }

            if (operation.Type.TypeKind == TypeKind.Enum)
            {
                var enumUnderlyingType = ((INamedTypeSymbol)operation.Type).EnumUnderlyingType;
                return enumUnderlyingType != null &&
                    enumUnderlyingType.IsPrimitiveType() &&
                    HasConstantValue(constantValue, enumUnderlyingType, comparand);
            }

            return false;
        }

        private static bool HasConstantValue(Optional<object> constantValue, ITypeSymbol constantValueType, ulong comparand)
        {
            if (constantValueType.SpecialType == SpecialType.System_Double || constantValueType.SpecialType == SpecialType.System_Single)
            {
                return (double)constantValue.Value == comparand;
            }

            return DiagnosticHelpers.TryConvertToUInt64(constantValue.Value, constantValueType.SpecialType, out ulong convertedValue) && convertedValue == comparand;
        }

        public static ITypeSymbol GetElementType(this IArrayCreationOperation arrayCreation)
        {
            return (arrayCreation?.Type as IArrayTypeSymbol)?.ElementType;
        }

        /// <summary>
        /// Gets all valid members of the block operation body, excluding the VB implicit label and return statements.
        /// </summary>
        public static ImmutableArray<IOperation> GetOperations(this ImmutableArray<IOperation> blockOperations)
        {
            if (blockOperations.IsDefaultOrEmpty)
            {
                return blockOperations;
            }

            if (blockOperations.Length > 1 && blockOperations[0].Language == LanguageNames.VisualBasic)
            {
                var lastOperation = blockOperations[blockOperations.Length - 1];
                var secondLastOperation = blockOperations[blockOperations.Length - 2];

                if (lastOperation.Kind == OperationKind.Return && lastOperation.IsImplicit &&
                    secondLastOperation.Kind == OperationKind.Labeled &&
                    ((ILabeledOperation)secondLastOperation).Label.Name == "exit" &&
                    secondLastOperation.IsImplicit)
                {
                    var builder = ImmutableArray.CreateBuilder<IOperation>();
                    builder.AddRange(blockOperations, blockOperations.Length - 2);
                    return builder.ToImmutable();
                }
                else
                {
                    return blockOperations;
                }
            }
            else
            {
                return blockOperations;
            }
        }

        /// <summary>
        /// True if this operation has no IOperation API support, i.e. <see cref="OperationKind.None"/> and
        /// is the root operation, i.e. <see cref="Operation.Parent"/> is null.
        /// For example, this returns true for attribute operations.
        /// </summary>
        public static bool IsOperationNoneRoot(this IOperation operation)
        {
            return operation.Kind == OperationKind.None && operation.Parent == null;
        }

        /// <summary>
        /// Returns the topmost <see cref="IBlockOperation"/> containing the given <paramref name="operation"/>.
        /// </summary>
        public static IBlockOperation GetTopmostParentBlock(this IOperation operation)
        {
            IOperation currentOperation = operation;
            IBlockOperation topmostBlockOperation = null;
            while (currentOperation != null)
            {
                if (currentOperation is IBlockOperation blockOperation)
                {
                    topmostBlockOperation = blockOperation;
                }

                currentOperation = currentOperation.Parent;
            }

            return topmostBlockOperation;
        }

        /// <summary>
        /// Gets the first ancestor of this operation with:
        ///  1. Specified OperationKind
        ///  2. If <paramref name="predicateOpt"/> is non-null, it succeeds for the ancestor.
        /// Returns null if there is no such ancestor.
        /// </summary>
        public static TOperation GetAncestor<TOperation>(this IOperation root, OperationKind ancestorKind, Func<TOperation, bool> predicateOpt = null) where TOperation : IOperation
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            IOperation ancestor = root;
            do
            {
                ancestor = ancestor.Parent;
            } while (ancestor != null && ancestor.Kind != ancestorKind);

            if (ancestor != null)
            {
                if (predicateOpt != null && !predicateOpt((TOperation)ancestor))
                {
                    return GetAncestor(ancestor, ancestorKind, predicateOpt);
                }
                return (TOperation)ancestor;
            }
            else
            {
                return default(TOperation);
            }
        }

        public static IConditionalAccessOperation GetConditionalAccess(this IConditionalAccessInstanceOperation operation)
        {
            Func<IConditionalAccessOperation, bool> predicate = c => c.Operation.Syntax == operation.Syntax;
            return operation.GetAncestor(OperationKind.ConditionalAccess, predicate);
        }

        /// <summary>
        /// Gets the operation for the object being created that is being referenced by <paramref name="operation"/>.
        /// If the operation is referencing an implicit or an explicit this/base/Me/MyBase/MyClass instance, then we return "null".
        /// </summary>
        /// <param name="operation"></param>
        /// <param name="isInsideAnonymousObjectInitializer">Flag to indicate if the operation is a descendant of an <see cref="IAnonymousObjectCreationOperation"/>.</param>
        /// <remarks>
        /// PERF: Note that the parameter <paramref name="isInsideAnonymousObjectInitializer"/> is to improve performance by avoiding walking the entire IOperation parent for non-initializer cases.
        /// </remarks>
        public static IOperation GetInstance(this IInstanceReferenceOperation operation, bool isInsideAnonymousObjectInitializer)
        {
            Debug.Assert(isInsideAnonymousObjectInitializer ==
                (operation.GetAncestor<IAnonymousObjectCreationOperation>(OperationKind.AnonymousObjectCreation) != null));

            if (isInsideAnonymousObjectInitializer)
            {
                for (IOperation current = operation; current != null && current.Kind != OperationKind.Block; current = current.Parent)
                {
                    switch (current.Kind)
                    {
                        // VB object initializer allows accessing the members of the object being created with "." operator.
                        // The syntax of such an IInstanceReferenceOperation points to the object being created.
                        // Check for such an IAnonymousObjectCreationOperation with matching syntax.
                        // For example, instance reference for members ".Field1" and ".Field2" in "New C() With { .Field1 = 0, .Field2 = .Field1 }".
                        case OperationKind.AnonymousObjectCreation:
                            if (current.Syntax == operation.Syntax)
                            {
                                return current;
                            }

                            break;
                    }
                }
            }

            // For all other cases, IInstanceReferenceOperation refers to the implicit or explicit this/base/Me/MyBase/MyClass reference.
            // We return null for such cases.
            return null;
        }

        /// <summary>
        /// Workaround for https://github.com/dotnet/roslyn/issues/22736 (IPropertyReferenceExpressions in IAnonymousObjectCreationExpression are missing a receiver).
        /// Gets the instance for the anonymous object being created that is being referenced by <paramref name="operation"/>.
        /// Otherwise, returns null
        /// </summary>
        public static IAnonymousObjectCreationOperation GetAnonymousObjectCreation(this IPropertyReferenceOperation operation)
        {
            if (operation.Instance == null &&
                operation.Property.ContainingType.IsAnonymousType)
            {
                var declarationSyntax = operation.Property.ContainingType.DeclaringSyntaxReferences[0].GetSyntax();
                Func<IAnonymousObjectCreationOperation, bool> predicate = a => a.Syntax == declarationSyntax;
                return operation.GetAncestor(OperationKind.AnonymousObjectCreation, predicate);
            }

            return null;
        }

        public static bool IsInsideCatchRegion(this IOperation operation, ControlFlowGraph cfg)
        {
            foreach (var block in cfg.Blocks)
            {
                var isCatchRegionBlock = false;
                var currentRegion = block.EnclosingRegion;
                while (currentRegion != null)
                {
                    switch (currentRegion.Kind)
                    {
                        case ControlFlowRegionKind.Catch:
                            isCatchRegionBlock = true;
                            break;
                    }

                    currentRegion = currentRegion.EnclosingRegion;
                }

                if (isCatchRegionBlock)
                {
                    foreach (var descendant in block.DescendantOperations())
                    {
                        if (operation == descendant)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public static bool HasAnyOperationDescendant(this ImmutableArray<IOperation> operationBlocks, Func<IOperation, bool> predicate)
        {
            foreach (var operationBlock in operationBlocks)
            {
                if (operationBlock.HasAnyOperationDescendant(predicate))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool HasAnyOperationDescendant(this IOperation operationBlock, Func<IOperation, bool> predicate)
        {
            Debug.Assert(operationBlock != null);
            Debug.Assert(predicate != null);
            foreach (var descendant in operationBlock.DescendantsAndSelf())
            {
                if (predicate(descendant))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool HasAnyOperationDescendant(this ImmutableArray<IOperation> operationBlocks, OperationKind kind)
        {
            return operationBlocks.HasAnyOperationDescendant(predicate: operation => operation.Kind == kind);
        }

        /// <summary>
        /// Indicates if the given <paramref name="binaryOperation"/> is a predicate operation used in a condition.
        /// </summary>
        /// <param name="binaryOperation"></param>
        /// <returns></returns>
        public static bool IsComparisonOperator(this IBinaryOperation binaryOperation)
        {
            switch (binaryOperation.OperatorKind)
            {
                case BinaryOperatorKind.Equals:
                case BinaryOperatorKind.NotEquals:
                case BinaryOperatorKind.ObjectValueEquals:
                case BinaryOperatorKind.ObjectValueNotEquals:
                case BinaryOperatorKind.LessThan:
                case BinaryOperatorKind.LessThanOrEqual:
                case BinaryOperatorKind.GreaterThan:
                case BinaryOperatorKind.GreaterThanOrEqual:
                    return true;

                default:
                    return false;
            }
        }

        public static ITypeSymbol GetThrowExceptionType(this IOperation thrownOperation, BasicBlock currentBlock)
        {
            if (thrownOperation?.Type != null)
            {
                return thrownOperation.Type;
            }

            // rethrow or throw with no argument.
            return currentBlock.GetEnclosingRegionExceptionType();
        }

        public static bool IsLambdaOrLocalFunctionOrDelegateInvocation(this IInvocationOperation operation)
            => operation.TargetMethod.IsLambdaOrLocalFunctionOrDelegate();

        public static bool IsLambdaOrLocalFunctionOrDelegateReference(this IMethodReferenceOperation operation)
            => operation.Method.IsLambdaOrLocalFunctionOrDelegate();

        public static ControlFlowGraph GetEnclosingControlFlowGraph(this IOperation operation)
        {
            while(operation.Parent != null)
            {
                operation = operation.Parent;
            }

            switch (operation)
            {
                case IBlockOperation blockOperation:
                    return SemanticModel.GetControlFlowGraph(blockOperation);

                case IMethodBodyOperation methodBodyOperation:
                    return SemanticModel.GetControlFlowGraph(methodBodyOperation);

                case IConstructorBodyOperation constructorBodyOperation:
                    return SemanticModel.GetControlFlowGraph(constructorBodyOperation);

                case IFieldInitializerOperation fieldInitializerOperation:
                    return SemanticModel.GetControlFlowGraph(fieldInitializerOperation);

                case IPropertyInitializerOperation propertyInitializerOperation:
                    return SemanticModel.GetControlFlowGraph(propertyInitializerOperation);

                case IParameterInitializerOperation parameterInitializerOperation:
                    return SemanticModel.GetControlFlowGraph(parameterInitializerOperation);

                default:
                    throw new NotSupportedException($"Unexpected root operation kind: {operation.Kind.ToString()}");
            }
        }
    }
}
