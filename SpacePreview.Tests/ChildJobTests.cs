using System.Diagnostics;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// A helper must not outlive the app. The job is what ends it when nothing
/// in the app is left to: closing the job's handle is all a crash does.
/// </summary>
public sealed class ChildJobTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void ClosingTheJobEndsAHelperStillRunning()
    {
        using var helper = StartIdleHelper();
        var job = new ChildJob();
        try
        {
            Assert.True(job.Add(helper));
            Assert.Equal(1, job.ActiveProcesses);
            Assert.False(helper.HasExited);
        }
        finally
        {
            job.Dispose();
        }

        Assert.True(helper.WaitForExit(Patience));
        Assert.Equal(0, job.ActiveProcesses);
    }

    [Fact]
    public void WhatAHelperStartsEndsWithIt()
    {
        // cmd is the helper and the long ping its child, as soffice.bin is
        // soffice.exe's. The short ping gives the job time to take cmd
        // first: a child started before that is outside, and ends alone.
        // Windows may count cmd's console host as one more.
        using var helper = Start("cmd.exe", "/c ping -n 2 127.0.0.1 > nul & ping -n 120 127.0.0.1 > nul");
        using var job = new ChildJob();
        Assert.True(job.Add(helper));
        Assert.True(WaitUntil(() => job.ActiveProcesses > 1));

        job.Terminate();

        Assert.True(helper.WaitForExit(Patience));
        Assert.True(WaitUntil(() => job.ActiveProcesses == 0));
    }

    [Fact]
    public void AJobHoldsItsHelpersToItsPriority()
    {
        using var helper = StartIdleHelper();
        using var job = new ChildJob(ProcessPriorityClass.BelowNormal);
        Assert.True(job.Add(helper));

        helper.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, helper.PriorityClass);
    }

    [Fact]
    public void AHelperAlreadyGoneIsNotAnError()
    {
        using var helper = Start("cmd.exe", "/c exit 0");
        Assert.True(helper.WaitForExit(Patience));
        using var job = new ChildJob();

        job.Add(helper);

        Assert.Equal(0, job.ActiveProcesses);
    }

    // One process that waits and starts nothing.
    private static Process StartIdleHelper() => Start("ping.exe", "-n 120 127.0.0.1");

    private static Process Start(string file, string arguments) =>
        Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

    private static bool WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Patience)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }
}
