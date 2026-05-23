namespace PalLLM.Domain.Runtime;

public static class PalStatusLine
{
    private static volatile string _current = "Starting PalLLM...";
    private static volatile bool _isReady;
    private static volatile bool _isError;
    private static int _activityCount;

    public static string Current => _current;

    public static bool IsReady => _isReady;

    public static bool IsError => _isError;

    public static int ActivityCount => Volatile.Read(ref _activityCount);

    public static void Set(string message)
    {
        _current = message;
        _isReady = false;
        _isError = false;
    }

    public static void SetReady(string message)
    {
        _current = message;
        _isReady = true;
        _isError = false;
    }

    public static void SetError(string message)
    {
        _current = message;
        _isReady = false;
        _isError = true;
    }

    public static void NoteActivity()
    {
        Interlocked.Increment(ref _activityCount);
    }
}
