using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace Watching.Common;

/// <summary>基于命名互斥体的单实例控制。</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    public bool IsPrimary { get; }

    public SingleInstance(string name)
    {
        try
        {
            _mutex = new Mutex(true, @"Global\Watching." + name, out bool created);
            IsPrimary = created;
        }
        catch
        {
            // 无法创建全局互斥体（权限不足等）时退化为本会话互斥体
            try
            {
                _mutex = new Mutex(true, @"Local\Watching." + name, out bool created);
                IsPrimary = created;
            }
            catch
            {
                IsPrimary = true;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (IsPrimary) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
    }
}

/// <summary>轻量命名管道，用于把“连接到某地址”的请求转交给已运行的客户端实例。</summary>
public static class NamedPipeHelper
{
    public static bool TrySend(string message)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", "WatchingClientPipe", PipeDirection.Out);
            client.Connect(400);
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.WriteLine(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Serve(string name, Action<string> onMessage)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(
                        "WatchingClientPipe", PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var r = new StreamReader(server);
                    string line;
                    while ((line = r.ReadLine()) != null)
                        onMessage(line);
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
        })
        { IsBackground = true, Name = "watching-pipe-server" };
        t.Start();
    }
}
