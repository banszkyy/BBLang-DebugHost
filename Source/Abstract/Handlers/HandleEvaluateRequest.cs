using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using LanguageCore;
using LanguageCore.BBLang.Generator;
using LanguageCore.Compiler;
using LanguageCore.Runtime;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DebugServer;

partial class BytecodeDebugAdapterBase
{
    protected abstract CompilerSettings CompilerSettings { get; }

    protected List<ExpressionVariable> GetExpressionVariables(int frameId)
    {
        if (!Processor.IsValid) return new();

        if (StackFrames.Count <= 0) return new();

        FetchedFrame item = StackFrames[0];
        if (item.Id != frameId) return new();

        List<ExpressionVariable> variables = new();

        foreach (FetchedScope scope in item.Scopes)
        {
            foreach (FetchedVariable variable in scope.Variables)
            {
                if (variable.Value.Kind == StackElementKind.Internal) continue;

                int address;
                if (variable.Value.Kind == StackElementKind.GlobalVariable)
                {
                    if (item.GlobalVariablesAddress.HasValue)
                    {
                        int _v = item.GlobalVariablesAddress.Value.Value.AbsoluteAddress(item.Raw.BasePointer, Processor.StackStart);
                        address = Processor.Memory.Get<int>(_v) + variable.Value.Address;
                    }
                    else
                    {
                        Log.Warn($"Trying to handle global variable but no offset has been captured");
                        continue;
                    }
                }
                else
                {
                    address = variable.Value.AbsoluteAddress(item.Raw.BasePointer, Processor.StackStart);
                }

                ExpressionVariable v = new(variable.Value.Identifier, address, variable.Value.Type);
                variables.Add(v);
                //Log.WriteLine(v);
            }
        }

        return variables;
    }

    protected bool TryCompileExpression(string expression, int? frameId, DiagnosticsCollection diagnostics, [NotNullWhen(true)] out CompilerResult compiled)
    {
        compiled = default;

        if (!Processor.IsValid) return false;

        List<ExpressionVariable> variables = frameId.HasValue ? GetExpressionVariables(frameId.Value) : new();

        try
        {
            compiled = StatementCompiler.CompileExpression(expression, new CompilerSettings(CompilerSettings)
            {
                ExternalFunctions = Compiled.ExternalFunctions,
                AdditionalImports = ImmutableArray.Create(Compiled.File.ToString()),
                ExternalConstants = ImmutableArray<ExternalConstant>.Empty,
                SourceProviders = ImmutableArray<ISourceProvider>.Empty,
                IsExpression = true,
                IgnoreTopLevelStatements = true,
                ExpressionVariables = variables.ToImmutableArray(),
            }, diagnostics, Compiled);

            if (diagnostics.HasErrors)
            {
                return false;
            }

            if (compiled.Statements.Length != 1)
            {
                Log.Debug($"Expression \"{expression}\" compiled into multiple statements:\n{string.Join('\n', compiled.Statements.Select(v => v.ToString()))}");
                diagnostics.Add(DiagnosticAt.Error($"Expression should only have one value, {compiled.Statements.Length} passed", compiled.Statements[1]));
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            diagnostics.Add(Diagnostic.FromException(ex));
            return false;
        }
    }

    protected bool TryEvaluate(string expression, int? frameId, DiagnosticsCollection diagnostics, [NotNullWhen(true)] out byte[]? memory, [NotNullWhen(true)] out int resultAddress, [NotNullWhen(true)] out GeneralType? resultType, out RuntimeException? error)
    {
        memory = null;
        resultAddress = 0;
        resultType = null;
        error = null;

        if (!Processor.IsValid) return false;

        if (!TryCompileExpression(expression, frameId, diagnostics, out CompilerResult compiled))
        {
            return false;
        }

        resultType = compiled.Statements[0] is CompiledExpression v && v.SaveValue ? v.Type : BuiltinType.Void;

        //Log.WriteLine($"{compiled.Statements[0].GetType().Name} {compiled.Statements[0]}");

        try
        {
            BBLangGeneratorResult generated = CodeGeneratorForMain.Generate(compiled, new(MainGeneratorSettings.Default)
            {
                IsExpression = true,
            }, null, diagnostics);
            if (diagnostics.HasErrors)
            {
                return false;
            }

            memory = new byte[Processor.Memory.Length];
            Processor.Memory.CopyTo(memory);

            BytecodeProcessor interpreter = new(
                BytecodeInterpreterSettings.Default,
                generated.Code,
                memory,
                generated.DebugInfo,
                compiled.ExternalFunctions,
                generated.GeneratedUnmanagedFunctions
            );

            interpreter.Registers.StackPointer = Processor.Registers.StackPointer;

            ProcessorState state = interpreter.GetState();
            for (int i = 0; i < 64000 && !state.IsDone; i++)
            {
                interpreter.Tick(ref state);
            }

            if (!state.IsDone)
            {
                diagnostics.Add(Diagnostic.Error("Evaluation time out"));
                return false;
            }

            resultAddress = interpreter.Registers.StackPointer;
            return true;
        }
        catch (RuntimeException ex)
        {
            error = ex;
            Log.Error(ex);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return false;
        }
    }

    protected bool TryEvaluate(string expression, int? frameId, DiagnosticsCollection diagnostics, [NotNullWhen(true)] out bool result, out RuntimeException? error)
    {
        result = default;
        error = null;

        if (!TryCompileExpression(expression, frameId, diagnostics, out CompilerResult compiled))
        {
            return false;
        }

        if (TryEvaluate(expression, frameId, diagnostics, out byte[]? memory, out int resultAddress, out GeneralType? resultType, out error))
        {
            ReadOnlySpan<byte> m = memory;

            // Log.WriteLine(resultAddress.ToString());

            switch (resultType.FinalValue)
            {
                case BuiltinType w:
                    switch (w.Type)
                    {
                        case BasicType.U8:
                            result = m.Get<byte>(resultAddress) != 0;
                            return true;
                        case BasicType.I8:
                            result = m.Get<sbyte>(resultAddress) != 0;
                            return true;
                        case BasicType.U16:
                            result = m.Get<ushort>(resultAddress) != 0;
                            return true;
                        case BasicType.I16:
                            result = m.Get<short>(resultAddress) != 0;
                            return true;
                        case BasicType.U32:
                            result = m.Get<uint>(resultAddress) != 0;
                            return true;
                        case BasicType.I32:
                            result = m.Get<int>(resultAddress) != 0;
                            return true;
                        case BasicType.U64:
                            result = m.Get<ulong>(resultAddress) != 0;
                            return true;
                        case BasicType.I64:
                            result = m.Get<long>(resultAddress) != 0;
                            return true;
                        case BasicType.F32:
                            result = m.Get<float>(resultAddress) != 0;
                            return true;
                        case BasicType.Void:
                        case BasicType.Any:
                        default:
                            diagnostics.Add(DiagnosticAt.Error($"Cannot convert a value of type {resultType.FinalValue} to boolean", compiled.Statements[0]));
                            return false; ;
                    }
                case PointerType:
                case ReferenceType:
                case FunctionType:
                    result = m.Get<int>(resultAddress) != 0;
                    return true;
                default:
                    diagnostics.Add(DiagnosticAt.Error($"Cannot convert a value of type {resultType.FinalValue} to boolean", compiled.Statements[0]));
                    return false;
            }
        }
        else
        {
            return false;
        }
    }

    protected abstract void SendKey(byte c);

    protected override EvaluateResponse HandleEvaluateRequest(EvaluateArguments arguments)
    {
        Log.Trace("[Handler] Evaluate");

        if (!Processor.IsValid) return new EvaluateResponse();

        if (!IsStopped)
        {
            Log.Trace("Using evaluation input as stdin");
            foreach (byte c in Encoding.UTF8.GetBytes(arguments.Expression + "\r"))
            {
                SendKey(c);
            }
            return new EvaluateResponse();
        }

        DiagnosticsCollection diagnostics = new();
        if (TryEvaluate(arguments.Expression, arguments.FrameId, diagnostics, out byte[]? memory, out int resultAddress, out GeneralType? resultType, out RuntimeException? error))
        {
            ReadOnlySpan<byte> m = memory;

            //Log.WriteLine(resultAddress.ToString());

            return resultType.FinalValue switch
            {
                BuiltinType w => w.Type switch
                {
                    BasicType.Void => new EvaluateResponse()
                    {
                        Result = "void",
                        Type = resultType.ToString(),
                    },
                    BasicType.Any => new EvaluateResponse()
                    {
                        Result = "?",
                        Type = resultType.ToString(),
                    },
                    BasicType.U8 => new EvaluateResponse()
                    {
                        Result = m.Get<byte>(resultAddress).ToString(),
                        Type = resultType.ToString(),
                    },
                    BasicType.I8 => new EvaluateResponse()
                    {
                        Result = m.Get<sbyte>(resultAddress).ToString(),
                        Type = resultType.ToString(),
                    },
                    BasicType.U16 => new EvaluateResponse()
                    {
                        Result = m.Get<ushort>(resultAddress).ToString(),
                        Type = resultType.ToString(),
                    },
                    BasicType.I16 => new EvaluateResponse()
                    {
                        Result = m.Get<short>(resultAddress).ToString(),
                        Type = resultType.ToString(),
                    },
                    BasicType.U32 => new EvaluateResponse()
                    {
                        Result = m.Get<uint>(resultAddress).ToString(),
                        Type = resultType.ToString(),
                    },
                    BasicType.I32 => new EvaluateResponse()
                    {
                        Result = m.Get<int>(resultAddress).ToString(),
                        Type = resultType.ToString(),
                    },
                    BasicType.U64 => new EvaluateResponse()
                    {
                        Result = m.Get<ulong>(resultAddress).ToString(),
                        Type = resultType.ToString(),
                    },
                    BasicType.I64 => new EvaluateResponse()
                    {
                        Result = m.Get<long>(resultAddress).ToString(),
                        Type = resultType.ToString(),
                    },
                    BasicType.F32 => new EvaluateResponse()
                    {
                        Result = m.Get<float>(resultAddress).ToString(),
                        Type = resultType.ToString(),
                    },
                    _ => throw new UnreachableException(),
                },
                _ => new EvaluateResponse()
                {
                    Result = resultType.ToString(),
                    Type = resultType.ToString(),
                },
            };
        }
        else
        {
            StringBuilder b = new();
            b.AppendLine("Failed to evaluate");
            diagnostics.WriteErrorsTo(b);
            if (error is not null) b.AppendLine(error.ToString());
            Protocol.SendEvent(new OutputEvent()
            {
                Output = b.ToString(),
                Category = OutputEvent.CategoryValue.Console,
                Severity = OutputEvent.SeverityValue.Error,
            });

            return new EvaluateResponse("Failed to evaluate", 0)
            {
                PresentationHint = new()
                {
                    Attributes = VariablePresentationHint.AttributesValue.FailedEvaluation,
                }
            };
        }
    }
}
