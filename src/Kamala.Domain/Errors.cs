namespace Kamala.Domain;

/// <summary>The user's input is invalid. The message is safe to show to the user (HTTP 400).</summary>
public sealed class InvalidInputException(string message) : Exception(message)
{
    public static void ThrowIf(bool condition, string message)
    {
        if (condition)
            throw new InvalidInputException(message);
    }
}

/// <summary>A tool needs an engine that this server doesn't have installed (HTTP 503).</summary>
public sealed class FeatureUnavailableException(string message) : Exception(message);

/// <summary>An engine failed for reasons that are not the user's fault (HTTP 500; details are only logged).</summary>
public sealed class ProcessingFailedException(string message, Exception? inner = null) : Exception(message, inner);
