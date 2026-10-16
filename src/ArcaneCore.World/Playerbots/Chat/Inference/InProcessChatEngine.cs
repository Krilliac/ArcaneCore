using System.Collections.Concurrent;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>
/// The in-process chat model at run time: the model (its file mapped once) and <see cref="Slots"/> generation slots, each a
/// <see cref="ChatInferenceContext"/> driven by a dedicated BelowNormal-priority thread of its own. <see cref="GenerateAsync"/> waits
/// for a free slot, hands the prompt to that slot's thread and completes when the reply is written; the caller's thread (a chat
/// worker on the pool) never computes. Pure managed code: no native library, no other process.
/// </summary>
internal sealed class InProcessChatEngine : IDisposable
{
    private readonly Slot[] _slots;
    private readonly ConcurrentStack<Slot> _idle = new();
    private readonly SemaphoreSlim _free;
    private int _disposed;

    private InProcessChatEngine(ChatModel model, int contextTokens, int threads, int slots)
    {
        Model = model;
        _slots = new Slot[slots];
        try
        {
            for (int i = 0; i < slots; i++)
            {
                _slots[i] = new Slot(new ChatInferenceContext(model, contextTokens, threads, $"bot-chat-{i}"), i);
                _idle.Push(_slots[i]);
            }
        }
        catch
        {
            foreach (Slot? slot in _slots) slot?.Stop(TimeSpan.FromSeconds(5));
            throw;
        }

        _free = new SemaphoreSlim(slots, slots);
    }

    public ChatModel Model { get; }

    public int Slots => _slots.Length;

    public int Threads => _slots[0].Context.Threads;

    public int ContextTokens => _slots[0].Context.ContextTokens;

    /// <summary>Bytes the slots allocated (caches and buffers; the weights are the mapped file).</summary>
    public long AllocatedBytes => _slots.Sum(slot => slot.Context.AllocatedBytes);

    /// <summary>Load <paramref name="path"/> and build the slots. Throws (<see cref="InvalidDataException"/>, I/O) with the reason when the file cannot be run.</summary>
    public static InProcessChatEngine Load(string path, int contextTokens, int threads, int slots)
    {
        ChatModel model = ChatModel.Load(path);
        try
        {
            return new InProcessChatEngine(model, contextTokens, threads, Math.Clamp(slots, 1, 8));
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    /// <summary>Build an engine over an already loaded model (tests; the engine then owns it).</summary>
    internal static InProcessChatEngine Over(ChatModel model, int contextTokens, int threads, int slots) => new(model, contextTokens, threads, Math.Clamp(slots, 1, 8));

    /// <summary>
    /// Generate on the next free slot. <paramref name="cancellationToken"/> stops the wait for a slot (throws
    /// <see cref="OperationCanceledException"/>) and, once generating, ends it at the next pass (<see cref="ChatGenerationStatus.Cancelled"/>).
    /// </summary>
    public async Task<ChatGenerationResult> GenerateAsync(IReadOnlyList<int> prompt, ChatGenerationSettings settings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _free.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_idle.TryPop(out Slot? slot))
        {
            _free.Release();
            throw new InvalidOperationException("no idle generation slot");
        }

        try
        {
            return await slot.Run(prompt, settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _idle.Push(slot);
            _free.Release();
        }
    }

    /// <summary>Stop the slot threads; the file is unmapped only when every thread has stopped (otherwise it is left mapped).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        bool stopped = true;
        foreach (Slot slot in _slots) stopped &= slot.Stop(TimeSpan.FromSeconds(10));
        if (stopped) Model.Dispose();
    }

    private sealed class Slot
    {
        private readonly Thread _thread;
        private readonly SemaphoreSlim _signal = new(0);
        private Work? _work;
        private volatile bool _stopping;

        public Slot(ChatInferenceContext context, int index)
        {
            Context = context;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"bot-chat-{index} generate",
            };
            _thread.Start();
        }

        public ChatInferenceContext Context { get; }

        public Task<ChatGenerationResult> Run(IReadOnlyList<int> prompt, ChatGenerationSettings settings, CancellationToken cancellationToken)
        {
            var work = new Work(prompt, settings, cancellationToken);
            Volatile.Write(ref _work, work);
            _signal.Release();
            return work.Done.Task;
        }

        private void Loop()
        {
            while (true)
            {
                _signal.Wait();
                if (_stopping) return;
                Work? work = Interlocked.Exchange(ref _work, null);
                if (work is null) continue;
                try
                {
                    work.Done.SetResult(Context.Generate(work.Prompt, work.Settings, work.Cancellation));
                }
                catch (Exception error)
                {
                    Context.Reset(); // the cache may hold half a pass
                    work.Done.SetException(error);
                }
            }
        }

        public bool Stop(TimeSpan timeout)
        {
            _stopping = true;
            _signal.Release();
            bool joined = _thread.Join(timeout);
            if (joined) Context.Dispose();
            return joined;
        }
    }

    private sealed record Work(IReadOnlyList<int> Prompt, ChatGenerationSettings Settings, CancellationToken Cancellation)
    {
        public TaskCompletionSource<ChatGenerationResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
