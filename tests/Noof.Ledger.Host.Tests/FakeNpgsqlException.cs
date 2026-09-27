namespace Npgsql;

// A type in a fake "Npgsql"-looking namespace, standing in for the real driver exception without
// Host.Tests taking a dependency on Npgsql itself - SafeFailureReason only ever inspects the type
// name string (CLAUDE.md's architecture rule: Application takes no database-driver reference).
public sealed class FakeNpgsqlException(string message) : Exception(message);
