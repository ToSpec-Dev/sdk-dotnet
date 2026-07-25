using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(ToSpec.Sdk.Benchmarks.ExchangeRedactorBenchmarks).Assembly).Run(args);
