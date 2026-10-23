using Microsoft.AspNetCore.Components;

namespace Commonlyz.Core.Components;

/// <summary>
/// Base class for module pages: runs an action, shows <see cref="AppException"/> messages inline
/// and lets real bugs bubble up to the layout's ErrorBoundary.
/// </summary>
public abstract class ModulePage : ComponentBase
{
    protected string? Error { get; set; }
    protected string? Success { get; set; }
    protected bool Busy { get; private set; }

    protected async Task<bool> RunAsync(Func<Task> action, string? success = null)
    {
        Error = null;
        Success = null;
        Busy = true;
        try
        {
            await action();
            Success = success;
            return true;
        }
        catch (AppException ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            Busy = false;
        }
    }
}
