using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// In-process: BenchmarkDotNet's out-of-process toolchain does not know the net11.0 moniker yet. Each benchmark still gets its own warmup and
// pilot stages; run on an otherwise idle machine and compare runs only from the same session.
IConfig config = DefaultConfig.Instance.AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance));
BenchmarkSwitcher.FromAssembly(typeof(ArcaneCore.Benchmarks.SpellbookBenchmarks).Assembly).Run(args, config);
