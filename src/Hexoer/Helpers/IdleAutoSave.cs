using Avalonia.Threading;

namespace Hexoer.Helpers;

/// <summary>
/// Restarts a one-shot timer on each <see cref="Schedule"/> call and runs
/// <paramref name="saveAsync"/> after the idle interval if <paramref name="shouldSave"/> is true.
/// </summary>
public sealed class IdleAutoSave : IDisposable
{
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _shouldSave;
    private readonly Func<Task> _saveAsync;
    private bool _running;

    public IdleAutoSave(Func<bool> shouldSave, Func<Task> saveAsync, TimeSpan? idle = null)
    {
        _shouldSave = shouldSave;
        _saveAsync = saveAsync;
        _timer = new DispatcherTimer { Interval = idle ?? DefaultIdle };
        _timer.Tick += OnTick;
    }

    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_running || !_shouldSave())
            return;

        _running = true;
        try
        {
            await _saveAsync().ConfigureAwait(true);
        }
        finally
        {
            _running = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
