// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace System.Data.Entity.Core.Query.PlanCompiler
{
    using System.Data.Entity.Core.Query.InternalTrees;
    using System.Diagnostics;
    using System.Linq;

    internal class NullSemantics : BasicOpVisitorOfNode
    {
        private Command _command;
        private bool _modified;
        private bool _negated;
        private VariableNullabilityTable _variableNullabilityTable 
            = new VariableNullabilityTable(capacity: 32);

        private NullSemantics(Command command)
        {
            _command = command;
        }

        public static bool Process(Command command)
        {
            var processor = new NullSemantics(command);

            command.Root = processor.VisitNode(command.Root);

            return processor._modified;
        }

        protected override Node VisitScalarOpDefault(ScalarOp op, Node n)
        {
            switch (op.OpType)
            {
                case OpType.Not:
                    return HandleNot(n);
                case OpType.Or:
                    return HandleOr(n);
                case OpType.EQ:
                    return HandleEQ(n);
                case OpType.NE:
                    return HandleNE(n);
                default:
                    return base.VisitScalarOpDefault(op, n);
            }
        }

        private Node HandleNot(Node n)
        {
            var negated = _negated;
            _negated = !_negated;

            n = base.VisitScalarOpDefault((ScalarOp)n.Op, n);

            _negated = negated;

            return n;
        }

        private Node HandleOr(Node n)
        {
            // Check for the pattern '(varRef IS NULL) OR expression'.
            var isNullNode =
                n.Child0.Op.OpType == OpType.IsNull
                    ? n.Child0
                    : null;

            if (isNullNode == null
                || isNullNode.Child0.Op.OpType != OpType.VarRef)
            {
                return base.VisitScalarOpDefault((ScalarOp)n.Op, n);
            }

            // Mark 'variable' as not nullable while 'expression' is visited.
            Var variable = ((VarRefOp)isNullNode.Child0.Op).Var;

            var nullable = _variableNullabilityTable[variable];
            _variableNullabilityTable[variable] = false;

            n.Child1 = VisitNode(n.Child1);

            _variableNullabilityTable[variable] = nullable;

            return n;
        }

        private Node HandleEQ(Node n)
        {
            _modified |= 
                !ReferenceEquals(n.Child0, n.Child0 = VisitNode(n.Child0)) ||
                !ReferenceEquals(n.Child1, n.Child1 = VisitNode(n.Child1)) ||
                !ReferenceEquals(n, n = ImplementEquality(n));

            return n;
        }

        private Node HandleNE(Node n)
        {
            // Transform a != b into !(a == b)
            n = _command.CreateNode(
                _command.CreateConditionalOp(OpType.Not),
                _command.CreateNode(
                    _command.CreateComparisonOp(OpType.EQ),
                    n.Child0, n.Child1));

            _modified = true;

            return base.VisitScalarOpDefault((ScalarOp)n.Op, n);
        }

        private bool IsNullableVarRef(Node n)
        {
            return n.Op.OpType == OpType.VarRef 
                && _variableNullabilityTable[((VarRefOp)n.Op).Var];
        }

        private Node ImplementEquality(Node n)
        {
            Debug.Assert(n.Op.OpType == OpType.EQ);

            var x = n.Child0;
            var y = n.Child1;

            switch (x.Op.OpType)
            {
                case OpType.Constant:
                case OpType.InternalConstant:
                case OpType.NullSentinel:
                    switch (y.Op.OpType)
                    {
                        case OpType.Constant:
                        case OpType.InternalConstant:
                        case OpType.NullSentinel:
                            return n;
                        case OpType.Null:
                            return False();
                        default:
                            return _negated
                                ? And(n, Not(IsNull(Clone(y))))
                                : n;
                    }
                case OpType.Null:
                    switch (y.Op.OpType)
                    {
                        case OpType.Constant:
                        case OpType.InternalConstant:
                        case OpType.NullSentinel:
                            return False();
                        case OpType.Null:
                            return True();
                        default:
                            return IsNull(y);
                    }
                default:
                    switch (y.Op.OpType)
                    {
                        case OpType.Constant:
                        case OpType.InternalConstant:
                        case OpType.NullSentinel:
                            return _negated && IsNullableVarRef(n)
                                ? And(n, Not(IsNull(Clone(x))))
                                : n;
                        case OpType.Null:
                            return IsNull(x);
                        default:
                            return _negated
                                ? And(n, NotXor(Clone(x), Clone(y)))
                                : Or(n, And(IsNull(Clone(x)), IsNull(Clone(y))));
                    }
            }
        }

        private Node Clone(Node x)
        {
            return OpCopier.Copy(_command, x);
        }

        private Node False()
        {
            return _command.CreateNode(_command.CreateFalseOp());
        }

        private Node True()
        {
            return _command.CreateNode(_command.CreateTrueOp());
        }

        private Node IsNull(Node x)
        {
            return _command.CreateNode(_command.CreateConditionalOp(OpType.IsNull), x);
        }

        private Node Not(Node x)
        {
            return _command.CreateNode(_command.CreateConditionalOp(OpType.Not), x);
        }

        private Node And(Node x, Node y)
        {
            return _command.CreateNode(_command.CreateConditionalOp(OpType.And), x, y);
        }

        private Node Or(Node x, Node y)
        {
            return _command.CreateNode(_command.CreateConditionalOp(OpType.Or), x, y);
        }

        private Node Boolean(bool value)
        {
            return _command.CreateNode(_command.CreateConstantOp(_command.BooleanType, value));
        }

        private Node NotXor(Node x, Node y)
        {
            return 
                _command.CreateNode(
                    _command.CreateComparisonOp(OpType.EQ),
                    _command.CreateNode(
                        _command.CreateCaseOp(_command.BooleanType),
                        IsNull(x), Boolean(true), Boolean(false)),
                    _command.CreateNode(
                        _command.CreateCaseOp(_command.BooleanType),
                        IsNull(y), Boolean(true), Boolean(false)));
        }

        private struct VariableNullabilityTable
        {
            private bool[] _entries;

            public VariableNullabilityTable(int capacity)
            {
                Debug.Assert(capacity > 0);
                _entries = Enumerable.Repeat(true, capacity).ToArray();
            }

            public bool this[Var variable]
            {
                get
                {
                    return variable.Id >= _entries.Length
                        || _entries[variable.Id];
                }

                set
                {
                    EnsureCapacity(variable.Id + 1);
                    _entries[variable.Id] = value;
                }
            }

            private void EnsureCapacity(int minimum)
            {
                Debug.Assert(_entries != null);

                if (_entries.Length < minimum)
                {
                    var capacity = _entries.Length * 2;
                    if (capacity < minimum)
                    {
                        capacity = minimum;
                    }

                    var newEntries = Enumerable.Repeat(true, capacity).ToArray();
                    Array.Copy(_entries, 0, newEntries, 0, _entries.Length);
                    _entries = newEntries;
                }
            }
        }
    }
}