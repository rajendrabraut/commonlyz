namespace Commonlyz.Core;

/// <summary>
/// A user-facing rule violation ("slug is taken", "end must be after start").
/// Pages show <see cref="Exception.Message"/>; anything else is treated as a bug.
/// </summary>
public sealed class AppException : Exception
{
    public AppException(string message) : base(message)
    {
    }

    public AppException()
    {
    }

    public AppException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public static void ThrowIf(bool condition, string message)
    {
        if (condition)
            throw new AppException(message);
    }

    public static T NotFound<T>(T? value, string what) where T : class =>
        value ?? throw new AppException($"{what} not found.");
}
