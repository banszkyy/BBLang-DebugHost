using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DebugServer;

partial class BytecodeDebugAdapter
{
    sealed class StartProfilingResponse : ResponseBody;
    sealed class StopProfilingResponse : ResponseBody
    {
        public required string Path { get; init; }
    }

    bool StartProfiling()
    {
        if (Profiler is not null) return false;
        Profiler = new LanguageCore.Profiling.GoogleProfiler(DebugInformation);
        ProfilerTick = 0;
        return true;
    }

    bool StopProfiling([NotNullWhen(true)] out string? path)
    {
        path = null;
        if (Profiler is null) return false;
        path = Compiled.File.LocalPath;
        if (!Compiled.File.IsFile || !Directory.Exists(Path.GetDirectoryName(path)))
        {
            path = Path.GetTempFileName();
        }
        path += ".cpuprofile";
        Profiler.WriteTo(path);
        Profiler = null;
        return true;
    }

    void HandleStartProfilingRequest(IRequestResponder<StartProfilingArguments> responder)
    {
        using (SyncLock.EnterScope())
        {
            StartProfiling();
        }
        responder.SetResponse(new StartProfilingResponse());
    }

    void HandleStopProfilingRequest(IRequestResponder<StopProfilingArguments> responder)
    {
        string? path;
        using (SyncLock.EnterScope())
        {
            if (!StopProfiling(out path)) return;
        }
        responder.SetResponse(new StopProfilingResponse()
        {
            Path = path
        });
    }
}
