using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using LanguageCore;
using LanguageCore.Compiler;
using LanguageCore.Runtime;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SysThread = System.Threading.Thread;

namespace DebugServer;

public class StopContext
{
    public required int CodePointer;
    public required ImmutableArray<CallTraceItem> StackTrace;
    public required FunctionInformation Function;
    public required SourceCodeLocation Location;
}

partial class BytecodeDebugAdapter
{
    SysThread? RuntimeThread;
    readonly ManualResetEvent AllowProceedEvent;
    readonly ManualResetEvent DidProceedEvent;
    StopContext? LastStopContext;
    bool ShouldStop;
    bool IsRestarting;
    StopReason? StopReason;
    int Time;
    bool StopOnEntry;
    LanguageCore.Profiling.Profiler? Profiler;
    ulong ProfilerTick;

    void RuntimeImpl()
    {
        using (SyncLock.EnterScope())
        {
            Log.Trace("[#] Started");
            Protocol.SendEvent(new ContinuedEvent()
            {
                ThreadId = 1,
                AllThreadsContinued = true,
            });
        }

        while (_processor is not null && !IsDisconnected && (!_processor.IsDone || (ShouldStop && StopReason is StopReason_Crash)))
        {
            if (ShouldStop)
            {
                using (SyncLock.EnterScope())
                {
                    List<CallTraceItem> stacktrace = new();
                    DebugUtils.TraceStack(_processor.Memory, _processor.Registers.BasePointer, DebugInformation.StackOffsets, stacktrace);
                    FunctionInformation function = DebugInformation.GetFunctionInformation(_processor.Registers.CodePointer);
                    SourceCodeLocation sourceLocation = default;

                    if (StopReason is not StopReason_StepInstruction)
                    {
                        if (!DebugInformation.TryGetSourceLocation(_processor.Registers.CodePointer, out sourceLocation) || sourceLocation.IsSubtle)
                        {
                            goto _procceed;
                        }

                        if (LastStopContext is not null)
                        {
                            if (sourceLocation.Location == LastStopContext.Location.Location)
                            {
                                goto _procceed;
                            }

                            if (StopReason is StopReason_StepForward && stacktrace.Count > LastStopContext.StackTrace.Length)
                            {
                                goto _procceed;
                            }

                            if (StopReason is StopReason_StepOut && stacktrace.Count >= LastStopContext.StackTrace.Length)
                            {
                                goto _procceed;
                            }
                        }
                    }

                    Log.Trace("[#] Stopped");
                    GatherInformation();
                    _isStopped = true;
                    LastStopContext = new StopContext()
                    {
                        CodePointer = _processor.Registers.CodePointer,
                        Function = function,
                        Location = sourceLocation,
                        StackTrace = stacktrace.ToImmutableArray(),
                    };

                    switch (StopReason)
                    {
                        case null:
                            Log.Trace("[#] Stopped for no reason");
                            throw new InvalidOperationException("Stopped for no reason");
                        case StopReason_StepForward:
                        case StopReason_StepIn:
                        case StopReason_StepOut:
                        case StopReason_StepInstruction:
                            Protocol.SendEvent(new StoppedEvent()
                            {
                                Reason = StoppedEvent.ReasonValue.Step,
                                AllThreadsStopped = true,
                                ThreadId = 1,
                            });
                            break;
                        case StopReason_Pause:
                            Protocol.SendEvent(new StoppedEvent()
                            {
                                Reason = StoppedEvent.ReasonValue.Pause,
                                AllThreadsStopped = true,
                                ThreadId = 1,
                            });
                            break;
                        case StopReason_Crash:
                            Protocol.SendEvent(new StoppedEvent()
                            {
                                Reason = StoppedEvent.ReasonValue.Exception,
                                AllThreadsStopped = true,
                                ThreadId = 1,
                            });
                            break;
                        case StopReason_Breakpoint v:
                            Protocol.SendEvent(new StoppedEvent()
                            {
                                Reason = StoppedEvent.ReasonValue.Breakpoint,
                                AllThreadsStopped = true,
                                ThreadId = 1,
                                HitBreakpointIds = v.Breakpoint.Id.HasValue ? new() { v.Breakpoint.Id.Value } : new(),
                            });
                            break;
                        default:
                            throw new NotImplementedException(StopReason.GetType().Name);
                    }
                }

                Log.Trace("[#] Waiting to continue ...");
                AllowProceedEvent.WaitOne();
                Log.Trace("[#] Continued");
                DidProceedEvent.Set();

                if (IsRestarting)
                {
                    Log.Trace("[#] Breaking runtime thread (restarting)");
                    break;
                }

                using (SyncLock.EnterScope())
                {
                    _isStopped = false;

                    Protocol.SendEvent(new ContinuedEvent()
                    {
                        ThreadId = 1,
                        AllThreadsContinued = true,
                    });
                }

            _procceed:;
            }

            if (CrashReason is not null) break;

            try
            {
                _processor.Tick();

                if (Profiler is not null)
                {
                    using (SyncLock.EnterScope())
                    {
                        Profiler?.Sample(_processor.GetState(), ++ProfilerTick);
                    }
                }
            }
            catch (RuntimeException ex)
            {
                CrashReason = ex;
                if (NoDebug)
                {
                    break;
                }
                else
                {
                    RequestStopUnsafe(new StopReason_Crash()
                    {
                        Exception = ex,
                    });
                    continue;
                }
            }

            if (!_processor.IsDone && StopReason is StopReason_StepForward or StopReason_StepIn or StopReason_StepOut)
            {
                RequestStopUnsafe(StopReason);
            }

            if (!NoDebug)
            {
                foreach ((Breakpoint Breakpoint, InstructionBreakpoint InstructionBreakpoint, int Address) item in _instructionBreakpoints)
                {
                    if (item.Address != _processor.Registers.CodePointer) continue;

                    Log.Trace($"BREAKPOINT HIT at {item.Address}");

                    RequestStopUnsafe(new StopReason_Breakpoint()
                    {
                        Breakpoint = item.Breakpoint,
                    });
                }

                foreach (List<CompiledBreakpoint> bps in _breakpoints.Values)
                {
                    foreach (CompiledBreakpoint breakpoint in bps)
                    {
                        if (breakpoint.Instruction != _processor.Registers.CodePointer) continue;

                        Log.Trace($"BREAKPOINT HIT {breakpoint.SourceBreakpoint.Line}:{breakpoint.SourceBreakpoint.Column} at {breakpoint.Instruction} in {breakpoint.Breakpoint.Source.Name}");

                        using (SyncLock.EnterScope())
                        {
                            bool informationGathered = false;

                            if (!string.IsNullOrWhiteSpace(breakpoint.Condition))
                            {
                                DiagnosticsCollection diagnostics = new();

                                if (!informationGathered)
                                {
                                    GatherInformation();
                                    informationGathered = true;
                                }

                                if (TryEvaluate(breakpoint.Condition, StackFrames.Count > 0 ? StackFrames[0].Id : null, diagnostics, out bool result, out RuntimeException? error))
                                {
                                    if (!result) goto skip;
                                }
                                else
                                {
                                    StringBuilder b = new();
                                    b.AppendLine($"Failed to evaluate breakpoint condition `{breakpoint.Condition}` at {breakpoint.SourceBreakpoint.Line}:{breakpoint.SourceBreakpoint.Column} in {breakpoint.Breakpoint.Source.Name}");
                                    diagnostics.WriteErrorsTo(b);
                                    if (error is not null) b.AppendLine(error.ToString());
                                    Protocol.SendEvent(new OutputEvent()
                                    {
                                        Output = b.ToString(),
                                        Severity = OutputEvent.SeverityValue.Error,
                                    });
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(breakpoint.LogMessage))
                            {
                                if (!informationGathered)
                                {
                                    GatherInformation();
                                    informationGathered = true;
                                }

                                List<ExpressionVariable> variables = StackFrames.Count > 0 ? GetExpressionVariables(StackFrames[0].Id) : new();
                                string template = breakpoint.LogMessage;
                                int i = 0;
                                StringBuilder res = new();
                                while (i < template.Length)
                                {
                                    int j = template.IndexOf('{', i);
                                    if (j != -1)
                                    {
                                        int k = template.IndexOf('}', j);
                                        if (k != -1)
                                        {
                                            res.Append(template[i..j]);

                                            string item = template[(j + 1)..k];

                                            if (variables.TryFind(v => v.Name == item, out ExpressionVariable variable))
                                            {
                                                UniqueIds uniqueIds = new();
                                                res.Append(ToVariable(variable.Address, variable.Type, _processor.Memory, variable.Name, ref uniqueIds).Value);
                                            }

                                            i = k + 1;
                                            continue;
                                        }
                                    }

                                    res.Append(template[i..]);
                                    break;
                                }
                                res.AppendLine();
                                Protocol.SendEvent(new OutputEvent()
                                {
                                    Output = res.ToString(),
                                    Category = OutputEvent.CategoryValue.Console,
                                    Source = breakpoint.Breakpoint.Source,
                                    Line = breakpoint.SourceBreakpoint.Line,
                                    Column = breakpoint.SourceBreakpoint.Column,
                                });
                                goto skip;
                            }

                            RequestStopUnsafe(new StopReason_Breakpoint()
                            {
                                Breakpoint = breakpoint.Breakpoint,
                            });

                        skip:;
                        }
                    }
                }
            }

            if (StdOutModifiedAt != 0 && Time - StdOutModifiedAt > 30)
            {
                FlushStdout();
                StdOutModifiedAt = 0;
            }

            SysThread.Yield();
        }

        if (!IsDisconnected)
        {
            FlushStdout();
            if (CrashReason is not null)
            {
                Protocol.SendEvent(new OutputEvent()
                {
                    Output = CrashReason.ToString(),
                    Category = OutputEvent.CategoryValue.Exception,
                    Severity = OutputEvent.SeverityValue.Error,
                });
            }

            if (!IsRestarting)
            {
                Protocol.SendEvent(new ExitedEvent() { ExitCode = 0 });
                Protocol.SendEvent(new TerminatedEvent());
            }
        }

        if (!IsRestarting)
        {
            using (SyncLock.EnterScope())
            {
                StopProfiling(out _);
            }
        }

        _processor = null;

        Log.Trace("[#] Exited");
    }
}
