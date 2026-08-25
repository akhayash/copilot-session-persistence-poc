namespace CopilotSessionPersistencePoc.SessionFs;

public readonly record struct SessionFsPath
{
    private SessionFsPath(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public string Name =>
        Value == "/"
            ? "/"
            : Value[(Value.LastIndexOf('/') + 1)..];

    public string? Parent =>
        Value switch
        {
            "/" => null,
            _ when Value.LastIndexOf('/') == 0 => "/",
            _ => Value[..Value.LastIndexOf('/')],
        };

    public string DescendantPrefix => Value == "/" ? "/" : $"{Value}/";

    public static SessionFsPath Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.Contains('\0') || path.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("SessionFS paths must use POSIX separators.", nameof(path));
        }

        var absolute = path.StartsWith('/')
            ? path
            : $"/{path}";
        if (absolute.Length > 1 && absolute.EndsWith('/'))
        {
            absolute = absolute.TrimEnd('/');
        }

        if (absolute == "/")
        {
            return new SessionFsPath(absolute);
        }

        var segments = absolute.Split('/', StringSplitOptions.None);
        if (segments.Skip(1).Any(segment =>
                segment.Length == 0
                || segment is "." or ".."))
        {
            throw new ArgumentException("SessionFS paths cannot contain empty, '.' or '..' segments.", nameof(path));
        }

        return new SessionFsPath(absolute);
    }

    public IEnumerable<string> Ancestors()
    {
        if (Value == "/")
        {
            yield break;
        }

        yield return "/";
        var current = string.Empty;
        var segments = Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current += $"/{segments[index]}";
            yield return current;
        }
    }

    public override string ToString() => Value;
}
