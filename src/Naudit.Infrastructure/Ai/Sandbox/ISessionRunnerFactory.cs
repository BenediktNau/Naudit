using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Wählt den IProcessRunner für einen Abo-Session-Lauf eines Accounts: in-process
/// (heutiges Verhalten) oder account-gebundener Docker-Container (SessionSandbox=Docker).
/// Die Naht sitzt in SessionSelectionFactory.ForAccount — SAST/git bleiben unberührt am
/// geteilten SystemProcessRunner.</summary>
public interface ISessionRunnerFactory
{
    IProcessRunner ForAccount(int accountId);
}

/// <summary>Default (SessionSandbox=None): immer der geteilte In-Process-Runner.</summary>
public sealed class InProcessSessionRunnerFactory(IProcessRunner runner) : ISessionRunnerFactory
{
    public IProcessRunner ForAccount(int accountId) => runner;
}
