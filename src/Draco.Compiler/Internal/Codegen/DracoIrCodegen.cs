using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Draco.Compiler.Internal.BoundTree;
using Draco.Compiler.Internal.DracoIr;
using Draco.Compiler.Internal.Lowering;
using Draco.Compiler.Internal.Symbols;
using Draco.Compiler.Internal.Symbols.Source;
using Draco.Compiler.Internal.Symbols.Synthetized;
using Draco.Compiler.Internal.Types;
using IrType = Draco.Compiler.Internal.DracoIr.Type;

namespace Draco.Compiler.Internal.Codegen;

/// <summary>
/// Generates Draco IR from the given bound tree.
/// </summary>
internal sealed class DracoIrCodegen : BoundTreeVisitor<Value>
{
    /// <summary>
    /// Generates IR code in the given <see cref="Assembly"/> for the given <see cref="SourceModuleSymbol"/>.
    /// </summary>
    /// <param name="assembly">The <see cref="Assembly"/> to generate the IR into.</param>
    /// <param name="module">The <see cref="SourceModuleSymbol"/> to generate IR code for.</param>
    public static void Generate(Assembly assembly, SourceModuleSymbol module)
    {
        var codegen = new DracoIrCodegen(assembly);
        codegen.CodegenModule(module);
        codegen.Finish();
    }

    private readonly Assembly assembly;
    private Procedure currentProcedure = null!;
    private InstructionWriter writer = null!;

    private readonly Dictionary<FunctionSymbol, Procedure> procedures = new();
    private readonly Dictionary<LabelSymbol, Label> labels = new();
    private readonly Dictionary<ParameterSymbol, Parameter> parameters = new();
    private readonly Dictionary<LocalSymbol, Local> locals = new();
    private readonly Dictionary<GlobalSymbol, Global> globals = new();

    private DracoIrCodegen(Assembly assembly)
    {
        this.assembly = assembly;
    }

    private IrType TranslateType(Types.Type type)
    {
        if (ReferenceEquals(type, IntrinsicTypes.Unit)) return IrType.Unit;
        if (ReferenceEquals(type, IntrinsicTypes.Bool)) return IrType.Bool;
        if (ReferenceEquals(type, IntrinsicTypes.Int32)) return IrType.Int32;
        if (type == IntrinsicTypes.Float64) return IrType.Float64;
        if (type == IntrinsicTypes.String) return IrType.String;

        if (type is Types.FunctionType func)
        {
            var args = func.ParameterTypes.Select(this.TranslateType).ToImmutableArray();
            var ret = this.TranslateType(func.ReturnType);
            return new IrType.Proc(args, ret);
        }

        throw new NotImplementedException();
    }

    private Procedure GetProcedure(FunctionSymbol function)
    {
        if (!this.procedures.TryGetValue(function, out var proc))
        {
            proc = this.assembly.DefineProcedure(function.Name);
            proc.ReturnType = this.TranslateType(function.ReturnType);
            foreach (var param in function.Parameters)
            {
                var paramValue = proc.DefineParameter(param.Name, this.TranslateType(param.Type));
                this.parameters.Add(param, paramValue);
            }
            this.procedures.Add(function, proc);
        }
        return proc;
    }

    private Label GetLabel(LabelSymbol label)
    {
        if (!this.labels.TryGetValue(label, out var lbl))
        {
            lbl = this.writer.DeclareLabel();
            this.labels.Add(label, lbl);
        }
        return lbl;
    }

    private Global GetGlobal(GlobalSymbol variable)
    {
        if (!this.globals.TryGetValue(variable, out var glob))
        {
            glob = this.assembly.DefineGlobal(variable.Name, this.TranslateType(variable.Type));
            this.globals.Add(variable, glob);
        }
        return glob;
    }

    private void Finish()
    {
        // Finish the global initializer
        var globalWriter = this.assembly.GlobalInitializer.Writer();
        globalWriter.Ret();

        // See, if there is a method called main
        // If so, set it as the entry point
        var mainMethod = this.procedures.Values.FirstOrDefault(p => p.Name == "main");
        if (mainMethod is not null) this.assembly.EntryPoint = mainMethod;
    }

    private void CodegenModule(SourceModuleSymbol module)
    {
        foreach (var member in module.Members)
        {
            if (member is SourceFunctionSymbol function) this.CodegenFunction(function);
            else if (member is SourceGlobalSymbol global) this.CodegenGlobal(global);
        }
    }

    private void CodegenFunction(SourceFunctionSymbol symbol)
    {
        // TODO: Maybe introduce context instead of this juggling?
        var oldWriter = this.writer;
        var oldProcedure = this.currentProcedure;

        var procedure = this.GetProcedure(symbol);
        this.currentProcedure = procedure;
        this.writer = procedure.Writer();

        // Desugar
        var body = symbol.Body.Accept(LocalRewriter.Instance);
        // Actual codegen
        body.Accept(this);

        // TODO: Maybe introduce context instead of this juggling?
        this.writer = oldWriter;
        this.currentProcedure = oldProcedure;
    }

    private void CodegenGlobal(SourceGlobalSymbol symbol)
    {
        var global = this.GetGlobal(symbol);
        if (symbol.Value is not null)
        {
            // TODO: Context juggling again...
            var oldWriter = this.writer;
            var oldProcedure = this.currentProcedure;
            this.writer = this.assembly.GlobalInitializer.Writer();

            // Desugar
            var value = symbol.Value.Accept(LocalRewriter.Instance);
            // Actual codegen
            var irValue = value.Accept(this);
            this.writer.Store(global, irValue);

            // TODO: Context juggling again...
            this.writer = oldWriter;
            this.currentProcedure = oldProcedure;
        }
    }

    public override Value VisitLocalDeclaration(BoundLocalDeclaration node)
    {
        var local = this.currentProcedure.DefineLocal(node.Local.Name, this.TranslateType(node.Local.Type));
        this.locals.Add(node.Local, local);
        if (node.Value is not null)
        {
            var value = node.Value.Accept(this);
            this.writer.Store(local, value);
        }
        return default!;
    }

    public override Value VisitLabelStatement(BoundLabelStatement node)
    {
        var label = this.GetLabel(node.Label);
        this.writer.PlaceLabel(label);
        return default!;
    }

    public override Value VisitBlockExpression(BoundBlockExpression node)
    {
        foreach (var stmt in node.Statements) stmt.Accept(this);
        return node.Value.Accept(this);
    }

    public override Value VisitIfExpression(BoundIfExpression node)
    {
        var thenLabel = this.writer.DeclareLabel();
        var elseLabel = this.writer.DeclareLabel();
        var endLabel = this.writer.DeclareLabel();

        // Allcoate value for result
        var result = this.currentProcedure.DefineLocal(null, this.TranslateType(node.TypeRequired));

        var condition = node.Condition.Accept(this);
        // In case the condition is a never type, we don't bother writing out the then and else bodies,
        // as they can not be evaluated
        // Note, that for side-effects we still emit the condition code
        if (ReferenceEquals(node.Condition.TypeRequired, NeverType.Instance)) return Value.Unit.Instance;

        this.writer.JmpIf(condition, thenLabel, elseLabel);

        this.writer.PlaceLabel(thenLabel);
        var thenValue = node.Then.Accept(this);
        this.writer.Store(result, thenValue);
        this.writer.Jmp(endLabel);

        this.writer.PlaceLabel(elseLabel);
        var elseValue = node.Else.Accept(this);
        this.writer.Store(result, elseValue);

        this.writer.PlaceLabel(endLabel);

        return this.writer.Load(result);
    }

    public override Value VisitReturnExpression(BoundReturnExpression node)
    {
        var value = node.Value.Accept(this);
        this.writer.Ret(value);
        return Value.Unit.Instance;
    }

    public override Value VisitGotoExpression(BoundGotoExpression node)
    {
        var label = this.GetLabel(node.Target);
        this.writer.Jmp(label);
        return Value.Unit.Instance;
    }

    public override Value VisitConditionalGotoStatement(BoundConditionalGotoStatement node)
    {
        var condition = node.Condition.Accept(this);
        var thenLabel = this.GetLabel(node.Target);
        var elseLabel = this.writer.DeclareLabel();
        this.writer.JmpIf(condition, thenLabel, elseLabel);
        this.writer.PlaceLabel(elseLabel);
        return Value.Unit.Instance;
    }

    public override Value VisitUnaryExpression(BoundUnaryExpression node)
    {
        var sub = node.Operand.Accept(this);
        if (node.Operator == IntrinsicSymbols.Bool_Not) return this.writer.Equal(sub, new Value.Const(false));
        if (IsPlus(node.Operator)) return sub;
        if (IsMinus(node.Operator)) return this.writer.Neg(sub);
        // TODO
        throw new NotImplementedException();
    }

    public override Value VisitBinaryExpression(BoundBinaryExpression node)
    {


        var left = node.Left.Accept(this);
        var right = node.Right.Accept(this);
        if (IsAdd(node.Operator)) return this.writer.Add(left, right);
        if (IsSub(node.Operator)) return this.writer.Sub(left, right);
        if (IsMul(node.Operator)) return this.writer.Mul(left, right);
        if (IsDiv(node.Operator)) return this.writer.Div(left, right);
        if (IsRem(node.Operator)) return this.writer.Rem(left, right);
        if (IsMod(node.Operator))
        {
            // a mod b
            // <=>
            // (a rem b + b) rem b
            var tmp1 = this.writer.Rem(left, right);
            var tmp2 = this.writer.Add(tmp1, right);
            return this.writer.Rem(tmp2, right);
        }
        if (IsLess(node.Operator)) return this.writer.Less(left, right);
        if (IsGreater(node.Operator)) return this.writer.Less(right, left);
        if (IsLessEqual(node.Operator))
        {
            var tmp = this.writer.Less(right, left);
            return this.writer.Equal(tmp, new Value.Const(false));
        }
        if (IsGreaterEqual(node.Operator))
        {
            var tmp = this.writer.Less(left, right);
            return this.writer.Equal(tmp, new Value.Const(false));
        }
        if (IsEqual(node.Operator)) return this.writer.Equal(left, right);
        if (IsNotEqual(node.Operator))
        {
            // a != b
            // <=>
            // (a == b) == false
            var tmp = this.writer.Equal(left, right);
            return this.writer.Equal(tmp, new Value.Const(false));
        }
        // TODO
        throw new NotImplementedException();
    }

    public override Value VisitCallExpression(BoundCallExpression node)
    {
        var called = node.Method.Accept(this);
        var args = node.Arguments
            .Select(a => a.Accept(this))
            .ToImmutableArray();
        return this.writer.Call(called, args);
    }

    public override Value VisitAssignmentExpression(BoundAssignmentExpression node)
    {
        var target = this.CompileLValue(node.Left);
        var right = node.Right.Accept(this);
        var toStore = right;
        if (node.CompoundOperator is not null)
        {
            var left = this.LoadLValue(target);
            if (IsAdd(node.CompoundOperator)) toStore = this.writer.Add(left, right);
            else if (IsSub(node.CompoundOperator)) toStore = this.writer.Sub(left, right);
            else if (IsMul(node.CompoundOperator)) toStore = this.writer.Mul(left, right);
            else if (IsDiv(node.CompoundOperator)) toStore = this.writer.Div(left, right);
            else throw new NotImplementedException();
        }
        if (target.IsGlobal()) this.writer.Store(target.AsGlobal(), toStore);
        else this.writer.Store(target.AsLocal(), toStore);
        return right;
    }

    public override Value VisitParameterExpression(BoundParameterExpression node) => new Value.Param(this.parameters[node.Parameter]);
    public override Value VisitLocalExpression(BoundLocalExpression node) => this.writer.Load(this.locals[node.Local]);
    public override Value VisitGlobalExpression(BoundGlobalExpression node) => this.writer.Load(this.GetGlobal(node.Global));
    public override Value VisitFunctionExpression(BoundFunctionExpression node)
    {
        static bool IsIntrinsicFunction(Symbol symbol) =>
               symbol == IntrinsicSymbols.Print_Int32
            || symbol == IntrinsicSymbols.Print_String
            || symbol == IntrinsicSymbols.Println_Int32
            || symbol == IntrinsicSymbols.Println_String;

        // We temporarily handle intrinsics here
        if (IsIntrinsicFunction(node.Function)) return new Value.Intrinsic(node.Function, this.TranslateType(node.Function.Type));

        // Default to procedure
        return new Value.Proc(this.GetProcedure(node.Function));
    }

    public override Value VisitUnitExpression(BoundUnitExpression node) => Value.Unit.Instance;
    public override Value VisitLiteralExpression(BoundLiteralExpression node) => new Value.Const(node.Value);

    public override Value VisitStringExpression(BoundStringExpression node)
    {
        if (node.Parts.Length == 0)
        {
            return new Value.Const(string.Empty);
        }
        else if (node.Parts.Length == 1 && node.Parts[0] is BoundStringText text)
        {
            return new Value.Const(text.Text);
        }
        else
        {
            // TODO: Should have been desugared
            throw new System.NotImplementedException();
        }
    }

    public override Value VisitLocalLvalue(BoundLocalLvalue node) => throw new InvalidOperationException("use CompileLValue instead");

    private Value LoadLValue(IInstructionOperand lvalue) => lvalue switch
    {
        Local loc => this.writer.Load(loc),
        Global glob => this.writer.Load(glob),
        _ => throw new ArgumentOutOfRangeException(nameof(lvalue)),
    };

    private IInstructionOperand CompileLValue(BoundLvalue expr) => expr switch
    {
        BoundLocalLvalue l => this.locals[l.Local],
        BoundGlobalLvalue g => this.GetGlobal(g.Global),
        _ => throw new ArgumentOutOfRangeException(nameof(expr)),
    };

    private static bool IsEqual(Symbol op) => op == IntrinsicSymbols.Int32_Equal
                                           || op == IntrinsicSymbols.Float64_Equal;
    private static bool IsNotEqual(Symbol op) => op == IntrinsicSymbols.Int32_NotEqual
                                              || op == IntrinsicSymbols.Float64_NotEqual;
    private static bool IsLess(Symbol op) => op == IntrinsicSymbols.Int32_LessThan
                                          || op == IntrinsicSymbols.Float64_LessThan;
    private static bool IsLessEqual(Symbol op) => op == IntrinsicSymbols.Int32_LessEqual
                                               || op == IntrinsicSymbols.Float64_LessEqual;
    private static bool IsGreater(Symbol op) => op == IntrinsicSymbols.Int32_GreaterThan
                                             || op == IntrinsicSymbols.Float64_GreaterThan;
    private static bool IsGreaterEqual(Symbol op) => op == IntrinsicSymbols.Int32_GreaterEqual
                                                  || op == IntrinsicSymbols.Float64_GreaterEqual;

    private static bool IsPlus(Symbol op) => op == IntrinsicSymbols.Int32_Plus
                                          || op == IntrinsicSymbols.Float64_Plus;
    private static bool IsMinus(Symbol op) => op == IntrinsicSymbols.Int32_Minus
                                           || op == IntrinsicSymbols.Float64_Minus;

    private static bool IsAdd(Symbol op) => op == IntrinsicSymbols.Int32_Add
                                         || op == IntrinsicSymbols.Float64_Add;
    private static bool IsSub(Symbol op) => op == IntrinsicSymbols.Int32_Sub
                                         || op == IntrinsicSymbols.Float64_Sub;
    private static bool IsMul(Symbol op) => op == IntrinsicSymbols.Int32_Mul
                                         || op == IntrinsicSymbols.Float64_Mul;
    private static bool IsDiv(Symbol op) => op == IntrinsicSymbols.Int32_Div
                                         || op == IntrinsicSymbols.Float64_Div;
    private static bool IsRem(Symbol op) => op == IntrinsicSymbols.Int32_Rem
                                         || op == IntrinsicSymbols.Float64_Rem;
    private static bool IsMod(Symbol op) => op == IntrinsicSymbols.Int32_Mod
                                         || op == IntrinsicSymbols.Float64_Mod;
}
