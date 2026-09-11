using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DebugServer;

class StartProfilingArguments
{

}

class StartProfilingRequest : DebugRequest<StartProfilingArguments>
{
    public StartProfilingRequest() : base("startProfiling") { }
}
