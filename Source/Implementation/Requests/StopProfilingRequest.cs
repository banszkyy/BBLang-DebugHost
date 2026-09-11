using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DebugServer;

class StopProfilingArguments
{

}

class StopProfilingRequest : DebugRequest<StopProfilingArguments>
{
    public StopProfilingRequest() : base("stopProfiling") { }
}
