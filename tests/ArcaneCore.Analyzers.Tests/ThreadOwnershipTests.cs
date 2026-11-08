namespace ArcaneCore.Analyzers.Tests;

/// <summary>ARC0001 (off-thread callback) and ARC0002 (after an await): positive and negative cases.</summary>
public sealed class ThreadOwnershipTests
{
    [Fact]
    public void TaskRunLambda_TouchingAWorldThreadMethod_IsReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { void M(Entity e) { Task.Run(() => e.Tick()); } }
            """);
        Assert.Equal(["ARC0001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void FieldsAndProperties_AreReportedToo()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { void M(Entity e) { Task.Run(() => { e.Hp = 1; return e.Level; }); } }
            """);
        Assert.Equal(["ARC0001", "ARC0001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void DerivedTypes_InheritTheOwnership()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { void M(Creature c) { Task.Run(() => c.Despawn()); } }
            """);
        Assert.Equal(["ARC0001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Theory]
    [InlineData("ThreadPool.QueueUserWorkItem(_ => e.Tick());")]
    [InlineData("new Thread(() => e.Tick()).Start();")]
    [InlineData("Task.CompletedTask.ContinueWith(_ => e.Tick());")]
    [InlineData("Task.Factory.StartNew(() => e.Tick());")]
    [InlineData("Parallel.Invoke(() => e.Tick());")]
    [InlineData("new Timer(_ => e.Tick(), null, 1, 1);")]
    [InlineData("CancellationToken.None.Register(() => e.Tick());")]
    public void EveryOffThreadApi_IsRecognised(string statement)
    {
        var diagnostics = AnalyzerHarness.Analyze("class C { void M(Entity e) { " + statement + " } }");
        Assert.Equal(["ARC0001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void MethodGroupHandedToTaskRun_IsReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { void M(Entity e) { Task.Run(e.Tick); } }
            """);
        Assert.Equal(["ARC0001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void ALambdaNestedInsideTheOffThreadOne_StaysOffThread()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { void M(List<Entity> es) { Task.Run(() => es.ForEach(x => x.Tick())); } }
            """);
        Assert.Equal(["ARC0001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AnyThreadMembers_AreNotReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { void M(Entity e) { Task.Run(() => { e.Peek(); return e.Id; }); } }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void HoppingBackThroughARunsOnWorldThreadMethod_IsAccepted()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { void M(World w, Entity e) { Task.Run(() => w.Post(() => e.Tick())); } }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void OnlyTheParameterMarkedRunsOnWorldThread_IsAccepted()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                void Hopped(World w, Entity e) { Task.Run(() => w.Hop(() => e.Tick(), () => { })); }
                void NotHopped(World w, Entity e) { Task.Run(() => w.Hop(() => { }, () => e.Tick())); }
            }
            """);
        Assert.Equal(["ARC0001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AMethodThatIsNotMarked_DoesNotHopBack()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { void M(World w, Entity e) { Task.Run(() => w.PostPlain(() => e.Tick())); } }
            """);
        Assert.Equal(["ARC0001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void PlainCodeAndOrdinaryLambdas_AreNotReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                void M(Entity e, List<Entity> es)
                {
                    e.Tick();
                    e.Hp = 3;
                    es.ForEach(x => x.Tick());
                    Action a = () => e.Tick();
                    a();
                }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AnOffThreadLambdaThatDoesNotTouchTheEntity_IsNotReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { void M(int x) { Task.Run(() => x + 1); } }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void UseAfterAwait_IsReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { async Task M(Entity e) { await Task.Delay(1); e.Tick(); } }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void UseBeforeTheAwait_IsNotReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { async Task M(Entity e) { e.Tick(); await Task.Delay(1); } }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void UseAfterAwaitInsideAPostedLambda_IsAccepted()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { async Task M(World w, Entity e) { await Task.Delay(1); w.Post(() => e.Tick()); } }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void UseAfterAwaitInsideAnOrdinaryLambda_IsReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { async Task M(List<Entity> es) { await Task.Delay(1); es.ForEach(x => x.Tick()); } }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AnAwaitInAnotherLambda_DoesNotTaintTheOuterMethod()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                void M(Entity e)
                {
                    Func<Task> later = async () => await Task.Delay(1);
                    e.Tick();
                }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AsyncCodeOnTypesThatAreNotWorldThreadOnly_IsNotReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { async Task M(World w) { await Task.Delay(1); w.PostPlain(() => { }); } }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ALocalFunctionDeclaredAfterAnAwait_ButCalledBeforeIt_IsNotReported()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e)
                {
                    Touch();
                    await Task.Delay(1);
                    void Touch() => e.Tick();
                }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AnAsyncLocalFunction_StillCountsItsOwnAwaits()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                void M(Entity e)
                {
                    Run();
                    async Task Run() { await Task.Delay(1); e.Tick(); }
                }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void ALocalFunctionInsideAnOffThreadLambda_StillReportsOffThreadAccess()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                void M(Entity e)
                {
                    Task.Run(() => { Touch(); void Touch() => e.Tick(); });
                }
            }
            """);
        Assert.Equal(["ARC0001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void ATryBlockThatReturns_StillFallsThroughWhenTheAwaitFails()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e)
                {
                    try { await Task.Delay(1); return; } catch { }
                    e.Tick();
                }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void ALoopBodyThatBreaksBeforeItsReturn_StillReachesCodeAfterTheLoop()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e, int[] xs)
                {
                    foreach (var i in xs) { await Task.Delay(1); if (i > 0) break; return; }
                    e.Tick();
                }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AnAwaitInABranchThatReturns_DoesNotTaintCodeAfterTheBranch()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e, bool b)
                {
                    if (b) { await Task.Delay(1); return; }
                    e.Tick();
                }
                async Task<int> N(Entity e, int k)
                {
                    switch (k)
                    {
                        case 1: await Task.Delay(1); return 1;
                        default: e.Tick(); return 0;
                    }
                }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AnAwaitInABranchThatFallsThrough_StillTaintsCodeAfterIt()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { async Task M(Entity e, bool b) { if (b) { await Task.Delay(1); } e.Tick(); } }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AnAssignmentWhoseValueAwaits_WritesAfterTheAwait()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e, Task<int> t) { e.Level = await t; }
                async Task N(Entity e, Task<int> t) { e.Hp += await t; }
            }
            """);
        Assert.Equal(["ARC0002", "ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void ACallWhoseArgumentAwaits_RunsAfterTheAwait()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C { async Task M(Entity e, Task<int> t) { e.Set(await t); } }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AwaitForeach_SuspendsBeforeItsBody()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e, IAsyncEnumerable<int> items)
                {
                    await foreach (int item in items) { e.Tick(); }
                }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AwaitUsing_SuspendsAtItsEnd()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e, IAsyncDisposable d)
                {
                    await using (d) { e.Tick(); }
                    e.Tick();
                }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AReferenceInAFinally_RunsAfterTheAwaitInItsTry()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e) { try { await Task.Delay(1); return; } finally { e.Tick(); } }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void ACatchThatRethrowsOrReturns_DoesNotFallThrough()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e) { try { await Task.Delay(1); return; } catch { throw; } e.Tick(); }
                async Task N(Entity e) { try { await Task.Delay(1); return; } catch { return; } e.Tick(); }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AReturnNestedInsideATryWithCatch_StillFallsThrough()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e, bool b) { try { if (b) { await Task.Delay(1); return; } } catch { } e.Tick(); }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AForeachReturnNestedInsideATryWithCatch_StillFallsThrough()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e, int[] xs) { try { foreach (var a in xs) { await Task.Delay(1); return; } } catch { } e.Tick(); }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AnInnerTryFinally_StillReachesTheOuterCatch()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e) { try { try { await Task.Delay(1); return; } finally { } } catch { } e.Tick(); }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void UseBeforeTheAwaitInALoopBody_RunsAfterItNextIteration()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e) { for (int i = 0; i < 3; i++) { e.Tick(); await Task.Delay(1); } }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AForeachBodyThatReturnsFromItsTry_DoesNotReachAfterTheLoop()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e, int[] xs) { foreach (var a in xs) { try { await Task.Delay(1); return; } finally { } } e.Tick(); }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AnAwaitInsideAFinally_TaintsCodeAfterIt()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e) { try { } finally { await Task.Delay(1); } e.Tick(); }
                async Task N(Entity e) { try { e.Tick(); } finally { await Task.Delay(1); } }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void ARejectedExceptionFilter_PropagatesToTheOuterCatch()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e, bool f) { try { try { await Task.Delay(1); return; } catch (Exception) when (f) { return; } } catch { } e.Tick(); }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AMemberReadOnAnAwaitedReceiver_RunsAfterTheAwait()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Task<Entity> t) { _ = (await t).Level; }
            }
            """);
        Assert.Equal(["ARC0002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void NameofIsNotAnAccess()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e) { await Task.Delay(1); _ = nameof(e.Level); }
                void N(Entity e) { Task.Run(() => nameof(e.Level)); }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AReasonedPragma_IsTheSanctionedSuppression()
    {
        var diagnostics = AnalyzerHarness.Analyze("""
            class C
            {
                async Task M(Entity e)
                {
                    await Task.Delay(1);
            #pragma warning disable ARC0002 // the entity is not in a map yet, so no world thread owns it
                    e.Tick();
            #pragma warning restore ARC0002
                }
            }
            """);
        Assert.Empty(diagnostics);
    }
}
