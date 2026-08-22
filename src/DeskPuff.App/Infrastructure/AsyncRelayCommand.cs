using System.Windows.Input;

namespace DeskPuff.App.Infrastructure;

internal sealed class AsyncRelayCommand(
    Func<CancellationToken, Task> execute,
    Action<Exception> handleError,
    Func<bool>? canExecute = null) : ICommand, IAsyncDisposable
{
    private CancellationTokenSource? executionCancellation;
    private Task currentExecution = Task.CompletedTask;
    private bool isExecuting;
    private bool disposed;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !disposed && !isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        currentExecution = ExecuteAsync();
        await currentExecution;
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public void Cancel()
    {
        try
        {
            executionCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The execution completed between reading the field and cancellation.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Cancel();
        NotifyCanExecuteChanged();
        await currentExecution.ConfigureAwait(false);
    }

    private async Task ExecuteAsync()
    {
        isExecuting = true;
        CancellationTokenSource cancellation = new();
        executionCancellation = cancellation;
        NotifyCanExecuteChanged();
        try
        {
            await execute(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            handleError(exception);
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(executionCancellation, cancellation))
            {
                executionCancellation = null;
            }

            isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }
}
