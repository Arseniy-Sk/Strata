using System.Windows.Threading;

namespace Browser.Core;

/// <summary>Схлопывает частые вызовы (сохранение, пересчёт списков) в один отложенный.</summary>
public sealed class Debouncer
{
    private readonly DispatcherTimer _timer;
    private Action? _action;

    public Debouncer(TimeSpan delay, DispatcherPriority priority = DispatcherPriority.Background)
    {
        _timer = new DispatcherTimer(priority) { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            var action = _action;
            _action = null;
            action?.Invoke();
        };
    }

    public void Run(Action action)
    {
        _action = action;
        _timer.Stop();
        _timer.Start();
    }

    public void Flush()
    {
        if (_action == null) return;
        _timer.Stop();
        var action = _action;
        _action = null;
        action();
    }
}
