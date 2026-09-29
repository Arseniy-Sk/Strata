using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace Browser.Core;

/// <summary>
/// Один экземпляр Strata на пользователя. Повторный запуск передаёт свои аргументы
/// уже работающему окну (через именованный канал) и завершается, а окно выходит на передний план.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Strata.SingleInstance.v1";
    private const string PipeName = "Strata.Activate.v1";
    private static Mutex? _mutex;

    public static bool IsPrimary { get; private set; }

    /// <summary>Возвращает true, если это первый экземпляр. Иначе пересылает аргументы и false.</summary>
    public static bool Claim(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        IsPrimary = createdNew;
        if (createdNew) return true;
        TrySend(args);
        return false;
    }

    private static void TrySend(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(string.Join("\n", args.Where(a => !a.StartsWith('-'))));
        }
        catch
        {
            // Основной экземпляр не ответил — второй просто закроется.
        }
    }

    /// <summary>Слушает запросы активации от повторных запусков.</summary>
    public static void Listen(Action<string[]> onActivate)
    {
        if (!IsPrimary) return;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var payload = reader.ReadToEnd();
                    var urls = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    onActivate(urls);
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
        })
        { IsBackground = true, Name = "Strata.Ipc" };
        thread.Start();
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
    }
}
