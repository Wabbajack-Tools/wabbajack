using System.CommandLine;

namespace Wabbajack.CLI.Verbs;

public interface IVerb
{
    public Command MakeCommand();
}