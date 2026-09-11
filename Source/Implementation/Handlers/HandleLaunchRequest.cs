using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using LanguageCore;
using LanguageCore.BBLang.Generator;
using LanguageCore.Compiler;
using LanguageCore.Runtime;
using LanguageCore.Workspaces;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Utilities;

namespace DebugServer;

partial class BytecodeDebugAdapter
{
    VirtualIO? IO;

    protected override LaunchResponse HandleLaunchRequest(LaunchArguments arguments)
    {
        Log.Trace($"[Handler] Launch");

        string fileName = arguments.ConfigurationProperties.GetValueAsString("program");
        if (string.IsNullOrEmpty(fileName))
        {
            Log.Error($"Program is null or empty");
            throw new ProtocolException("Launch failed because launch configuration did not specify 'program'.");
        }

        fileName = Path.GetFullPath(fileName);
        if (!File.Exists(fileName))
        {
            Log.Error($"file \"{fileName}\" doesn't exists");
            throw new ProtocolException("Launch failed because 'program' files does not exist.");
        }

        Log.Trace($"Disposing previous session");
        DisposeSession();

        NoDebug = arguments.NoDebug ?? false;
        StopOnEntry = arguments.ConfigurationProperties.GetValueAsBool("stopOnEntry") ?? false;
        if (arguments.ConfigurationProperties.GetValueAsBool("profile") ?? false)
        {
            Profiler = new LanguageCore.Profiling.GoogleProfiler(DebugInformation);
            ProfilerTick = 0;
        }

        Log.Trace($"Preparing");
        IO = new();
        List<IExternalFunction> externalFunctions = BytecodeProcessor.GetExternalFunctions(IO);
        IO.OnData += WriteStdout;

        DiagnosticsCollection diagnostics = new();

        Log.Trace($"Parsing configuration");
        Configuration config = Configuration.Parse(ConfigurationManager.Search(new Uri(fileName, UriKind.Absolute)), diagnostics);
        if (diagnostics.HasErrors)
        {
            Log.Trace($"Diagnostic errors");
            StringBuilder b = new();
            diagnostics.WriteErrorsTo(b);
            Protocol.SendEvent(new OutputEvent()
            {
                Output = b.ToString(),
                Severity = OutputEvent.SeverityValue.Error,
            });
        }
        diagnostics.Clear();

        Log.Trace($"Compiling code");
        try
        {
            _compiled = StatementCompiler.CompileFile(fileName, new(CodeGeneratorForMain.DefaultCompilerSettings)
            {
                ExternalFunctions = externalFunctions.ToImmutableArray(),
                AdditionalImports = config.AdditionalImports,
                ExternalConstants = config.ExternalConstants,
                SourceProviders = ImmutableArray.Create<ISourceProvider>(new FileSourceProvider()
                {
                    ExtraDirectories = config.ExtraDirectories,
                }),
                Optimizations = OptimizationSettings.None,
            }, diagnostics);
        }
        catch (Exception ex)
        {
            Log.Trace($"Diagnostic errors");
            Protocol.SendEvent(new OutputEvent()
            {
                Output = ex.ToString(),
                Severity = OutputEvent.SeverityValue.Error,
            });
            Protocol.SendEvent(new ExitedEvent() { ExitCode = -1 });
            Protocol.SendEvent(new TerminatedEvent());
            return new LaunchResponse();
        }
        if (diagnostics.HasErrors)
        {
            Log.Trace($"Diagnostic errors");
            StringBuilder b = new();
            diagnostics.WriteErrorsTo(b);
            Protocol.SendEvent(new OutputEvent()
            {
                Output = b.ToString(),
                Severity = OutputEvent.SeverityValue.Error,
            });
            Protocol.SendEvent(new ExitedEvent() { ExitCode = -1 });
            Protocol.SendEvent(new TerminatedEvent());
            return new LaunchResponse();
        }

        Log.Trace($"Generating code");
        _generated = CodeGeneratorForMain.Generate(_compiled, new MainGeneratorSettings(MainGeneratorSettings.Default)
        {
            Optimizations = GeneratorOptimizationSettings.None,
        }, null, diagnostics);
        if (diagnostics.HasErrors)
        {
            Log.Trace($"Diagnostic errors");
            Protocol.SendEvent(new ExitedEvent() { ExitCode = -1 });
            Protocol.SendEvent(new TerminatedEvent());
            return new LaunchResponse();
        }

        Log.Trace($"Preparing processor");
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
            Log.Trace($"Stopping on entry");
            RequestStop(StopReason_Pause.Instance);
        }
        else
        {
            StopReason = null;
        }

        Log.Trace($"Creating thread");
        RuntimeThread = new(RuntimeImpl)
        {
            Name = "Runtime Thread"
        };

        Log.Trace($"Thread started");

        return new LaunchResponse();
    }
}
