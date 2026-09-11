using System.IO;
using System.Threading;
using LanguageCore.BBLang.Generator;
using LanguageCore.Compiler;
using LanguageCore.Runtime;

namespace DebugServer;

partial class BytecodeDebugAdapter : BytecodeDebugAdapterBase
{
    CompilerResult _compiled;
    BBLangGeneratorResult _generated;
    BytecodeProcessor? _processor;
    protected bool IsDisconnected;
    bool _isStopped;

    protected override bool IsStopped => _isStopped;

    protected override CompilerResult Compiled => _compiled;
    protected override ReadOnlyProcessorState Processor => _processor is null ? default : _processor.GetReadOnlyState();
    protected override CompiledDebugInformation DebugInformation => _processor?.DebugInformation ?? new(null);
    protected override CompilerSettings CompilerSettings => CodeGeneratorForMain.DefaultCompilerSettings;

    public BytecodeDebugAdapter(Stream stdIn, Stream stdOut, Logger log) : base(stdIn, stdOut, log)
    {
        AllowProceedEvent = new ManualResetEvent(true);
        DidProceedEvent = new ManualResetEvent(false);

        base.Protocol.RegisterRequestType<StartProfilingRequest, StartProfilingArguments>(HandleStartProfilingRequest);
        base.Protocol.RegisterRequestType<StopProfilingRequest, StopProfilingArguments>(HandleStopProfilingRequest);
    }

    protected override void ResetSession()
    {
        base.ResetSession();

        IO = null;
        StackFrames.Clear();
        IndirectVariables.Clear();
        _isStopped = false;
        LastStopContext = null;
        ShouldStop = false;
        StopReason = null;
        CrashReason = null;
        Time = 0;
        AllowProceedEvent.Set();
        DidProceedEvent.Reset();
        StdOut.Clear();
        StdOutCommonTraceItem = null;
        StdOutModifiedAt = 0;

        RuntimeThread?.Join();

        RuntimeThread = null;
        IsRestarting = false;
        _processor = null;
    }

    protected override void DisposeSession()
    {
        base.DisposeSession();

        ResetSession();
        _compiled = default;
        _generated = default;
        _processor = null;
        IsDisconnected = false;
    }

    public override void Run()
    {
        Protocol.Run();
        while (Protocol.IsRunning && !IsDisconnected)
        {
            Thread.Sleep(50);
        }
        Log.Info("Stopping protocol");
        Protocol.Stop();
    }
}
