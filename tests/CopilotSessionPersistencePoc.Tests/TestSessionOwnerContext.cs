using CopilotSessionPersistencePoc.AppState;

namespace CopilotSessionPersistencePoc.Tests;

internal sealed class TestSessionOwnerContext(string ownerId) : ISessionOwnerContext
{
    public string OwnerKey { get; } = SessionOwnerKey.Create(ownerId);
}
