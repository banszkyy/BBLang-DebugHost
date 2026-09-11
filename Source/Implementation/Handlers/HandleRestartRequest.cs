using LanguageCore.Runtime;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DebugServer;

partial class BytecodeDebugAdapter
{
    protected override RestartResponse HandleRestartRequest(RestartArguments arguments)
    {
        Log.Trace($"[Handler] Restart");

        IsRestarting = true;

        Log.Trace($"restarting ...");

        Log.Trace($" allow runtime to proceed");
        AllowProceedEvent.Set();
        Log.Trace($" waiting for proceeding");
        DidProceedEvent.WaitOne();

        Log.Trace($" reset session");
        ResetSession();

        Log.Trace($" creating new processor");
        _processor = new BytecodeProcessor(
            BytecodeInterpreterSettings.Default,
            _generated.Code,
            null,
            _generated.DebugInfo,
            _compiled.ExternalFunctions,
            _generated.GeneratedUnmanagedFunctions
        );

        if (!NoDebug && StopOnEntry)
        {
            RequestStop(StopReason_Pause.Instance);
        }
        else
        {
            StopReason = null;
        }

        if (Profiler is not null)
        {
            Profiler = null;
            StartProfiling();
        }

        Log.Trace($" creating runtime thread");
        RuntimeThread = new(RuntimeImpl)
        {
            Name = "Runtime Thread"
        };
        Log.Trace($" starting runtime thread");
        RuntimeThread.Start();

        return new RestartResponse();
    }
}
