namespace ion.syntax.test;

/// <summary>
/// A wall-clock budget for a parse, used by the pathological-input tests.
/// </summary>
/// <remarks>
/// NUnit's <c>[Timeout]</c> is inert on this target framework — the runner reports "TargetFramework
/// doesn't support timeout on tests", because cancelling a running test needs <c>Thread.Abort</c>,
/// which .NET Core removed. Running the parse on a pool thread and waiting with a deadline gives the
/// guard back: a grammar that no longer fails fast on 100 000 nested constructs turns into a failing
/// test instead of a suite that never finishes.
/// <para>
/// The runaway thread is left to finish on its own. There is no safe way to stop it, and it is a
/// pure function over a string, so it cannot corrupt anything while it does.
/// </para>
/// </remarks>
internal static class ParseBudget
{
    private static readonly TimeSpan Default = TimeSpan.FromSeconds(20);

    public static T Within<T>(Func<T> parse)
    {
        var task = Task.Run(parse);

        Assert.That(task.Wait(Default), Is.True,
            $"the parse did not finish within {Default.TotalSeconds:0} s — the input is meant to fail fast");

        return task.GetAwaiter().GetResult();
    }
}
